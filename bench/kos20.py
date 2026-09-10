#!/usr/bin/env python3
"""Acceptance gate 5: recall parity of the 2.0 ranking on the owner's gold set.

The v0 harness (`kos.py`) imported extracted `retrieve.py` checkpoints from
`bench/.versions/` and produced TREC runs. The 2.0 backend has no importable
Python surface: the ranking lives in `src/Oom/Retrieve/Retrieve.cs` and is
reachable only through the executable. This script is that backend. It drives

    oom.exe --vault <vault> retrieve --batch <jsonl> --top <k>

which is strictly read-only over the vault: `Retrieve.Query` loads the concept
corpus from `<vault>/knowledge/concepts/*.md`, ranks with field-weighted BM25
and returns; nothing is written to the vault and the served-dedupe table is a
process-local dictionary, so repeated runs are independent.

Output: a TREC run file tagged `oom-2.0`, a results JSON under
`bench/results/`, and a Markdown table on stdout. Numbers only -- no note body,
transcript or vault text ever reaches the repository.

Python 3 standard library only.
"""

from __future__ import annotations

import argparse
import json
import os
import platform
import subprocess
import sys
import time
from datetime import date
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_EXE = ROOT / "publish" / "win-x64" / "Oom.exe"
DEFAULT_GOLD = ROOT / ".brief" / "gold-sorular.jsonl"
DEFAULT_PROBE = ROOT / "bench" / "probe-30.jsonl"
DEFAULT_VAULT = r"<vault>"
RUN_TAG = "oom-2.0"
CANARY = "kanarya"


# ---------------------------------------------------------------- gold set


def load_gold(path: Path, limit: int | None) -> list[dict[str, Any]]:
    """Read the gold JSONL and stamp each row with a stable query id."""
    rows: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8") as handle:
        for index, line in enumerate(handle, 1):
            text = line.strip()
            if not text:
                continue
            row = json.loads(text)
            question = str(row.get("soru", "")).strip()
            if not question:
                raise SystemExit(f"{path}:{index}: empty 'soru'")
            # `kanarya` rows carry an empty `gold` on purpose: they are negative controls
            # for which no note should be relevant, so they are scored apart from recall.
            gold = [str(slug) for slug in row.get("gold", [])]
            sinif = str(row.get("sinif", "bilinmiyor"))
            if not gold and sinif != CANARY:
                raise SystemExit(f"{path}:{index}: empty 'gold' on a non-{CANARY} row")
            rows.append(
                {
                    "id": f"q{index:03d}",
                    "soru": question,
                    "gold": gold,
                    "sinif": sinif,
                    "lane": str(row.get("lane", "")),
                }
            )
            if limit is not None and len(rows) >= limit:
                break
    if not rows:
        raise SystemExit(f"{path}: no rows")
    return rows


# ---------------------------------------------------------------- the backend


def slug(name: str) -> str:
    """`unity-secimi.md` -> `unity-secimi`; gold slugs carry no extension."""
    return name[:-3] if name.lower().endswith(".md") else name


def parse_hits(line: str) -> list[dict[str, Any]]:
    payload = json.loads(line)
    hits = []
    for hit in payload.get("hits", []):
        hits.append({"slug": slug(str(hit.get("name", ""))), "score": float(hit.get("score", 0.0))})
    return hits


def run_batch(exe: Path, vault: str, rows: list[dict[str, Any]], top: int, work: Path) -> tuple[list[list[dict[str, Any]]], float]:
    """One process, one query per line; returns rankings in input order."""
    work.mkdir(parents=True, exist_ok=True)
    batch = work / "gold-batch.jsonl"
    with batch.open("w", encoding="utf-8", newline="\n") as handle:
        for row in rows:
            handle.write(json.dumps({"id": row["id"], "soru": row["soru"]}, ensure_ascii=False) + "\n")

    command = [str(exe), "--vault", vault, "retrieve", "--batch", str(batch), "--top", str(top)]
    started = time.perf_counter()
    completed = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", cwd=str(ROOT))
    elapsed_ms = (time.perf_counter() - started) * 1000.0
    if completed.returncode != 0:
        raise SystemExit(f"retrieve --batch failed ({completed.returncode}): {completed.stderr.strip()[:400]}")

    lines = [line for line in completed.stdout.splitlines() if line.strip()]
    if len(lines) != len(rows):
        raise SystemExit(f"batch returned {len(lines)} lines for {len(rows)} queries; alignment is not safe")
    return [parse_hits(line) for line in lines], elapsed_ms


