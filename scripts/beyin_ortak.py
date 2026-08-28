#!/usr/bin/env python3
"""Shared stdlib-only filesystem, locking, hashing, and health helpers.

yazan: codex
model: gpt-5.6-sol
"""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import time
from typing import Any


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


def _atomic_write_json(path: Path, payload: dict[str, Any]) -> None:
    """Write durable UTF-8 JSON and atomically replace the destination."""
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(payload, ensure_ascii=False, indent=2) + "\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
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
