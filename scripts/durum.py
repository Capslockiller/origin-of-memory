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
VAULT_ROOT = SCRIPT_DIR.parent.parent
SCHEMA_VERSION = 1
COMPONENTS = ("flush", "compile", "ingest")
CALLS_WINDOW_DAYS = 7
WARNING_STALE_SECONDS = 24 * 60 * 60
HEALTH_NAME = "health.json"

# Uyarı → bilgi çeviri tablosu (Astra A14, 2026-09-06).
# ``warn:registry-truncated:77/525`` compile.py'de BAŞARILI her sınırlı seçimde
# yazılır: 525 satırlık kayıt defterinden 77'si seçildiyse mekanizma çalışmıştır,
# arıza yoktur. Bu bir telemetri kalemidir, uyarı değil. health.json'a
# DOKUNULMAZ (ham dize orada aynen durur, uyumluluk için); yalnız burada,
# gösterim katmanında çevrilir ve uyarı sayımının dışında tutulur.
# Anahtar = ham önek, değer = gösterilecek önek.
WARNING_INFO_PREFIXES = {
    "warn:registry-truncated:": "info:registry-selection:",
}


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


def _demote(message: str) -> tuple[str, bool]:
    """``(gösterilecek mesaj, bilgi mi)`` — bkz. ``WARNING_INFO_PREFIXES``."""
    for raw_prefix, info_prefix in WARNING_INFO_PREFIXES.items():
        if message.startswith(raw_prefix):
            return info_prefix + message[len(raw_prefix):], True
    return message, False


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
        raw = _warning_message(entry)
        message, info = _demote(raw)
        result.append(
            {
                "message": message,
                "raw": raw,
                "info": info,
                "ts": int(effective_ts) if effective_ts is not None else None,
                "age_seconds": age_seconds,
                "eski": eski,
            }
        )
    return result


def _warning_count(warnings: Sequence[dict[str, Any]]) -> int:
    """Hattı yeşilden çıkaran kalem sayısı — bilgi kalemleri sayılmaz."""
    return sum(1 for entry in warnings if not entry.get("info"))


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


def _empty_budget() -> dict[str, int]:
    """One budget row: calls, the estimate pair, and the measured/unmeasured split."""
    return {
        "calls": 0,
        "input_tokens_est": 0,
        "output_tokens_est": 0,
        "real_usage_calls": 0,
        "unknown_usage_calls": 0,
        "output_tokens_real": 0,
        "cache_read_tokens_real": 0,
    }


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
    purpose_totals: dict[str, dict[str, int]] = {}
    group_totals: dict[str, dict[str, int]] = {}
    ok_calls = 0
    real_usage_calls = 0
    unknown_usage_calls = 0
    for record in records:
        backend = str(record.get("backend", "unknown")) or "unknown"
        component = str(record.get("component", "unknown")) or "unknown"
        # A line written before the field existed is `work` by absence, which
        # is exactly what `normalize_purpose` says — no guessing here either.
        purpose, _missing = beyin_ortak.normalize_purpose(record.get("purpose"))
        group = beyin_ortak.purpose_group(purpose)
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
                "unknown_usage_calls": 0,
                "input_tokens_real": 0,
                "output_tokens_real": 0,
                "cache_read_tokens_real": 0,
                "cache_write_tokens_real": 0,
            },
        )
        budgets = [
            purpose_totals.setdefault(purpose, _empty_budget()),
            group_totals.setdefault(group, _empty_budget()),
        ]
        totals["calls"] += 1
        totals["input_tokens_est"] += _number(record, "input_tokens_est")
        totals["output_tokens_est"] += _number(record, "output_tokens_est")
        for budget in budgets:
            budget["calls"] += 1
            budget["input_tokens_est"] += _number(record, "input_tokens_est")
            budget["output_tokens_est"] += _number(record, "output_tokens_est")
        if record.get("usage_source") == beyin_ortak.USAGE_SESSION_LOG:
            totals["real_usage_calls"] += 1
            real_usage_calls += 1
            totals["input_tokens_real"] += _number(record, "input_tokens")
            totals["output_tokens_real"] += _number(record, "output_tokens")
            totals["cache_read_tokens_real"] += _number(record, "cache_read_tokens")
            totals["cache_write_tokens_real"] += _number(record, "cache_write_tokens")
            for budget in budgets:
                budget["real_usage_calls"] += 1
                budget["output_tokens_real"] += _number(record, "output_tokens")
                budget["cache_read_tokens_real"] += _number(
                    record, "cache_read_tokens"
                )
        else:
            # Counted, never estimated into the real totals: an unmeasured call
            # is a hole in the measurement and the report has to show the hole.
            totals["unknown_usage_calls"] += 1
            unknown_usage_calls += 1
            for budget in budgets:
                budget["unknown_usage_calls"] += 1
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
    purposes = [
        {"purpose": purpose, **totals}
        for purpose, totals in sorted(
            purpose_totals.items(), key=lambda item: (-item[1]["calls"], item[0])
        )
    ]
    groups = [
        {"group": group, **group_totals[group]}
        for group in beyin_ortak.GROUP_ORDER
        if group in group_totals
    ]
    return {
        "window_days": CALLS_WINDOW_DAYS,
        "total_calls": len(records),
        "ok_calls": ok_calls,
        "failed_calls": len(records) - ok_calls,
        "real_usage_calls": real_usage_calls,
        "unknown_usage_calls": unknown_usage_calls,
        "backends": backends,
        "components": components,
        "purposes": purposes,
        "purpose_groups": groups,
    }


