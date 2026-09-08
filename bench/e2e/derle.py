#!/usr/bin/env python3
"""Compile LoCoMo scratch vaults and collect compiler-call costs.

yazan: codex
model: gpt-5.6-sol
"""

from __future__ import annotations

import argparse
import csv
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
from typing import Any

from ortak import MODEL_ID, WORK_DIR, raw_by_sample, vault_path, write_json_atomic, write_text_atomic


COST_PATH = WORK_DIR / "maliyet-derleme.csv"
LOGS_DIR = WORK_DIR / "logs"
STATE_NAME = "compile-state.json"


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _load_state(vault: Path) -> dict[str, Any]:
    path = vault / ".claude" / "scripts" / ".state" / STATE_NAME
    if not path.is_file():
        return {
            "ingested": {},
            "cursor": "",
            "last_run": "",
            "last_status": "ok",
            "runs": [],
            "concepts_manifest": "",
            "quarantined": {},
        }
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"invalid state: {path}")
    value.setdefault("ingested", {})
    value.setdefault("quarantined", {})
    value.setdefault("runs", [])
    return value


def _changed_daily(vault: Path) -> list[tuple[Path, str]]:
    state = _load_state(vault)
    ingested = state.get("ingested", {})
    quarantined = state.get("quarantined", {})
    if not isinstance(ingested, dict) or not isinstance(quarantined, dict):
        raise ValueError(f"invalid compiler state in {vault.name}")
    changed: list[tuple[Path, str]] = []
    for path in sorted((vault / "daily").glob("*.md"), key=lambda value: value.name):
        digest = _sha256(path)
        if ingested.get(path.name) != digest and digest not in quarantined:
            changed.append((path, digest))
    return changed


def _append_log(sample_id: str, text: str) -> None:
    LOGS_DIR.mkdir(parents=True, exist_ok=True)
    path = LOGS_DIR / f"{sample_id}.log"
    prefix = "" if path.exists() else f"# yazan: codex · {MODEL_ID}\n"
    with path.open("a", encoding="utf-8", newline="\n") as handle:
        handle.write(prefix + text.rstrip() + "\n")


def _yaml_quote(value: str) -> str:
    return json.dumps(value, ensure_ascii=False)


def _fake_note(
    sample_id: str,
    daily: Path,
    part: int,
    dialogue: list[str],
) -> tuple[str, str]:
    session_match = re.search(r"__s(\d+)\.md$", daily.name)
    session_number = int(session_match.group(1)) if session_match else 0
    day = daily.name[:10]
    slug = f"{sample_id}-session-{session_number:02d}-part-{part}"
    title = f"{sample_id} session {session_number} memory {part}"
    anchor = next(
        (line for line in daily.read_text(encoding="utf-8").splitlines() if line.startswith("<!-- session:")),
        "",
    )
    detail = "\n".join(dialogue) or "No dialogue in this deterministic partition."
    body = (
        "---\n"
        f"title: {_yaml_quote(title)}\n"
        "aliases: []\n"
        "tags: [locomo, e2e-fake]\n"
        f"sources: [{_yaml_quote(daily.name)}]\n"
        f"created: {day}\n"
        f"updated: {day}\n"
        "yazan: codex\n"
        f"model: {MODEL_ID}\n"
        "---\n\n"
        f"# {title}\n\n"
        "Deterministic offline compiler output preserving one partition of the source dialogue.\n\n"
        "## Önemli Noktalar\n\n"
        f"- Source conversation: {sample_id}.\n"
        f"- Source session: {session_number}.\n"
        f"- Deterministic partition: {part} of 3.\n\n"
        "## Detaylar\n\n"
        f"{detail}\n\n"
        "## İlgili Kavramlar\n\n"
        f"- [[{sample_id}-session-{session_number:02d}-part-{(part % 3) + 1}]] — Same session.\n"
        f"- [[{sample_id}-conversation]] — Same conversation.\n\n"
        "## Kaynaklar\n\n"
        f"- {daily.name}\n"
        f"{anchor}\n"
    )
    return slug, body


