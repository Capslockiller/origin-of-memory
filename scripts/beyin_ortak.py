#!/usr/bin/env python3
"""Shared stdlib-only filesystem, locking, hashing, and health helpers.

yazan: codex
model: gpt-5.6-sol
"""

from __future__ import annotations

import datetime as dt
import hashlib
import itertools
import json
import os
from pathlib import Path
import threading
import time
from typing import Any


CALLS_LEDGER_NAME = "calls.jsonl"
CALLS_LEDGER_MAX_BYTES = 5 * 1024 * 1024
# Every token figure in the ledger is characters ÷ 4. It is an estimate, not a
# provider count, which is why the fields carry the `_est` suffix.
CHARS_PER_TOKEN_ESTIMATE = 4

# ``usage_source`` vocabulary for the four *real* token fields.
# "session-log" is the only value that means measured; anything else means the
# provider told us nothing, and then the real fields are null. There is no
# "estimate" value any more: an estimate lives in ``*_tokens_est`` and is never
# allowed to sit in a field whose name promises a measurement (Astra B2).
USAGE_SESSION_LOG = "session-log"
USAGE_UNKNOWN = "unknown"

# Why a model call was made — the closed vocabulary that makes memory upkeep
# separable from the work the memory serves (Astra B2, 2026-09-06).
PURPOSE_CAPTURE = "capture"        # flush: turning a session into a summary
PURPOSE_EXTRACT = "extract"        # reserved: the future fact extractor
PURPOSE_CONCEPT = "concept"        # compile: writing concept notes
PURPOSE_RETRIEVE = "retrieve"      # reserved: no model call in retrieval today
PURPOSE_INGEST = "ingest"          # ingest_*: importing outside transcripts
PURPOSE_BENCHMARK = "benchmark"    # tools/benchmark harness
PURPOSE_HAND_MEMORY = "hand-memory"  # reserved: memory handled by hand
PURPOSE_WORK = "work"              # the default: work the memory is *for*

PURPOSES = (
    PURPOSE_CAPTURE,
    PURPOSE_EXTRACT,
    PURPOSE_CONCEPT,
    PURPOSE_RETRIEVE,
    PURPOSE_INGEST,
    PURPOSE_BENCHMARK,
    PURPOSE_HAND_MEMORY,
    PURPOSE_WORK,
)
PURPOSE_DEFAULT = PURPOSE_WORK

# The three budgets. Turkish names because this is the reporting vocabulary
# Master reads, and the report is the point.
GROUP_BAKIM = "bakım"
GROUP_GELISTIRME = "geliştirme"
GROUP_IS = "iş"
GROUP_BILINMIYOR = "sınıflandırılamadı"
PURPOSE_GROUPS: dict[str, str] = {
    PURPOSE_CAPTURE: GROUP_BAKIM,
    PURPOSE_EXTRACT: GROUP_BAKIM,
    PURPOSE_CONCEPT: GROUP_BAKIM,
    PURPOSE_RETRIEVE: GROUP_BAKIM,
    PURPOSE_INGEST: GROUP_BAKIM,
    PURPOSE_BENCHMARK: GROUP_GELISTIRME,
    PURPOSE_WORK: GROUP_IS,
    PURPOSE_HAND_MEMORY: GROUP_IS,
}
GROUP_ORDER = (GROUP_BAKIM, GROUP_GELISTIRME, GROUP_IS, GROUP_BILINMIYOR)


def purpose_group(purpose: Any) -> str:
    """The budget a purpose belongs to; an unrecognised one is never guessed."""
    return PURPOSE_GROUPS.get(str(purpose or ""), GROUP_BILINMIYOR)


def normalize_purpose(purpose: Any) -> tuple[str, bool]:
    """``(value, missing)`` — an absent or unknown purpose reads as ``work``.

    Refusing the call would be worse than mislabelling it: ``record_call`` sits
    inside every hook, and a ledger that can raise is a hook that can fail. So
    the value falls back to ``work`` and the caller is told to warn instead.
    Old ledger lines, written before this field existed, read as ``work`` for
    the same reason — by absence, not by claim.
    """
    value = str(purpose or "").strip()
    if value in PURPOSE_GROUPS:
        return value, False
    return PURPOSE_DEFAULT, True


try:
    import fcntl
except ImportError:  # Windows has no fcntl; msvcrt region locks stand in.
    fcntl = None  # type: ignore[assignment]
    import msvcrt


def _lock_exclusive(lock_file: Any, blocking: bool) -> None:
    """Take a portable exclusive lock on an already-open file."""
    if fcntl is not None:
        flags = fcntl.LOCK_EX if blocking else fcntl.LOCK_EX | fcntl.LOCK_NB
        fcntl.flock(lock_file.fileno(), flags)
        return
    lock_file.seek(0)
    if blocking:
        deadline = time.time() + 300
        while True:
            try:
                msvcrt.locking(lock_file.fileno(), msvcrt.LK_NBLCK, 1)
                return
            except OSError:
                if time.time() >= deadline:
                    raise
                time.sleep(1)
    try:
        msvcrt.locking(lock_file.fileno(), msvcrt.LK_NBLCK, 1)
    except OSError as exc:
        raise BlockingIOError(str(exc)) from exc


