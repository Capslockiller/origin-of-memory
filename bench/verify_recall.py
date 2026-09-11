#!/usr/bin/env python3
"""An identity-pinned recall gate: measure the ranking, or refuse to report a number.

Why this exists beside `bench/kos20.py`
---------------------------------------
`kos20.py` already measures recall on the gold set and already emits
`bench/PROVENANCE.md`'s four-part tuple. What it does not do -- what nothing in
`bench/` did before this file -- is make the *identity* of what was measured a
precondition of measuring it. `kos20.py` records `binary_sha256` after the run,
from whatever executable it happened to invoke; it records `dataset.path` as a
bare string and never hashes the gold set; it never hashes the corpus at all.
A run against the wrong exe, a mutated gold file, or a corpus that shifted
underfoot therefore produces a perfectly well-formed, `run_status: "ok"` results
file, and the only defence is a reader comparing hex by eye.

This script inverts that order. The identity triple -- executable, corpus, gold
set -- is computed and compared against caller-supplied expectations *before the
first query is issued*. A mismatch is not a warning appended to a result; it
means no measurement happens at all and no `pass` or `decision` field is ever
populated.

It also does not trust the backend's output. `kos20.py` checks one thing (that
the number of output lines equals the number of queries) and otherwise parses
whatever it is handed. Here, every ranking is checked against the corpus that
was actually measured: a returned note that does not exist on disk, a result
list whose scores do not descend, a duplicated note inside one list, a response
whose echoed `query` is not the query that was sent -- each of these means the
retrieval output is corrupt, and a corrupt ranking scores nothing. Recall
computed over fabricated slugs is not a low number, it is not a number.

What it deliberately does NOT do
--------------------------------
- It does not rename anything. The gold set's own class labels (`tek-not`,
  `cok-not`, or whatever a given gold file carries) are reported verbatim. This
  script never emits the words "episodic" or "concept" as class names, because
  no measurement in this repository produces such an axis; see the
  `episodic_axis` block, which records that absence structurally instead of
  quietly letting two other labels stand in for it.
- It does not let the hook path or the negative controls stand in for recall.
  They are measured, they are reported, and they live in their own `controls`
  block with their own verdict. `decision.recall_gate` is computed from the
  scored questions and from nothing else.
- It does not invent a second provenance contract. `bench/PROVENANCE.md` SS2-SS4
  is the contract; `bench/provenance.py` is its implementation; this script
  imports that module rather than reimplementing its rules.

The scoring predicate (`slug`, `recall_at`) is deliberately identical to
`kos20.py`'s. `selftest` asserts that identity against the imported module
rather than asserting it in a comment.

Python 3 standard library only.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import random
import shlex
import shutil
import subprocess
import sys
import time
from datetime import date
from pathlib import Path
from typing import Any

import provenance

ROOT = Path(__file__).resolve().parents[1]
CANARY = "kanarya"
CONCEPTS = ("knowledge", "concepts")

# The gate this lane was asked to install. Thresholds are the gold set's own,
# unchanged from bench/results/*.json's `thresholds` block.
RECALL_AT_3 = 0.80
RECALL_AT_5 = 0.88
SCORED_MINIMUM = 125


# ------------------------------------------------------------------ identity


def tree_sha256(directory: Path, pattern: str = "*.md") -> tuple[str, list[dict[str, Any]]]:
    """A digest over a corpus directory that changes if any name or byte changes.

    The digest covers `name\\0<sha256 of bytes>\\n` for every match, ordinal-sorted
    by name -- so a renamed note, a reordered directory listing, an edited note
    and a deleted note all move it, while the directory's mtime and the order
    the filesystem happens to enumerate in do not. Byte-exact on purpose: a
    CRLF/LF normalisation would make two materially different corpora hash the
    same, and the point of this number is that it cannot be true of two things.
    """
    manifest: list[dict[str, Any]] = []
    digest = hashlib.sha256()
    if not directory.is_dir():
        return "", manifest
    for path in sorted(directory.glob(pattern), key=lambda item: item.name):
        file_hash = provenance.sha256_file(path) or ""
        digest.update(path.name.encode("utf-8"))
        digest.update(b"\0")
        digest.update(file_hash.encode("ascii"))
        digest.update(b"\n")
        manifest.append({"name": path.name, "sha256": file_hash, "bytes": path.stat().st_size})
    return digest.hexdigest(), manifest


def corpus_identity(vault: Path) -> dict[str, Any]:
    concepts = vault.joinpath(*CONCEPTS)
    sha, manifest = tree_sha256(concepts)
    return {
        "vault": str(vault),
        "concepts_dir": "/".join(CONCEPTS),
        "files": len(manifest),
        "sha256": sha or None,
        "manifest": manifest,
    }


def gold_identity(path: Path, rows: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "path": str(path),
        "sha256": provenance.sha256_file(path),
        "rows": len(rows),
        "scored": sum(1 for row in rows if row["sinif"] != CANARY),
        "canaries": sum(1 for row in rows if row["sinif"] == CANARY),
        "classes": sorted({row["sinif"] for row in rows if row["sinif"] != CANARY}),
    }


def snapshot_corpus(source_vault: Path, destination: Path) -> Path:
    """The "fixed corpus copy": measure a snapshot, never a directory that can move.

    A gate that reads the corpus straight out of a working vault is measuring a
    moving target -- an edit landing between query 3 and query 90 splits the run
    across two corpora and nothing in the output would say so. Copying first
    means the hash in the results file describes exactly the bytes every query
    in that run was ranked against.
    """
    concepts_src = source_vault.joinpath(*CONCEPTS)
    concepts_dst = destination.joinpath(*CONCEPTS)
    if concepts_dst.exists():
        shutil.rmtree(concepts_dst)
    concepts_dst.mkdir(parents=True, exist_ok=True)
    for path in sorted(concepts_src.glob("*.md"), key=lambda item: item.name):
        shutil.copy2(path, concepts_dst / path.name)
    return destination


# ------------------------------------------------------------------ gold set


def load_gold(path: Path, limit: int | None = None) -> list[dict[str, Any]]:
    """Same row contract as `kos20.load_gold`: `soru`, `gold`, `sinif`, optional `lane`."""
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
            gold = [str(item) for item in row.get("gold", [])]
            sinif = str(row.get("sinif", "bilinmiyor"))
            if not gold and sinif != CANARY:
                raise SystemExit(f"{path}:{index}: empty 'gold' on a non-{CANARY} row")
            rows.append({"id": f"q{index:03d}", "soru": question, "gold": gold,
                         "sinif": sinif, "lane": str(row.get("lane", ""))})
            if limit is not None and len(rows) >= limit:
                break
    if not rows:
        raise SystemExit(f"{path}: no rows")
    return rows


# ------------------------------------------------------------------ scoring
# Identical to bench/kos20.py's predicate on purpose; `selftest` asserts the
# identity against the imported module instead of trusting this comment.


def slug(name: str) -> str:
    """`unity-secimi.md` -> `unity-secimi`; gold slugs carry no extension."""
    return name[:-3] if name.lower().endswith(".md") else name


def recall_at(hits: list[dict[str, Any]], gold: list[str], k: int) -> int:
    top_slugs = {hit["slug"] for hit in hits[:k]}
    return 1 if any(item in top_slugs for item in gold) else 0


def rank_of_first_gold(hits: list[dict[str, Any]], gold: list[str]) -> int | None:
    for rank, hit in enumerate(hits, 1):
        if hit["slug"] in gold:
            return rank
    return None


def summarise(records: list[dict[str, Any]]) -> dict[str, Any]:
    count = len(records)
    if count == 0:
        return {"n": 0, "recall@3": 0.0, "recall@5": 0.0, "mrr@5": 0.0}
    reciprocal = sum(1.0 / record["first_gold_rank"]
                     for record in records
                     if record["first_gold_rank"] and record["first_gold_rank"] <= 5)
    return {
        "n": count,
        "recall@3": round(sum(record["hit@3"] for record in records) / count, 4),
        "recall@5": round(sum(record["hit@5"] for record in records) / count, 4),
        "mrr@5": round(reciprocal / count, 4),
    }


# ------------------------------------------------------------------ backend


def backend_command(exe: Path, vault: Path, batch: Path, top: int,
                    prefix: list[str]) -> list[str]:
    return [*prefix, str(exe), "--vault", str(vault),
            "retrieve", "--batch", str(batch), "--top", str(top)]


def run_batch(exe: Path, vault: Path, rows: list[dict[str, Any]], top: int,
              work: Path, raw_path: Path, prefix: list[str]) -> dict[str, Any]:
    """One process, one JSON line per query, raw stdout persisted verbatim.

    `raw_path` is PROVENANCE.md SS2's `raw_artifact`. Unlike `kos20.py`, which
    writes it under the gitignored `bench/.out/`, the caller here is expected to
    place it beside the results file under `bench/results/`, where a reader of
    the repository can actually open the thing the summary claims to summarise.
    """
    work.mkdir(parents=True, exist_ok=True)
    batch = work / "gold-batch.jsonl"
    with batch.open("w", encoding="utf-8", newline="\n") as handle:
        for row in rows:
            handle.write(json.dumps({"id": row["id"], "soru": row["soru"]}, ensure_ascii=False) + "\n")

    command = backend_command(exe, vault, batch, top, prefix)
    started = time.perf_counter()
    completed = subprocess.run(command, capture_output=True, text=True,
                               encoding="utf-8", cwd=str(ROOT))
    elapsed_ms = (time.perf_counter() - started) * 1000.0

    raw_written: Path | None = None
    try:
        raw_path.parent.mkdir(parents=True, exist_ok=True)
        raw_path.write_text(completed.stdout, encoding="utf-8", newline="\n")
        raw_written = raw_path
    except OSError:
        raw_written = None

    return {
        "command": command,
        "returncode": completed.returncode,
        "stdout": completed.stdout,
        "stderr": completed.stderr,
        "elapsed_ms": round(elapsed_ms, 1),
        "raw_written": raw_written,
    }


def parse_and_check(stdout: str, rows: list[dict[str, Any]],
                    corpus_names: set[str], top: int) -> tuple[list[list[dict[str, Any]]], list[str]]:
    """Parse the backend's rankings and prove they describe the corpus measured.

    Every finding appended to `faults` makes the run invalid. These are not
    quality judgements about the ranking -- a genuinely bad ranker returns real
    notes in a bad order and scores badly, which is a number. These catch output
    that is not a ranking of this corpus at all, which is not.
    """
    faults: list[str] = []
    lines = [line for line in stdout.splitlines() if line.strip()]
    if len(lines) != len(rows):
        faults.append(f"alignment: backend returned {len(lines)} lines for {len(rows)} queries")
        return [], faults

    rankings: list[list[dict[str, Any]]] = []
    for row, line in zip(rows, lines):
        try:
            payload = json.loads(line)
        except json.JSONDecodeError as error:
            faults.append(f"{row['id']}: output line is not JSON ({error})")
            rankings.append([])
            continue

        if payload.get("schema_version") != 1:
            faults.append(f"{row['id']}: schema_version={payload.get('schema_version')!r}, expected 1")

        # The executable echoes the query it answered. If that is not the query
        # we sent, the lines are misaligned and every score below is attached to
        # the wrong question -- silently, and in a way a line count cannot see.
        echoed = str(payload.get("query", ""))
        if echoed != row["soru"]:
            faults.append(f"{row['id']}: echoed query does not match the query sent")

        hits: list[dict[str, Any]] = []
        previous: float | None = None
        seen: set[str] = set()
        for hit in payload.get("hits", []):
            name = str(hit.get("name", ""))
            score = float(hit.get("score", 0.0))
            note = slug(name)
            # A correction hit is named `Duzeltmeler.md#3` and is not a concept
            # file; it is a legitimate answer and is exempt from the existence
            # check, which exists to catch fabricated concept slugs.
            if "#" not in name and note not in corpus_names:
                faults.append(f"{row['id']}: returned '{name}', which is not in the measured corpus")
            if note in seen:
                faults.append(f"{row['id']}: returned '{name}' twice in one ranking")
            seen.add(note)
            if previous is not None and score > previous + 1e-12:
                faults.append(f"{row['id']}: scores do not descend ({previous} then {score})")
            previous = score
            hits.append({"slug": note, "score": score, "source": str(hit.get("source", ""))})
        if len(hits) > top:
            faults.append(f"{row['id']}: {len(hits)} hits returned for --top {top}")
        rankings.append(hits)

    scored_rows = [index for index, row in enumerate(rows) if row["sinif"] != CANARY]
    if scored_rows and all(not rankings[index] for index in scored_rows):
        faults.append("every scored query returned an empty ranking")
    return rankings, faults


# ------------------------------------------------------------------ controls


def hook_controls(exe: Path, vault: Path, rows: list[dict[str, Any]],
                  sample: int, prefix: list[str]) -> dict[str, Any]:
    """The hook path and the negative controls -- measured apart, reported apart.

    `retrieve --hook` is a different code path from `retrieve --batch`: it applies
    an intent gate and a score floor that `Query` never applies, so a recall
    number says nothing whatsoever about it. The `kanarya` rows are the owner's
    own negative controls -- prompts for which no note is relevant -- so any
    injection on them is a false positive. Neither number may be substituted for
    recall, and `decision.recall_gate` is computed without reading this block.
    """
    canaries = [row for row in rows if row["sinif"] == CANARY]
    positives = [row for row in rows if row["sinif"] != CANARY][:sample]
    counts = {CANARY: [0, 0], "gold": [0, 0]}
    detail: list[dict[str, Any]] = []
    for row in canaries + positives:
        payload = json.dumps({"session_id": f"verify-{row['id']}",
                              "hook_event_name": "UserPromptSubmit",
                              "prompt": row["soru"]}, ensure_ascii=False)
        completed = subprocess.run([*prefix, str(exe), "--vault", str(vault), "retrieve", "--hook"],
                                   input=payload, capture_output=True, text=True,
                                   encoding="utf-8", cwd=str(ROOT))
        injected = completed.stdout.strip().startswith("{")
        key = CANARY if row["sinif"] == CANARY else "gold"
        counts[key][0] += int(injected)
        counts[key][1] += 1
        detail.append({"id": row["id"], "sinif": row["sinif"], "injected": injected,
                       "reason": (completed.stdout.strip() or completed.stderr.strip() or None)
                       if not injected else None})

    def rate(key: str) -> float | None:
        return round(counts[key][0] / counts[key][1], 4) if counts[key][1] else None

    false_positive = rate(CANARY)
    injection = rate("gold")
    return {
        "instrument": "oom.exe --vault <vault> retrieve --hook  (stdin: UserPromptSubmit payload)",
        "measures": "the hook path's injection decision; NOT recall, and never a substitute for it",
        "negative_controls": {"label": CANARY, "n": counts[CANARY][1],
                              "injected": counts[CANARY][0], "false_positive_rate": false_positive},
        "positive_sample": {"n": counts["gold"][1], "injected": counts["gold"][0],
                            "injection_rate": injection},
        "detail": detail,
        # A hook that injects nothing at all has a false-positive rate of zero,
        # and that number means nothing whatsoever: silence on the controls is
        # only evidence of discrimination if the same hook speaks on the
        # questions that do have answers. So a run where the positive sample
        # never injected yields no verdict here, not a pass.
        "pass": None if (false_positive is None or not injection) else bool(false_positive == 0.0),
        "vacuous": bool(injection == 0.0) if injection is not None else None,
        "vacuous_note": "the positive sample never injected, so a zero false-positive rate on "
                        "the negative controls distinguishes nothing"
                        if injection == 0.0 else None,
    }


# ------------------------------------------------------------------ the gate


def measure(arguments: argparse.Namespace) -> tuple[dict[str, Any], int]:
    exe = Path(arguments.exe).resolve()
    gold_path = Path(arguments.gold).resolve()
    prefix = shlex.split(arguments.backend_prefix) if arguments.backend_prefix else []

    reasons: list[str] = []
    blocking: list[str] = []

    # ---- 1. identity, computed and compared BEFORE any query is issued.
    exe_sha = provenance.sha256_file(exe)
    if exe_sha is None:
        blocking.append(f"executable not readable: {exe}")

    source_vault = Path(arguments.vault).resolve()
    if arguments.snapshot:
        snapshot = Path(arguments.snapshot).resolve()
        snapshot.mkdir(parents=True, exist_ok=True)
        measured_vault = snapshot_corpus(source_vault, snapshot)
    else:
        measured_vault = source_vault
    corpus = corpus_identity(measured_vault)

    rows = load_gold(gold_path, arguments.max_queries)
    gold = gold_identity(gold_path, rows)

    identity = {
        "executable": {"path": str(exe), "sha256": exe_sha,
                       "expected": arguments.expect_exe_sha256,
                       "verified": None},
        "corpus": {"source_vault": str(source_vault), "measured_vault": str(measured_vault),
                   "snapshot": bool(arguments.snapshot), "files": corpus["files"],
                   "sha256": corpus["sha256"], "expected": arguments.expect_corpus_sha256,
                   "verified": None},
        "gold": {"path": str(gold_path), "sha256": gold["sha256"], "rows": gold["rows"],
                 "scored": gold["scored"], "canaries": gold["canaries"],
                 "expected": arguments.expect_gold_sha256, "verified": None},
        "backend_prefix": prefix or None,
    }

    for key, actual, expected, label in (
        ("executable", exe_sha, arguments.expect_exe_sha256, "executable"),
        ("corpus", corpus["sha256"], arguments.expect_corpus_sha256, "corpus"),
        ("gold", gold["sha256"], arguments.expect_gold_sha256, "gold set"),
    ):
        if expected is None:
            identity[key]["verified"] = None
            reasons.append(f"{label} identity was not pinned (no --expect-... given); "
                           f"the recorded hash is a description, not a check")
            continue
        matched = (actual is not None and actual.lower() == expected.lower())
        identity[key]["verified"] = matched
        if not matched:
            blocking.append(f"{label} identity mismatch: measured {actual!r}, expected {expected!r}")

    # A shimmed backend is not the executable whose hash was verified, so no run
    # that uses one may ever assert a pass -- whatever it measured, it did not
    # measure the binary this file names. This is deliberately NOT a
    # refuse-before-measuring condition: the shim exists so the output-integrity
    # checks below can be exercised against a backend that really does return
    # corrupt output, and refusing early would skip the very checks it is there
    # to prove. So the run proceeds, the faults are recorded, and the verdict is
    # withheld afterwards.
    shim_reasons: list[str] = []
    if prefix:
        shim_reasons.append("backend was invoked through a shim; binary_sha256 does not "
                            "describe what actually answered the queries")

    payload_base: dict[str, Any] = {
        "gate": "recall-evidence",
        "measured": date.today().isoformat(),
        "measured_by": arguments.measured_by,
        "command": literal_command(),
        "source_commit": provenance.git_commit(ROOT),
        "binary_sha256": exe_sha,
        "identity": identity,
        "thresholds": {"recall@3": RECALL_AT_3, "recall@5": RECALL_AT_5,
                       "scored_minimum": SCORED_MINIMUM},
        "episodic_axis": {
            "measured": False,
            "reason": "No instrument in this repository measures an episodic recall axis. "
                      "The gold set's classes are answer-cardinality labels (one note answers "
                      "vs several); they are reported here under their own names and are NOT "
                      "renamed to 'episodic'/'concept'. This axis is open, not covered.",
        },
    }

    # ---- 2. refuse before measuring, if identity did not hold.
    if blocking:
        payload = {
            **payload_base,
            "run_status": "invalid",
            "run_status_reasons": blocking + reasons,
            "raw_artifact": "none-kept",
            "substitute": {"active": bool(prefix), "for": str(exe) if prefix else None,
                           "reason": "backend shim stood in for the executable" if prefix else None},
            "refused_before_measuring": True,
            "overall": None,
            "per_sinif": None,
            "controls": None,
            "pass": None,
            "decision": None,
        }
        provenance.redact_verdicts(payload)
        return payload, 2

    # ---- 3. measure.
    work = Path(arguments.work_dir).resolve()
    raw_path = Path(arguments.raw).resolve()
    batch = run_batch(exe, measured_vault, rows, arguments.top, work, raw_path, prefix)

    corpus_names = {slug(item["name"]) for item in corpus["manifest"]}
    rankings, faults = parse_and_check(batch["stdout"], rows, corpus_names, arguments.top)
    if batch["returncode"] != 0:
        faults.append(f"backend exited {batch['returncode']}: {batch['stderr'].strip()[:300]}")

    raw_written = batch["raw_written"]
    if raw_written is not None:
        try:
            raw_artifact = str(raw_written.relative_to(ROOT)).replace(os.sep, "/")
        except ValueError:
            raw_artifact = str(raw_written)
    else:
        raw_artifact = "none-kept"
        faults.append("no raw artifact was persisted for this run (raw_artifact=none-kept)")

    # ---- 4. corrupt output is not a low score; it is not a score.
    if faults:
        # A corrupt backend produces one fault per hit per query -- hundreds of lines saying the
        # same thing. The full count is the fact; the first few are the evidence. Keeping all of
        # them would bury both under a file nobody opens. The raw stdout is committed beside this
        # summary, so nothing is lost that a reader might want to check.
        shown = faults[:40]
        payload = {
            **payload_base,
            "run_status": "invalid",
            "run_status_reasons": shown + shim_reasons + reasons,
            "raw_artifact": raw_artifact,
            "substitute": {"active": False, "for": None, "reason": None},
            "refused_before_measuring": False,
            "output_integrity": {"ok": False, "faults": shown, "fault_count": len(faults),
                                 "truncated": len(faults) > len(shown)},
            "backend": {"command": " ".join(batch["command"]), "elapsed_ms": batch["elapsed_ms"],
                        "returncode": batch["returncode"]},
            "overall": None,
            "per_sinif": None,
            "controls": None,
            "pass": None,
            "decision": None,
        }
        provenance.redact_verdicts(payload)
        return payload, 3

    # ---- 5. score.
    records = []
    for row, hits in zip(rows, rankings):
        records.append({
            "id": row["id"], "sinif": row["sinif"], "gold": row["gold"],
            "hit@3": recall_at(hits, row["gold"], 3),
            "hit@5": recall_at(hits, row["gold"], 5),
            "first_gold_rank": rank_of_first_gold(hits, row["gold"]),
            "top": [{"slug": hit["slug"], "score": round(hit["score"], 6)} for hit in hits],
        })

    scored = [record for record in records if record["sinif"] != CANARY]
    overall = summarise(scored)
    classes = sorted({record["sinif"] for record in scored})
    per_sinif = {name: summarise([r for r in scored if r["sinif"] == name]) for name in classes}

    controls = hook_controls(exe, measured_vault, rows, arguments.hook_sample, prefix) \
        if arguments.hook_sample else None

    index = {"concept_files": corpus["files"], "index_notes": None, "error": None,
             "note": "retrieval ranks the corpus on disk; the FTS index only narrows candidates "
                     "(src/Oom/Retrieve/Retrieve.cs, Search/CandidateNames), so a missing index "
                     "changes speed, not answers. index_notes is left null deliberately -- this "
                     "gate does not build or read state.db."}
    run_status, assessed = provenance.assess(
        {"concept_files": corpus["files"], "index_notes": 0, "error": None}, len(scored))
    reasons = assessed + shim_reasons + reasons
    if shim_reasons:
        run_status = "invalid"

    enough = len(scored) >= SCORED_MINIMUM
    if not enough:
        reasons.append(f"only {len(scored)} scored questions, the gate requires {SCORED_MINIMUM}")

    passes = {
        "recall@3": bool(overall["recall@3"] >= RECALL_AT_3),
        "recall@5": bool(overall["recall@5"] >= RECALL_AT_5),
        "scored_minimum": bool(enough),
    }
    recall_gate = bool(all(passes.values()))

    payload = {
        **payload_base,
        "run_status": run_status,
        "run_status_reasons": reasons,
        "raw_artifact": raw_artifact,
        "substitute": {"active": False, "for": None, "reason": None},
        "refused_before_measuring": False,
        "output_integrity": {"ok": True, "faults": []},
        "backend": {"command": " ".join(batch["command"]), "elapsed_ms": batch["elapsed_ms"],
                    "returncode": batch["returncode"], "read_only": True},
        "index": index,
        "dataset": {"path": str(gold_path), "rows": len(rows), "scored": len(scored),
                    "canaries_excluded": len(rows) - len(scored), "classes": classes},
        "overall": overall,
        "per_sinif": per_sinif,
        "controls": controls,
        "records": records,
        "corpus_manifest": corpus["manifest"],
        "pass": passes,
        "decision": {
            "recall_gate": recall_gate,
            "computed_from": "the scored questions only; controls are reported separately and "
                             "cannot substitute for this verdict",
            "controls_gate": None if controls is None else controls["pass"],
        },
    }

    if run_status != "ok":
        nulled = provenance.redact_verdicts(payload)
        payload["run_status_reasons"] = payload["run_status_reasons"] + [
            f"verdicts nulled per PROVENANCE.md SS3: {', '.join(nulled)}" if nulled else
            "no verdict fields needed nulling"]
        return payload, 4

    return payload, 0 if recall_gate else 1


def literal_command() -> str:
    script = Path(sys.argv[0]).resolve()
    try:
        script = script.relative_to(ROOT)
    except ValueError:
        pass
    return " ".join(["python", shlex.quote(str(script).replace(os.sep, "/"))]
                    + [shlex.quote(argument) for argument in sys.argv[1:]])


# Nothing committed under bench/results/ may name this machine. The repo's own
# files already scrub vault and state paths to `<vault>` / `<state-root>`; these
# are the same convention extended to the three prefixes a harness leaks by
# accident -- the repository root, the user profile, and the temp directory.
# Applied on the way out, in write_json, so no emit path can forget it.
def _scrub_map() -> list[tuple[str, str]]:
    import tempfile
    candidates = [
        (str(ROOT), "<repo>"),
        (os.path.expanduser("~"), "<home>"),
        (tempfile.gettempdir(), "<temp>"),
    ]
    pairs: list[tuple[str, str]] = []
    # Longest first: <repo> lives under neither <home> nor <temp> here, but a
    # tree that did would otherwise be half-replaced by the shorter prefix.
    for raw, token in sorted(candidates, key=lambda item: len(item[0]), reverse=True):
        if not raw:
            continue
        for spelling in {raw, raw.replace("\\", "/"), raw.replace("/", "\\")}:
            pairs.append((spelling, token))
    return pairs


def scrub(value: Any, pairs: list[tuple[str, str]]) -> Any:
    if isinstance(value, str):
        result = value
        for raw, token in pairs:
            if raw.lower() in result.lower():
                # Case-insensitive replace, preserving everything else verbatim.
                lowered = result.lower()
                needle = raw.lower()
                out: list[str] = []
                position = 0
                while True:
                    found = lowered.find(needle, position)
                    if found < 0:
                        out.append(result[position:])
                        break
                    out.append(result[position:found])
                    out.append(token)
                    position = found + len(needle)
                result = "".join(out)
                lowered = result.lower()
        return result
    if isinstance(value, dict):
        return {key: scrub(item, pairs) for key, item in value.items()}
    if isinstance(value, list):
        return [scrub(item, pairs) for item in value]
    return value


def write_json(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    cleaned = scrub(payload, _scrub_map())
    path.write_text(json.dumps(cleaned, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
                    encoding="utf-8", newline="\n")


# ------------------------------------------------------------------ fixtures


FILLER = [
    "kasa", "not", "oturum", "gunluk", "derleme", "kanca", "getirme", "indeks",
    "kapi", "olcum", "esik", "kanarya", "sorgu", "siralama", "belge", "kayit",
]


def build_fixture(destination: Path, seed: int = 20260911,
                  notes: int = 160, questions: int = 130) -> dict[str, Path]:
    """A synthetic corpus and gold set this lane owns, for proving the wiring.

    Not a stand-in for the owner's gold set and never presented as one: the
    results it produces carry `substitute.active: true`. Its job is to make
    every branch of this script executable without the private corpus -- and to
    be hard enough that the thresholds are actually exercised, which is why one
    question in six is built from shared filler only and is expected to miss.
    """
    rng = random.Random(seed)
    concepts = destination.joinpath(*CONCEPTS)
    if concepts.exists():
        shutil.rmtree(concepts)
    concepts.mkdir(parents=True, exist_ok=True)

    names: list[str] = []
    tokens: list[str] = []
    families: list[str] = []
    for index in range(notes):
        name = f"fix-{index:03d}"
        token = f"zeug{index:03d}ma"
        # Every note also carries a family token shared with five others. A
        # question asked with only the family token has six equally good
        # answers and --top 5 returns five of them, so the gold note is inside
        # recall@5's window far more often than inside recall@3's -- which is
        # what makes the two numbers differ here. A fixture where every question
        # either hits at rank 1 or misses entirely reports the same value for
        # both and cannot tell the two thresholds apart; the first cut of this
        # fixture used twelve-note families and did exactly that.
        family = f"kume{index // 6:02d}tipi"
        body_words = [rng.choice(FILLER) for _ in range(rng.randint(18, 34))]
        body = " ".join(body_words)
        concepts.joinpath(f"{name}.md").write_text(
            "---\n"
            f"title: {name} basligi {token}\n"
            "aliases: []\n"
            "tags: []\n"
            "sources: []\n"
            "created: 2026-01-01\n"
            "updated: 2026-01-02\n"
            "---\n"
            f"Bu not {token} terimini aciklar. {family} ailesindendir. {body}\n",
            encoding="utf-8", newline="\n")
        names.append(name)
        tokens.append(token)
        families.append(family)

    gold_path = destination / "gold-fixture.jsonl"
    lines: list[str] = []
    scored_target = questions - 5
    for index in range(scored_target):
        primary = index % notes
        # One question in sixteen is built from shared filler only: no rare token
        # and no family token, so the ranker has nothing distinctive to find and
        # the question is expected to miss outright. Without these the fixture
        # would score 1.000 and prove nothing about a threshold.
        if index % 16 == 15:
            question = " ".join(rng.choice(FILLER) for _ in range(6))
            row = {"soru": question, "gold": [names[primary]], "sinif": "tek-not"}
        # One in six asks with the family token only: a dozen notes answer it
        # equally well, so the gold note lands mid-ranking and the question
        # separates recall@5 from recall@3.
        elif index % 6 == 3:
            row = {"soru": f"{families[primary]} ailesi hakkinda ne yaziyor",
                   "gold": [names[primary]], "sinif": "tek-not"}
        elif index % 5 == 0:
            second = (primary + 7) % notes
            row = {"soru": f"{tokens[primary]} ve {tokens[second]} nedir",
                   "gold": [names[primary], names[second]], "sinif": "cok-not"}
        else:
            row = {"soru": f"{tokens[primary]} hakkinda ne yaziyor",
                   "gold": [names[primary]], "sinif": "tek-not"}
        lines.append(json.dumps(row, ensure_ascii=False))
    for index in range(5):
        lines.append(json.dumps(
            {"soru": f"tesekkurler devam edelim {index}", "gold": [], "sinif": CANARY},
            ensure_ascii=False))
    gold_path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    return {"vault": destination, "gold": gold_path}


CORRUPT_SHIM = '''#!/usr/bin/env python3
"""A backend that answers with notes that do not exist. Deliberately corrupt.

