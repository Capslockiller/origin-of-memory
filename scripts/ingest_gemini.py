#!/usr/bin/env python3
# yazan: codex
# model: gpt-5.6-sol
"""Gemini canonical kayıtlarını takvim günü bazında ingest oturumlarına çevirir."""

from __future__ import annotations

import datetime as dt
import json
from pathlib import Path
from typing import Any

import ingest_common
from ingest_common import Session


SOURCE = "gemini"
RECORDS_PATH = ingest_common.VAULT_ROOT / ".stage" / "gemini" / "kayitlar.jsonl"
MAX_DAY_CHARS = 200_000


def _load_records(path: Path) -> tuple[list[dict[str, Any]], str]:
    if not path.is_file():
        return [], "gemini-kayitlar-missing"
    records: list[dict[str, Any]] = []
    try:
        with path.open("r", encoding="utf-8") as source:
            for line_number, line in enumerate(source, start=1):
                if not line.strip():
                    continue
                try:
                    value = json.loads(line)
                except json.JSONDecodeError:
                    return [], f"gemini-kayitlar-invalid:{line_number}"
                if not isinstance(value, dict):
                    return [], f"gemini-kayitlar-invalid:{line_number}"
                records.append(value)
    except OSError:
        return [], "gemini-kayitlar-unreadable"
    return records, ""


def _timestamp(value: Any) -> dt.datetime | None:
    if not isinstance(value, str) or not value:
        return None
    try:
        parsed = dt.datetime.fromisoformat(value)
    except ValueError:
        return None
    if parsed.tzinfo is None:
        return None
    return parsed


def _record_chars(record: dict[str, Any]) -> int:
    value = record.get("chars")
    if isinstance(value, int) and value >= 0:
        return value
    return len(str(record.get("soru", ""))) + len(str(record.get("cevap", "")))


def _day_parts(records: list[dict[str, Any]]) -> list[list[dict[str, Any]]]:
    parts: list[list[dict[str, Any]]] = []
    current: list[dict[str, Any]] = []
    current_chars = 0
    for record in records:
        size = _record_chars(record)
        if current and current_chars + size > MAX_DAY_CHARS:
            parts.append(current)
            current = []
            current_chars = 0
        current.append(record)
        current_chars += size
    if current:
        parts.append(current)
    return parts


def candidates(
    state: dict[str, Any],
    retry_failed: bool = False,
    records_path: Path | None = None,
) -> tuple[list[Session], str]:
    path = RECORDS_PATH if records_path is None else records_path
    values, error = _load_records(path)
    if error:
        return [], error

    dated: list[tuple[dt.datetime, dict[str, Any]]] = []
    for line_number, record in enumerate(values, start=1):
        if record.get("tip") != "soru":
            continue
        when = _timestamp(record.get("ts"))
        soru = record.get("soru")
        cevap = record.get("cevap")
        if when is None or not isinstance(soru, str) or not isinstance(cevap, str):
            return [], f"gemini-kayitlar-invalid:{line_number}"
        dated.append((when, record))
    dated.sort(key=lambda item: item[0])

    by_day: dict[str, list[dict[str, Any]]] = {}
    for when, record in dated:
        by_day.setdefault(when.date().isoformat(), []).append(record)

    sessions: list[Session] = []
    for day in sorted(by_day):
        for part_number, part in enumerate(_day_parts(by_day[day]), start=1):
            key = f"gemini:{day}"
            if part_number > 1:
                key += f"#{part_number}"
            if ingest_common.should_skip(
                ingest_common.done_entry(state, SOURCE, key),
                retry_failed,
            ):
                continue
            first_when = _timestamp(part[0].get("ts"))
            assert first_when is not None
            turns: list[tuple[str, str]] = []
            for record in part:
                turns.append(("user", str(record["soru"])))
                turns.append(("assistant", str(record["cevap"])))
            sessions.append(
                Session(
                    source=SOURCE,
                    key=key,
                    when=first_when,
                    turns=turns,
                    origin=str(path),
                    watermark=str(part[-1].get("ts", "")),
                    model="",
                    label="gemini",
                )
            )
    return sessions, ""
