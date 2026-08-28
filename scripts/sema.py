#!/usr/bin/env python3
"""Concept-note frontmatter schema, enforced at the compiler's promotion gate.

``rootmap.load_concepts`` and ``retrieve.read_concept`` are deliberately
tolerant — a note with broken frontmatter still loads, with its title degraded
to the filename and empty tags — because the existing corpus is imperfect and
must keep working. This module does not change that. It stops **new** damage:
a staged note that misses the schema is held back instead of promoted.

Nothing here repairs anything, and nothing here blocks the live vault.
``survey_concepts`` reads the corpus and reports; it never writes. Inventing a
missing ``created`` date would invent a fact, so a bad note is named, not fixed.

The frontmatter dialect is rootmap's: ``_unquote`` and ``_inline_list`` are
imported rather than re-implemented, so the gate cannot drift from the reader it
guards. The line grammar below is stricter than rootmap's on purpose — rootmap
skips a line it cannot read, and a gate that skips is not a gate.

yazan: claude
model: opus-5
"""

from __future__ import annotations

import datetime as dt
from pathlib import Path
import re
from typing import Any, Iterable

import rootmap


FRONTMATTER = re.compile(r"\A---\s*\n(.*?)\n---\s*(?:\n|\Z)", re.DOTALL)
_KEY_LINE = re.compile(r"\A(?P<key>[A-Za-z_][A-Za-z0-9_.-]*)[ \t]*:(?P<value>.*)\Z")
_LIST_ITEM = re.compile(r"\A[ \t]+-[ \t]*(?P<item>.*)\Z")
_ISO_DAY = re.compile(r"\A\d{4}-\d{2}-\d{2}\Z")

TEXT_KEYS = ("title",)
DATE_KEYS = ("created", "updated")
LIST_KEYS = ("tags", "aliases", "sources")
REQUIRED_KEYS = TEXT_KEYS + DATE_KEYS + LIST_KEYS

# How many offending notes the read-only survey names before it stops listing.
SURVEY_SAMPLE = 5


def _is_iso_day(value: str) -> bool:
    """``YYYY-MM-DD`` and a date that exists — 2026-02-31 is neither."""
    if not _ISO_DAY.match(value):
        return False
    try:
        dt.date.fromisoformat(value)
    except ValueError:
        return False
    return True


def _parse_block(block: str, name: str) -> tuple[dict[str, Any], list[str]]:
    """Parse the frontmatter body strictly; return values plus parse problems.

    Duplicate keys are reported and the last value wins, because the note is
    being refused either way and a partial parse still produces useful problems.
    """
    values: dict[str, Any] = {}
    problems: list[str] = []
    lines = block.splitlines()
    index = 0
    while index < len(lines):
        line = lines[index]
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            index += 1
            continue
        match = _KEY_LINE.match(line)
        if match is None:
            # An indented list item only means something under a key, and any
            # other shape is outside the dialect this vault writes.
            problems.append(f"frontmatter-unparsable:{name}:line-{index + 1}")
            index += 1
            continue
        key = match.group("key")
        raw = match.group("value").strip()
        if key in values:
            problems.append(f"duplicate-key:{name}:{key}")
        if raw.startswith("["):
            if not raw.endswith("]"):
                # rootmap reads an unterminated inline list as empty; here that
                # would silently pass `tags: [a, b` off as a valid empty list.
                problems.append(f"frontmatter-unparsable:{name}:line-{index + 1}")
                index += 1
                continue
            values[key] = rootmap._inline_list(raw)
        elif raw:
            values[key] = rootmap._unquote(raw)
        else:
            items: list[str] = []
            cursor = index + 1
            while cursor < len(lines):
                item = _LIST_ITEM.match(lines[cursor])
                if item is None:
                    break
                items.append(rootmap._unquote(item.group("item")))
                cursor += 1
            values[key] = items
            index = cursor - 1
        index += 1
    return values, problems


def validate_concept(text: str, path: Path) -> list[str]:
    """Return the schema problems in one concept note; empty means valid.

    Messages carry the note's filename the way ``rootmap`` and ``retrieve``
    already report theirs (``frontmatter-missing:<file>``), so a problem list
    stays readable after it is copied into a quarantine sidecar or a survey.
    """
    name = Path(path).name
    match = FRONTMATTER.match(text)
    if match is None:
        return [f"frontmatter-missing:{name}"]

    values, problems = _parse_block(match.group(1), name)

    for key in REQUIRED_KEYS:
        if key not in values:
            problems.append(f"key-missing:{name}:{key}")

    title = values.get("title")
    if title is not None:
        if isinstance(title, str):
            if not title.strip():
                problems.append(f"title-empty:{name}")
        elif isinstance(title, list) and not title:
            # `title:` with nothing after it parses as an empty block list.
            problems.append(f"title-empty:{name}")
        else:
            problems.append(f"title-not-a-string:{name}")

    for key in DATE_KEYS:
        value = values.get(key)
        if value is None:
            continue
        if not isinstance(value, str) or not _is_iso_day(value):
            problems.append(f"date-invalid:{name}:{key}")

    for key in LIST_KEYS:
        value = values.get(key)
        if value is None:
            continue
        if not isinstance(value, list):
            # A bare scalar where a list belongs: `tags: deneme`.
            problems.append(f"not-a-list:{name}:{key}")
    # List items need no separate string check: both list paths above are
    # built from `_inline_list` and `_unquote`, which only ever yield strings.

    if not text[match.end() :].strip():
        problems.append(f"body-empty:{name}")

    return problems


def survey_concepts(
    concepts_dir: Path,
    sample_limit: int = SURVEY_SAMPLE,
) -> dict[str, Any]:
    """Count how many live notes would fail validation today. Read-only.

    This is a census, not a gate: the corpus predates the schema and is never
    modified, blocked or repaired on the strength of what this returns.
    """
    checked = 0
    invalid: list[dict[str, Any]] = []
    directory = Path(concepts_dir)
    paths: Iterable[Path] = ()
    if directory.is_dir():
        paths = sorted(directory.glob("*.md"), key=lambda item: item.name)
    for path in paths:
        checked += 1
        try:
            text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeError) as exc:
            invalid.append(
                {"note": path.name, "problems": [f"unreadable:{path.name}:{exc.__class__.__name__}"]}
            )
            continue
        problems = validate_concept(text, path)
        if problems:
            invalid.append({"note": path.name, "problems": problems})
    return {
        "checked": checked,
        "invalid": len(invalid),
        "sample": invalid[: max(0, sample_limit)],
    }
