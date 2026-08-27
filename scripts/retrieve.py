#!/usr/bin/env python3
"""Build and query the deterministic FTS5 memory-retrieval index."""

# yazan: codex · gpt-5.6-sol

from __future__ import annotations

import argparse
import csv
import datetime as dt
from dataclasses import dataclass
import json
import math
import os
from pathlib import Path
import re
import sqlite3
import tempfile
import time
from typing import Any, Sequence


SCRIPT_DIR = Path(__file__).resolve().parent
VAULT_ROOT = SCRIPT_DIR.parent.parent
STATE_DIR = SCRIPT_DIR / ".state"
DB_NAME = "notes.db"
PER_NOTE_CAP = 1_500
TOTAL_BODY_CAP = 4_500
LEDGER_MAX_AGE_SECONDS = 7 * 24 * 60 * 60

_TURKISH_I = str.maketrans({"I": "ı", "İ": "i"})
_WORD = re.compile(r"[^\W_]+", re.UNICODE)
_FRONTMATTER = re.compile(
    r"\A---[ \t]*(?:\r?\n)(.*?)(?:\r?\n)---[ \t]*(?:\r?\n|\Z)",
    re.DOTALL,
)
_SAFE_SESSION = re.compile(r"[A-Za-z0-9_.-]{1,128}\Z")

BENCH_QUERIES = (
    "hafıza",
    "karar alma",
    "Odena Studio",
    "yapay zeka",
    "İstanbul",
    "proje yönetimi",
    "yaratıcı süreç",
    "ikinci beyin",
    "bilgi mimarisi",
    "kavramsal bağlantılar",
    "oyun tasarımı",
    "psikolojik korku",
    "ışık gösterisi",
    "kullanıcı deneyimi",
    "güvenlik sınırı",
    "oturum özeti",
    "kalıcı bellek",
    "üretim ortamı",
    "doğrulama testi",
    "Türkçe tokenizasyon",
)


class RetrieveError(ValueError):
    """The retrieval index or one of its source notes is invalid."""


@dataclass(frozen=True)
class ConceptNote:
    name: str
    title: str
    aliases: tuple[str, ...]
    tags: tuple[str, ...]
    body: str


@dataclass(frozen=True)
class SearchHit:
    name: str
    title: str
    body: str
    score: float


def turkish_fold(value: str) -> str:
    """Fold Turkish dotted/dotless I explicitly, never through lower/upper."""
    return value.translate(_TURKISH_I).casefold()


def expanded_tokens(value: str) -> list[str]:
    """Return raw folded and F5 tokens for every word of at least 3 chars."""
    folded = turkish_fold(value)
    tokens: list[str] = []
    for word in _WORD.findall(folded):
        if len(word) < 3:
            continue
        tokens.append(word)
        if len(word) > 5:
            tokens.append(word[:5])
    return tokens


def token_text(value: str) -> str:
    """Preprocess source text into the exact token stream stored by FTS5."""
    return " ".join(expanded_tokens(value))


def _unquote(value: str) -> str:
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
        if value[0] == '"':
            try:
                decoded = json.loads(value)
                if isinstance(decoded, str):
                    return decoded
            except json.JSONDecodeError:
                pass
        return value[1:-1].replace("''", "'")
    return value


def _inline_list(value: str) -> tuple[str, ...]:
    value = value.strip()
    if not (value.startswith("[") and value.endswith("]")):
        return ()
    inner = value[1:-1].strip()
    if not inner:
        return ()
    return tuple(
        _unquote(item)
        for item in next(csv.reader([inner], skipinitialspace=True))
        if item.strip()
    )


