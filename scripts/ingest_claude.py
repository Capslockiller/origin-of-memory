#!/usr/bin/env python3
"""Eski Claude Code transkriptlerini (``~/.claude/projects``) arşivden çeker."""

from __future__ import annotations

import datetime as dt
import fnmatch
import json
from pathlib import Path
import time
from typing import Any

import flush
import ingest_common
from ingest_common import Session


SOURCE = "arşiv-claude"
PROJECTS_ROOT = Path.home() / ".claude" / "projects"
SCRIPT_DIR = Path(__file__).resolve().parent
CONFIG_PATH = SCRIPT_DIR / "ingest-config.json"
MIN_FILE_BYTES = 4096
# Canlı flush hâlâ yazıyor olabilir; taze dosyayı arşiv saymayız.
FRESH_SECONDS = 2 * 60 * 60

BUILTIN_EXCLUDE_GLOBS = (
    "*beyin-flush-*",
    "*compile-stage*",
    "*scratchpad-compile*",
    "c--windows-system32",
)


def _load_config(path: Path = CONFIG_PATH) -> tuple[tuple[str, ...], tuple[str, ...]]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, json.JSONDecodeError):
        return (), ()
    if not isinstance(payload, dict):
        return (), ()

    def strings(key: str) -> tuple[str, ...]:
        value = payload.get(key, [])
        if not isinstance(value, list):
            return ()
        return tuple(item for item in value if isinstance(item, str) and item)

    return strings("extra_projects"), strings("exclude_globs")


EXTRA_PROJECTS, EXCLUDE_GLOBS = _load_config()


def is_noise_project(name: str) -> bool:
    lowered = name.lower()
    return any(
        fnmatch.fnmatchcase(lowered, pattern.lower())
        for pattern in BUILTIN_EXCLUDE_GLOBS + EXCLUDE_GLOBS
    )


def _record_turn(record: dict[str, Any]) -> tuple[str, str] | None:
    """flush._text_from_content semantiği: yalnız ``type=="text"`` blokları."""
    if record.get("isSidechain") is True:
        return None
    role, content = flush._message_parts(record)
    if role not in {"user", "assistant"}:
        return None
    text = ingest_common.collapse(flush._text_from_content(content))
    if not text:
        return None
    return role, text


def read_transcript(path: Path) -> tuple[list[tuple[str, str]], str, str]:
    """(turlar, son dahil edilen turun zaman damgası, kaynak model) döndürür."""
    turns: list[tuple[str, str]] = []
    last_timestamp = ""
    models: set[str] = set()
    with path.open("r", encoding="utf-8") as transcript:
        for raw_line in transcript:
            if not raw_line.strip():
                continue
            try:
                record = json.loads(raw_line)
            except json.JSONDecodeError:
                # Arşiv dosyası bozuk satır taşıyabilir; koşuyu düşürmeyiz.
                continue
            if not isinstance(record, dict):
                continue
            turn = _record_turn(record)
            if turn is None:
                continue
            message = record.get("message")
            if isinstance(message, dict):
                model = message.get("model")
                if isinstance(model, str) and model:
                    models.add(model)
            turns.append(turn)
            timestamp = record.get("timestamp")
            if isinstance(timestamp, str) and timestamp:
                last_timestamp = timestamp
    model_text = models.pop() if len(models) == 1 else ""
    return turns, last_timestamp, model_text


def _live_flushed(state_dir: Path, key: str) -> bool:
    path = flush._session_state_path(state_dir, key)
    if not path.exists():
        return False
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, json.JSONDecodeError):
        return False
    return isinstance(payload, dict) and payload.get("status") == "ok"


def candidates(
    state: dict[str, Any],
    projects_root: Path | None = None,
    state_dir: Path | None = None,
    only_project: str | None = None,
    retry_failed: bool = False,
    now_epoch: float | None = None,
) -> tuple[list[Session], list[tuple[str, str]]]:
    """(işlenecek oturumlar, deftere yazılacak ön-atlamalar) döndürür."""
    projects_root = PROJECTS_ROOT if projects_root is None else projects_root
    state_dir = ingest_common.STATE_DIR if state_dir is None else state_dir
    sessions: list[Session] = []
    skips: list[tuple[str, str]] = []
    if not projects_root.exists():
        return sessions, skips
    current = time.time() if now_epoch is None else now_epoch

    for project_dir in sorted(projects_root.iterdir()):
        if not project_dir.is_dir():
            continue
        if only_project is not None and project_dir.name != only_project:
            continue
        if is_noise_project(project_dir.name):
            continue
        for path in sorted(project_dir.glob("*.jsonl")):
            try:
                file_stat = path.stat()
            except OSError:
                continue
            if file_stat.st_size < MIN_FILE_BYTES:
                continue
            key = path.stem
            entry = ingest_common.done_entry(state, SOURCE, key)
            if ingest_common.should_skip(entry, retry_failed):
                continue
            if current - file_stat.st_mtime < FRESH_SECONDS:
                continue
            if _live_flushed(state_dir, key):
                skips.append((key, "skipped-live"))
                continue
            try:
                turns, timestamp, model = read_transcript(path)
            except OSError:
                continue
            when = ingest_common.to_local(timestamp)
            if when is None:
                when = dt.datetime.fromtimestamp(file_stat.st_mtime).astimezone()
            sessions.append(
                Session(
                    source=SOURCE,
                    key=key,
                    when=when,
                    turns=turns,
                    origin=str(path),
                    watermark="",
                    model=model,
                )
            )
    return sessions, skips


def is_extra_project(session: Session) -> bool:
    return Path(session.origin).parent.name in EXTRA_PROJECTS