def _record_fake_call(state_dir: Path, input_chars: int, output_chars: int) -> None:
    record = {
        "yazan": "codex",
        "writer_model": MODEL_ID,
        "ts": datetime.now().astimezone().isoformat(timespec="seconds"),
        "backend": "fake",
        "component": "compile",
        "model_tier": "fake",
        "model_slug": "deterministic-canned-notes",
        "input_chars": input_chars,
        "output_chars": output_chars,
        "input_tokens_est": input_chars // 4,
        "output_tokens_est": output_chars // 4,
        "duration_ms": 0,
        "outcome": "ok",
    }
    state_dir.mkdir(parents=True, exist_ok=True)
    with (state_dir / "calls.jsonl").open("a", encoding="utf-8", newline="\n") as handle:
        handle.write(json.dumps(record, ensure_ascii=False) + "\n")


def _rebuild_fake_index_full(vault: Path) -> None:
    rows: list[str] = []
    for note in sorted((vault / "knowledge" / "concepts").glob("*.md")):
        text = note.read_text(encoding="utf-8")
        match = re.search(r"(?m)^title:\s*(.+)$", text)
        title = note.stem if match is None else str(json.loads(match.group(1)))
        source = ""
        source_match = re.search(r"(?m)^sources:\s*\[\"([^\"]+)\"\]", text)
        if source_match:
            source = source_match.group(1)
        rows.append(f"| [[{note.stem}|{title}]] | Deterministic LoCoMo dialogue partition. | {source} | {source[:10]} |")
    text = (
        "---\n"
        "yazan: codex\n"
        f"model: {MODEL_ID}\n"
        "---\n\n"
        "# Bilgi Tabanı — Tam Kavram İndeksi\n\n"
        "| Makale | Özet | Kaynak | Güncellendi |\n"
        "|---|---|---|---|\n"
        + "\n".join(rows)
        + ("\n" if rows else "")
    )
    write_text_atomic(vault / "knowledge" / "index-full.md", text)


def _run_fake(sample_id: str, vault: Path, max_calls: int) -> None:
    state_dir = vault / ".claude" / "scripts" / ".state"
    state_path = state_dir / STATE_NAME
    while True:
        changed = _changed_daily(vault)
        if not changed:
            break
        for daily, digest in changed[:max_calls]:
            raw_text = daily.read_text(encoding="utf-8")
            dialogue = [
                line
                for line in raw_text.splitlines()
                if line and not line.startswith(("#", "<!--"))
            ]
            partitions = [dialogue[index::3] for index in range(3)]
            outputs: list[str] = []
            for part, lines in enumerate(partitions, 1):
                slug, note_text = _fake_note(sample_id, daily, part, lines)
                write_text_atomic(vault / "knowledge" / "concepts" / f"{slug}.md", note_text)
                outputs.append(note_text)
            _record_fake_call(state_dir, len(raw_text), sum(map(len, outputs)))
            state = _load_state(vault)
            stamp = datetime.now().astimezone().isoformat(timespec="seconds")
            state["ingested"][daily.name] = digest
            state["cursor"] = daily.name
            state["last_run"] = stamp
            state["last_status"] = "ok"
            state.setdefault("runs", []).append(
                {"ts": stamp, "daily_file": daily.name, "status": "ok:fake"}
            )
            state["runs"] = state["runs"][-20:]
            write_json_atomic(state_path, state)
            _append_log(sample_id, f"fake compiled {daily.name}: notes=3")
    _rebuild_fake_index_full(vault)