# A pid-only temp name is NOT unique enough: kule's lane threads share the
# pid, so two concurrent writers opened the SAME .tmp — one truncating the
# other mid-write (silent corruption on any OS), and on Windows the loser's
# open handle made os.replace throw WinError 32 (caught on CI 2026-09-02,
# LaneCapTests). Thread id + a process-wide counter make each writer's temp
# file its own.
_TMP_SAYAC = itertools.count()


def _unique_tmp(path: Path) -> Path:
    return path.with_name(
        f".{path.name}.{os.getpid()}.{threading.get_ident()}.{next(_TMP_SAYAC)}.tmp"
    )


def _replace_with_retry(temporary: Path, path: Path) -> None:
    """os.replace, absorbing Windows sharing-violation races, bounded.

    A concurrent reader of the destination (CPython's open() does not pass
    FILE_SHARE_DELETE) can make the replace fail transiently with
    PermissionError. Retry briefly; past the deadline the error surfaces —
    fail loud, never fail silent.
    """
    deadline = time.monotonic() + 2.0
    while True:
        try:
            os.replace(temporary, path)
            return
        except PermissionError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(0.025)


def _atomic_write_json(path: Path, payload: dict[str, Any]) -> None:
    """Write durable UTF-8 JSON and atomically replace the destination."""
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = _unique_tmp(path)
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(payload, ensure_ascii=False, indent=2) + "\n")
            handle.flush()
            os.fsync(handle.fileno())
        _replace_with_retry(temporary, path)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def _sha256(path: Path) -> str:
    """Return the SHA-256 digest of a file without loading it all into memory."""
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _clean_int(value: Any) -> int | None:
    """``value`` as a real ``int``, or ``None`` — bools never pass as counts."""
    if isinstance(value, bool) or not isinstance(value, int):
        return None
    return value


def estimate_tokens(chars: int) -> int:
    """Characters ÷ 4. An estimate — never call it a token count."""
    return max(0, int(chars)) // CHARS_PER_TOKEN_ESTIMATE


