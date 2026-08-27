#!/usr/bin/env python3
"""claude.ai resmî dışa aktarım ZIP'inden web sohbetlerini okur.

Tek meşru yol budur: ``.import\\`` klasörüne bırakılan ZIP içindeki
``conversations.json``. Arşiv adları zip-slip'e karşı denetlenir, açılmış
boyut tavanı aşılırsa koşu istisna atmadan sağlık dosyasına yazılır.
"""

from __future__ import annotations

import io
import json
from pathlib import Path
from typing import Any
import zipfile

import flush
import ingest_common
from ingest_common import Session


SOURCE = "web"
UPDATED_LABEL = "web (güncellenmiş)"
IMPORT_DIR = ingest_common.VAULT_ROOT / ".import"
MAX_UNCOMPRESSED_BYTES = 512 * 1024 * 1024
DEFAULT_MAX_CONVERSATIONS = 50
MEMBER_NAME = "conversations.json"


def newest_zip(import_dir: Path | None = None) -> Path | None:
    import_dir = IMPORT_DIR if import_dir is None else import_dir
    if not import_dir.exists():
        return None
    archives = [path for path in import_dir.glob("*.zip") if path.is_file()]
    if not archives:
        return None
    return max(archives, key=lambda path: path.stat().st_mtime)


def _member_is_safe(name: str) -> bool:
    normalized = name.replace("\\", "/")
    if normalized.startswith("/") or normalized.startswith("//"):
        return False
    if len(normalized) > 1 and normalized[1] == ":":
        return False
    return ".." not in normalized.split("/")


def load_conversations(zip_path: Path) -> tuple[list[dict[str, Any]], str]:
    """(sohbetler, hata) — hata boş değilse sohbet listesi boştur."""
    try:
        with zipfile.ZipFile(zip_path) as archive:
            members = archive.infolist()
            for info in members:
                if not _member_is_safe(info.filename):
                    return [], f"zip-slip:{info.filename}"
            target = None
            for info in members:
                if info.is_dir():
                    continue
                if Path(info.filename.replace("\\", "/")).name == MEMBER_NAME:
                    target = info
                    break
            if target is None:
                return [], "conversations-json-missing"
            if target.file_size > MAX_UNCOMPRESSED_BYTES:
                return [], "conversations-too-large"
            with archive.open(target) as raw:
                stream = io.TextIOWrapper(raw, encoding="utf-8")
                data = json.load(stream)
    except (OSError, zipfile.BadZipFile) as exc:
        return [], f"zip-unreadable:{exc.__class__.__name__}"
    except (ValueError, json.JSONDecodeError):
        return [], "conversations-json-invalid"
    if not isinstance(data, list):
        return [], "conversations-not-list"
    return [item for item in data if isinstance(item, dict)], ""


def _message_text(message: dict[str, Any]) -> str:
    text = message.get("text")
    if isinstance(text, str) and text.strip():
        return text
    return flush._text_from_content(message.get("content"))


def _turns(conversation: dict[str, Any]) -> tuple[list[tuple[str, str]], str]:
    messages = conversation.get("chat_messages")
    if not isinstance(messages, list):
        return [], ""
    turns: list[tuple[str, str]] = []
    last_created = ""
    for message in messages:
        if not isinstance(message, dict):
            continue
        sender = message.get("sender")
        if sender == "human":
            role = "user"
        elif sender == "assistant":
            role = "assistant"
        else:
            continue
        text = ingest_common.collapse(_message_text(message))
        if not text:
            continue
        turns.append((role, text))
        created = message.get("created_at")
        if isinstance(created, str) and created:
            last_created = created
    return turns, last_created


def candidates(
    state: dict[str, Any],
    zip_path: Path,
    retry_failed: bool = False,
    resummarize: bool = False,
    max_conversations: int = DEFAULT_MAX_CONVERSATIONS,
) -> tuple[list[Session], str]:
    conversations, error = load_conversations(zip_path)
    if error:
        return [], error

    sessions: list[Session] = []
    for conversation in conversations:
        if len(sessions) >= max_conversations:
            break
        uuid = conversation.get("uuid")
        if not isinstance(uuid, str) or not uuid:
            continue
        updated = conversation.get("updated_at") or conversation.get("created_at")
        updated_text = updated if isinstance(updated, str) else ""
        label = ""
        entry = ingest_common.done_entry(state, SOURCE, uuid)
        if ingest_common.should_skip(entry, retry_failed):
            # Filigran büyümüş olsa bile varsayılan davranış ATLAMAK.
            if not resummarize:
                continue
            stored = str((entry or {}).get("watermark", ""))
            if not updated_text or updated_text <= stored:
                continue
            label = UPDATED_LABEL
        turns, last_created = _turns(conversation)
        when = ingest_common.to_local(last_created)
        if when is None:
            when = ingest_common.to_local(conversation.get("created_at"))
        if when is None:
            when = ingest_common.to_local(updated_text)
        if when is None:
            continue
        title = conversation.get("name")
        origin = ingest_common.collapse(title if isinstance(title, str) else "")
        sessions.append(
            Session(
                source=SOURCE,
                key=uuid,
                when=when,
                turns=turns,
                origin=origin[:48] or "(başlıksız)",
                watermark=updated_text,
                model="",
                label=label,
            )
        )
    return sessions, ""
