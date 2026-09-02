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
    ledger_name: str = CALLS_LEDGER_NAME,
    max_bytes: int = CALLS_LEDGER_MAX_BYTES,
) -> None:
    """Append one accounting line for a model call: numbers, never content.

    The signature is the guarantee. This function is handed character *counts*,
    not the prompt and not the response, so there is no path by which either can
    reach the file — a ledger is not a log. ``outcome`` carries the runner's
    fixed error vocabulary (``claude-timeout``, ``ollama-model-unset``, …),
    which is written by this repository rather than by a model.

    Accounting must never break the call it is accounting for, so every failure
    here is swallowed the way ``write_health`` swallows its own.
    """
    try:
        record = {
            "ts": dt.datetime.now().astimezone().isoformat(timespec="seconds"),
            "backend": str(backend),
            "component": str(component),
            "model_tier": str(model_tier),
            "model_slug": str(model_slug),
            "input_chars": int(input_chars),
            "output_chars": int(output_chars),
            "input_tokens_est": estimate_tokens(input_chars),
            "output_tokens_est": estimate_tokens(output_chars),
            "duration_ms": int(duration_ms),
            "outcome": str(outcome),
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