def bekleyen_kaynak(
    vault_root: Path, compile_state: dict[str, Any]
) -> dict[str, Any]:
    """Derlenmeyi bekleyen günlükler (Astra A14).

    ``compile.changed_daily_logs`` ile aynı ölçüt — ``daily/*.md`` içeriğinin
    sha256'sı ``compile-state.json["ingested"]`` içindeki değerle eşleşmiyorsa
    dosya beklemededir — ama burası salt okunur bir rapor yüzeyi: karantina ve
    park edilmiş kalemler bekleyenden düşülür, hiçbir yol ihlali istisna
    fırlatmaz (okunamayan dosya sessizce atlanır). ``compile`` içe aktarılmaz;
    o modül model çalıştırıcılarını da yükler, durum raporu bunu kaldırmaz.
    """
    daily_dir = Path(vault_root) / "daily"
    ingested = compile_state.get("ingested")
    ingested = ingested if isinstance(ingested, dict) else {}
    quarantined = compile_state.get("quarantined")
    quarantined = quarantined if isinstance(quarantined, dict) else {}
    parked = compile_state.get("parked")
    parked = parked if isinstance(parked, dict) else {}

    pending: list[str] = []
    try:
        candidates = sorted(daily_dir.glob("*.md"))
    except OSError:
        candidates = []
    for path in candidates:
        try:
            digest = beyin_ortak._sha256(path)
        except OSError:
            continue
        if ingested.get(path.name) == digest or digest in quarantined:
            continue
        park = parked.get(path.name)
        if isinstance(park, dict) and park.get("digest") == digest:
            continue
        pending.append(path.name)
    return {
        "count": len(pending),
        "oldest": pending[0] if pending else None,
        "files": pending,
    }


def mutabakat_ozeti(
    state_dir: Path, now: dt.datetime | None = None
) -> dict[str, Any]:
    """Expose authoritative reconciliation coverage without scanning sources."""
    moment = (now or dt.datetime.now()).astimezone()
    payload = _read_object(Path(state_dir) / "mutabakat.json")
    count = payload.get("uncovered_session_count", 0)
    if isinstance(count, bool) or not isinstance(count, int):
        count = 0
    unmatched = payload.get("unmatched_ingress_count", 0)
    if isinstance(unmatched, bool) or not isinstance(unmatched, int):
        unmatched = 0
    oldest = payload.get("oldest_unprocessed_source_time")
    age: int | None = None
    if isinstance(oldest, str) and oldest:
        try:
            parsed = dt.datetime.fromisoformat(oldest)
            if parsed.tzinfo is None:
                parsed = parsed.astimezone()
            age = max(0, int((moment - parsed).total_seconds()))
        except ValueError:
            oldest = None
    else:
        oldest = None
    return {
        "uncovered_sessions": max(0, count),
        "oldest_unprocessed_source_time": oldest,
        "oldest_unprocessed_source_age_seconds": age,
        "unmatched_ingress": max(0, unmatched),
    }


