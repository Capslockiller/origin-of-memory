#!/usr/bin/env python3
"""Run extracted retrieve.py versions and emit deterministic TREC run files."""

# yazan: codex · gpt-5.6-sol

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import importlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import shutil
import sqlite3
import sys
import tempfile
import time
from types import ModuleType
from typing import Any, Iterable


ROOT = Path(__file__).resolve().parent
SETS_DIR = ROOT / ".data" / "sets"
VERSIONS_DIR = ROOT / ".versions"
OUT = ROOT / ".out"
RUNS_DIR = OUT / "runs"
LATENCY_DIR = OUT / "latency"
MODULE_NAMES = ("retrieve", "beyin_ortak", "sema", "rootmap")


def _load_jsonl(path: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, 1):
            if not line.strip():
                continue
            row = json.loads(line)
            if not isinstance(row, dict):
                raise ValueError(f"{path}:{line_number}: expected a JSON object")
            rows.append(row)
    return rows


def _load_retrieve(version_dir: Path) -> ModuleType:
    importlib.invalidate_caches()
    for name in MODULE_NAMES:
        sys.modules.pop(name, None)
    # V2/V5 eagerly import sema, whose rootmap dependency is irrelevant to the
    # benchmark query path and was not one of the checkpoint's required files.
    sys.modules["rootmap"] = ModuleType("rootmap")
    directory = str(version_dir)
    sys.path.insert(0, directory)
    try:
        path = version_dir / "retrieve.py"
        spec = importlib.util.spec_from_file_location("retrieve", path)
        if spec is None or spec.loader is None:
            raise ImportError(f"cannot create module spec for {path}")
        module = importlib.util.module_from_spec(spec)
        sys.modules["retrieve"] = module
        spec.loader.exec_module(module)
        return module
    except Exception:
        for name in MODULE_NAMES:
            sys.modules.pop(name, None)
        raise
    finally:
        try:
            sys.path.remove(directory)
        except ValueError:
            pass


def _purge_modules() -> None:
    for name in MODULE_NAMES:
        sys.modules.pop(name, None)


def _select(value: str, allowed: Iterable[str], label: str) -> list[str]:
    allowed = list(allowed)
    chosen = allowed if value == "all" else [part.strip() for part in value.split(",") if part.strip()]
    unknown = [item for item in chosen if item not in allowed]
    if unknown:
        raise SystemExit(f"unknown {label}(s): {', '.join(unknown)}")
    return chosen


def _percentile(values: list[float], fraction: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    weight = position - lower
    return ordered[lower] * (1.0 - weight) + ordered[upper] * weight


def _note(module: ModuleType, row: dict[str, Any], profile: str) -> Any:
    kwargs = {
        "name": str(row["_id"]),
        "title": str(row.get("title") or ""),
        "aliases": (),
        "tags": (),
        "body": str(row.get("text") or ""),
    }
    if profile == "B":
        kwargs["source_date"] = str(row.get("source_date") or "")
    return module.ConceptNote(**kwargs)


def _search(
    module: ModuleType,
    profile: str,
    mode: str,
    query: str,
    limit: int,
    db_path: Path,
    now_iso: str | None = None,
) -> list[Any]:
    kwargs: dict[str, Any] = {"limit": limit, "db_path": db_path}
    if profile == "B":
        kwargs["mode"] = mode
    if now_iso is None or not hasattr(module, "dt"):
        return module.search(query, **kwargs)

    # V2/V4 fold wall-clock age into their legacy RRF score. Evaluate each
    # LongMemEval question at its own question date so scores are reproducible.
    datetime_module = module.dt
    real_datetime = datetime_module.datetime
    fixed = real_datetime.fromisoformat(now_iso)

    class FixedDateTime(real_datetime):
        @classmethod
        def now(cls, tz: Any = None) -> Any:
            if tz is None:
                return fixed.replace(tzinfo=None)
            return fixed.astimezone(tz)

    datetime_module.datetime = FixedDateTime
    try:
        return module.search(query, **kwargs)
    finally:
        datetime_module.datetime = real_datetime


def _score_for_trec(hit: Any, rank: int, mode: str) -> float:
    # SQLite bm25() is lower-is-better. Historical RRF is higher-is-better.
    return float(hit.score) if mode == "rrf" else float(1000 - rank)


def _trec_lines(query_id: str, hits: list[Any], mode: str, tag: str) -> list[str]:
    lines = []
    seen: set[str] = set()
    for rank, hit in enumerate(hits, 1):
        document_id = str(hit.name)
        if document_id in seen:
            raise ValueError(f"{query_id}: duplicate result document {document_id}")
        if any(character.isspace() for character in query_id + document_id):
            raise ValueError("TREC ids may not contain whitespace")
        seen.add(document_id)
        score = _score_for_trec(hit, rank, mode)
        lines.append(f"{query_id} Q0 {document_id} {rank} {score:.17g} {tag}\n")
    return lines


def _write_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(text, encoding="utf-8", newline="\n")
    os.replace(temporary, path)


def _write_json(path: Path, payload: Any) -> None:
    _write_text(path, json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=True) + "\n")


