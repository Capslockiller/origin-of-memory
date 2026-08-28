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

# Schema 1 = name/title/aliases/tags/body.  Schema 2 adds documents.source_date,
# the note's newest source timestamp, which the recency signals below rank on.
SCHEMA_VERSION = 2

MODE_BM25 = "bm25"
MODE_RRF = "rrf"
RETRIEVAL_MODES = (MODE_BM25, MODE_RRF)
# The fused path stays opt-in until it is measured against the gold set.
DEFAULT_RETRIEVAL_MODE = MODE_BM25
RETRIEVAL_MODE_ENV = "BEYIN_RETRIEVAL"

RRF_K_ENV = "BEYIN_RRF_K"
DEFAULT_RRF_K = 60

RECENCY_HALF_LIFE_ENV = "BEYIN_RECENCY_HALFLIFE_DAYS"
DEFAULT_RECENCY_HALF_LIFE_DAYS = 180.0
# An old-but-perfect match must stay reachable, so decay is bounded below.
RECENCY_WEIGHT_FLOOR = 0.25
SECONDS_PER_DAY = 86_400.0

# Bookkeeping written by flush.py into each daily session block and carried into
# a concept note's Kaynaklar section by the compiler.  Never context: stripped
# out of the index at build time and out of every hit body at query time.
SESSION_SOURCES = ("claude", "codex", "web", "gemini")
DEFAULT_SESSION_SOURCE = "claude"
SESSION_ANCHOR = re.compile(
    r"<!--[ \t]*session:(?P<session>\S+)[ \t]+ts:(?P<ts>\S+)"
    r"[ \t]+source:(?P<source>[a-z]+)[ \t]*-->"
)
# Same shape, but line-anchored and swallowing the newline it sits on, so
# removing an anchor does not leave a blank line behind in the note body.
_ANCHOR_LINE = re.compile(
    r"(?m)^[ \t]*<!--[ \t]*session:\S+[ \t]+ts:\S+"
    r"[ \t]+source:[a-z]+[ \t]*-->[ \t]*\r?\n?"
)

_TURKISH_I = str.maketrans({"I": "ı", "İ": "i"})
_WORD = re.compile(r"[^\W_]+", re.UNICODE)
_FRONTMATTER = re.compile(
    r"\A---[ \t]*(?:\r?\n)(.*?)(?:\r?\n)---[ \t]*(?:\r?\n|\Z)",
    re.DOTALL,
)
_SAFE_SESSION = re.compile(r"[A-Za-z0-9_.-]{1,128}\Z")
_ANCHOR_ID_UNSAFE = re.compile(r"[^A-Za-z0-9_.:-]+")
# Timestamps additionally keep ``+``, or every UTC offset would render as -00:00.
_ANCHOR_TS_UNSAFE = re.compile(r"[^A-Za-z0-9_.:+-]+")