def single_call_latency(exe: Path, vault: str, rows: list[dict[str, Any]], top: int, sample: int) -> list[float]:
    """Per-process cost of one `retrieve --query`, for the batch-vs-single note."""
    timings = []
    for row in rows[:sample]:
        command = [str(exe), "--vault", vault, "retrieve", "--query", row["soru"], "--json", "--top", str(top)]
        started = time.perf_counter()
        subprocess.run(command, capture_output=True, text=True, encoding="utf-8", cwd=str(ROOT))
        timings.append((time.perf_counter() - started) * 1000.0)
    return timings


def hook_probe(exe: Path, vault: str, rows: list[dict[str, Any]], sample: int) -> dict[str, Any]:
    """Injection rate of the hook path (`retrieve --hook`), which `Query` never exercises.

    Spec 11-5 asks for a 30-prompt probe set; bench/ has none, so this substitute
    uses the gold set's own `kanarya` rows -- the owner marked them as prompts for
    which no note is relevant, so any injection on them is a false positive -- plus
    a sample of ordinary gold questions as the true-positive side. It is a smaller
    and different instrument than the specified probe set and is labelled as such.
    """
    canaries = [row for row in rows if row["sinif"] == CANARY]
    positives = [row for row in rows if row["sinif"] != CANARY][:sample]
    counts = {"kanarya": [0, 0], "gold": [0, 0]}
    detail = []
    for row in canaries + positives:
        payload = json.dumps(
            {"session_id": f"bench-probe-{row['id']}", "hook_event_name": "UserPromptSubmit", "prompt": row["soru"]},
            ensure_ascii=False,
        )
        completed = subprocess.run(
            [str(exe), "--vault", vault, "retrieve", "--hook"],
            input=payload, capture_output=True, text=True, encoding="utf-8", cwd=str(ROOT),
        )
        injected = completed.stdout.strip().startswith("{")
        key = CANARY if row["sinif"] == CANARY else "gold"
        counts[key][0] += int(injected)
        counts[key][1] += 1
        if key == CANARY:
            detail.append({"id": row["id"], "injected": injected, "reason": completed.stderr.strip() or None})

    return {
        "note": "substitute probe: the spec's 30-prompt probe set does not exist in bench/",
        "false_positive_rate_on_kanarya": round(counts[CANARY][0] / counts[CANARY][1], 4) if counts[CANARY][1] else None,
        "injection_rate_on_gold": round(counts["gold"][0] / counts["gold"][1], 4) if counts["gold"][1] else None,
        "counts": {key: {"injected": value[0], "n": value[1]} for key, value in counts.items()},
        "kanarya_detail": detail,
    }


def probe_gate(exe: Path, vault: str, path: Path) -> dict[str, Any]:
    """Spec 10.1 #17: at most 8 injections over a 30-prompt probe set.

    Unlike `hook_probe`, this is the instrument the spec actually asks for. Each row
    carries the prompt, whether it *should* inject (`enjekte`) and why, so the run
    reports three separate numbers: the injection count against the cap of 8, the
    false positives among the prompts that must stay silent, and the true positives
    among the real questions -- which all have to keep injecting for a lower
    injection count to mean anything.
    """
    rows = []
    with path.open("r", encoding="utf-8") as handle:
        for index, line in enumerate(handle, 1):
            text = line.strip()
            if not text:
                continue
            row = json.loads(text)
            if "prompt" not in row or "enjekte" not in row:
                raise SystemExit(f"{path}:{index}: 'prompt' and 'enjekte' are required")
            rows.append(row)
    if len(rows) != 30:
        raise SystemExit(f"{path}: the probe set is {len(rows)} prompts, spec 10.1 #17 wants 30")

    detail = []
    for row in rows:
        payload = json.dumps(
            {"session_id": f"bench-probe30-{row['id']}", "hook_event_name": "UserPromptSubmit", "prompt": row["prompt"]},
            ensure_ascii=False,
        )
        completed = subprocess.run(
            [str(exe), "--vault", vault, "retrieve", "--hook"],
            input=payload, capture_output=True, text=True, encoding="utf-8", cwd=str(ROOT),
        )
        injected = completed.stdout.strip().startswith("{")
        detail.append({"id": row["id"], "sinif": row.get("sinif", ""), "expected": bool(row["enjekte"]),
                       "injected": injected, "agrees": injected == bool(row["enjekte"])})

    injected = [d for d in detail if d["injected"]]
    positives = [d for d in detail if d["expected"]]
    negatives = [d for d in detail if not d["expected"]]
    return {
        "path": str(path),
        "n": len(rows),
        "cap": 8,
        "injected": len(injected),
        "within_cap": len(injected) <= 8,
        "true_positives": {"injected": sum(1 for d in positives if d["injected"]), "n": len(positives)},
        "false_positives": {"injected": sum(1 for d in negatives if d["injected"]), "n": len(negatives)},
        "all_real_questions_inject": all(d["injected"] for d in positives),
        "detail": detail,
    }