def _display_path(path: Path) -> str:
    try:
        return str(path.relative_to(ROOT))
    except ValueError:
        return str(path)


def _resolve_out_dir(value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else ROOT / path


def _record_key(record: dict[str, Any]) -> tuple[str, str, str]:
    try:
        return (str(record["set"]), str(record["version"]), str(record["mode"]))
    except KeyError as exc:
        raise SystemExit(f"manifest record is missing {exc.args[0]!r}") from exc


def _load_run_manifest(path: Path) -> dict[str, Any] | None:
    if not path.is_file():
        return None
    try:
        manifest = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise SystemExit(f"cannot read existing manifest {_display_path(path)}: {exc}") from exc
    if not isinstance(manifest, dict):
        raise SystemExit(f"existing manifest {_display_path(path)} must be a JSON object")
    if not isinstance(manifest.get("records", []), list):
        raise SystemExit(f"existing manifest {_display_path(path)} has a non-list records field")
    if not isinstance(manifest.get("history", []), list):
        raise SystemExit(f"existing manifest {_display_path(path)} has a non-list history field")
    return manifest


def _version_sha256s(versions_by_name: dict[str, dict[str, Any]]) -> dict[str, Any]:
    return {name: row["files"] for name, row in versions_by_name.items()}


def _check_version_sha256s(
    existing: dict[str, Any] | None,
    current: dict[str, Any],
    allow_mixed_versions: bool,
) -> None:
    if existing is None or "version_sha256s" not in existing:
        return
    if existing["version_sha256s"] != current and not allow_mixed_versions:
        raise SystemExit(
            "checkpoint hashes differ from the existing kosu-manifest.json; "
            "use --allow-mixed-versions to merge records produced by different checkpoint files"
        )


def _history_with_previous(existing: dict[str, Any] | None) -> list[dict[str, Any]]:
    if existing is None:
        return []
    history = list(existing.get("history", []))
    parameters = existing.get("parameters", {})
    if not isinstance(parameters, dict):
        raise SystemExit("existing manifest has a non-object parameters field")
    records = existing.get("records", [])
    history.append(
        {
            "timestamp_utc": existing.get("timestamp_utc"),
            "parameters": parameters,
            "versions": parameters.get(
                "versions", sorted({_record_key(record)[1] for record in records})
            ),
            "sets": parameters.get(
                "sets", sorted({_record_key(record)[0] for record in records})
            ),
        }
    )
    return history


def _merge_records(
    existing: dict[str, Any] | None,
    new_records: list[dict[str, Any]],
    *,
    existing_wins: bool,
) -> list[dict[str, Any]]:
    old_records = [] if existing is None else existing.get("records", [])
    ordered_sources = (new_records, old_records) if existing_wins else (old_records, new_records)
    merged: dict[tuple[str, str, str], dict[str, Any]] = {}
    for source in ordered_sources:
        for record in source:
            if not isinstance(record, dict):
                raise SystemExit("manifest records must be JSON objects")
            merged[_record_key(record)] = record
    return [merged[key] for key in sorted(merged)]


def _build_run_manifest(
    parameters: dict[str, Any],
    version_sha256s: dict[str, Any],
    records: list[dict[str, Any]],
    history: list[dict[str, Any]],
) -> dict[str, Any]:
    return {
        "format_version": 1,
        "yazan": "codex",
        "model": "gpt-5.6-sol",
        "timestamp_utc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "python_version": sys.version,
        "sqlite_version": sqlite3.sqlite_version,
        "sqlite_fts5_available": _fts5_available(),
        "machine": {
            "platform": platform.platform(),
            "processor": platform.processor(),
            "architecture": platform.machine(),
        },
        "parameters": parameters,
        "version_sha256s": version_sha256s,
        "score_policy": {
            "bm25": "1000-rank because SQLite bm25 is lower-is-better",
            "rrf": "historical hit.score because RRF is higher-is-better",
        },
        "retrieval_environment": "BEYIN retrieval/tuning variables cleared; historical defaults used",
        "longmemeval_clock": "each search evaluated at that question's question_date",
        "locomo_clock": "each search evaluated at its conversation's last indexed (dialogue-bearing) session date_time",
        "history": history,
        "records": records,
    }


def _artifact_key(path: Path, suffix: str) -> tuple[str, str, str] | None:
    stem = path.name[: -len(suffix)]
    parts = stem.split("__")
    if len(parts) != 3 or not all(parts):
        return None
    return (parts[0], parts[1], parts[2])


def _distinct_run_query_count(path: Path) -> int:
    query_ids: set[str] = set()
    with path.open("r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, 1):
            if not line.strip():
                continue
            fields = line.split()
            if len(fields) != 6:
                raise SystemExit(
                    f"{_display_path(path)}:{line_number}: expected a six-column TREC row"
                )
            query_ids.add(fields[0])
    return len(query_ids)


def _rebuild_records(
    out_dir: Path,
    versions_by_name: dict[str, dict[str, Any]],
) -> list[dict[str, Any]]:
    runs_dir = out_dir / "runs"
    latency_dir = out_dir / "latency"
    latency_by_key: dict[tuple[str, str, str], Path] = {}
    for latency_path in sorted(latency_dir.glob("*.json")):
        key = _artifact_key(latency_path, ".json")
        if key is None:
            print(f"ignored unrecognized latency filename: {_display_path(latency_path)}")
            continue
        latency_by_key[key] = latency_path

    records: list[dict[str, Any]] = []
    matched_latency_keys: set[tuple[str, str, str]] = set()
    bm25_sets: set[str] = set()
    for run_path in sorted(runs_dir.glob("*.trec")):
        key = _artifact_key(run_path, ".trec")
        if key is None:
            print(f"ignored unrecognized run filename: {_display_path(run_path)}")
            continue
        set_name, version, mode = key
        if mode == "bm25":
            bm25_sets.add(set_name)
        latency_path = latency_by_key.get(key)
        if latency_path is None:
            print(f"ignored run without matching latency file: {_display_path(run_path)}")
            continue
        matched_latency_keys.add(key)
        record = {
            "set": set_name,
            "version": version,
            "mode": mode,
            "status": "ok",
            "run": str(run_path.relative_to(out_dir)).replace("\\", "/"),
            "latency": str(latency_path.relative_to(out_dir)).replace("\\", "/"),
            "n_queries": _distinct_run_query_count(run_path),
        }
        version_record = versions_by_name.get(version)
        if version_record is not None and version != version_record["representative"]:
            record["reused_from"] = version_record["representative"]
        records.append(record)

    for key, latency_path in sorted(latency_by_key.items()):
        if key not in matched_latency_keys:
            print(f"ignored latency without matching run file: {_display_path(latency_path)}")

    profile_a_versions = sorted(
        name for name, row in versions_by_name.items() if row.get("profile") == "A"
    )
    for set_name in sorted(bm25_sets):
        for version in profile_a_versions:
            records.append(
                {
                    "set": set_name,
                    "version": version,
                    "mode": "rrf",
                    "status": "n/a",
                    "reason": "Profile A has no rrf mode",
                }
            )
    return records


def _print_rebuild_summary(records: list[dict[str, Any]]) -> None:
    per_set: dict[str, int] = {}
    for record in records:
        set_name = str(record["set"])
        per_set[set_name] = per_set.get(set_name, 0) + 1
    print(f"records total: {len(records)}")
    print("per set: " + ", ".join(f"{name}={per_set[name]}" for name in sorted(per_set)))
    print(f"n/a count: {sum(record.get('status') == 'n/a' for record in records)}")


def _load_set_manifest() -> dict[str, Any]:
    manifest_path = SETS_DIR / "manifest.json"
    manifest = (
        json.loads(manifest_path.read_text(encoding="utf-8"))
        if manifest_path.is_file()
        else {"datasets": {}}
    )
    locomo_manifest_path = SETS_DIR / "locomo" / "manifest.jsonl"
    if locomo_manifest_path.is_file():
        rows = _load_jsonl(locomo_manifest_path)
        if len(rows) != 1:
            raise SystemExit(f"{_display_path(locomo_manifest_path)}: expected one manifest record")
        manifest.setdefault("datasets", {})["locomo"] = rows[0]
    if not manifest.get("datasets"):
        raise SystemExit("missing normalized set manifests; run setler.py first")
    return manifest


def _latency_payload(query_ids: list[str], repeats_ms: list[list[float]], index_ms: list[float], repeat: int) -> dict[str, Any]:
    flattened = [value for one_repeat in repeats_ms for value in one_repeat]
    return {
        "yazan": "codex",
        "model": "gpt-5.6-sol",
        "repeat": repeat,
        "query_ids": query_ids,
        "per_query_ms_by_repeat": repeats_ms,
        "index_ms": index_ms,
        "p50_ms": _percentile(flattened, 0.50),
        "p95_ms": _percentile(flattened, 0.95),
    }


def _run_beir(module: ModuleType, profile: str, set_dir: Path, mode: str, limit: int, repeat: int, max_queries: int | None) -> tuple[str, dict[str, Any]]:
    corpus_path = set_dir / "corpus.jsonl"
    queries_path = set_dir / "queries.jsonl"
    qrels_path = set_dir / "qrels.trec"
    missing = [path for path in (corpus_path, queries_path, qrels_path) if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(str(path.relative_to(ROOT)) for path in missing))
    corpus = sorted(_load_jsonl(corpus_path), key=lambda row: str(row["_id"]))
    queries = sorted(_load_jsonl(queries_path), key=lambda row: str(row["_id"]))
    if max_queries is not None:
        queries = queries[:max_queries]
    query_ids = [str(row["_id"]) for row in queries]
    notes = [_note(module, row, profile) for row in corpus]
    run_lines: list[str] = []
    repeats_ms: list[list[float]] = []
    index_ms: list[float] = []
    with tempfile.TemporaryDirectory(prefix="origin-memory-benchmark-") as temporary:
        db_path = Path(temporary) / "state" / "notes.db"
        db_path.parent.mkdir()
        started = time.perf_counter_ns()
        module._create_database(db_path, notes)
        index_ms.append((time.perf_counter_ns() - started) / 1_000_000)
        for repeat_index in range(repeat):
            one_repeat: list[float] = []
            for row in queries:
                started = time.perf_counter_ns()
                hits = _search(module, profile, mode, str(row["text"]), limit, db_path)
                one_repeat.append((time.perf_counter_ns() - started) / 1_000_000)
                if repeat_index == 0:
                    run_lines.extend(_trec_lines(str(row["_id"]), hits, mode, "origin-memory"))
            repeats_ms.append(one_repeat)
    return "".join(run_lines), _latency_payload(query_ids, repeats_ms, index_ms, repeat)


def _run_longmemeval(module: ModuleType, profile: str, set_dir: Path, mode: str, limit: int, repeat: int, max_queries: int | None) -> tuple[str, dict[str, Any]]:
    items_path = set_dir / "items.jsonl"
    qrels_path = set_dir / "qrels.trec"
    missing = [path for path in (items_path, qrels_path) if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(str(path.relative_to(ROOT)) for path in missing))
    items = sorted(_load_jsonl(items_path), key=lambda row: str(row["_id"]))
    if max_queries is not None:
        items = items[:max_queries]
    query_ids = [str(row["_id"]) for row in items]
    run_lines: list[str] = []
    repeats_ms: list[list[float]] = [[] for _ in range(repeat)]
    index_ms: list[float] = []
    with tempfile.TemporaryDirectory(prefix="origin-memory-longmemeval-") as temporary:
        state_root = Path(temporary) / "state"
        state_root.mkdir()
        for item_number, item in enumerate(items):
            db_path = state_root / f"{item_number:04d}.db"
            notes = [_note(module, row, profile) for row in item["documents"]]
            started = time.perf_counter_ns()
            module._create_database(db_path, notes)
            index_ms.append((time.perf_counter_ns() - started) / 1_000_000)
            for repeat_index in range(repeat):
                started = time.perf_counter_ns()
                hits = _search(
                    module,
                    profile,
                    mode,
                    str(item["text"]),
                    limit,
                    db_path,
                    str(item["question_date"]),
                )
                repeats_ms[repeat_index].append((time.perf_counter_ns() - started) / 1_000_000)
                if repeat_index == 0:
                    run_lines.extend(_trec_lines(str(item["_id"]), hits, mode, "origin-memory"))
    return "".join(run_lines), _latency_payload(query_ids, repeats_ms, index_ms, repeat)


def _run_locomo(module: ModuleType, profile: str, set_dir: Path, mode: str, limit: int, repeat: int, max_queries: int | None) -> tuple[str, dict[str, Any]]:
    items_path = set_dir / "items.jsonl"
    queries_path = set_dir / "queries.jsonl"
    qrels_path = set_dir / "qrels.trec"
    missing = [path for path in (items_path, queries_path, qrels_path) if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(str(path.relative_to(ROOT)) for path in missing))
    items = sorted(_load_jsonl(items_path), key=lambda row: str(row["_id"]))
    remaining = max_queries
    selected_items: list[tuple[dict[str, Any], list[dict[str, Any]]]] = []
    query_ids: list[str] = []
    for item in items:
        item_queries = sorted(item["queries"], key=lambda row: str(row["_id"]))
        if remaining is not None:
            item_queries = item_queries[:remaining]
            remaining -= len(item_queries)
        if item_queries:
            selected_items.append((item, item_queries))
            query_ids.extend(str(row["_id"]) for row in item_queries)
        if remaining == 0:
            break

    run_lines: list[str] = []
    repeats_ms: list[list[float]] = [[] for _ in range(repeat)]
    index_ms: list[float] = []
    with tempfile.TemporaryDirectory(prefix="origin-memory-locomo-") as temporary:
        state_root = Path(temporary) / "state"
        state_root.mkdir()
        for item_number, (item, item_queries) in enumerate(selected_items):
            db_path = state_root / f"{item_number:04d}.db"
            notes = [_note(module, row, profile) for row in item["documents"]]
            started = time.perf_counter_ns()
            module._create_database(db_path, notes)
            index_ms.append((time.perf_counter_ns() - started) / 1_000_000)
            for repeat_index in range(repeat):
                for query in item_queries:
                    started = time.perf_counter_ns()
                    hits = _search(
                        module,
                        profile,
                        mode,
                        str(query["text"]),
                        limit,
                        db_path,
                        str(item["question_date"]),
                    )
                    repeats_ms[repeat_index].append((time.perf_counter_ns() - started) / 1_000_000)
                    if repeat_index == 0:
                        run_lines.extend(_trec_lines(str(query["_id"]), hits, mode, "origin-memory"))
    return "".join(run_lines), _latency_payload(query_ids, repeats_ms, index_ms, repeat)


def _fts5_available() -> bool:
    connection = sqlite3.connect(":memory:")
    try:
        connection.execute("CREATE VIRTUAL TABLE probe USING fts5(text)")
        return True
    except sqlite3.OperationalError:
        return False
    finally:
        connection.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rebuild-manifest", action="store_true", help="reconstruct manifest records from existing run and latency files")
    parser.add_argument("--allow-mixed-versions", action="store_true", help="allow merging records when checkpoint hashes changed")
    parser.add_argument("--versions", default="all", help="all or comma-separated versions")
    parser.add_argument("--sets", default="all", help="all or comma-separated normalized sets")
    parser.add_argument("--modes", default="bm25,rrf", help="comma-separated bm25,rrf")
    parser.add_argument("--limit", type=int, default=100)
    parser.add_argument("--repeat", type=int, default=3)
    parser.add_argument("--max-queries", type=int)
    parser.add_argument("--out-dir", default=".out", help="output directory, relative to tools/benchmark by default")
    args = parser.parse_args()
    if args.limit < 1 or args.repeat < 1 or (args.max_queries is not None and args.max_queries < 1):
        parser.error("--limit, --repeat, and --max-queries must be positive")
    version_manifest_path = VERSIONS_DIR / "manifest.json"
    if not version_manifest_path.is_file():
        raise SystemExit("missing .versions/manifest.json; run surum_cikar.py first")
    version_manifest = json.loads(version_manifest_path.read_text(encoding="utf-8"))
    versions_by_name = {row["version"]: row for row in version_manifest["versions"]}
    current_version_sha256s = _version_sha256s(versions_by_name)
    out_dir = _resolve_out_dir(args.out_dir)
    manifest_path = out_dir / "kosu-manifest.json"
    existing_manifest = _load_run_manifest(manifest_path)
    _check_version_sha256s(existing_manifest, current_version_sha256s, args.allow_mixed_versions)
    if args.rebuild_manifest:
        rebuilt_records = _rebuild_records(out_dir, versions_by_name)
        records = _merge_records(existing_manifest, rebuilt_records, existing_wins=True)
        discovered_versions = sorted({_record_key(record)[1] for record in rebuilt_records})
        discovered_sets = sorted({_record_key(record)[0] for record in rebuilt_records})
        parameters = {
            "operation": "rebuild-manifest",
            "versions": discovered_versions,
            "sets": discovered_sets,
            "modes": sorted({_record_key(record)[2] for record in rebuilt_records}),
            "out_dir": args.out_dir,
            "allow_mixed_versions": args.allow_mixed_versions,
        }
        run_manifest = _build_run_manifest(
            parameters,
            current_version_sha256s,
            records,
            _history_with_previous(existing_manifest),
        )
        _write_json(manifest_path, run_manifest)
        _print_rebuild_summary(records)
        print(f"manifest: {_display_path(manifest_path)}")
        return 0

    set_manifest = _load_set_manifest()
    selected_versions = _select(args.versions, versions_by_name, "version")
    selected_sets = _select(args.sets, set_manifest["datasets"], "set")
    modes = _select(args.modes, ("bm25", "rrf"), "mode")
    runs_dir = out_dir / "runs"
    latency_dir = out_dir / "latency"
    runs_dir.mkdir(parents=True, exist_ok=True)
    latency_dir.mkdir(parents=True, exist_ok=True)
    records: list[dict[str, Any]] = []
    computed: dict[tuple[str, str, str], tuple[Path, Path]] = {}
    retrieval_environment_names = (
        "BEYIN_RETRIEVAL",
        "BEYIN_RRF_K",
        "BEYIN_RECENCY_HALFLIFE_DAYS",
        "BEYIN_RRF_RECENCY_CHANNEL_WEIGHT",
        "BEYIN_RRF_LEGACY_MULTIPLIER",
    )
    saved_environment = {
        name: os.environ.pop(name)
        for name in retrieval_environment_names
        if name in os.environ
    }
    try:
        for set_name in selected_sets:
            set_record = set_manifest["datasets"].get(set_name, {})
            if set_record.get("status") != "ready":
                reason = set_record.get("reason", "normalized set is not ready")
                print(f"{set_name}: skipped: {reason}")
                for version in selected_versions:
                    for mode in modes:
                        records.append({"set": set_name, "version": version, "mode": mode, "status": "skipped", "reason": reason})
                continue
            for version in selected_versions:
                requested = versions_by_name[version]
                representative = requested["representative"]
                representative_record = versions_by_name[representative]
                for mode in modes:
                    if requested["profile"] == "A" and mode == "rrf":
                        print(f"{set_name} {version} rrf: n/a (Profile A)")
                        records.append({"set": set_name, "version": version, "mode": mode, "status": "n/a", "reason": "Profile A has no rrf mode"})
                        continue
                    key = (set_name, representative, mode)
                    canonical_run = runs_dir / f"{set_name}__{representative}__{mode}.trec"
                    canonical_latency = latency_dir / f"{set_name}__{representative}__{mode}.json"
                    try:
                        if key not in computed:
                            module = _load_retrieve(VERSIONS_DIR / representative)
                            if set_name == "longmemeval-s":
                                run_text, latency = _run_longmemeval(module, representative_record["profile"], SETS_DIR / set_name, mode, args.limit, args.repeat, args.max_queries)
                            elif set_name == "locomo":
                                run_text, latency = _run_locomo(module, representative_record["profile"], SETS_DIR / set_name, mode, args.limit, args.repeat, args.max_queries)
                            else:
                                run_text, latency = _run_beir(module, representative_record["profile"], SETS_DIR / set_name, mode, args.limit, args.repeat, args.max_queries)
                            _write_text(canonical_run, run_text)
                            _write_json(canonical_latency, latency)
                            computed[key] = (canonical_run, canonical_latency)
                        target_run = runs_dir / f"{set_name}__{version}__{mode}.trec"
                        target_latency = latency_dir / f"{set_name}__{version}__{mode}.json"
                        source_run, source_latency = computed[key]
                        if target_run != source_run:
                            shutil.copyfile(source_run, target_run)
                            shutil.copyfile(source_latency, target_latency)
                        record = {
                            "set": set_name,
                            "version": version,
                            "mode": mode,
                            "status": "ok",
                            "run": str(target_run.relative_to(out_dir)).replace("\\", "/"),
                            "latency": str(target_latency.relative_to(out_dir)).replace("\\", "/"),
                            "n_queries": len(json.loads(target_latency.read_text(encoding="utf-8"))["query_ids"]),
                        }
                        if version != representative:
                            record["reused_from"] = representative
                        records.append(record)
                        suffix = f" (reused from {representative})" if version != representative else ""
                        print(f"{set_name} {version} {mode}: ok{suffix}")
                    except FileNotFoundError as exc:
                        reason = f"missing normalized file: {exc}"
                        print(f"{set_name} {version} {mode}: skipped: {reason}")
                        records.append({"set": set_name, "version": version, "mode": mode, "status": "skipped", "reason": reason})
                    finally:
                        _purge_modules()
    finally:
        os.environ.update(saved_environment)
    parameters = {
        "versions": selected_versions,
        "sets": selected_sets,
        "modes": modes,
        "limit": args.limit,
        "repeat": args.repeat,
        "max_queries": args.max_queries,
        "out_dir": args.out_dir,
        "allow_mixed_versions": args.allow_mixed_versions,
    }
    records = _merge_records(existing_manifest, records, existing_wins=False)
    run_manifest = _build_run_manifest(
        parameters,
        current_version_sha256s,
        records,
        _history_with_previous(existing_manifest),
    )
    _write_json(manifest_path, run_manifest)
    print(f"manifest: {_display_path(manifest_path)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
