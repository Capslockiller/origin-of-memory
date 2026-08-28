"""Test yardımcıları: yol köprüsü, sentetik fikstürler, model vekilleri.

Hiçbir test ağa ya da gerçek modele çıkmaz; ``flush._run_claude`` her zaman
vekille değiştirilir.
"""

from __future__ import annotations

import json
from pathlib import Path
import sys


SCRIPTS_DIR = Path(__file__).resolve().parent.parent
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))
TOOLS_DIR = SCRIPTS_DIR.parent / "tools"
if TOOLS_DIR.is_dir() and str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))


GOOD_SUMMARY = (
    "## Bağlam\nOturum arşivden geldi.\n"
    "## Önemli Konuşmalar\nKısa bir konuşma.\n"
    "## Alınan Kararlar\nKarar yok.\n"
    "## Öğrenilenler\nBir şey öğrenildi.\n"
    "## Yapılacaklar\nYapılacak yok.\n"
)

BEGIN_MARKER = "--- BEGIN UNTRUSTED TRANSCRIPT DATA ---"
END_MARKER = "--- END UNTRUSTED TRANSCRIPT DATA ---"


def canned_stub(
    _prompt: str, _vault_root: Path, _timeout: int | None = None
) -> tuple[str | None, str | None]:
    """Sabit, sözleşmeye uygun özet döndürür."""
    return GOOD_SUMMARY, None


def echo_stub(
    prompt: str, _vault_root: Path, _timeout: int | None = None
) -> tuple[str | None, str | None]:
    """Transkripti özetin gövdesine aynen kopyalar (sır sızıntısı testi için)."""
    body = prompt.split(BEGIN_MARKER, 1)[1].split(END_MARKER, 1)[0]
    return (
        "## Bağlam\n"
        + body.strip()
        + "\n## Önemli Konuşmalar\na\n## Alınan Kararlar\nb\n"
        "## Öğrenilenler\nc\n## Yapılacaklar\nd\n",
        None,
    )


def write_jsonl(path: Path, records: list[dict]) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as handle:
        for record in records:
            handle.write(json.dumps(record, ensure_ascii=False) + "\n")
    return path


def claude_record(
    role: str,
    text: str,
    timestamp: str,
    sidechain: bool = False,
    model: str | None = None,
) -> dict:
    message: dict = {"role": role, "content": [{"type": "text", "text": text}]}
    if model is not None:
        message["model"] = model
    return {
        "type": role,
        "isSidechain": sidechain,
        "timestamp": timestamp,
        "message": message,
    }


def claude_tool_result_record(timestamp: str) -> dict:
    """Yalnız tool_result taşıyan kayıt — düz metne inince boşalır ve düşer."""
    return {
        "type": "user",
        "isSidechain": False,
        "timestamp": timestamp,
        "message": {
            "role": "user",
            "content": [
                {
                    "type": "tool_result",
                    "tool_use_id": "toolu_1",
                    "content": "komut çıktısı",
                }
            ],
        },
    }


def pad_claude_transcript(path: Path, records: list[dict]) -> Path:
    """4 KB alt sınırını aşmak için dosyayı yorum satırlarıyla değil,
    gerçek ama uzun bir ilk kullanıcı turuyla doldurur."""
    padded = [
        claude_record("user", "dolgu " * 900, "2026-08-20T06:00:00.000Z"),
        *records,
    ]
    return write_jsonl(path, padded)


def codex_meta(
    session_id: str,
    thread_source: str | None = "user",
    cwd: str = "E:\\\\Workspace",
    timestamp: str = "2026-08-24T05:00:00.000Z",
) -> dict:
    payload: dict = {
        "session_id": session_id,
        "cwd": cwd,
        "timestamp": timestamp,
        "originator": "codex_work_desktop",
    }
    if thread_source is not None:
        payload["thread_source"] = thread_source
    return {"timestamp": timestamp, "type": "session_meta", "payload": payload}


def codex_message(role: str, text: str, timestamp: str) -> dict:
    block_type = "input_text" if role in {"user", "developer"} else "output_text"
    return {
        "timestamp": timestamp,
        "type": "response_item",
        "payload": {
            "type": "message",
            "role": role,
            "content": [{"type": block_type, "text": text}],
        },
    }


def codex_turn_context(model: str, timestamp: str = "2026-08-24T05:00:01.000Z") -> dict:
    return {
        "timestamp": timestamp,
        "type": "turn_context",
        "payload": {"model": model, "cwd": "E:\\\\Workspace"},
    }


def conversation(
    uuid: str,
    name: str,
    created: str,
    updated: str,
    messages: list[tuple[str, str, str]],
) -> dict:
    return {
        "uuid": uuid,
        "name": name,
        "created_at": created,
        "updated_at": updated,
        "chat_messages": [
            {"sender": sender, "text": text, "created_at": stamp}
            for sender, text, stamp in messages
        ],
    }
