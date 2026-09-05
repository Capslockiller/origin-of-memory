#!/usr/bin/env python3
"""Report or prune bounded, disposable pipeline state.

Dry-run is the default. Pass ``--uygula`` to delete or rotate eligible state.

yazan: codex
model: gpt-5.6-sol
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import shutil
import stat
import time
from typing import Any, Sequence


SCRIPT_DIR = Path(__file__).resolve().parent
STATE_DIR = SCRIPT_DIR / ".state"
HOOK_STATE_DIR = SCRIPT_DIR.parent / "hooks" / ".state"

RETRIEVE_TTL_SECONDS = 7 * 24 * 60 * 60
TMP_TTL_SECONDS = 24 * 60 * 60
LOCK_TTL_SECONDS = 7 * 24 * 60 * 60
SESSION_TTL_SECONDS = 3 * 24 * 60 * 60
ENJEKSIYON_MAX_BYTES = 1024 * 1024


def _old_regular_file(path: Path, now: float, ttl: int) -> bool:
    try:
        details = path.lstat()
    except OSError:
        return False
    return (
        stat.S_ISREG(details.st_mode)
        and not stat.S_ISLNK(details.st_mode)
        and now - details.st_mtime > ttl
    )


def _old_session_directory(path: Path, now: float) -> bool:
    try:
        details = path.lstat()
    except OSError:
        return False
    return (
        stat.S_ISDIR(details.st_mode)
        and not stat.S_ISLNK(details.st_mode)
        and now - details.st_mtime > SESSION_TTL_SECONDS
    )


def _result(category: str, action: str) -> dict[str, Any]:
    return {"category": category, "eligible": 0, "changed": 0, "errors": 0, "action": action}


def _prune_files(
    state_dir: Path,
    pattern: str,
    now: float,
    ttl: int,
    apply: bool,
    result: dict[str, Any],
    *,
    require_empty: bool = False,
) -> None:
    try:
        candidates = list(state_dir.glob(pattern))
    except OSError:
        result["errors"] += 1
        return
    for candidate in candidates:
        if not _old_regular_file(candidate, now, ttl):
            continue
        if require_empty:
            try:
                if candidate.stat().st_size != 0:
                    continue
            except OSError:
                result["errors"] += 1
                continue
        result["eligible"] += 1
        if not apply:
            continue
        try:
            candidate.unlink()
            result["changed"] += 1
        except OSError:
            result["errors"] += 1


def _rotate_enjeksiyon(
    state_dir: Path,
    apply: bool,
    result: dict[str, Any],
) -> None:
    source = state_dir / "enjeksiyon.jsonl"
    try:
        details = source.lstat()
    except OSError:
        return
    if (
        not stat.S_ISREG(details.st_mode)
        or stat.S_ISLNK(details.st_mode)
        or details.st_size < ENJEKSIYON_MAX_BYTES
    ):
        return
    result["eligible"] = 1
    if not apply:
        return
    try:
        os.replace(source, state_dir / "enjeksiyon.jsonl.1")
        result["changed"] = 1
    except OSError:
        result["errors"] = 1


def _prune_session_directories(
    hook_state_dir: Path,
    now: float,
    apply: bool,
    result: dict[str, Any],
) -> None:
    try:
        candidates = list(hook_state_dir.glob("oturum-*"))
    except OSError:
        result["errors"] += 1
        return
    for candidate in candidates:
        if not _old_session_directory(candidate, now):
            continue
        result["eligible"] += 1
        if not apply:
            continue
        try:
            shutil.rmtree(candidate)
            result["changed"] += 1
        except OSError:
            result["errors"] += 1


def run_maintenance(
    state_dir: Path,
    hook_state_dir: Path,
    *,
    apply: bool = False,
    now: float | None = None,
) -> list[dict[str, Any]]:
    """Return per-category counts, optionally applying the bounded cleanup."""
    moment = time.time() if now is None else now
    reports = [
        _result("retrieve-session ledgers", "delete"),
        _result("orphan tmp files", "delete"),
        _result("enjeksiyon.jsonl", "rotate"),
        _result("zero-byte lock carriers", "delete"),
        _result("stale session directories", "delete"),
    ]
    _prune_files(
        state_dir,
        "retrieve-session-*.json",
        moment,
        RETRIEVE_TTL_SECONDS,
        apply,
        reports[0],
    )
    _prune_files(
        state_dir,
        "*.tmp",
        moment,
        TMP_TTL_SECONDS,
        apply,
        reports[1],
    )
    _rotate_enjeksiyon(state_dir, apply, reports[2])
    _prune_files(
        state_dir,
        "*.lock",
        moment,
        LOCK_TTL_SECONDS,
        apply,
        reports[3],
        require_empty=True,
    )
    _prune_session_directories(hook_state_dir, moment, apply, reports[4])
    return reports


def _print_table(reports: Sequence[dict[str, Any]], apply: bool) -> None:
    headers = ("category", "eligible", "changed", "errors", "mode")
    rows = [
        (
            str(report["category"]),
            str(report["eligible"]),
            str(report["changed"]),
            str(report["errors"]),
            str(report["action"] if apply else f"would {report['action']}"),
        )
        for report in reports
    ]
    widths = [
        max([len(headers[index])] + [len(row[index]) for row in rows])
        for index in range(len(headers))
    ]
    print(" | ".join(value.ljust(widths[index]) for index, value in enumerate(headers)))
    print("-+-".join("-" * width for width in widths))
    for row in rows:
        print(" | ".join(value.ljust(widths[index]) for index, value in enumerate(row)))


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--dry-run", action="store_true", help="report only (default)")
    mode.add_argument("--uygula", action="store_true", help="apply eligible cleanup")
    parser.add_argument("--state-dir", type=Path, default=STATE_DIR)
    parser.add_argument("--hook-state-dir", type=Path)
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    if os.environ.get("BEYIN_INVOKED_BY"):
        return 0
    try:
        args = _parse_args(argv)
        hook_state_dir = args.hook_state_dir
        if hook_state_dir is None:
            hook_state_dir = (
                HOOK_STATE_DIR if args.state_dir == STATE_DIR else args.state_dir
            )
        reports = run_maintenance(
            args.state_dir,
            hook_state_dir,
            apply=args.uygula,
        )
        _print_table(reports, args.uygula)
    except (Exception, SystemExit):
        # Maintenance is also called from hooks; it must never block a session.
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
