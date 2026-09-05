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

import beyin_ortak
from beyin_ortak import CALLS_LEDGER_NAME


SCRIPT_DIR = Path(__file__).resolve().parent
STATE_DIR = SCRIPT_DIR / ".state"
SCHEMA_VERSION = 1
COMPONENTS = ("flush", "compile", "ingest")
CALLS_WINDOW_DAYS = 7
WARNING_STALE_SECONDS = 24 * 60 * 60
HEALTH_NAME = "health.json"


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


def _warning_message(entry: Any) -> str:
    if isinstance(entry, dict):
        for key in ("message", "warning", "text"):
            value = entry.get(key)
            if isinstance(value, str) and value:
                return value
        return json.dumps(entry, ensure_ascii=False, sort_keys=True)
    return str(entry)


def _warning_entry_ts(entry: Any) -> float | None:
    if not isinstance(entry, dict):
        return None
    value = entry.get("ts")
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) else None


def _resolve_warning_ts(entry: Any, top_ts: float | None) -> float | None:
    own = _warning_entry_ts(entry)
    return own if own is not None else top_ts


def summarize_warnings(
    health: dict[str, Any], now: dt.datetime | None = None
) -> list[dict[str, Any]]:
    """Age each health warning from its own ``ts`` if present, else the top-level one."""
    moment = (now or dt.datetime.now()).astimezone()
    now_ts = moment.timestamp()
    top_ts_raw = health.get("ts")
    top_ts = (
        top_ts_raw
        if isinstance(top_ts_raw, (int, float)) and not isinstance(top_ts_raw, bool)
        else None
    )
    raw_warnings = health.get("warnings", [])
    if not isinstance(raw_warnings, list):
        raw_warnings = []

    result = []
    for entry in raw_warnings:
        effective_ts = _resolve_warning_ts(entry, top_ts)
        age_seconds = (
            max(0, int(round(now_ts - effective_ts))) if effective_ts is not None else None
        )
        eski = age_seconds is not None and age_seconds > WARNING_STALE_SECONDS
        result.append(
            {
                "message": _warning_message(entry),
                "ts": int(effective_ts) if effective_ts is not None else None,
                "age_seconds": age_seconds,
                "eski": eski,
            }
        )
    return result


def temizle_uyarilar(
    state_dir: Path,
    now: dt.datetime | None = None,
    health_name: str = HEALTH_NAME,
) -> dict[str, Any]:
    """Rewrite ``health.json`` keeping only warnings younger than 24 h.

    A no-op (file untouched) when the file is absent, unreadable, has no
    warnings, or has nothing stale to drop — the atomic replace only happens
    when the warning list actually shrinks.
    """
    path = Path(state_dir) / health_name
    if not path.exists():
        return {"kept": 0, "dropped": 0, "changed": False}
    payload = _read_object(path)
    if not payload:
        return {"kept": 0, "dropped": 0, "changed": False}
    raw_warnings = payload.get("warnings", [])
    if not isinstance(raw_warnings, list) or not raw_warnings:
        return {"kept": 0, "dropped": 0, "changed": False}

    aged = summarize_warnings(payload, now=now)
    kept_raw = [
        entry for entry, info in zip(raw_warnings, aged) if not info["eski"]
    ]
    dropped = len(raw_warnings) - len(kept_raw)
    if dropped == 0:
        return {"kept": len(kept_raw), "dropped": 0, "changed": False}

    payload["warnings"] = kept_raw
    beyin_ortak._atomic_write_json(path, payload)
    return {"kept": len(kept_raw), "dropped": dropped, "changed": True}


def _format_age(age_seconds: int | None) -> str:
    if age_seconds is None:
        return "unknown"
    if age_seconds < 60:
        return f"{age_seconds}s"
    minutes = age_seconds // 60
    if minutes < 60:
        return f"{minutes}m"
    hours = minutes // 60
    if hours < 48:
        return f"{hours}h"
    days = hours // 24
    return f"{days}d"


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
    real_usage_calls = 0
    for record in records:
        backend = str(record.get("backend", "unknown")) or "unknown"
        component = str(record.get("component", "unknown")) or "unknown"
        backend_durations.setdefault(backend, []).append(
            float(_number(record, "duration_ms"))
        )
        totals = component_totals.setdefault(
            component,
            {
                "calls": 0,
                "input_tokens_est": 0,
                "output_tokens_est": 0,
                "real_usage_calls": 0,
                "input_tokens_real": 0,
                "output_tokens_real": 0,
                "cache_read_tokens_real": 0,
                "cache_write_tokens_real": 0,
            },
        )
        totals["calls"] += 1
        totals["input_tokens_est"] += _number(record, "input_tokens_est")
        totals["output_tokens_est"] += _number(record, "output_tokens_est")
        if record.get("usage_source") == "session-log":
            totals["real_usage_calls"] += 1
            real_usage_calls += 1
            totals["input_tokens_real"] += _number(record, "input_tokens")
            totals["output_tokens_real"] += _number(record, "output_tokens")
            totals["cache_read_tokens_real"] += _number(record, "cache_read_tokens")
            totals["cache_write_tokens_real"] += _number(record, "cache_write_tokens")
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
        "real_usage_calls": real_usage_calls,
        "backends": backends,
        "components": components,
    }


def build_summary(
    state_dir: Path, now: dt.datetime | None = None
) -> dict[str, Any]:
    moment = (now or dt.datetime.now()).astimezone()
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
        "warnings": summarize_warnings(health, now=moment),
        "calls": summarize_calls(state_dir, now=moment),
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
    if calls.get("real_usage_calls"):
        print()
        print(
            f"real usage (provider-reported, {calls['real_usage_calls']} of "
            f"{calls['total_calls']} calls):"
        )
        print()
        _print_grid(
            (
                "component",
                "real calls",
                "in tokens",
                "out tokens",
                "cache read",
                "cache write",
            ),
            [
                (
                    str(entry["component"]),
                    str(entry["real_usage_calls"]),
                    str(entry["input_tokens_real"]),
                    str(entry["output_tokens_real"]),
                    str(entry["cache_read_tokens_real"]),
                    str(entry["cache_write_tokens_real"]),
                )
                for entry in calls["components"]
                if entry["real_usage_calls"]
            ],
        )


def _print_warnings(warnings: Sequence[dict[str, Any]]) -> None:
    print()
    if not warnings:
        print("warnings: none recorded")
        return
    print(f"warnings: {len(warnings)}")
    print()
    _print_grid(
        ("warning", "age", "eski"),
        [
            (
                str(entry["message"]),
                _format_age(entry["age_seconds"]),
                "eski" if entry["eski"] else "",
            )
            for entry in warnings
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
    _print_warnings(summary.get("warnings", []))
    _print_calls(summary["calls"])


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--state-dir", type=Path, default=STATE_DIR)
    parser.add_argument(
        "--temizle-uyarilar",
        action="store_true",
        help="health.json'daki 24 saatten eski uyarıları siler ve çıkar",
    )
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    if os.environ.get("BEYIN_INVOKED_BY"):
        return 0
    try:
        args = _parse_args(argv)
        if args.temizle_uyarilar:
            result = temizle_uyarilar(args.state_dir)
            if result["changed"]:
                print(
                    f"temizlendi: {result['dropped']} eski uyarı silindi, "
                    f"{result['kept']} kaldı."
                )
            else:
                print("temizlenecek eski uyarı yok.")
            return 0
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