def _frontmatter_values(block: str) -> dict[str, Any]:
    """Parse the scalar/list subset used by atomic concept frontmatter."""
    lines = block.splitlines()
    result: dict[str, Any] = {}
    index = 0
    while index < len(lines):
        line = lines[index]
        if not line or line[:1].isspace() or ":" not in line:
            index += 1
            continue
        key, raw = line.split(":", 1)
        key = key.strip()
        raw = raw.strip()
        if raw.startswith("["):
            result[key] = _inline_list(raw)
        elif raw:
            result[key] = _unquote(raw)
        else:
            items: list[str] = []
            cursor = index + 1
            while cursor < len(lines):
                nested = lines[cursor]
                match = re.match(r"^[ \t]+-[ \t]+(.*)$", nested)
                if match is None:
                    break
                items.append(_unquote(match.group(1)))
                cursor += 1
            result[key] = tuple(items)
            index = cursor - 1
        index += 1
    return result


def read_concept(path: Path) -> ConceptNote:
    text = path.read_text(encoding="utf-8")
    match = _FRONTMATTER.match(text)
    if match is None:
        raise RetrieveError(f"frontmatter-missing:{path.name}")
    values = _frontmatter_values(match.group(1))
    title = values.get("title", path.stem)
    aliases = values.get("aliases", ())
    tags = values.get("tags", ())
    if not isinstance(title, str):
        raise RetrieveError(f"title-invalid:{path.name}")
    if isinstance(aliases, str):
        aliases = (aliases,)
    if isinstance(tags, str):
        tags = (tags,)
    if not isinstance(aliases, tuple) or not all(
        isinstance(item, str) for item in aliases
    ):
        raise RetrieveError(f"aliases-invalid:{path.name}")
    if not isinstance(tags, tuple) or not all(isinstance(item, str) for item in tags):
        raise RetrieveError(f"tags-invalid:{path.name}")
    return ConceptNote(
        name=path.stem,
        title=title,
        aliases=aliases,
        tags=tags,
        body=text[match.end() :],
    )


def _prune_ledgers(state_dir: Path, now: float | None = None) -> int:
    cutoff = (time.time() if now is None else now) - LEDGER_MAX_AGE_SECONDS
    removed = 0
    for path in state_dir.glob("retrieve-session-*.json"):
        try:
            if path.stat().st_mtime < cutoff:
                path.unlink()
                removed += 1
        except FileNotFoundError:
            continue
        except OSError:
            continue
    return removed


def _create_database(path: Path, notes: list[ConceptNote]) -> None:
    connection = sqlite3.connect(path)
    try:
        connection.executescript(
            """
            PRAGMA journal_mode=DELETE;
            PRAGMA synchronous=FULL;
            CREATE VIRTUAL TABLE notes USING fts5(
                name UNINDEXED,
                title,
                aliases,
                tags,
                body
            );
            CREATE TABLE documents(
                rowid INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                aliases TEXT NOT NULL,
                tags TEXT NOT NULL,
                body TEXT NOT NULL
            );
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """
        )
        for note in notes:
            aliases = " ".join(note.aliases)
            tags = " ".join(note.tags)
            cursor = connection.execute(
                "INSERT INTO documents(name, title, aliases, tags, body) "
                "VALUES (?, ?, ?, ?, ?)",
                (note.name, note.title, aliases, tags, note.body),
            )
            connection.execute(
                "INSERT INTO notes(rowid, name, title, aliases, tags, body) "
                "VALUES (?, ?, ?, ?, ?, ?)",
                (
                    cursor.lastrowid,
                    note.name,
                    token_text(note.title),
                    token_text(aliases),
                    token_text(tags),
                    token_text(note.body),
                ),
            )
        built_at = dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")
        connection.executemany(
            "INSERT INTO meta(key, value) VALUES (?, ?)",
            (("note_count", str(len(notes))), ("built_at", built_at)),
        )
        connection.execute("PRAGMA user_version=1")
        connection.commit()
    finally:
        connection.close()