Invoked by `verify_recall.py selftest` as the negative control for output
integrity: it speaks the executable's JSON protocol perfectly -- right
`schema_version`, right line count, right echoed query, descending scores -- and
every note it names is fabricated. A gate that only counts lines and averages
hit@k reports this as recall 0.000 with `run_status: "ok"`; a gate that checks
its inputs reports that it was not handed a ranking of the measured corpus.
"""
import json, sys

mode = "fabricate"
batch = None
argv = sys.argv[1:]
for index, argument in enumerate(argv):
    if argument == "--batch" and index + 1 < len(argv):
        batch = argv[index + 1]
    if argument == "--corrupt-mode" and index + 1 < len(argv):
        mode = argv[index + 1]

if batch is None:
    sys.exit(0)

with open(batch, "r", encoding="utf-8") as handle:
    rows = [json.loads(line) for line in handle if line.strip()]

for position, row in enumerate(rows):
    if mode == "truncate" and position >= len(rows) - 3:
        break
    if mode == "misalign":
        echoed = rows[(position + 1) % len(rows)]["soru"]
    else:
        echoed = row["soru"]
    hits = [{"name": f"hayali-{position}-{rank}.md", "score": 9.0 - rank,
             "source": "concept", "updated": "2026-01-02T00:00:00+00:00"}
            for rank in range(5)]
    print(json.dumps({"schema_version": 1, "query": echoed, "hits": hits}, ensure_ascii=False))
'''


# ------------------------------------------------------------------ selftest


def scenario(name: str, expectation: str, arguments: argparse.Namespace,
             out_dir: Path) -> dict[str, Any]:
    payload, code = measure(arguments)
    write_json(out_dir / f"{name}.json", payload)
    return {
        "scenario": name,
        "expectation": expectation,
        "exit_code": code,
        "run_status": payload["run_status"],
        "decision": payload.get("decision"),
        "pass": payload.get("pass"),
        "refused_before_measuring": payload.get("refused_before_measuring"),
        "overall": payload.get("overall"),
        "evidence": f"{name}.json",
        "reasons": payload.get("run_status_reasons", [])[:6],
    }


def selftest(arguments: argparse.Namespace) -> int:
    """Prove the gate on fixtures this lane owns: honest run, wrong exe, corrupt output."""
    # The scoring predicate must be the one bench/kos20.py already uses. Assert
    # it rather than claim it.
    import kos20
    parity = []
    for case in ([{"slug": "a"}, {"slug": "b"}], [], [{"slug": "z"}] * 3):
        for gold in (["a"], ["z"], ["missing"]):
            for k in (3, 5):
                parity.append((recall_at(case, gold, k), kos20.recall_at(case, gold, k)))
    for name in ("Duzeltmeler.md#3", "a-b.MD", "plain", "x.md"):
        parity.append((slug(name), kos20.slug(name)))
    mismatched = [pair for pair in parity if pair[0] != pair[1]]
    if mismatched:
        raise SystemExit(f"scoring predicate has drifted from kos20.py: {mismatched}")

    work = Path(arguments.work_dir).resolve()
    fixture_root = work / "fixture"
    fixture_root.mkdir(parents=True, exist_ok=True)
    fixture = build_fixture(fixture_root)
    shim = work / "corrupt_backend.py"
    shim.write_text(CORRUPT_SHIM, encoding="utf-8", newline="\n")

    out_dir = Path(arguments.out_dir).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)

    exe = Path(arguments.exe).resolve()
    snapshot = work / "snapshot"
    exe_sha = provenance.sha256_file(exe)
    # The snapshot must exist before its hash can be pinned, so take the honest
    # run first and reuse the hashes it establishes.
    corpus_sha, _ = tree_sha256(snapshot_corpus(fixture["vault"], snapshot).joinpath(*CONCEPTS))
    gold_sha = provenance.sha256_file(fixture["gold"])

    def options(**overrides: Any) -> argparse.Namespace:
        base = dict(exe=str(exe), vault=str(fixture["vault"]), gold=str(fixture["gold"]),
                    snapshot=str(snapshot), top=5, max_queries=None,
                    hook_sample=arguments.hook_sample, measured_by=arguments.measured_by,
                    work_dir=str(work), backend_prefix=None,
                    expect_exe_sha256=exe_sha, expect_corpus_sha256=corpus_sha,
                    expect_gold_sha256=gold_sha,
                    raw=str(out_dir / "raw" / "honest.stdout.txt"))
        base.update(overrides)
        return argparse.Namespace(**base)

    results = [
        scenario("selftest-1-honest",
                 "identity verified, output intact -> a verdict is issued",
                 options(), out_dir),
        scenario("selftest-2-wrong-exe-hash",
                 "expected executable hash does not match -> refuse BEFORE measuring, no verdict",
                 options(expect_exe_sha256="0" * 64,
                         raw=str(out_dir / "raw" / "wrong-exe.stdout.txt")), out_dir),
        scenario("selftest-3-corrupt-output",
                 "backend returns notes that are not in the corpus -> invalid, no verdict",
                 options(backend_prefix=f"{shlex.quote(sys.executable)} {shlex.quote(str(shim))}",
                         raw=str(out_dir / "raw" / "corrupt.stdout.txt")), out_dir),
        scenario("selftest-4-misaligned-output",
                 "backend echoes the wrong query -> invalid, no verdict",
                 options(backend_prefix=f"{shlex.quote(sys.executable)} {shlex.quote(str(shim))} "
                                        f"--corrupt-mode misalign",
                         expect_exe_sha256=None,
                         raw=str(out_dir / "raw" / "misaligned.stdout.txt")), out_dir),
    ]

    summary = {
        "gate": "recall-evidence-selftest",
        "measured": date.today().isoformat(),
        "measured_by": arguments.measured_by,
        "command": literal_command(),
        "run_status": "ok",
        "run_status_reasons": [],
        "raw_artifact": "bench/results/astra-dalga-1/raw/honest.stdout.txt",
        "source_commit": provenance.git_commit(ROOT),
        "binary_sha256": exe_sha,
        "substitute": {
            "active": True,
            "for": ".brief/gold-sorular.jsonl and the owner's concept vault",
            "reason": "This is the lane's own synthetic fixture corpus and gold set, not the "
                      "owner's. It proves the gate's wiring and its refusal paths; it is NOT a "
                      "measurement of the product's recall and no number here describes it.",
        },
        "measures": "that verify_recall.py issues a verdict when identity holds and output is "
                    "intact, and refuses one when either fails",
        "does_not_measure": "the product's recall on the owner's gold set; that run happens "
                            "against the final unified candidate's published exe",
        "fixture": {
            "corpus_sha256": corpus_sha,
            "corpus_files": len(list(snapshot.joinpath(*CONCEPTS).glob("*.md"))),
            "gold_sha256": gold_sha,
            "gold_rows": sum(1 for _ in fixture["gold"].open(encoding="utf-8")),
            "seed": 20260911,
        },
        "scenarios": results,
        "pass": None,
        "decision": None,
    }
    # Structural, not narrative: the selftest passes only if each scenario behaved
    # the way its expectation says, and that verdict is computed, not asserted.
    expectations = [
        results[0]["run_status"] == "ok" and results[0]["refused_before_measuring"] is False,
        results[1]["run_status"] == "invalid" and results[1]["refused_before_measuring"] is True
        and results[1]["decision"] is None,
        results[2]["run_status"] == "invalid" and results[2]["decision"] is None,
        results[3]["run_status"] == "invalid" and results[3]["decision"] is None,
    ]
    summary["pass"] = {"every_scenario_behaved_as_specified": bool(all(expectations))}
    summary["decision"] = {"selftest": bool(all(expectations)),
                           "recall_gate": None,
                           "why_recall_gate_is_null": "measured against a fixture corpus, not the "
                                                      "gold set; a fixture number is not a product "
                                                      "verdict"}
    write_json(out_dir / "selftest-summary.json", summary)

    print(json.dumps({"scenarios": [{k: r[k] for k in ("scenario", "exit_code", "run_status")}
                                    for r in results],
                      "selftest": summary["decision"]["selftest"]}, indent=2))
    return 0 if all(expectations) else 1


# ------------------------------------------------------------------ cli


def main() -> int:
    parser = argparse.ArgumentParser(
        description="identity-pinned recall gate over the 2.0 ranking")
    subparsers = parser.add_subparsers(dest="mode", required=True)

    def common(target: argparse.ArgumentParser) -> None:
        target.add_argument("--exe", required=True)
        target.add_argument("--measured-by", default="claude")
        target.add_argument("--hook-sample", type=int, default=20,
                            help="ordinary gold questions in the hook control; 0 skips it")
        target.add_argument("--work-dir", default=str(ROOT / "bench" / ".data" / "verify"))

    run = subparsers.add_parser("run", help="measure, or refuse to")
    common(run)
    run.add_argument("--vault", required=True)
    run.add_argument("--gold", required=True)
    run.add_argument("--snapshot", help="copy the corpus here and measure the copy")
    run.add_argument("--top", type=int, default=5)
    run.add_argument("--max-queries", type=int)
    run.add_argument("--backend-prefix",
                     help="argv prefix in front of --exe; any value forces run_status away "
                          "from 'ok', since binary_sha256 then describes something that did "
                          "not answer the queries")
    run.add_argument("--expect-exe-sha256")
    run.add_argument("--expect-corpus-sha256")
    run.add_argument("--expect-gold-sha256")
    run.add_argument("--raw", required=True,
                     help="where the backend's raw stdout is persisted (PROVENANCE.md SS2)")
    run.add_argument("--out", required=True)

    identity = subparsers.add_parser("identity", help="print the identity triple and exit")
    identity.add_argument("--exe", required=True)
    identity.add_argument("--vault", required=True)
    identity.add_argument("--gold", required=True)

    test = subparsers.add_parser("selftest", help="prove the gate on this lane's own fixtures")
    common(test)
    test.add_argument("--out-dir", default=str(ROOT / "bench" / "results" / "astra-dalga-1"))

    arguments = parser.parse_args()

    if arguments.mode == "identity":
        vault = Path(arguments.vault).resolve()
        gold_path = Path(arguments.gold).resolve()
        rows = load_gold(gold_path)
        print(json.dumps({
            "exe_sha256": provenance.sha256_file(Path(arguments.exe).resolve()),
            "corpus_sha256": corpus_identity(vault)["sha256"],
            "corpus_files": corpus_identity(vault)["files"],
            "gold_sha256": provenance.sha256_file(gold_path),
            "gold_rows": len(rows),
            "gold_scored": sum(1 for row in rows if row["sinif"] != CANARY),
        }, indent=2))
        return 0

    if arguments.mode == "selftest":
        return selftest(arguments)

    payload, code = measure(arguments)
    write_json(Path(arguments.out).resolve(), payload)
    print(json.dumps({"run_status": payload["run_status"],
                      "decision": payload.get("decision"),
                      "overall": payload.get("overall"),
                      "reasons": payload.get("run_status_reasons", [])[:6]}, indent=2))
    return code


if __name__ == "__main__":
    sys.exit(main())
