#!/usr/bin/env python3
# yazan: codex
# model: gpt-5.6-sol
"""Capture settled agent transcripts without depending on lifecycle hooks."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import time
from typing import Any, NamedTuple, Sequence

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parent))

import flush
import ingest_claude
import ingest_codex
import ingest_common
from ingest_common import Session


DEFAULT_INTERVAL = 15 * 60.0
DEFAULT_MAX_SESSIONS = 10
DEFAULT_SLEEP = 4.0
GENERIC_SOURCE = "generic"
GENERIC_EXTENSIONS = {".jsonl", ".md"}
GENERIC_HEADING = re.compile(r"(?m)^##[ \t]+(User|Assistant)[ \t]*\r?$", re.I)


class GenericRoot(NamedTuple):
    name: str
    path: Path


class Reject(NamedTuple):
    source: str
    path: Path
    size: int
    mtime: float
    reason: str


def _generic_source(name: str) -> str:
    return f"{GENERIC_SOURCE}:{name}"


def _generic_key(name: str, root: Path, path: Path) -> str:
    relative = path.relative_to(root).as_posix()
    digest = hashlib.sha256(f"{name}\0{relative}".encode("utf-8")).hexdigest()[:24]
    return f"generic-{digest}"


def _read_markdown(path: Path) -> list[tuple[str, str]]:
    text = path.read_text(encoding="utf-8")
    matches = list(GENERIC_HEADING.finditer(text))
    turns: list[tuple[str, str]] = []
    for index, match in enumerate(matches):
        end = matches[index + 1].start() if index + 1 < len(matches) else len(text)
        body = ingest_common.collapse(text[match.end() : end])
        if body:
            turns.append((match.group(1).casefold(), body))
    return turns


def _read_generic(path: Path) -> list[tuple[str, str]]:
    if path.suffix.casefold() == ".jsonl":
        return flush.read_transcript(path)
    return _read_markdown(path)


def generic_candidates(
    state: dict[str, Any],
    generic_root: GenericRoot,
    *,
    now_epoch: float | None = None,
    fresh_seconds: float = DEFAULT_INTERVAL,
) -> tuple[list[Session], list[Reject]]:
    source = _generic_source(generic_root.name)
    after_watermark = ingest_common.latest_watermark(state, source)
    sessions: list[Session] = []
    rejects: list[Reject] = []
    if not generic_root.path.exists():
        return sessions, rejects
    current = time.time() if now_epoch is None else now_epoch
    paths = sorted(
        (
            path
            for path in generic_root.path.rglob("*")
            if path.is_file() and path.suffix.casefold() in GENERIC_EXTENSIONS
        ),
        key=lambda path: path.as_posix(),
    )
    for path in paths:
        try:
            file_stat = path.stat()
        except OSError:
            continue
        if current - file_stat.st_mtime < fresh_seconds:
            continue
        if ingest_common.file_unchanged(
            state, source, str(path),
            file_stat.st_size, file_stat.st_mtime,
        ):
            continue
        watermark = ingest_common.file_watermark(path, file_stat)
        key = _generic_key(generic_root.name, generic_root.path, path)
        entry = ingest_common.done_entry(state, source, key)
        if ingest_common.should_skip(entry, retry_failed=True):
            continue
        if after_watermark and watermark <= after_watermark and entry is None:
            continue
        try:
            turns = _read_generic(path)
        except (OSError, ValueError, json.JSONDecodeError) as exc:
            rejects.append(
                Reject(source, path, file_stat.st_size, file_stat.st_mtime,
                       str(exc) or exc.__class__.__name__)
            )
            continue
        if not turns:
            rejects.append(
                Reject(source, path, file_stat.st_size, file_stat.st_mtime,
                       "no-user-assistant-turns")
            )
            continue
        when = dt.datetime.fromtimestamp(file_stat.st_mtime).astimezone()
        sessions.append(
            Session(
                source=source,
                key=key,
                when=when,
                turns=turns,
                origin=str(path),
                watermark=watermark,
                label=source,
                # The current provenance vocabulary calls external imports web.
                anchor=flush.session_anchor(key, when, "web"),
            )
        )
    return sessions, rejects


def _save_state(state: dict[str, Any], state_dir: Path) -> None:
    ingest_common.save_state(state, state_dir)


def _record_reject(state: dict[str, Any], reject: Reject) -> None:
    try:
        name = str(reject.path)
        ingest_common.record_file(
            state, reject.source, name, reject.size, reject.mtime
        )
    except (OSError, ValueError):
        return


def _capture_session(
    session: Session,
    state: dict[str, Any],
    state_dir: Path,
    vault_root: Path,
    model: str,
) -> tuple[str, bool]:
    with ingest_common.flush_session_lock(session.key, state_dir):
        if ingest_common.daily_has_session_anchor(vault_root, session):
            ingest_common.record_done(
                state, session.source, session.key, "skipped-live",
                watermark=session.watermark,
            )
            _save_state(state, state_dir)
            return "skipped", False

        result = ingest_common.summarize_session(
            session, vault_root, state_dir, model, min_turns=1
        )
        if result.status == "ok":
            daily = ingest_common.append_historical(
                vault_root, result.summary, session, model
            )
            ingest_common.record_done(
                state, session.source, session.key, "appended", daily=daily,
                watermark=session.watermark,
            )
            flush._write_flush_state(
                state_dir, session.key, time.time(), "ok", "watcher-appended"
            )
            outcome = "ingested"
        elif result.status == "bos":
            ingest_common.record_done(
                state, session.source, session.key, "bos",
                watermark=session.watermark,
            )
            flush._write_flush_state(
                state_dir, session.key, time.time(), "ok", "watcher-bos"
            )
            outcome = "skipped"
        else:
            ingest_common.record_done(
                state, session.source, session.key, f"fail:{result.detail}",
                watermark=session.watermark,
            )
            outcome = "failed"
        _save_state(state, state_dir)
        return outcome, True


def sweep(
    args: argparse.Namespace,
    *,
    state_dir: Path = ingest_common.STATE_DIR,
    vault_root: Path = ingest_common.VAULT_ROOT,
    now_epoch: float | None = None,
) -> dict[str, int]:
    state = ingest_common.load_state(state_dir)
    current = time.time() if now_epoch is None else now_epoch
    sessions: list[Session] = []
    rejects: list[Reject] = []

    if not args.no_claude:
        source = ingest_claude.SOURCE
        found, live_skips = ingest_claude.candidates(
            state,
            projects_root=args.claude_root,
            state_dir=state_dir,
            retry_failed=True,
            now_epoch=current,
            fresh_seconds=args.settle_seconds,
            after_watermark=ingest_common.latest_watermark(state, source),
        )
        sessions.extend(found)
        for key, status in live_skips:
            ingest_common.record_done(state, source, key, status)

    if not args.no_codex:
        source = ingest_codex.SOURCE
        found, codex_rejects = ingest_codex.candidates(
            state,
            sessions_root=args.codex_root,
            retry_failed=True,
            now_epoch=current,
            fresh_seconds=args.settle_seconds,
            after_watermark=ingest_common.latest_watermark(state, source),
        )
        sessions.extend(found)
        for name, size, mtime, reason in codex_rejects:
            rejects.append(Reject(source, Path(name), size, mtime, reason))

    for generic_root in args.generic:
        found, generic_rejects = generic_candidates(
            state,
            generic_root,
            now_epoch=current,
            fresh_seconds=args.settle_seconds,
        )
        sessions.extend(found)
        rejects.extend(generic_rejects)

    for reject in rejects:
        _record_reject(state, reject)
    if rejects:
        _save_state(state, state_dir)

    sessions.sort(key=lambda session: (session.watermark, session.when, session.key))
    counts = {
        "scanned": len(sessions) + len(rejects),
        "ingested": 0,
        "skipped": len(rejects),
        "failed": 0,
    }
    calls_made = 0
    for session in sessions[: args.max_sessions]:
        if calls_made:
            time.sleep(args.sleep)
        outcome, called = _capture_session(
            session, state, state_dir, vault_root, args.model
        )
        if called:
            calls_made += 1
        counts[outcome] += 1
    return counts


def _generic_root(value: str) -> GenericRoot:
    name, separator, raw_path = value.partition("=")
    if not separator or not name.strip() or not raw_path.strip():
        raise argparse.ArgumentTypeError("generic root must be NAME=PATH")
    return GenericRoot(name.strip(), Path(raw_path).expanduser())


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--interval", type=float, default=DEFAULT_INTERVAL)
    parser.add_argument("--settle-seconds", type=float, default=DEFAULT_INTERVAL)
    parser.add_argument("--max-sessions", type=int, default=DEFAULT_MAX_SESSIONS)
    parser.add_argument("--sleep", type=float, default=DEFAULT_SLEEP)
    parser.add_argument("--model", default=ingest_common.DEFAULT_MODEL)
    parser.add_argument("--claude-root", type=Path, default=ingest_claude.PROJECTS_ROOT)
    parser.add_argument("--codex-root", type=Path, default=ingest_codex.SESSIONS_ROOT)
    parser.add_argument("--no-claude", action="store_true")
    parser.add_argument("--no-codex", action="store_true")
    parser.add_argument("--generic", action="append", type=_generic_root, default=[])
    args = parser.parse_args(argv)
    if args.interval <= 0 or args.settle_seconds < 0:
        parser.error("interval must be positive and settle-seconds non-negative")
    if args.max_sessions < 1 or args.sleep < 0:
        parser.error("max-sessions must be positive and sleep non-negative")
    return args


def _health_failure(state_dir: Path, error: str) -> None:
    ingest_common.write_health(
        state_dir,
        f"watcher:{error}",
        component="ingest",
        health_name=ingest_common.HEALTH_NAME,
    )


def _clear_watcher_failure(state_dir: Path) -> None:
    path = ingest_common.health_path(state_dir)
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, json.JSONDecodeError):
        return
    if isinstance(payload, dict) and str(payload.get("error", "")).startswith(
        "watcher:"
    ):
        ingest_common.write_health(
            state_dir,
            "",
            component="ingest",
            health_name=ingest_common.HEALTH_NAME,
        )


def main(argv: Sequence[str] | None = None) -> int:
    if os.environ.get("BEYIN_INVOKED_BY"):
        return 0
    state_dir = ingest_common.STATE_DIR
    try:
        args = _parse_args(argv)
        while True:
            sweep(args, state_dir=state_dir, vault_root=ingest_common.VAULT_ROOT)
            _clear_watcher_failure(state_dir)
            if args.once:
                return 0
            time.sleep(args.interval)
    except KeyboardInterrupt:
        return 0
    except SystemExit as exc:
        if exc.code:
            _health_failure(state_dir, "invalid-arguments")
        return 0
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        _health_failure(state_dir, f"input:{str(exc) or exc.__class__.__name__}")
        return 0
    except Exception as exc:
        _health_failure(state_dir, f"unexpected:{exc.__class__.__name__}")
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