def build_index(
    vault_root: Path = VAULT_ROOT,
    state_dir: Path | None = None,
) -> dict[str, Any]:
    """Build a complete temporary index and atomically publish it."""
    vault_root = Path(vault_root)
    target_state = Path(state_dir) if state_dir is not None else vault_root / ".claude" / "scripts" / ".state"
    concepts_dir = vault_root / "knowledge" / "concepts"
    paths = sorted(concepts_dir.glob("*.md"), key=lambda item: item.name)
    notes = [read_concept(path) for path in paths]
    target_state.mkdir(parents=True, exist_ok=True)
    pruned = _prune_ledgers(target_state)
    target = target_state / DB_NAME
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{DB_NAME}.", suffix=".tmp", dir=target_state
    )
    os.close(descriptor)
    temporary = Path(temporary_name)
    try:
        temporary.unlink()
        _create_database(temporary, notes)
        os.replace(temporary, target)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass
    return {
        "note_count": len(notes),
        "built_at": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds"),
        "db_path": str(target),
        "db_size": target.stat().st_size,
        "ledgers_pruned": pruned,
    }


def _fts_query(text: str) -> str:
    # Tokens contain only Unicode word characters, but quoting every token keeps
    # the trust boundary explicit even if tokenization changes later.
    unique = dict.fromkeys(expanded_tokens(text))
    return " OR ".join(f'"{token.replace(chr(34), chr(34) * 2)}"' for token in unique)


