#!/usr/bin/env python3
"""Build isolated LoCoMo scratch vaults for the real compiler pipeline.

yazan: codex
model: gpt-5.6-sol
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import shutil
from typing import Any

from ortak import MODEL_ID, REPO_ROOT, ensure_subset, raw_by_sample, sorted_sessions, turn_text, vault_path


def _write_if_changed(path: Path, text: str) -> None:
    if path.is_file() and path.read_text(encoding="utf-8") == text:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.e2e.tmp")
    temporary.write_text(text, encoding="utf-8", newline="\n")
    temporary.replace(path)


def _write_if_missing(path: Path, text: str) -> None:
    if path.exists():
        return
    _write_if_changed(path, text)


def _copy_scripts(destination: Path) -> None:
    """Copy every source/support file, excluding only volatile runtime state."""
    shutil.copytree(
        REPO_ROOT / "scripts",
        destination,
        dirs_exist_ok=True,
        ignore=shutil.ignore_patterns(".state", "__pycache__", "*.pyc"),
    )
    (destination / ".state").mkdir(parents=True, exist_ok=True)


def _daily_text(sample_id: str, session: dict[str, Any]) -> str:
    stamp = session["date"]
    lines = [
        f"# Günlük Log: {stamp.date().isoformat()}",
        "",
        f"<!-- yazan: codex · {MODEL_ID} -->",
        "",
        "## Oturumlar",
        "",
        f"### Oturum ({stamp.strftime('%H:%M')})",
        "",
        (
            f"<!-- session:{sample_id}-s{session['number']:02d} "
            f"ts:{stamp.isoformat(timespec='seconds')} source:locomo -->"
        ),
        "",
    ]
    lines.extend(turn_text(turn, caption_label=True) for turn in session["turns"])
    return "\n".join(lines).rstrip() + "\n"


def _scaffold(vault: Path) -> None:
    knowledge = vault / "knowledge"
    for name in ("concepts", "connections", "hubs"):
        (knowledge / name).mkdir(parents=True, exist_ok=True)
    (vault / "daily").mkdir(parents=True, exist_ok=True)
    (vault / ".stage").mkdir(parents=True, exist_ok=True)

    _write_if_missing(
        knowledge / "index.md",
        (
            "---\n"
            "yazan: codex\n"
            f"model: {MODEL_ID}\n"
            "---\n\n"
            "# Kök Harita\n\n"
            "LoCoMo deney kasası için başlangıç haritası.\n"
        ),
    )
    _write_if_missing(
        knowledge / "index-full.md",
        (
            "---\n"
            "yazan: codex\n"
            f"model: {MODEL_ID}\n"
            "---\n\n"
            "# Bilgi Tabanı — Tam Kavram İndeksi\n\n"
            "| Makale | Özet | Kaynak | Güncellendi |\n"
            "|---|---|---|---|\n"
        ),
    )
    _write_if_missing(
        knowledge / "log.md",
        f"<!-- yazan: codex · {MODEL_ID} -->\n\n# Derleme Günlüğü\n",
    )


def build_vault(sample_id: str, entry: dict[str, Any]) -> tuple[int, int]:
    vault = vault_path(sample_id)
    scripts = vault / ".claude" / "scripts"
    _copy_scripts(scripts)
    _scaffold(vault)

    hub_config = {
        "yazan": "codex",
        "model": MODEL_ID,
        "_schema": "E2E LoCoMo scratch-vault hub definitions v1",
        "catch_all": "locomo",
        "hubs": [
            {
                "id": "locomo",
                "ad": "LoCoMo",
                "kapsam": "LoCoMo konuşmalarından derlenen deney notları",
                "tags": [],
                "title_keys": [],
            }
        ],
    }
    _write_if_changed(
        scripts / "hub-config.json",
        json.dumps(hub_config, ensure_ascii=False, indent=2) + "\n",
    )

    sessions = sorted_sessions(entry)
    for session in sessions:
        day = session["date"].date().isoformat()
        name = f"{day}__{sample_id}__s{session['number']:02d}.md"
        _write_if_changed(vault / "daily" / name, _daily_text(sample_id, session))
    return len(sessions), len(list((vault / "daily").glob("*.md")))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--only", help="build only one sample_id")
    args = parser.parse_args()

    entries = raw_by_sample()
    if args.only:
        if args.only not in entries:
            raise SystemExit(f"unknown sample_id: {args.only}")
        selected = [(args.only, entries[args.only])]
    else:
        selected = list(entries.items())

    ensure_subset()
    for sample_id, entry in selected:
        sessions, daily_files = build_vault(sample_id, entry)
        status = "ok" if sessions == daily_files else "extra-files-present"
        print(f"{sample_id}: sessions={sessions} daily_files={daily_files} status={status}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