# ---------------------------------------------------------------- scoring


def recall_at(hits: list[dict[str, Any]], gold: list[str], k: int) -> int:
    top_slugs = {hit["slug"] for hit in hits[:k]}
    return 1 if any(slug in top_slugs for slug in gold) else 0


def rank_of_first_gold(hits: list[dict[str, Any]], gold: list[str]) -> int | None:
    for rank, hit in enumerate(hits, 1):
        if hit["slug"] in gold:
            return rank
    return None


def recall_curve(rows: list[dict[str, Any]], rankings: list[list[dict[str, Any]]], depths: list[int]) -> dict[str, Any]:
    """Where the gold note actually lands. Separates a candidate-generation gap
    (gold never scores) from an ordering gap (gold is found but ranked too low)."""
    ranks: list[tuple[str, int | None]] = []
    for row, hits in zip(rows, rankings):
        if row["sinif"] == CANARY:
            continue
        ranks.append((row["id"], rank_of_first_gold(hits, row["gold"])))

    total = len(ranks)
    curve = {}
    for depth in depths:
        hit = sum(1 for _, rank in ranks if rank is not None and rank <= depth)
        curve[str(depth)] = round(hit / total, 4) if total else 0.0
    return {
        "n": total,
        "depth": max(depths),
        "recall_at": curve,
        "beyond_depth": [qid for qid, rank in ranks if rank is None],
        "gold_rank": {qid: rank for qid, rank in ranks},
    }


def summarise(records: list[dict[str, Any]]) -> dict[str, Any]:
    total = len(records)
    at3 = sum(record["hit@3"] for record in records)
    at5 = sum(record["hit@5"] for record in records)
    reciprocal = sum(1.0 / record["first_gold_rank"] for record in records if record["first_gold_rank"])
    return {
        "n": total,
        "recall@3": round(at3 / total, 4) if total else 0.0,
        "recall@5": round(at5 / total, 4) if total else 0.0,
        "mrr@5": round(reciprocal / total, 4) if total else 0.0,
    }


def markdown_table(overall: dict[str, Any], per_class: dict[str, dict[str, Any]]) -> str:
    lines = [
        "| kume | n | recall@3 | recall@5 | MRR@5 |",
        "| --- | ---: | ---: | ---: | ---: |",
        f"| **genel** | {overall['n']} | {overall['recall@3']:.3f} | {overall['recall@5']:.3f} | {overall['mrr@5']:.3f} |",
    ]
    for name in sorted(per_class):
        value = per_class[name]
        lines.append(f"| {name} | {value['n']} | {value['recall@3']:.3f} | {value['recall@5']:.3f} | {value['mrr@5']:.3f} |")
    return "\n".join(lines)


# ---------------------------------------------------------------- artifacts