BENCH_QUERIES = (
    "hafıza",
    "karar alma",
    "iş akışı",
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


_UNSET = object()


class RetrieveError(ValueError):
    """The retrieval index or one of its source notes is invalid."""


@dataclass(frozen=True)
class ConceptNote:
    name: str
    title: str
    aliases: tuple[str, ...]
    tags: tuple[str, ...]
    body: str
    source_date: str = ""


@dataclass(frozen=True)
class SearchHit:
    name: str
    title: str
    body: str
    score: float


@dataclass(frozen=True)
class SessionAnchor:
    session: str
    timestamp: str
    source: str


def format_session_anchor(
    session: str,
    timestamp: str,
    source: str = DEFAULT_SESSION_SOURCE,
) -> str:
    """Render one provenance anchor; inputs are sanitised, never trusted."""
    identifier = _ANCHOR_ID_UNSAFE.sub("-", session).strip("-")[:128]
    if not identifier:
        import hashlib

        identifier = "sha256-" + hashlib.sha256(
            session.encode("utf-8")
        ).hexdigest()[:32]
    stamp = _ANCHOR_TS_UNSAFE.sub("-", timestamp).strip("-")[:64] or "unknown"
    kind = source if source in SESSION_SOURCES else DEFAULT_SESSION_SOURCE
    return f"<!-- session:{identifier} ts:{stamp} source:{kind} -->"


def parse_session_anchors(text: str) -> list[SessionAnchor]:
    """Return every provenance anchor found in ``text``, in document order."""
    return [
        SessionAnchor(
            session=match.group("session"),
            timestamp=match.group("ts"),
            source=match.group("source"),
        )
        for match in SESSION_ANCHOR.finditer(text)
    ]


def strip_session_anchors(text: str) -> str:
    """Remove provenance anchors, and the lines they occupy on their own."""
    if "<!--" not in text:
        return text
    # Whole-line anchors go first, newline included; anything left is an inline
    # anchor sharing a line with real prose, so only the comment is removed.
    cleaned = _ANCHOR_LINE.sub("", text)
    return SESSION_ANCHOR.sub("", cleaned)


def _parse_timestamp(value: str) -> dt.datetime | None:
    """Parse an ISO8601 date or datetime; naive values are read as UTC."""
    text = value.strip()
    if not text:
        return None
    try:
        parsed = dt.datetime.fromisoformat(text)
    except ValueError:
        try:
            parsed = dt.datetime.combine(
                dt.date.fromisoformat(text[:10]), dt.time()
            )
        except ValueError:
            return None
    if parsed.tzinfo is None:
        return parsed.replace(tzinfo=dt.timezone.utc)
    return parsed.astimezone(dt.timezone.utc)


def _normalize_timestamp(value: str) -> str:
    parsed = _parse_timestamp(value)
    return "" if parsed is None else parsed.isoformat(timespec="seconds")


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


def _resolve_source_date(
    path: Path,
    values: dict[str, Any],
    body: str,
) -> str:
    """Newest source date for the recency signal, best evidence first.

    Session anchors carry a real event timestamp, so they win.  Without them
    the note's own frontmatter is next (``updated``/``modified`` before
    ``created``), and file mtime is the last resort.
    """
    stamps = [
        _normalize_timestamp(anchor.timestamp)
        for anchor in parse_session_anchors(body)
    ]
    stamps = [stamp for stamp in stamps if stamp]
    if stamps:
        return max(stamps)
    for key in ("updated", "modified", "created"):
        raw = values.get(key)
        if isinstance(raw, str):
            normalized = _normalize_timestamp(raw)
            if normalized:
                return normalized
    try:
        mtime = path.stat().st_mtime
    except OSError:
        return ""
    return dt.datetime.fromtimestamp(mtime, dt.timezone.utc).isoformat(
        timespec="seconds"
    )


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
    raw_body = text[match.end() :]
    return ConceptNote(
        name=path.stem,
        title=title,
        aliases=aliases,
        tags=tags,
        # Anchors are bookkeeping: they must not become searchable tokens, and
        # they must never reach a session as context.
        body=strip_session_anchors(raw_body),
        source_date=_resolve_source_date(path, values, raw_body),
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
                body TEXT NOT NULL,
                source_date TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """
        )
        for note in notes:
            aliases = " ".join(note.aliases)
            tags = " ".join(note.tags)
            cursor = connection.execute(
                "INSERT INTO documents"
                "(name, title, aliases, tags, body, source_date) "
                "VALUES (?, ?, ?, ?, ?, ?)",
                (
                    note.name,
                    note.title,
                    aliases,
                    tags,
                    note.body,
                    note.source_date,
                ),
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
        connection.execute(f"PRAGMA user_version={SCHEMA_VERSION}")
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


def resolve_mode(
    mode: str | None = None,
    environment: dict[str, str] | None = None,
) -> str:
    """Explicit argument wins, then ``BEYIN_RETRIEVAL``, then the safe default."""
    if mode in RETRIEVAL_MODES:
        return mode
    env = os.environ if environment is None else environment
    candidate = (env.get(RETRIEVAL_MODE_ENV) or "").strip().lower()
    return candidate if candidate in RETRIEVAL_MODES else DEFAULT_RETRIEVAL_MODE


def resolve_rrf_k(environment: dict[str, str] | None = None) -> int:
    """``BEYIN_RRF_K``; anything unparsable or below 1 falls back to 60."""
    env = os.environ if environment is None else environment
    try:
        value = int((env.get(RRF_K_ENV) or "").strip())
    except ValueError:
        return DEFAULT_RRF_K
    return value if value >= 1 else DEFAULT_RRF_K


def resolve_half_life_days(environment: dict[str, str] | None = None) -> float:
    """``BEYIN_RECENCY_HALFLIFE_DAYS``; ``0`` disables decay, junk falls back."""
    env = os.environ if environment is None else environment
    raw = (env.get(RECENCY_HALF_LIFE_ENV) or "").strip()
    if not raw:
        return DEFAULT_RECENCY_HALF_LIFE_DAYS
    try:
        value = float(raw)
    except ValueError:
        return DEFAULT_RECENCY_HALF_LIFE_DAYS
    if value < 0 or value != value:  # negatives and NaN are not a half-life
        return DEFAULT_RECENCY_HALF_LIFE_DAYS
    return value


def competition_ranks(
    ordered_names: Sequence[str],
    key: Any,
) -> dict[str, int]:
    """1-based ranks over an already-sorted list, ties sharing the best rank.

    Equal evidence must earn equal rank: BM25 returns 0.0 for a term every note
    contains, and without this the alphabetical tie-break would silently hand
    the first note a better rank in two lists at once.
    """
    ranks: dict[str, int] = {}
    previous_key: Any = _UNSET
    previous_rank = 0
    for position, name in enumerate(ordered_names, 1):
        current = key(name)
        if previous_key is _UNSET or current != previous_key:
            previous_rank = position
            previous_key = current
        ranks[name] = previous_rank
    return ranks


def rrf_fuse(
    ranked_lists: Sequence[Sequence[str] | dict[str, int]],
    k: int = DEFAULT_RRF_K,
) -> dict[str, float]:
    """Reciprocal Rank Fusion: ``score = Σ 1/(k + rank_i)`` over the lists.

    Each list is either a sequence of names, where rank is position, or a
    ``{name: rank}`` mapping, which is how tied notes share one rank.  A note
    absent from a list simply contributes nothing from it, which is what makes
    sparse signals (tag overlap) safe to fuse with dense ones (BM25).
    """
    scores: dict[str, float] = {}
    for ranked in ranked_lists:
        pairs = (
            ranked.items()
            if isinstance(ranked, dict)
            else ((name, rank) for rank, name in enumerate(ranked, 1))
        )
        for name, rank in pairs:
            denominator = k + rank
            if denominator <= 0:
                continue
            scores[name] = scores.get(name, 0.0) + 1.0 / denominator
    return scores


def recency_weight(
    age_days: float,
    half_life_days: float = DEFAULT_RECENCY_HALF_LIFE_DAYS,
    floor: float = RECENCY_WEIGHT_FLOOR,
) -> float:
    """Bounded exponential decay: ``0.5 ** (age/half_life)``, never below floor."""
    if half_life_days <= 0:
        return 1.0
    try:
        weight = 0.5 ** (age_days / half_life_days)
    except OverflowError:
        weight = 0.0
    return min(1.0, max(floor, weight))


def tag_overlap(query_tokens: set[str], title: str, aliases: str, tags: str) -> int:
    """Query tokens that also appear in a note's title, aliases or tags.

    Both sides go through :func:`expanded_tokens`, so Turkish dotted/dotless I
    folds exactly as it does in the index and no separate rule can drift.
    """
    if not query_tokens:
        return 0
    field_tokens = set(expanded_tokens(f"{title} {aliases} {tags}"))
    return len(query_tokens & field_tokens)


def indexed_tag_overlap(query_tokens: set[str], *tokenized_fields: str) -> int:
    """:func:`tag_overlap` over fields the index already tokenized.

    ``token_text`` stored these as a space-joined stream at build time, so this
    is the same set intersection without re-running the tokenizer per row.
    """
    if not query_tokens:
        return 0
    field_tokens: set[str] = set()
    for field in tokenized_fields:
        if field:
            field_tokens.update(field.split())
    return len(query_tokens & field_tokens)


# SQLite's host-parameter ceiling is far higher, but chunking keeps a very
# large match set from ever reaching it.
_NAME_CHUNK = 500


def _bm25_rows(
    connection: sqlite3.Connection,
    expression: str,
) -> list[sqlite3.Row]:
    """Every FTS match with the metadata the fused lists need — never bodies.

    The title/aliases/tags columns come from ``notes``, not ``documents``: the
    FTS side already holds them as the exact token stream :func:`token_text`
    produced at build time, so the tag-overlap signal costs a ``split()``
    instead of re-running the tokenizer over every matched row.
    """
    for date_column in ("documents.source_date", "'' AS source_date"):
        try:
            return list(
                connection.execute(
                    f"""
                    SELECT documents.name, documents.title, {date_column},
                           notes.title AS fts_title,
                           notes.aliases AS fts_aliases,
                           notes.tags AS fts_tags,
                           bm25(notes, 8.0, 6.0, 3.0, 1.0) AS score
                    FROM notes
                    JOIN documents ON documents.rowid = notes.rowid
                    WHERE notes MATCH ?
                    ORDER BY score, documents.name
                    """,
                    (expression,),
                )
            )
        except sqlite3.OperationalError:
            # An index built before schema 2 has no source_date; the recency
            # signal is simply empty until the next rebuild.
            continue
    return []


def _column_by_name(
    connection: sqlite3.Connection,
    column: str,
    names: Sequence[str],
) -> dict[str, str]:
    """Fetch one ``documents`` column for the given note names, in chunks."""
    values: dict[str, str] = {}
    for start in range(0, len(names), _NAME_CHUNK):
        chunk = tuple(names[start : start + _NAME_CHUNK])
        rows = connection.execute(
            f"SELECT name, {column} FROM documents "
            f"WHERE name IN ({','.join('?' * len(chunk))})",
            chunk,
        )
        for row in rows:
            values[str(row["name"])] = str(row[column] or "")
    return values


def _fused_search(
    text: str,
    *,
    limit: int,
    connection: sqlite3.Connection,
    min_score: float,
    k: int,
    half_life_days: float,
    now: dt.datetime,
) -> list[SearchHit]:
    """Fuse BM25, recency and tag overlap, then apply bounded recency decay."""
    expression = _fts_query(text)
    rows = _bm25_rows(connection, expression)
    if not rows:
        return []

    scores = {str(row["name"]): float(row["score"]) for row in rows}
    titles = {str(row["name"]): str(row["title"]) for row in rows}

    # 1. BM25 — today's ranking, unchanged.  The --min-score floor is a floor on
    #    this component alone; the fused score lives on a different scale.
    bm25_list = [name for name, score in scores.items() if -score >= min_score]
    bm25_ranks = competition_ranks(bm25_list, scores.__getitem__)

    # 2. Tag/entity overlap — sparse by construction: zero-overlap notes are
    #    absent from the list rather than ranked last.
    query_tokens = set(expanded_tokens(text))
    overlaps = {
        str(row["name"]): indexed_tag_overlap(
            query_tokens,
            str(row["fts_title"]),
            str(row["fts_aliases"]),
            str(row["fts_tags"]),
        )
        for row in rows
    }
    tag_list = sorted(
        (name for name, count in overlaps.items() if count > 0),
        key=lambda name: (-overlaps[name], name),
    )
    tag_ranks = competition_ranks(tag_list, overlaps.__getitem__)

    # Only notes present in at least one list are candidates.  Recency ranks
    # those candidates; it never introduces a note of its own, or every query
    # would inherit the whole corpus sorted by date.
    candidates = set(bm25_list) | set(tag_list)
    if not candidates:
        return []

    parsed_dates = {}
    for row in rows:
        name = str(row["name"])
        if name not in candidates:
            continue
        stamp = _parse_timestamp(str(row["source_date"] or ""))
        if stamp is not None:
            parsed_dates[name] = stamp

    # 3. Recency — newest source date first.
    recency_list = sorted(
        parsed_dates,
        key=lambda name: (-parsed_dates[name].timestamp(), name),
    )
    recency_ranks = competition_ranks(
        recency_list, lambda name: parsed_dates[name]
    )

    fused = rrf_fuse([bm25_ranks, recency_ranks, tag_ranks], k=k)
    weighted: dict[str, float] = {}
    for name in candidates:
        score = fused.get(name, 0.0)
        stamp = parsed_dates.get(name)
        if stamp is None:
            weighted[name] = score
            continue
        age_days = (now - stamp).total_seconds() / SECONDS_PER_DAY
        weighted[name] = score * recency_weight(age_days, half_life_days)

    ordered = sorted(weighted, key=lambda name: (-weighted[name], name))[:limit]
    bodies = _column_by_name(connection, "body", ordered)
    return [
        SearchHit(
            name=name,
            title=titles.get(name, name),
            body=strip_session_anchors(bodies.get(name, "")),
            score=weighted[name],
        )
        for name in ordered
    ]


def search(
    text: str,
    *,
    limit: int = 3,
    db_path: Path | None = None,
    connection: sqlite3.Connection | None = None,
    min_score: float = 0.0,
    mode: str | None = None,
) -> list[SearchHit]:
    """Return ranked matches for ``text``.

    In ``bm25`` mode (the default) ``score`` is raw ``bm25()`` — lower is
    better — and ``min_score`` is a floor on positive ``-bm25`` relevance.  In
    ``rrf`` mode ``score`` is the recency-weighted fused score, where *higher*
    is better; ``min_score`` still gates the BM25 component only.
    """
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
        if resolve_mode(mode) == MODE_RRF:
            return _fused_search(
                text,
                limit=limit,
                connection=connection,
                min_score=min_score,
                k=resolve_rrf_k(),
                half_life_days=resolve_half_life_days(),
                now=dt.datetime.now(dt.timezone.utc),
            )
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
                    body=strip_session_anchors(str(row["body"])),
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
    mode: str | None = None,
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
        mode=mode,
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


def benchmark(
    db_path: Path | None = None,
    mode: str | None = None,
) -> dict[str, Any]:
    resolved = Path(db_path) if db_path is not None else STATE_DIR / DB_NAME
    resolved_mode = resolve_mode(mode)
    connection = _open_readonly(resolved)
    timings: list[dict[str, Any]] = []
    try:
        for query in BENCH_QUERIES:
            started = time.perf_counter()
            hits = search(query, limit=3, connection=connection, mode=resolved_mode)
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
    return {"mode": resolved_mode, "queries": timings, "p95_ms": round(p95, 3)}


def verify_index(
    vault_root: Path = VAULT_ROOT,
    state_dir: Path | None = None,
    db_path: Path | None = None,
) -> dict[str, Any]:
    """Diff what the FTS index SHOULD hold against what ``notes.db`` holds.

    Expectation is recomputed from ``knowledge/concepts/*.md`` file names alone,
    so an unparsable note still shows up as missing instead of hiding the drift.
    """
    vault_root = Path(vault_root)
    if db_path is not None:
        target = Path(db_path)
    else:
        resolved_state = (
            Path(state_dir)
            if state_dir is not None
            else vault_root / ".claude" / "scripts" / ".state"
        )
        target = resolved_state / DB_NAME
    concepts_dir = vault_root / "knowledge" / "concepts"
    expected = sorted(path.stem for path in concepts_dir.glob("*.md"))
    report: dict[str, Any] = {
        "ok": False,
        "db_path": str(target),
        "concepts_dir": str(concepts_dir),
        "expected_count": len(expected),
        "indexed_count": 0,
        "fts_count": 0,
        "missing": expected,
        "extra": [],
        "schema_version": 0,
        "built_at": "",
    }
    if not target.is_file():
        report["error"] = "index-missing"
        return report
    try:
        connection = _open_readonly(target)
    except sqlite3.Error as exc:
        report["error"] = f"index-unreadable:{exc.__class__.__name__}"
        return report
    try:
        indexed = sorted(
            str(row["name"])
            for row in connection.execute("SELECT name FROM documents")
        )
        fts_count = int(
            connection.execute("SELECT count(*) FROM notes").fetchone()[0]
        )
        version = int(connection.execute("PRAGMA user_version").fetchone()[0])
        meta = dict(connection.execute("SELECT key, value FROM meta"))
    except sqlite3.Error as exc:
        report["error"] = f"index-unreadable:{exc.__class__.__name__}"
        return report
    finally:
        connection.close()
    missing = sorted(set(expected) - set(indexed))
    extra = sorted(set(indexed) - set(expected))
    report.update(
        {
            "indexed_count": len(indexed),
            "fts_count": fts_count,
            "missing": missing,
            "extra": extra,
            "schema_version": version,
            "built_at": str(meta.get("built_at", "")),
            "ok": (
                not missing
                and not extra
                and len(indexed) == len(expected)
                and fts_count == len(indexed)
            ),
        }
    )
    return report


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="mode", required=True)

    build_parser = subparsers.add_parser("build", help="atomically rebuild FTS5")
    build_parser.add_argument("--vault-root", type=Path, default=VAULT_ROOT)
    build_parser.add_argument("--state-dir", type=Path)

    verify_parser = subparsers.add_parser(
        "verify", help="diff knowledge/concepts against the built index"
    )
    verify_parser.add_argument("--vault-root", type=Path, default=VAULT_ROOT)
    verify_parser.add_argument("--state-dir", type=Path)
    verify_parser.add_argument("--db", type=Path)

    query_parser = subparsers.add_parser("query", help="query the FTS5 index")
    query_parser.add_argument("text", nargs="?")
    query_parser.add_argument("--limit", type=int, default=3)
    query_parser.add_argument("--session")
    query_parser.add_argument("--format", choices=("hook", "plain"), default="plain")
    query_parser.add_argument("--min-score", type=float, default=0.0)
    query_parser.add_argument("--db", type=Path, default=STATE_DIR / DB_NAME)
    query_parser.add_argument("--bench", action="store_true")
    query_parser.add_argument(
        "--retrieval",
        choices=RETRIEVAL_MODES,
        default=None,
        help=(
            "Ranking mode; defaults to $"
            f"{RETRIEVAL_MODE_ENV} and then to {DEFAULT_RETRIEVAL_MODE}."
        ),
    )
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = _parse_args(argv)
    if args.mode == "build":
        report = build_index(vault_root=args.vault_root, state_dir=args.state_dir)
        print(json.dumps(report, ensure_ascii=False))
        return 0
    if args.mode == "verify":
        report = verify_index(
            vault_root=args.vault_root,
            state_dir=args.state_dir,
            db_path=args.db,
        )
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 0 if report["ok"] else 1
    if args.bench:
        print(
            json.dumps(
                benchmark(args.db, mode=args.retrieval),
                ensure_ascii=False,
                indent=2,
            )
        )
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
            mode=args.retrieval,
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
                    mode=args.retrieval,
                )
            )
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
