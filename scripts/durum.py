#!/usr/bin/env python3
"""Print the memory pipeline's stable health summary; reporting always exits 0.

yazan: codex
model: gpt-5.6-sol
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import math
import os
from pathlib import Path
import statistics
from typing import Any, Sequence

from beyin_ortak import CALLS_LEDGER_NAME


SCRIPT_DIR = Path(__file__).resolve().parent
STATE_DIR = SCRIPT_DIR / ".state"
SCHEMA_VERSION = 1
COMPONENTS = ("flush", "compile", "ingest")
CALLS_WINDOW_DAYS = 7


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


def _percentile_ms(durations: Sequence[float], fraction: float) -> int:
    """Nearest-rank percentile, the same convention ``retrieve.benchmark`` uses."""
    ordered = sorted(durations)
    index = max(0, math.ceil(fraction * len(ordered)) - 1)
    return int(round(ordered[index]))


def _read_calls(path: Path, cutoff: dt.datetime) -> list[dict[str, Any]]:
    """Read ledger lines newer than ``cutoff``; unreadable lines are skipped.

    A ledger is evidence, not state: a truncated or hand-edited line loses that
    one call rather than the whole report.
    """
    try:
        raw = path.read_text(encoding="utf-8")
    except (OSError, ValueError):
        return []
    records = []
    for line in raw.splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            record = json.loads(line)
        except (ValueError, json.JSONDecodeError):
            continue
        if not isinstance(record, dict):
            continue
        stamp = record.get("ts")
        if not isinstance(stamp, str):
            continue
        try:
            when = dt.datetime.fromisoformat(stamp)
        except ValueError:
            continue
        if when.tzinfo is None:
            when = when.astimezone()
        if when >= cutoff:
            records.append(record)
    return records


def _number(record: dict[str, Any], key: str) -> int:
    value = record.get(key)
    return value if isinstance(value, int) and not isinstance(value, bool) else 0


def summarize_calls(
    state_dir: Path,
    now: dt.datetime | None = None,
) -> dict[str, Any]:
    """Summarise the last 7 days of model calls from ``.state/calls.jsonl``.

    Every token figure here is a chars ÷ 4 estimate carried straight from the
    ledger, never a provider count — the field names keep saying so.
    """
    moment = (now or dt.datetime.now()).astimezone()
    cutoff = moment - dt.timedelta(days=CALLS_WINDOW_DAYS)
    records = _read_calls(Path(state_dir) / CALLS_LEDGER_NAME, cutoff)

    backend_durations: dict[str, list[float]] = {}
    component_totals: dict[str, dict[str, int]] = {}
    ok_calls = 0
    for record in records:
        backend = str(record.get("backend", "unknown")) or "unknown"
        component = str(record.get("component", "unknown")) or "unknown"
        backend_durations.setdefault(backend, []).append(
            float(_number(record, "duration_ms"))
        )
        totals = component_totals.setdefault(
            component, {"calls": 0, "input_tokens_est": 0, "output_tokens_est": 0}
        )
        totals["calls"] += 1
        totals["input_tokens_est"] += _number(record, "input_tokens_est")
        totals["output_tokens_est"] += _number(record, "output_tokens_est")
        if record.get("outcome") == "ok":
            ok_calls += 1

    backends = [
        {
            "backend": backend,
            "calls": len(durations),
            "median_ms": int(round(statistics.median(durations))),
            "p95_ms": _percentile_ms(durations, 0.95),
        }
        for backend, durations in sorted(
            backend_durations.items(), key=lambda item: (-len(item[1]), item[0])
        )
    ]
    components = [
        {"component": component, **totals}
        for component, totals in sorted(
            component_totals.items(), key=lambda item: (-item[1]["calls"], item[0])
        )
    ]
    return {
        "window_days": CALLS_WINDOW_DAYS,
        "total_calls": len(records),
        "ok_calls": ok_calls,
        "failed_calls": len(records) - ok_calls,
        "backends": backends,
        "components": components,
    }


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
    return {
        "schema_version": SCHEMA_VERSION,
        "rows": rows,
        "calls": summarize_calls(state_dir),
    }


def _print_grid(headers: Sequence[str], rows: Sequence[Sequence[str]]) -> None:
    # The list form, not `max(header, *cells)`: a grid with no rows must print
    # its header rather than raise on an empty unpacking.
    widths = [
        max([len(headers[index])] + [len(row[index]) for row in rows])
        for index in range(len(headers))
    ]
    print(" | ".join(value.ljust(widths[i]) for i, value in enumerate(headers)))
    print("-+-".join("-" * width for width in widths))
    for row in rows:
        print(" | ".join(value.ljust(widths[i]) for i, value in enumerate(row)))


def _print_calls(calls: dict[str, Any]) -> None:
    window = calls["window_days"]
    total = calls["total_calls"]
    print()
    if not total:
        print(f"model calls (last {window} days): none recorded")
        return
    print(
        f"model calls (last {window} days): {total} "
        f"({calls['ok_calls']} ok, {calls['failed_calls']} failed)"
    )
    print()
    _print_grid(
        ("backend", "calls", "median ms", "p95 ms"),
        [
            (
                str(entry["backend"]),
                str(entry["calls"]),
                str(entry["median_ms"]),
                str(entry["p95_ms"]),
            )
            for entry in calls["backends"]
        ],
    )
    print()
    # "est" is not decoration: these are characters ÷ 4, not provider counts.
    _print_grid(
        ("component", "calls", "in tokens (est)", "out tokens (est)"),
        [
            (
                str(entry["component"]),
                str(entry["calls"]),
                str(entry["input_tokens_est"]),
                str(entry["output_tokens_est"]),
            )
            for entry in calls["components"]
        ],
    )


def _print_table(summary: dict[str, Any]) -> None:
    _print_grid(
        ("component", "last status", "last run", "last error/skip", "quarantine"),
        [
            (
                row["component"],
                row["last_status"],
                row["last_run"],
                row["last_error_or_skip"],
                str(row["quarantine_count"]),
            )
            for row in summary["rows"]
        ],
    )
    _print_calls(summary["calls"])


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
