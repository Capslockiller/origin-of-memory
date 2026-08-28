"""Mirror the root map into the context files other agents already read.

Per-message injection needs a prompt hook, and only some harnesses offer one.
Everything else can still see the memory if the map is written where the agent
looks anyway: AGENTS.md, GEMINI.md, CLAUDE.md. This module owns one delimited
block inside those files and nothing else.

Two hard rules, both load-bearing:

* A file that does not exist is never created. The file's existence is the
  user's consent; the wizard is where a new one gets opened, not here.
* Nothing outside the delimited block is ever rewritten. A file whose markers
  are unbalanced is left untouched and reported, because a half-marker means
  someone edited the block by hand and guessing would destroy their text.
"""

# yazan: odena · claude-opus-5

from __future__ import annotations

import os
from pathlib import Path
import re
import sys
import tempfile
from typing import Any, Iterable

SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import secret_guard
from beyin_ortak import write_health

VAULT_ROOT = SCRIPT_DIR.parent.parent

DEFAULT_TARGETS = ("AGENTS.md", "GEMINI.md", "CLAUDE.md")

START_PREFIX = "<!-- beyin:start"
END_MARKER = "<!-- beyin:end -->"
START_MARKER = (
    "<!-- beyin:start (generated - refreshed on every compile;"
    " edits inside this block are overwritten) -->"
)

_START_LINE = re.compile(r"^[ \t]*<!--[ \t]*beyin:start\b.*?-->[ \t]*$", re.MULTILINE)
_END_LINE = re.compile(r"^[ \t]*<!--[ \t]*beyin:end[ \t]*-->[ \t]*$", re.MULTILINE)
_FRONTMATTER = re.compile(r"\A---\s*\n.*?\n---\s*(?:\n|\Z)", re.DOTALL)

# A blockquote, not a heading: this block is a guest inside someone else's
# document and must not outrank the host's own structure.
HEADER_LINES = (
    "> **Memory - root map.** Entry layer of a local knowledge base compiled",
    "> from past sessions; hubs and the full concept table sit beside it in the",
    "> vault. For a specific question, search instead of reading it all:",
    "> `python .claude/scripts/retrieve.py \"<query>\"` - or the `memory_search`",
    "> tool if this agent speaks MCP.",
)

_HEADING = re.compile(r"^(#{1,6})(?=\s)", re.MULTILINE)


def _demote_headings(text: str) -> str:
    """Push every heading down one level so the map nests under its host."""
    return _HEADING.sub(
        lambda match: match.group(1) + "#" if len(match.group(1)) < 6 else match.group(1),
        text,
    )


class BridgeError(RuntimeError):
    """A context file could not be refreshed safely."""


def _strip_frontmatter(text: str) -> str:
    return _FRONTMATTER.sub("", text, count=1).lstrip("\n")


def build_block(root_map_text: str) -> str:
    """Render the delimited block for a given root map."""
    body = _demote_headings(_strip_frontmatter(root_map_text).rstrip("\n"))
    if not body:
        raise BridgeError("root-map-empty")
    lines = [START_MARKER, "", *HEADER_LINES, "", body, "", END_MARKER]
    return "\n".join(lines) + "\n"


def _splice(existing: str, block: str) -> tuple[str, str]:
    """Return (new_text, action) for one context file.

    Actions: ``updated`` (block replaced), ``inserted`` (appended for the first
    time), ``unchanged`` (byte-identical block already in place).
    """
    starts = list(_START_LINE.finditer(existing))
    ends = list(_END_LINE.finditer(existing))
    if len(starts) > 1 or len(ends) > 1:
        raise BridgeError("marker-duplicated")
    if bool(starts) != bool(ends):
        raise BridgeError("marker-unbalanced")

    if not starts:
        # First run on a file the user opened for us. Appending is the only way
        # to establish the block; every later run stays inside it.
        prefix = existing if existing.endswith("\n") or not existing else existing + "\n"
        separator = "\n" if prefix.strip() else ""
        return prefix + separator + block, "inserted"

    start, end = starts[0], ends[0]
    if end.start() < start.start():
        raise BridgeError("marker-inverted")
    current = existing[start.start() : end.end()]
    replacement = block.rstrip("\n")
    if current == replacement:
        return existing, "unchanged"
    return existing[: start.start()] + replacement + existing[end.end() :], "updated"


def _atomic_write(path: Path, text: str) -> None:
    handle, temporary = tempfile.mkstemp(dir=str(path.parent), suffix=".beyin-tmp")
    try:
        with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(text)
        os.replace(temporary, path)
    except BaseException:
        Path(temporary).unlink(missing_ok=True)
        raise


def refresh(
    vault_root: Path = VAULT_ROOT,
    state_dir: Path | None = None,
    targets: Iterable[str] | None = None,
) -> dict[str, Any]:
    """Write the current root map into every context file that exists.

    Returns a per-file report. Raises :class:`BridgeError` only when the root
    map itself is unusable; a single unsafe target is reported, not fatal, so
    one hand-edited file cannot stop the others from refreshing.
    """
    vault_root = Path(vault_root)
    state_dir = (
        Path(state_dir)
        if state_dir is not None
        else vault_root / ".claude" / "scripts" / ".state"
    )
    names = tuple(targets) if targets is not None else DEFAULT_TARGETS

    root_map_path = vault_root / "knowledge" / "index.md"
    try:
        root_map_text = root_map_path.read_text(encoding="utf-8")
    except OSError as exc:
        raise BridgeError("root-map-unreadable") from exc

    block = build_block(root_map_text)
    # The map is built from notes that already passed the guard, so a finding
    # here means an earlier gate leaked. Refuse rather than fan it out into
    # files other agents read.
    findings = secret_guard.scan(block)
    if findings:
        raise BridgeError(f"secret-detected:{findings[0]}")

    report: dict[str, str] = {}
    failures: list[str] = []
    for name in names:
        path = vault_root / name
        if not path.is_file():
            report[name] = "missing"
            continue
        try:
            existing = path.read_text(encoding="utf-8")
            new_text, action = _splice(existing, block)
            if action != "unchanged":
                _atomic_write(path, new_text)
            report[name] = action
        except (BridgeError, OSError) as exc:
            detail = exc.args[0] if isinstance(exc, BridgeError) else "write-failed"
            report[name] = f"skipped:{detail}"
            failures.append(f"{name}:{detail}")

    if failures:
        write_health(
            state_dir,
            "context-bridge:" + ",".join(failures),
            warning=True,
            component="context-bridge",
        )
    else:
        write_health(state_dir, "", component="context-bridge")
    return {"block_chars": len(block), "targets": report}


def enabled() -> bool:
    """The bridge writes outside the vault's own notes, so it stays togglable."""
    return os.environ.get("BEYIN_CONTEXT_BRIDGE", "on").strip().lower() not in {
        "off",
        "0",
        "false",
        "no",
    }


def main(argv: list[str] | None = None) -> int:
    import argparse
    import json

    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--vault", default=str(VAULT_ROOT))
    parser.add_argument(
        "--target",
        action="append",
        help="context file name, repeatable (default: AGENTS.md GEMINI.md CLAUDE.md)",
    )
    args = parser.parse_args(argv)
    if not enabled():
        print(json.dumps({"skipped": "BEYIN_CONTEXT_BRIDGE=off"}, ensure_ascii=False))
        return 0
    report = refresh(vault_root=Path(args.vault), targets=args.target)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