def _open_readonly(db_path: Path) -> sqlite3.Connection:
    connection = sqlite3.connect(db_path.resolve().as_uri() + "?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    return connection


def search(
    text: str,
    *,
    limit: int = 3,
    db_path: Path | None = None,
    connection: sqlite3.Connection | None = None,
    min_score: float = 0.0,
) -> list[SearchHit]:
    """Return ranked lexical matches; min_score is positive ``-bm25`` relevance."""
    if limit < 1:
        return []
    expression = _fts_query(text)
    if not expression:
        return []
    owns_connection = connection is None
    if connection is None:
        resolved = Path(db_path) if db_path is not None else STATE_DIR / DB_NAME
        connection = _open_readonly(resolved)
    try:
        rows = connection.execute(
            """
            SELECT documents.name, documents.title, documents.body,
                   bm25(notes, 8.0, 6.0, 3.0, 1.0) AS score
            FROM notes
            JOIN documents ON documents.rowid = notes.rowid
            WHERE notes MATCH ?
            ORDER BY score, documents.name
            """,
            (expression,),
        )
        hits: list[SearchHit] = []
        for row in rows:
            score = float(row["score"])
            if -score < min_score:
                continue
            hits.append(
                SearchHit(
                    name=str(row["name"]),
                    title=str(row["title"]),
                    body=str(row["body"]),
                    score=score,
                )
            )
            if len(hits) >= limit:
                break
        return hits
    finally:
        if owns_connection:
            connection.close()


def _ledger_path(state_dir: Path, session: str) -> Path:
    if _SAFE_SESSION.fullmatch(session) is None:
        import hashlib

        session = "sha256-" + hashlib.sha256(session.encode("utf-8")).hexdigest()
    return state_dir / f"retrieve-session-{session}.json"


def _read_ledger(path: Path) -> set[str]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (FileNotFoundError, OSError, UnicodeError, json.JSONDecodeError):
        return set()
    returned = payload.get("returned", []) if isinstance(payload, dict) else payload
    if not isinstance(returned, list):
        return set()
    return {item for item in returned if isinstance(item, str)}


def _atomic_write_json(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(payload, ensure_ascii=False, indent=2) + "\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def hook_result(
    text: str,
    *,
    limit: int = 3,
    session: str | None = None,
    min_score: float = 0.0,
    db_path: Path | None = None,
    state_dir: Path | None = None,
) -> dict[str, Any]:
    """Produce capped hook JSON and update the optional session ledger."""
    resolved_db = Path(db_path) if db_path is not None else STATE_DIR / DB_NAME
    resolved_state = Path(state_dir) if state_dir is not None else resolved_db.parent
    ledger = _ledger_path(resolved_state, session) if session is not None else None
    seen = _read_ledger(ledger) if ledger is not None else set()
    # Dedup may discard the highest-ranked rows, so examine the complete match set.
    candidates = search(
        text,
        limit=2_147_483_647,
        db_path=resolved_db,
        min_score=min_score,
    )
    notes: list[dict[str, Any]] = []
    total = 0
    returned_names: list[str] = []
    for hit in candidates:
        if hit.name in seen:
            continue
        remaining = TOTAL_BODY_CAP - total
        if remaining <= 0 or len(notes) >= limit:
            break
        body = hit.body[: min(PER_NOTE_CAP, remaining)]
        notes.append({"name": hit.name, "chars": len(body), "body": body})
        returned_names.append(hit.name)
        total += len(body)
    if ledger is not None and returned_names:
        combined = sorted(seen.union(returned_names))
        _atomic_write_json(
            ledger,
            {"updated": int(time.time()), "returned": combined},
        )
    return {"notes": notes, "total_chars": total}


def _plain_output(hits: list[SearchHit]) -> str:
    lines: list[str] = []
    for rank, hit in enumerate(hits, 1):
        preview = re.sub(r"\s+", " ", hit.body).strip()[:120]
        lines.append(f"{rank}\t{hit.name}\t{hit.score:.8f}\t{preview}")
    return "\n".join(lines)


def benchmark(db_path: Path | None = None) -> dict[str, Any]:
    resolved = Path(db_path) if db_path is not None else STATE_DIR / DB_NAME
    connection = _open_readonly(resolved)
    timings: list[dict[str, Any]] = []
    try:
        for query in BENCH_QUERIES:
            started = time.perf_counter()
            hits = search(query, limit=3, connection=connection)
            elapsed_ms = (time.perf_counter() - started) * 1_000
            timings.append(
                {
                    "query": query,
                    "ms": round(elapsed_ms, 3),
                    "results": [hit.name for hit in hits],
                }
            )
    finally:
        connection.close()
    ordered = sorted(float(item["ms"]) for item in timings)
    p95 = ordered[max(0, math.ceil(0.95 * len(ordered)) - 1)]
    return {"queries": timings, "p95_ms": round(p95, 3)}


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="mode", required=True)

    build_parser = subparsers.add_parser("build", help="atomically rebuild FTS5")
    build_parser.add_argument("--vault-root", type=Path, default=VAULT_ROOT)
    build_parser.add_argument("--state-dir", type=Path)

    query_parser = subparsers.add_parser("query", help="query the FTS5 index")
    query_parser.add_argument("text", nargs="?")
    query_parser.add_argument("--limit", type=int, default=3)
    query_parser.add_argument("--session")
    query_parser.add_argument("--format", choices=("hook", "plain"), default="plain")
    query_parser.add_argument("--min-score", type=float, default=0.0)
    query_parser.add_argument("--db", type=Path, default=STATE_DIR / DB_NAME)
    query_parser.add_argument("--bench", action="store_true")
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = _parse_args(argv)
    if args.mode == "build":
        report = build_index(vault_root=args.vault_root, state_dir=args.state_dir)
        print(json.dumps(report, ensure_ascii=False))
        return 0
    if args.bench:
        print(json.dumps(benchmark(args.db), ensure_ascii=False, indent=2))
        return 0
    if args.text is None:
        raise SystemExit("query text is required unless --bench is used")
    if args.limit < 1:
        raise SystemExit("--limit must be at least 1")
    if args.min_score < 0:
        raise SystemExit("--min-score must be non-negative")
    if args.format == "hook":
        result = hook_result(
            args.text,
            limit=args.limit,
            session=args.session,
            min_score=args.min_score,
            db_path=args.db,
        )
        print(json.dumps(result, ensure_ascii=False))
    else:
        print(
            _plain_output(
                search(
                    args.text,
                    limit=args.limit,
                    db_path=args.db,
                    min_score=args.min_score,
                )
            )
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