def _run_live(sample_id: str, vault: Path, max_calls: int, timeout: int) -> None:
    compile_path = vault / ".claude" / "scripts" / "compile.py"
    iteration = 0
    while True:
        before = _changed_daily(vault)
        if not before:
            return
        iteration += 1
        environment = os.environ.copy()
        environment.pop("BEYIN_INVOKED_BY", None)
        environment["BEYIN_MODEL_BACKEND"] = "claude"
        environment["BEYIN_COMPILE_MODE"] = "tools"
        environment["BEYIN_COMPILE_TIMEOUT"] = str(timeout)
        command = [sys.executable, str(compile_path), "--max-calls", str(max_calls)]
        started = time.monotonic()
        try:
            result = subprocess.run(
                command,
                cwd=vault,
                env=environment,
                text=True,
                encoding="utf-8",
                errors="replace",
                capture_output=True,
                timeout=timeout * max_calls + 60,
                check=False,
            )
        except subprocess.TimeoutExpired as exc:
            _append_log(sample_id, f"iteration={iteration} driver-timeout={exc.timeout}")
            raise RuntimeError(f"{sample_id}: compiler driver timed out") from exc
        elapsed_ms = int((time.monotonic() - started) * 1000)
        after = _changed_daily(vault)
        _append_log(
            sample_id,
            (
                f"iteration={iteration} exit={result.returncode} elapsed_ms={elapsed_ms} "
                f"changed_before={len(before)} changed_after={len(after)}\n"
                f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}"
            ),
        )
        if result.returncode != 0:
            raise RuntimeError(f"{sample_id}: compiler exited {result.returncode}")
        if len(after) >= len(before):
            state = _load_state(vault)
            raise RuntimeError(
                f"{sample_id}: compiler made no progress; last_status={state.get('last_status', '')}"
            )


def _ledger_cost(vault: Path) -> dict[str, int]:
    path = vault / ".claude" / "scripts" / ".state" / "calls.jsonl"
    records = [] if not path.is_file() else [
        json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()
    ]
    records = [row for row in records if row.get("component") == "compile"]
    return {
        "calls": len(records),
        "input_tokens_est": sum(int(row.get("input_tokens_est", 0)) for row in records),
        "output_tokens_est": sum(int(row.get("output_tokens_est", 0)) for row in records),
        "duration_ms": sum(int(row.get("duration_ms", 0)) for row in records),
    }


def _quarantine_count(vault: Path) -> int:
    root = vault / ".stage" / "karantina"
    return len(list(root.rglob("*.md"))) if root.is_dir() else 0


def _write_costs(updated: dict[str, dict[str, Any]]) -> None:
    existing: dict[str, dict[str, str]] = {}
    if COST_PATH.is_file():
        with COST_PATH.open("r", encoding="utf-8", newline="") as handle:
            existing = {row["vault"]: row for row in csv.DictReader(handle)}
    existing.update(updated)
    fields = [
        "yazan", "model", "vault", "calls", "input_tokens_est",
        "output_tokens_est", "duration_ms", "notes_written", "quarantined",
    ]
    import io
    buffer = io.StringIO(newline="")
    writer = csv.DictWriter(buffer, fieldnames=fields, lineterminator="\n")
    writer.writeheader()
    for sample_id in sorted(existing):
        writer.writerow(existing[sample_id])
    write_text_atomic(COST_PATH, buffer.getvalue())


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--backend", choices=("claude", "fake"), default="claude")
    parser.add_argument("--only", help="compile only one sample_id")
    parser.add_argument("--max-calls", type=int, default=3)
    parser.add_argument("--timeout", type=int, default=900, help="BEYIN_COMPILE_TIMEOUT seconds")
    args = parser.parse_args()
    if args.max_calls < 1 or args.timeout < 1:
        raise SystemExit("--max-calls and --timeout must be positive")

    known = raw_by_sample()
    sample_ids = [args.only] if args.only else list(known)
    if args.only and args.only not in known:
        raise SystemExit(f"unknown sample_id: {args.only}")

    costs: dict[str, dict[str, Any]] = {}
    for sample_id in sample_ids:
        vault = vault_path(sample_id)
        if not (vault / ".claude" / "scripts" / "compile.py").is_file():
            raise SystemExit(f"missing vault for {sample_id}; run vault_kur.py first")
        if args.backend == "fake":
            _run_fake(sample_id, vault, args.max_calls)
        else:
            _run_live(sample_id, vault, args.max_calls, args.timeout)
        cost = _ledger_cost(vault)
        row: dict[str, Any] = {
            "yazan": "codex",
            "model": MODEL_ID,
            "vault": sample_id,
            **cost,
            "notes_written": len(list((vault / "knowledge" / "concepts").glob("*.md"))),
            "quarantined": _quarantine_count(vault),
        }
        costs[sample_id] = row
        print(
            f"{sample_id}: calls={row['calls']} notes={row['notes_written']} "
            f"quarantined={row['quarantined']}"
        )
    _write_costs(costs)
    print(f"costs: {COST_PATH}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
