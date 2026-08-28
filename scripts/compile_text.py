"""Tool-free compile: parse a delimited model answer and write the files ourselves.

In tool mode the model holds Read/Write/Edit inside a staging tree and we audit
what it did afterwards. That works, but it costs the whole `--tools` and
`--permission-mode` surface, and it is the single reason the local backends
cannot compile at all: none of them can scope filesystem writes per call.

Here the model returns text and *our* code writes the files. Everything
downstream of the write — manifest diff, path allowlist, directive quarantine,
secret guard, atomic promotion — is untouched and keeps auditing exactly as
before. Only the hand on the pen changes.

The contract:

    === FILE: knowledge/concepts/slug.md ===
    <complete file content>
    === END FILE ===
    === DONE ===

Markdown delimiters rather than JSON on purpose. The payload is already
markdown with wikilinks and Turkish prose; JSON escaping would add a failure
mode where one bad escape discards the entire answer. Here a damaged block
costs only that block, and the rest still promotes.
"""

# yazan: odena · claude-opus-5

from __future__ import annotations

from pathlib import Path, PurePosixPath
import re
from typing import Iterable, NamedTuple

FILE_OPEN = re.compile(r"^===[ \t]*FILE:[ \t]*(?P<path>.+?)[ \t]*===[ \t]*$", re.MULTILINE)
FILE_CLOSE = re.compile(r"^===[ \t]*END[ \t]+FILE[ \t]*===[ \t]*$", re.MULTILINE)
TERMINAL = re.compile(r"^===[ \t]*(?P<marker>DONE|MORE)[ \t]*===[ \t]*$", re.MULTILINE)

# One page of Turkish prose is a few thousand characters; a hundred times that
# is not an article, it is a runaway generation and it should not reach disk.
MAX_BLOCK_CHARS = 200_000
MAX_BLOCKS = 40


class ParseError(ValueError):
    """The answer could not be read as a file-block transcript at all."""


class Block(NamedTuple):
    path: str
    content: str


class Parsed(NamedTuple):
    blocks: list[Block]
    marker: str | None          # "DONE", "MORE", or None when it was truncated
    dropped: list[str]          # human-readable reasons, one per rejected block

    @property
    def wants_more(self) -> bool:
        return self.marker == "MORE"

    @property
    def truncated(self) -> bool:
        return self.marker is None


def _normalise_path(raw: str) -> str:
    """Return a vault-relative POSIX path, or raise for anything escaping it.

    Rejection here is defence in depth: `_validate_manifest_diff` would also
    catch a forbidden path after the fact, but a `..` segment would have already
    written outside the staging tree by then.
    """
    candidate = raw.strip().strip('"').strip("'").replace("\\", "/")
    if not candidate:
        raise ValueError("empty-path")
    if candidate.startswith("/") or re.match(r"^[A-Za-z]:", candidate):
        raise ValueError("absolute-path")
    pure = PurePosixPath(candidate)
    if any(part in {"..", ""} or part.startswith("~") for part in pure.parts):
        raise ValueError("path-traversal")
    if pure.is_absolute():
        raise ValueError("absolute-path")
    return pure.as_posix()


def parse(answer: str, *, is_allowed: callable) -> Parsed:
    """Read a model answer into validated file blocks.

    ``is_allowed`` is the caller's path allowlist — the same predicate the
    manifest diff uses, passed in so this module owns no policy of its own.
    """
    if not answer or not answer.strip():
        raise ParseError("empty-answer")

    opens = list(FILE_OPEN.finditer(answer))
    if not opens:
        raise ParseError("no-file-blocks")

    terminal_match = None
    for match in TERMINAL.finditer(answer):
        terminal_match = match
    marker = terminal_match.group("marker") if terminal_match else None

    blocks: list[Block] = []
    dropped: list[str] = []
    seen: set[str] = set()

    for index, opening in enumerate(opens):
        raw_path = opening.group("path")
        body_start = opening.end() + 1 if answer[opening.end() : opening.end() + 1] == "\n" else opening.end()
        limit = opens[index + 1].start() if index + 1 < len(opens) else len(answer)
        closing = FILE_CLOSE.search(answer, body_start, limit)
        if closing is None:
            # A block with no terminator is a truncated generation. Dropping
            # only this one is the whole reason the format is delimiters.
            dropped.append(f"unterminated:{raw_path.strip()[:80]}")
            continue

        try:
            relative = _normalise_path(raw_path)
        except ValueError as exc:
            dropped.append(f"{exc.args[0]}:{raw_path.strip()[:80]}")
            continue
        if not is_allowed(relative):
            dropped.append(f"forbidden-path:{relative}")
            continue
        if relative in seen:
            # Two versions of one file in a single answer: we cannot know which
            # the model meant, and picking one silently would be a coin flip.
            dropped.append(f"duplicate-path:{relative}")
            continue

        content = answer[body_start : closing.start()]
        if len(content) > MAX_BLOCK_CHARS:
            dropped.append(f"oversized:{relative}:{len(content)}")
            continue
        if not content.strip():
            dropped.append(f"empty-content:{relative}")
            continue

        seen.add(relative)
        blocks.append(Block(relative, content))
        if len(blocks) > MAX_BLOCKS:
            dropped.append(f"block-limit:{MAX_BLOCKS}")
            blocks.pop()
            break

    if not blocks:
        raise ParseError("no-usable-blocks:" + (dropped[0] if dropped else "unknown"))
    return Parsed(blocks, marker, dropped)


def _normalise_content(content: str) -> str:
    text = content.replace("\r\n", "\n").replace("\r", "\n")
    return text.strip("\n") + "\n"


def apply_blocks(stage: Path, blocks: Iterable[Block]) -> list[str]:
    """Write parsed blocks into the staging tree and return the paths written.

    Writes are confined to ``stage`` by construction and re-checked after
    resolution, so a symlink planted in the tree cannot redirect one outside.
    """
    stage = Path(stage).resolve()
    written: list[str] = []
    for block in blocks:
        destination = (stage / block.path).resolve()
        try:
            destination.relative_to(stage)
        except ValueError:
            raise ParseError(f"escaped-stage:{block.path}") from None
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(
            _normalise_content(block.content), encoding="utf-8", newline="\n"
        )
        written.append(block.path)
    return written
