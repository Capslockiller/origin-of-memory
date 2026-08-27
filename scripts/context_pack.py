#!/usr/bin/env python3
"""Compose persistent-memory context for manual use in consumer web chats."""

# yazan: codex · model: gpt-5.6-sol

from __future__ import annotations

import argparse
import os
from pathlib import Path
import sqlite3
import subprocess
from typing import Sequence

import retrieve


SCRIPT_DIR = Path(__file__).resolve().parent
VAULT_ROOT = SCRIPT_DIR.parent.parent
HEADER = "# Persistent memory context\nPaste this above your question."
NO_INDEX = "_Root map unavailable: knowledge/index.md was not found._"
NO_DATABASE = "_Retrieval index unavailable; only the root map is included._"
NO_MATCHES = "_No relevant memory notes were found._"


def compose_context(
    question: str,
    *,
    vault_root: Path = VAULT_ROOT,
    limit: int = 3,
    include_map: bool = True,
) -> str:
    """Return a capped Markdown context block for ``question``."""
    if not 1 <= limit <= 5:
        raise ValueError("note-count-out-of-range")
    root = Path(vault_root)
    sections = [HEADER]

    if include_map:
        index_path = root / "knowledge" / "index.md"
        try:
            root_map = index_path.read_text(encoding="utf-8").strip()
        except (FileNotFoundError, OSError, UnicodeError):
            root_map = NO_INDEX
        sections.append("## Root map\n\n" + root_map)

    db_path = root / ".claude" / "scripts" / ".state" / retrieve.DB_NAME
    if not db_path.is_file():
        sections.append(NO_DATABASE)
        return "\n\n".join(sections) + "\n"

    try:
        result = retrieve.hook_result(question, limit=limit, db_path=db_path)
    except (OSError, sqlite3.Error):
        sections.append(NO_DATABASE)
        return "\n\n".join(sections) + "\n"

    notes = result["notes"]
    if not notes:
        sections.append("## Relevant notes\n\n" + NO_MATCHES)
    else:
        rendered = []
        for note in notes:
            rendered.append(
                f"### knowledge/concepts/{note['name']}.md\n\n{note['body']}"
            )
        sections.append("## Relevant notes\n\n" + "\n\n".join(rendered))
    return "\n\n".join(sections) + "\n"


def copy_to_clipboard(block: str) -> None:
    """Send Unicode text to Windows ``clip.exe`` as UTF-16LE bytes."""
    try:
        result = subprocess.run(
            ["clip.exe"],
            input=block.encode("utf-16le"),
            check=False,
        )
    except OSError as exc:
        raise RuntimeError("clipboard-unavailable") from exc
    if result.returncode != 0:
        raise RuntimeError(f"clipboard-exit-{result.returncode}")


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("question")
    parser.add_argument("--clip", action="store_true")
    parser.add_argument("--no-map", action="store_true")
    parser.add_argument("-k", type=int, default=3, metavar="N")
    parser.add_argument("--vault", type=Path, default=VAULT_ROOT)
    args = parser.parse_args(argv)
    if not 1 <= args.k <= 5:
        parser.error("-k must be between 1 and 5")
    return args


def main(argv: Sequence[str] | None = None) -> int:
    if os.environ.get("BEYIN_INVOKED_BY"):
        return 0
    args = _parse_args(argv)
    block = compose_context(
        args.question,
        vault_root=args.vault,
        limit=args.k,
        include_map=not args.no_map,
    )
    if args.clip:
        copy_to_clipboard(block)
    else:
        print(block, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