def _rotate_calls_ledger(path: Path, max_bytes: int) -> None:
    """Past the cap, keep the newest lines that fit in half of it.

    Halving rather than trimming one line per append keeps rotation amortised:
    the rewrite happens once per half-cap of traffic instead of on every call.
    """
    try:
        size = path.stat().st_size
    except OSError:
        return
    if size <= max_bytes:
        return
    try:
        with path.open("rb") as handle:
            handle.seek(max(0, size - max_bytes // 2))
            tail = handle.read()
    except OSError:
        return
    # The seek lands mid-line; drop the partial head so every kept line parses.
    newline = tail.find(b"\n")
    tail = tail[newline + 1 :] if newline != -1 else b""
    temporary = _unique_tmp(path)
    try:
        with temporary.open("wb") as handle:
            handle.write(tail)
            handle.flush()
            os.fsync(handle.fileno())
        _replace_with_retry(temporary, path)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def record_call(
    state_dir: Path,
    *,
    backend: str,
    model_tier: str,
    model_slug: str,
    component: str,
    input_chars: int,
    output_chars: int,
    duration_ms: int,
    outcome: str,
    purpose: str = "",
    input_tokens: int | None = None,
    output_tokens: int | None = None,
    cache_read_tokens: int | None = None,
    cache_write_tokens: int | None = None,
    model_actual: str | None = None,
    usage_source: str = USAGE_UNKNOWN,
    ledger_name: str = CALLS_LEDGER_NAME,
    health_name: str = "health.json",
    max_bytes: int = CALLS_LEDGER_MAX_BYTES,
) -> None:
    """Append one accounting line for a model call: numbers, never content.

    The signature is the guarantee. This function is handed character *counts*,
    not the prompt and not the response, so there is no path by which either can
    reach the file — a ledger is not a log. ``outcome`` carries the runner's
    fixed error vocabulary (``claude-timeout``, ``ollama-model-unset``, …),
    which is written by this repository rather than by a model.

    ``input_tokens``/``output_tokens``/``cache_read_tokens``/``cache_write_tokens``
    and ``model_actual`` are real provider-reported figures when the caller has
    them (currently: the claude backend, from its own ``--output-format json``
    session summary) — never the chars/4 estimate, which stays in
    ``*_tokens_est`` regardless. ``usage_source`` says which kind the real
    fields are: ``"session-log"`` when they come from the provider, or
    ``"unknown"`` when nothing better was available — and then the four
    real-usage fields are forced to ``None`` here, not merely left unset. That
    is the point of the rename (Astra B2): a line that says ``unknown`` must
    not also carry numbers a reader could total up as measured. The estimate
    is still recorded, in ``*_tokens_est``, where the name says what it is.

    ``purpose`` says *why* the call was made — see ``PURPOSES``. It is required
    by convention, not by exception: an absent or unrecognised value is written
    as ``work`` and raises a ``warn:call-purpose-missing:<component>`` health
    warning, because a ledger that can fail is a hook that can fail.

    Accounting must never break the call it is accounting for, so every failure
    here is swallowed the way ``write_health`` swallows its own.
    """
    resolved_purpose, purpose_missing = normalize_purpose(purpose)
    source = str(usage_source or "").strip() or USAGE_UNKNOWN
    if source != USAGE_SESSION_LOG:
        source = USAGE_UNKNOWN
        input_tokens = output_tokens = None
        cache_read_tokens = cache_write_tokens = None
    try:
        record = {
            "ts": dt.datetime.now().astimezone().isoformat(timespec="seconds"),
            "backend": str(backend),
            "component": str(component),
            "purpose": resolved_purpose,
            "model_tier": str(model_tier),
            "model_slug": str(model_slug),
            "input_chars": int(input_chars),
            "output_chars": int(output_chars),
            "input_tokens_est": estimate_tokens(input_chars),
            "output_tokens_est": estimate_tokens(output_chars),
            "duration_ms": int(duration_ms),
            "outcome": str(outcome),
            "input_tokens": _clean_int(input_tokens),
            "output_tokens": _clean_int(output_tokens),
            "cache_read_tokens": _clean_int(cache_read_tokens),
            "cache_write_tokens": _clean_int(cache_write_tokens),
            "model_actual": str(model_actual) if model_actual else "",
            "usage_source": source,
        }
        line = json.dumps(record, ensure_ascii=False) + "\n"
        state_dir = Path(state_dir)
        state_dir.mkdir(parents=True, exist_ok=True)
        path = state_dir / ledger_name
        _rotate_calls_ledger(path, max_bytes)
        with path.open("a", encoding="utf-8", newline="\n") as handle:
            handle.write(line)
    except (OSError, TypeError, ValueError):
        pass
    if purpose_missing:
        # Loud, but never fatal: the line is already written as ``work``.
        write_health(
            Path(state_dir),
            f"warn:call-purpose-missing:{component}",
            warning=True,
            component=str(component),
            health_name=health_name,
        )


def write_health(
    state_dir: Path,
    error: str = "",
    warning: bool = False,
    *,
    component: str = "compile",
    health_name: str = "health.json",
    counts: dict[str, int] | None = None,
    last_run: dict[str, Any] | None = None,
) -> None:
    """Record component health without allowing reporting itself to crash."""
    try:
        path = state_dir / health_name
        payload: dict[str, Any] = {}
        if path.exists():
            try:
                loaded = json.loads(path.read_text(encoding="utf-8"))
                if isinstance(loaded, dict):
                    payload.update(loaded)
            except (OSError, ValueError, json.JSONDecodeError):
                pass
        payload.update(
            {"ts": int(time.time()), "component": component, "error": error}
        )
        if warning:
            warnings = payload.get("warnings", [])
            if not isinstance(warnings, list):
                warnings = []
            if error not in warnings:
                warnings.append(error)
            payload["warnings"] = warnings[-20:]
        if counts is not None:
            payload["counts"] = counts
        if last_run is not None:
            payload["last_run"] = last_run
        _atomic_write_json(path, payload)
    except OSError:
        pass


def write_health_skip(
    state_dir: Path,
    reason: str,
    component: str = "compile",
    *,
    health_name: str = "health.json",
) -> None:
    """Record a bounded, counted deliberate skip without setting an error."""
    try:
        path = state_dir / health_name
        payload: dict[str, Any] = {}
        if path.exists():
            try:
                loaded = json.loads(path.read_text(encoding="utf-8"))
                if isinstance(loaded, dict):
                    payload.update(loaded)
            except (OSError, ValueError, json.JSONDecodeError):
                pass
        now = int(time.time())
        skips = payload.get("skips", [])
        if not isinstance(skips, list):
            skips = []
        entries = [item for item in skips if isinstance(item, dict)]
        existing = next(
            (item for item in entries if item.get("reason") == reason), None
        )
        if existing is None:
            entries.append({"reason": reason, "ts": now, "count": 1})
        else:
            existing["ts"] = now
            count = existing.get("count", 0)
            existing["count"] = (count if isinstance(count, int) else 0) + 1
        payload["skips"] = entries[-20:]
        payload["last_skip"] = {
            "ts": now,
            "component": component,
            "reason": reason,
        }
        _atomic_write_json(path, payload)
    except OSError:
        pass
