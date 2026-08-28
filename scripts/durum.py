#!/usr/bin/env python3
"""Print the memory pipeline's stable health summary; reporting always exits 0.

yazan: codex
model: gpt-5.6-sol
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
from pathlib import Path
from typing import Any, Sequence


SCRIPT_DIR = Path(__file__).resolve().parent
STATE_DIR = SCRIPT_DIR / ".state"
SCHEMA_VERSION = 1
COMPONENTS = ("flush", "compile", "ingest")


def _read_object(path: Path) -> dict[str, Any]:
    try:
        loaded = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, json.JSONDecodeError):
        return {}
    return loaded if isinstance(loaded, dict) else {}


def _timestamp(value: Any) -> str:
    if isinstance(value, (int, float)):
        return dt.datetime.fromtimestamp(value).astimezone().isoformat(
            timespec="seconds"
        )
    return value if isinstance(value, str) and value else "unknown"


def _problem(health: dict[str, Any], component: str) -> str:
    error = health.get("error")
    if health.get("component") == component and isinstance(error, str) and error:
        return error
    last_skip = health.get("last_skip")
    if isinstance(last_skip, dict) and last_skip.get("component") == component:
        reason = last_skip.get("reason")
        if isinstance(reason, str) and reason:
            return reason
    return "unknown"


def build_summary(state_dir: Path) -> dict[str, Any]:
    health = _read_object(state_dir / "health.json")
    ingest_health = _read_object(state_dir / "ingest-health.json")
    compile_state = _read_object(state_dir / "compile-state.json")
    last_flush = _read_object(state_dir / "last-flush.json")
    quarantined = compile_state.get("quarantined", {})
    quarantine_count = len(quarantined) if isinstance(quarantined, dict) else 0

    ingest_last_run = ingest_health.get("last_run")
    ingest_ts = (
        ingest_last_run.get("ts")
        if isinstance(ingest_last_run, dict)
        else ingest_health.get("ts")
    )
    ingest_error = ingest_health.get("error")
    if ingest_health:
        ingest_status = "ok" if not ingest_error else "fail"
        ingest_problem = ingest_error if isinstance(ingest_error, str) else "unknown"
        if not ingest_problem:
            ingest_problem = "unknown"
    else:
        ingest_status = "unknown"
        ingest_problem = "unknown"

    rows = [
        {
            "component": "flush",
            "last_status": str(last_flush.get("status", "unknown")),
            "last_run": _timestamp(last_flush.get("ts")),
            "last_error_or_skip": _problem(health, "flush"),
            "quarantine_count": quarantine_count,
        },
        {
            "component": "compile",
            "last_status": str(compile_state.get("last_status", "unknown")),
            "last_run": _timestamp(compile_state.get("last_run")),
            "last_error_or_skip": _problem(health, "compile"),
            "quarantine_count": quarantine_count,
        },
        {
            "component": "ingest",
            "last_status": ingest_status,
            "last_run": _timestamp(ingest_ts),
            "last_error_or_skip": ingest_problem,
            "quarantine_count": quarantine_count,
        },
    ]
    return {"schema_version": SCHEMA_VERSION, "rows": rows}


def _print_table(summary: dict[str, Any]) -> None:
    headers = (
        "component",
        "last status",
        "last run",
        "last error/skip",
        "quarantine",
    )
    rows = [
        (
            row["component"],
            row["last_status"],
            row["last_run"],
            row["last_error_or_skip"],
            str(row["quarantine_count"]),
        )
        for row in summary["rows"]
    ]
    widths = [
        max(len(headers[index]), *(len(row[index]) for row in rows))
        for index in range(len(headers))
    ]
    print(" | ".join(value.ljust(widths[i]) for i, value in enumerate(headers)))
    print("-+-".join("-" * width for width in widths))
    for row in rows:
        print(" | ".join(value.ljust(widths[i]) for i, value in enumerate(row)))


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--state-dir", type=Path, default=STATE_DIR)
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    if os.environ.get("BEYIN_INVOKED_BY"):
        return 0
    try:
        args = _parse_args(argv)
        summary = build_summary(args.state_dir)
        if args.json:
            print(json.dumps(summary, ensure_ascii=False, indent=2))
        else:
            _print_table(summary)
    except (Exception, SystemExit):
        # This is a reporting surface: even broken/missing state is not a hook error.
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