def write_trec(path: Path, rows: list[dict[str, Any]], rankings: list[list[dict[str, Any]]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    lines = []
    for row, hits in zip(rows, rankings):
        for rank, hit in enumerate(hits, 1):
            document = hit["slug"]
            if any(character.isspace() for character in row["id"] + document):
                raise SystemExit("TREC ids may not contain whitespace")
            lines.append(f"{row['id']} Q0 {document} {rank} {hit['score']:.17g} {RUN_TAG}\n")
    path.write_text("".join(lines), encoding="utf-8", newline="\n")


def write_json(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")


def state_digest(vault: str) -> str:
    """`VaultPaths.Digest`: first eight bytes of SHA-256 over the UTF-8 path, trailing separator trimmed."""
    import hashlib

    return hashlib.sha256(vault.rstrip("\\/").encode("utf-8")).hexdigest()[:16]


def index_counts(vault: str) -> dict[str, Any]:
    """Concept files on disk vs `notes` rows in the state root; both read-only."""
    concepts = Path(vault) / "knowledge" / "concepts"
    files = len(list(concepts.glob("*.md"))) if concepts.is_dir() else None
    rows: int | None = None
    error: str | None = None
    digest = state_digest(vault)
    state = Path(os.environ.get("LOCALAPPDATA", "")) / "oom" / digest / "state.db"
    try:
        import sqlite3

        if not state.is_file():
            raise FileNotFoundError(state)
        uri = "file:" + str(state).replace(os.sep, "/") + "?mode=ro"
        connection = sqlite3.connect(uri, uri=True)
        rows = int(connection.execute("select count(*) from notes").fetchone()[0])
        connection.close()
    except Exception as exc:  # measurement detail, never fatal
        error = f"{type(exc).__name__}: {exc}"
    return {
        "concept_files": files,
        "index_notes": rows,
        "state_root": str(state.parent),
        "error": error,
    }


# ---------------------------------------------------------------- main


def main() -> int:
    parser = argparse.ArgumentParser(description="gate 5: recall of the 2.0 ranking on the gold set")
    parser.add_argument("--exe", default=str(DEFAULT_EXE))
    parser.add_argument("--vault", default=DEFAULT_VAULT)
    parser.add_argument("--gold", default=str(DEFAULT_GOLD))
    parser.add_argument("--top", type=int, default=5)
    parser.add_argument("--curve-top", type=int, default=100,
                        help="second, diagnostic pass at this depth: recall@k curve and the rank of the gold note")
    parser.add_argument("--max-queries", type=int)
    parser.add_argument("--latency-sample", type=int, default=5, help="single-call timings for the batch comparison")
    parser.add_argument("--hook-sample", type=int, default=20, help="ordinary gold questions in the hook probe; 0 skips it")
    parser.add_argument("--probe", nargs="?", const=str(DEFAULT_PROBE), default=None,
                        help="30-prompt probe set for the hook gate (spec 10.1 #17); bare flag uses bench/probe-30.jsonl")
    parser.add_argument("--out")
    parser.add_argument("--run-file", default=str(ROOT / "bench" / ".out" / "oom-2.0.run"))
    parser.add_argument("--work-dir", default=str(ROOT / "bench" / ".data"))
    arguments = parser.parse_args()

    exe = Path(arguments.exe)
    if not exe.is_file():
        raise SystemExit(f"executable not found: {exe}")
    gold_path = Path(arguments.gold)
    if not gold_path.is_file():
        raise SystemExit(f"gold set not found: {gold_path}")

    rows = load_gold(gold_path, arguments.max_queries)
    rankings, batch_ms = run_batch(exe, arguments.vault, rows, arguments.top, Path(arguments.work_dir))
    singles = single_call_latency(exe, arguments.vault, rows, arguments.top, max(0, arguments.latency_sample))

    hook = (hook_probe(exe, arguments.vault, rows, arguments.hook_sample) if arguments.hook_sample
            else {"status": "not run", "why": "--hook-sample 0"})

    probe: dict[str, Any] | None = None
    if arguments.probe:
        probe_path = Path(arguments.probe)
        if not probe_path.is_file():
            raise SystemExit(f"probe set not found: {probe_path}")
        probe = probe_gate(exe, arguments.vault, probe_path)

    curve: dict[str, Any] | None = None
    if arguments.curve_top and arguments.curve_top > arguments.top:
        depths = [depth for depth in (1, 2, 3, 4, 5, 6, 8, 10, 15, 20, 30, 50, 100) if depth <= arguments.curve_top]
        deep, _ = run_batch(exe, arguments.vault, rows, arguments.curve_top, Path(arguments.work_dir))
        curve = recall_curve(rows, deep, depths)

    records = []
    for row, hits in zip(rows, rankings):
        records.append(
            {
                "id": row["id"],
                "soru": row["soru"],
                "sinif": row["sinif"],
                "gold": row["gold"],
                "hit@3": recall_at(hits, row["gold"], 3),
                "hit@5": recall_at(hits, row["gold"], 5),
                "first_gold_rank": rank_of_first_gold(hits, row["gold"]),
                "top": [{"slug": hit["slug"], "score": round(hit["score"], 4)} for hit in hits],
            }
        )

    # Canaries have no relevant note by construction; scoring them as recall misses would
    # deflate the gate by 5/130 without measuring anything about the ranking.
    scored = [record for record in records if record["sinif"] != CANARY]
    canaries = [record for record in records if record["sinif"] == CANARY]

    overall = summarise(scored)
    classes = sorted({record["sinif"] for record in scored})
    per_class = {name: summarise([record for record in scored if record["sinif"] == name]) for name in classes}

    misses = sorted(
        (record for record in scored if not record["hit@5"]),
        key=lambda record: (record["first_gold_rank"] or 10**6, record["id"]),
    )

    payload = {
        "gate": 5,
        "measured": date.today().isoformat(),
        "run_tag": RUN_TAG,
        "backend": {
            "executable": str(exe),
            "command": f"oom.exe --vault {arguments.vault} retrieve --batch <jsonl> --top {arguments.top}",
            "read_only": True,
            "vault": arguments.vault,
        },
        "dataset": {
            "path": str(gold_path),
            "rows": len(rows),
            "scored": len(scored),
            "canaries_excluded": len(canaries),
            "classes": classes,
        },
        "thresholds": {"recall@3": 0.80, "recall@5": 0.88},
        "overall": overall,
        "per_sinif": per_class,
        "pass": {
            "recall@3": overall["recall@3"] >= 0.80,
            "recall@5": overall["recall@5"] >= 0.88,
        },
        "diagnostic_recall_curve": curve,
        "index": index_counts(arguments.vault),
        "latency_ms": {
            "batch_total": round(batch_ms, 1),
            "batch_per_query": round(batch_ms / len(rows), 2),
            "single_call_samples": [round(value, 1) for value in singles],
            "single_call_mean": round(sum(singles) / len(singles), 1) if singles else None,
        },
        "machine": {"os": platform.platform(), "python": platform.python_version()},
        "hook_gate_probe": hook,
        "probe_30": probe,
        "canaries": [
            {
                "id": record["id"],
                "soru": record["soru"],
                "top1": record["top"][0] if record["top"] else None,
                "hits": len(record["top"]),
            }
            for record in canaries
        ],
        "misses": [
            {
                "id": record["id"],
                "sinif": record["sinif"],
                "soru": record["soru"],
                "gold": record["gold"],
                "top": record["top"],
            }
            for record in misses
        ],
        "records": records,
    }

    out = Path(arguments.out) if arguments.out else ROOT / "bench" / "results" / f"recall-{date.today().isoformat()}.json"
    write_json(out, payload)
    write_trec(Path(arguments.run_file), rows, rankings)

    print(f"# Gate 5 -- recall parity, run `{RUN_TAG}`, {len(scored)} scored questions "
          f"({len(canaries)} {CANARY} rows excluded, no gold by construction)\n")
    print(markdown_table(overall, per_class))
    print()
    print(f"thresholds: recall@3 >= 0.80 ({'PASS' if payload['pass']['recall@3'] else 'FAIL'}), "
          f"recall@5 >= 0.88 ({'PASS' if payload['pass']['recall@5'] else 'FAIL'})")
    if curve:
        print("\nrecall@k (diagnostic, depth %d): %s" % (
            curve["depth"], "  ".join(f"@{k}={v:.3f}" for k, v in curve["recall_at"].items())))
        print(f"gold outside the top {curve['depth']}: {len(curve['beyond_depth'])} of {curve['n']} -> {curve['beyond_depth']}")
    if probe:
        print(f"\nprobe-30 (spec 10.1 #17): {probe['injected']}/30 injected, cap 8 "
              f"({'PASS' if probe['within_cap'] else 'FAIL'}); "
              f"real questions {probe['true_positives']['injected']}/{probe['true_positives']['n']}, "
              f"false positives {probe['false_positives']['injected']}/{probe['false_positives']['n']}")
        disagree = [d["id"] for d in probe["detail"] if not d["agrees"]]
        print(f"probe disagreements: {disagree or 'none'}")
    if hook.get("counts"):
        print(f"hook probe (substitute): kanarya false-positive {hook['false_positive_rate_on_kanarya']:.2f} "
              f"({hook['counts']['kanarya']['injected']}/{hook['counts']['kanarya']['n']}), "
              f"gold injection {hook['injection_rate_on_gold']:.2f} "
              f"({hook['counts']['gold']['injected']}/{hook['counts']['gold']['n']})")
    print(f"index: {payload['index']['index_notes']} notes rows / {payload['index']['concept_files']} concept files")
    print(f"latency: batch {payload['latency_ms']['batch_total']} ms total, "
          f"{payload['latency_ms']['batch_per_query']} ms/query; single call mean {payload['latency_ms']['single_call_mean']} ms")
    print(f"misses (no gold in top {arguments.top}): {len(misses)}")
    for record in misses[:10]:
        top = ", ".join(f"{hit['slug']}({hit['score']:.1f})" for hit in record["top"])
        print(f"  {record['id']} [{record['sinif']}] gold={record['gold']} top5={top or '(none)'}")
    print(f"\nresults: {out}\nrun file: {arguments.run_file}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
