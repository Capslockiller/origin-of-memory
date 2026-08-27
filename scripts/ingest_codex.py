#!/usr/bin/env python3
"""Codex rollout kayıtlarını (``~/.codex/sessions``) kasaya hazırlar.

515 MB'lık log'un yalnız kullanıcı sohbeti kısmı alınır: ilk satır
``session_meta`` olmalı ve ``payload.thread_source == "user"`` şartını
sağlamalı. Anahtar yoksa da elenir — bozuk dev dosya böyle düşer.
"""

from __future__ import annotations

import datetime as dt
import json
from pathlib import Path
from typing import Any

import ingest_common
from ingest_common import Session


SOURCE = "codex"
SESSIONS_ROOT = Path.home() / ".codex" / "sessions"
MAX_LINES = 4000
MAX_BYTES = 8 * 1024 * 1024

ENVELOPE_PREFIXES = (
    "<app-context",
    "<environment_context",
    "<user_instructions",
    "<multi_agent_mode",
    "<guardian",
    "<plan_tool_output",
)
NOISE_CWD_TOKENS = ("system32", "compile-stage", "beyin-flush")
TEXT_BLOCK_TYPES = {"input_text", "output_text"}


def _payload(record: Any) -> dict[str, Any]:
    if not isinstance(record, dict):
        return {}
    payload = record.get("payload")
    return payload if isinstance(payload, dict) else {}


def _is_noise_cwd(cwd: Any) -> bool:
    lowered = str(cwd or "").replace("\\", "/").lower()
    return any(token in lowered for token in NOISE_CWD_TOKENS)


def _message_text(payload: dict[str, Any]) -> str:
    content = payload.get("content")
    if not isinstance(content, list):
        return ""
    parts = []
    for block in content:
        if not isinstance(block, dict):
            continue
        if block.get("type") not in TEXT_BLOCK_TYPES:
            continue
        text = block.get("text")
        if isinstance(text, str):
            parts.append(text)
    return "".join(parts)


def _is_envelope(text: str) -> bool:
    head = text.lstrip().lower()
    return head.startswith(ENVELOPE_PREFIXES)


def read_rollout(path: Path) -> tuple[Session | None, str]:
    """(Session, "") ya da (None, red-gerekçesi)."""
    turns: list[tuple[str, str]] = []
    last_timestamp = ""
    model = ""
    with path.open("r", encoding="utf-8", errors="replace") as handle:
        first_line = handle.readline()
        if not first_line.strip():
            return None, "empty"
        try:
            meta = json.loads(first_line)
        except json.JSONDecodeError:
            return None, "meta-invalid"
        if not isinstance(meta, dict) or meta.get("type") != "session_meta":
            return None, "not-session-meta"
        meta_payload = _payload(meta)
        if meta_payload.get("thread_source") != "user":
            return None, "thread-source-not-user"
        session_id = meta_payload.get("session_id")
        if not isinstance(session_id, str) or not session_id:
            return None, "session-id-missing"
        if _is_noise_cwd(meta_payload.get("cwd")):
            return None, "noise-cwd"
        candidate_model = meta_payload.get("model")
        if isinstance(candidate_model, str) and candidate_model:
            model = candidate_model

        consumed = len(first_line.encode("utf-8", "replace"))
        for line_number, raw_line in enumerate(handle, start=1):
            if line_number > MAX_LINES or consumed > MAX_BYTES:
                break
            consumed += len(raw_line.encode("utf-8", "replace"))
            if not raw_line.strip():
                continue
            try:
                record = json.loads(raw_line)
            except json.JSONDecodeError:
                continue
            if not isinstance(record, dict):
                continue
            payload = _payload(record)
            if record.get("type") == "turn_context" and not model:
                turn_model = payload.get("model")
                if isinstance(turn_model, str) and turn_model:
                    model = turn_model
                continue
            if record.get("type") != "response_item":
                continue
            if payload.get("type") != "message":
                continue
            role = payload.get("role")
            if role not in {"user", "assistant"}:
                continue
            text = _message_text(payload)
            if not text or _is_envelope(text):
                continue
            text = ingest_common.collapse(text)
            if not text:
                continue
            turns.append((role, text))
            timestamp = record.get("timestamp")
            if isinstance(timestamp, str) and timestamp:
                last_timestamp = timestamp

    when = ingest_common.to_local(last_timestamp)
    if when is None:
        when = ingest_common.to_local(_payload(meta).get("timestamp"))
    if when is None:
        when = dt.datetime.fromtimestamp(path.stat().st_mtime).astimezone()
    return (
        Session(
            source=SOURCE,
            key=session_id,
            when=when,
            turns=turns,
            origin=str(path),
            watermark="",
            model=model,
        ),
        "",
    )


def candidates(
    state: dict[str, Any],
    sessions_root: Path | None = None,
    retry_failed: bool = False,
) -> tuple[list[Session], list[tuple[str, int, float, str]]]:
    """(işlenecek oturumlar, [(dosya, boyut, mtime, gerekçe)]) döndürür."""
    sessions_root = SESSIONS_ROOT if sessions_root is None else sessions_root
    sessions: list[Session] = []
    rejects: list[tuple[str, int, float, str]] = []
    if not sessions_root.exists():
        return sessions, rejects

    for path in sorted(sessions_root.glob("*/*/*/rollout-*.jsonl")):
        try:
            file_stat = path.stat()
        except OSError:
            continue
        name = path.name
        if not retry_failed and ingest_common.file_unchanged(
            state,
            SOURCE,
            name,
            file_stat.st_size,
            file_stat.st_mtime,
        ):
            continue
        try:
            session, reason = read_rollout(path)
        except OSError:
            continue
        if session is None:
            rejects.append(
                (name, file_stat.st_size, file_stat.st_mtime, f"reject:{reason}")
            )
            continue
        entry = ingest_common.done_entry(state, SOURCE, session.key)
        if ingest_common.should_skip(entry, retry_failed):
            rejects.append(
                (name, file_stat.st_size, file_stat.st_mtime, "done")
            )
            continue
        sessions.append(session)
    return sessions, rejects