def build_summary(
    state_dir: Path, now: dt.datetime | None = None, vault_root: Path | None = None
) -> dict[str, Any]:
    moment = (now or dt.datetime.now()).astimezone()
    # ``.state`` scripts/ altında, scripts/ de <vault>/.claude altındadır;
    # --state-dir ile taşındığında kök de onunla birlikte taşınsın.
    if vault_root is None:
        vault_root = Path(state_dir).resolve().parent.parent.parent
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
    warnings = summarize_warnings(health, now=moment)
    return {
        "schema_version": SCHEMA_VERSION,
        "rows": rows,
        "warnings": warnings,
        "warning_count": _warning_count(warnings),
        "info_count": len(warnings) - _warning_count(warnings),
        "bekleyen": bekleyen_kaynak(vault_root, compile_state),
        "duzeltme": bekleyen_duzeltme(vault_root, now=moment),
        "mutabakat": mutabakat_ozeti(state_dir, now=moment),
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
    groups = calls.get("purpose_groups") or []
    if groups:
        print()
        # The two budgets Astra B2 asked for: what the memory costs to keep,
        # what building it costs, and what the work it serves costs.
        print("bütçe (amaç grubuna göre):")
        print()
        _print_grid(
            (
                "grup",
                "calls",
                "out tokens (est)",
                "out tokens",
                "cache read",
                "unknown",
            ),
            [
                (
                    str(entry["group"]),
                    str(entry["calls"]),
                    str(entry["output_tokens_est"]),
                    str(entry["output_tokens_real"]),
                    str(entry["cache_read_tokens_real"]),
                    f"{entry['unknown_usage_calls']} calls",
                )
                for entry in groups
            ],
        )
    if calls.get("real_usage_calls") or calls.get("unknown_usage_calls"):
        print()
        print(
            f"real usage (provider-reported, {calls['real_usage_calls']} of "
            f"{calls['total_calls']} calls; "
            f"{calls.get('unknown_usage_calls', 0)} unknown):"
        )
        print()
        # Every component appears, measured or not. A component with no
        # provider figures used to vanish from this table, which read as "it
        # cost nothing" when it meant "nobody counted" — the `unknown` column
        # is the difference, and no estimate is ever summed into these totals.
        _print_grid(
            (
                "component",
                "real calls",
                "in tokens",
                "out tokens",
                "cache read",
                "cache write",
                "unknown",
            ),
            [
                (
                    str(entry["component"]),
                    str(entry["real_usage_calls"]),
                    str(entry["input_tokens_real"]),
                    str(entry["output_tokens_real"]),
                    str(entry["cache_read_tokens_real"]),
                    str(entry["cache_write_tokens_real"]),
                    f"{entry.get('unknown_usage_calls', 0)} calls",
                )
                for entry in calls["components"]
            ],
        )


def _print_warnings(warnings: Sequence[dict[str, Any]]) -> None:
    print()
    if not warnings:
        print("warnings: none recorded")
        return
    real = _warning_count(warnings)
    info = len(warnings) - real
    headline = f"warnings: {real}"
    if info:
        headline += f" (+{info} info)"
    print(headline)
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


def _print_pending(pending: dict[str, Any]) -> None:
    # Gövde bilerek ASCII: bu tablo bir rapor yüzeyi ve main() her istisnayı
    # yutuyor, yani cp437 gibi dar bir konsolda tek bir "ü" raporun geri
    # kalanını sessizce kesebilirdi.
    print()
    count = pending.get("count", 0)
    if not count:
        print("bekleyen kaynak: none pending")
        return
    oldest = str(pending.get("oldest") or "?")
    if oldest.endswith(".md"):
        oldest = oldest[: -len(".md")]
    print(f"bekleyen kaynak: {count} daily uncompiled (oldest {oldest})")


def _print_reconciliation(summary: dict[str, Any]) -> None:
    print()
    count = int(summary.get("uncovered_sessions", 0))
    unmatched = int(summary.get("unmatched_ingress", 0))
    age = _format_age(summary.get("oldest_unprocessed_source_age_seconds"))
    print(
        f"mutabakat: {count} uncovered sessions "
        f"(oldest source {age}; unmatched ingress {unmatched})"
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
    _print_pending(summary.get("bekleyen", {}))
    _print_duzeltme(summary.get("duzeltme", {}))
    _print_reconciliation(summary.get("mutabakat", {}))
    _print_warnings(summary.get("warnings", []))
    _print_calls(summary["calls"])


# --------------------------------------------------------------------------
# Faz 1 · A3-1D — bekleyen düzeltme satırı.
# Kendi içinde kapalı bir ek: ``build_summary`` tek anahtar, ``_print_table``
# tek çağrı ekler. ``duzelt`` tembel içe aktarılır — durum bir rapor yüzeyidir
# ve defteri okuyamamak raporu düşürmemeli.
# --------------------------------------------------------------------------


def bekleyen_duzeltme(
    vault_root: Path | None, now: dt.datetime | None = None
) -> dict[str, Any]:
    """Uygulanmamış düzeltmeler: sayı ve en eskisinin yaşı.

    Bir düzeltme "bekliyor"da kaldığı sürece bilinen bir yanlış hâlâ kavramda
    duruyor demektir; bu yüzden bekleyen kaynak satırının hemen yanında durur.
    """
    if vault_root is None:
        return {"count": 0, "oldest": None, "oldest_ts": None, "oldest_age_seconds": None}
    try:
        import duzelt

        return duzelt.ozet(vault_root, now=now)
    except Exception:
        return {"count": 0, "oldest": None, "oldest_ts": None, "oldest_age_seconds": None}


def _print_duzeltme(pending: dict[str, Any]) -> None:
    # Gövde bilerek ASCII, ``_print_pending`` ile aynı gerekçe.
    print()
    count = pending.get("count", 0)
    if not count:
        print("bekleyen duzeltme: none pending")
        return
    oldest = str(pending.get("oldest") or "?")
    age = _format_age(pending.get("oldest_age_seconds"))
    print(f"bekleyen duzeltme: {count} unapplied (oldest {oldest}, {age})")


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--state-dir", type=Path, default=STATE_DIR)
    parser.add_argument(
        "--vault-root",
        type=Path,
        default=None,
        help="günlük dizininin kökü (varsayılan: --state-dir'den türetilir)",
    )
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
        # The budget table added Turkish headings to what used to be an
        # ASCII-only report; a legacy Windows console codepage would mangle or
        # raise on them. Same guard harcama_defteri already carries.
        import sys

        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
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
        summary = build_summary(args.state_dir, vault_root=args.vault_root)
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
