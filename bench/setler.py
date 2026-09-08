#!/usr/bin/env python3
"""Normalize the pre-downloaded benchmark datasets without network access."""

# yazan: codex · gpt-5.6-sol

from __future__ import annotations

import argparse
import csv
import datetime as dt
import hashlib
import io
import json
import os
from pathlib import Path
import re
import sys
from typing import Any, Iterable
import zipfile

import pandas as pd


ROOT = Path(__file__).resolve().parent
RAW = ROOT / ".data" / "raw"
SETS = ROOT / ".data" / "sets"

TRMTEB = ("scifact-tr", "nfcorpus-tr", "fiqa-tr", "arguana-tr")
BEIR = ("scifact", "nfcorpus", "fiqa", "arguana")
ALL_SETS = (*TRMTEB, "scifact-tr-saoud", *BEIR, "longmemeval-s", "locomo")

LOCOMO_CATEGORIES = {
    1: "multi-hop",
    2: "temporal",
    3: "open-domain",
    4: "single-hop",
}
LOCOMO_MONTHS = {
    name: number
    for number, name in enumerate(
        (
            "January",
            "February",
            "March",
            "April",
            "May",
            "June",
            "July",
            "August",
            "September",
            "October",
            "November",
            "December",
        ),
        1,
    )
}


class MissingRawFile(Exception):
    pass


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _need(paths: Iterable[Path]) -> list[Path]:
    paths = list(paths)
    missing = [path for path in paths if not path.is_file()]
    if missing:
        raise MissingRawFile(", ".join(str(path.relative_to(ROOT)) for path in missing))
    return paths


def _write_jsonl(path: Path, rows: Iterable[dict[str, Any]]) -> int:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    count = 0
    with temporary.open("w", encoding="utf-8", newline="\n") as handle:
        for row in rows:
            handle.write(json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n")
            count += 1
    os.replace(temporary, path)
    return count


def _write_qrels(path: Path, rows: Iterable[tuple[str, str, int]]) -> int:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    count = 0
    with temporary.open("w", encoding="utf-8", newline="\n") as handle:
        for query_id, document_id, relevance in rows:
            handle.write(f"{query_id} 0 {document_id} {relevance}\n")
            count += 1
    os.replace(temporary, path)
    return count


def _read_jsonl(path: Path) -> list[dict[str, Any]]:
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


def _normal_doc(row: dict[str, Any]) -> dict[str, str]:
    return {
        "_id": str(row["_id"]),
        "title": str(row.get("title") or ""),
        "text": str(row.get("text") or ""),
    }


def _normal_query(row: dict[str, Any]) -> dict[str, str]:
    return {"_id": str(row["_id"]), "text": str(row.get("text") or "")}


def _normal_qrels(rows: Iterable[dict[str, Any]]) -> list[tuple[str, str, int]]:
    qrels: dict[tuple[str, str], int] = {}
    for row in rows:
        key = (str(row["query-id"]), str(row["corpus-id"]))
        relevance = int(float(row.get("score", 1)))
        qrels[key] = max(relevance, qrels.get(key, relevance))
    return sorted((qid, docid, rel) for (qid, docid), rel in qrels.items())


def _metadata(name: str, source: str, files: list[Path], counts: dict[str, int]) -> dict[str, Any]:
    return {
        "name": name,
        "source": source,
        "license": "unverified",
        "raw_files": [
            {"path": str(path.relative_to(RAW)).replace("\\", "/"), "sha256": _sha256(path)}
            for path in files
        ],
        "counts": counts,
    }


def convert_trmteb(name: str) -> dict[str, Any]:
    files = _need(
        RAW / "trmteb" / f"{name}__{part}-00000-of-00001.parquet"
        for part in ("corpus", "queries", "test")
    )
    corpus_frame, queries_frame, qrels_frame = (
        pd.read_parquet(path, engine="fastparquet") for path in files
    )
    qrels = _normal_qrels(qrels_frame.to_dict("records"))
    test_queries = {qid for qid, _, _ in qrels}
    corpus = sorted((_normal_doc(row) for row in corpus_frame.to_dict("records")), key=lambda x: x["_id"])
    queries = sorted(
        (_normal_query(row) for row in queries_frame.to_dict("records") if str(row["_id"]) in test_queries),
        key=lambda x: x["_id"],
    )
    target = SETS / name
    docs_count = _write_jsonl(target / "corpus.jsonl", corpus)
    query_count = _write_jsonl(target / "queries.jsonl", queries)
    qrels_count = _write_qrels(target / "qrels.trec", qrels)
    return _metadata(name, f"mteb/{name}", files, {"docs": docs_count, "queries": query_count, "qrels": qrels_count})


def convert_beir(name: str) -> dict[str, Any]:
    (archive,) = _need([RAW / "beir" / f"{name}.zip"])
    with zipfile.ZipFile(archive) as source:
        required = {
            "corpus": f"{name}/corpus.jsonl",
            "queries": f"{name}/queries.jsonl",
            "qrels": f"{name}/qrels/test.tsv",
        }
        absent = [member for member in required.values() if member not in source.namelist()]
        if absent:
            raise MissingRawFile(f"{archive.relative_to(ROOT)} members: {', '.join(absent)}")
        corpus = [json.loads(line) for line in source.read(required["corpus"]).decode("utf-8").splitlines() if line]
        queries = [json.loads(line) for line in source.read(required["queries"]).decode("utf-8").splitlines() if line]
        qrel_reader = csv.DictReader(io.StringIO(source.read(required["qrels"]).decode("utf-8")), delimiter="\t")
        qrels = _normal_qrels(qrel_reader)
    test_queries = {qid for qid, _, _ in qrels}
    target = SETS / name
    docs_count = _write_jsonl(target / "corpus.jsonl", sorted(map(_normal_doc, corpus), key=lambda x: x["_id"]))
    query_count = _write_jsonl(
        target / "queries.jsonl",
        sorted((_normal_query(row) for row in queries if str(row["_id"]) in test_queries), key=lambda x: x["_id"]),
    )
    qrels_count = _write_qrels(target / "qrels.trec", qrels)
    return _metadata(name, f"BEIR/{name}", [archive], {"docs": docs_count, "queries": query_count, "qrels": qrels_count})


def convert_saoud() -> dict[str, Any]:
    name = "scifact-tr-saoud"
    files = _need(RAW / "saoud" / item for item in ("corpus.jsonl", "queries.jsonl", "qrels-test.jsonl"))
    corpus_rows, query_rows, qrel_rows = map(_read_jsonl, files)
    qrels = _normal_qrels(qrel_rows)
    test_queries = {qid for qid, _, _ in qrels}
    target = SETS / name
    docs_count = _write_jsonl(target / "corpus.jsonl", sorted(map(_normal_doc, corpus_rows), key=lambda x: x["_id"]))
    query_count = _write_jsonl(
        target / "queries.jsonl",
        sorted((_normal_query(row) for row in query_rows if str(row["_id"]) in test_queries), key=lambda x: x["_id"]),
    )
    qrels_count = _write_qrels(target / "qrels.trec", qrels)
    return _metadata(
        name,
        "AbdulkaderSaoud/scifact-tr + scifact-tr-qrels",
        files,
        {"docs": docs_count, "queries": query_count, "qrels": qrels_count},
    )


def _iso_longmemeval_date(value: str) -> str:
    # The historical retrievers consume ISO-8601. LongMemEval stores
    # "YYYY/MM/DD (Day) HH:MM", so normalize it without changing the instant.
    date_part = value.partition(" ")[0]
    clock = value.rsplit(" ", 1)[-1] if ":" in value else "00:00"
    return f"{date_part.replace('/', '-')}T{clock}:00+00:00"


def convert_longmemeval() -> dict[str, Any]:
    name = "longmemeval-s"
    (source_path,) = _need([RAW / "longmemeval" / "longmemeval_s_cleaned.json"])
    payload = json.loads(source_path.read_text(encoding="utf-8"))
    if not isinstance(payload, list):
        raise ValueError(f"{source_path}: expected a JSON array")
    items: list[dict[str, Any]] = []
    qrels: list[tuple[str, str, int]] = []
    docs_count = 0
    skipped_abstention = 0
    for raw_item in sorted(payload, key=lambda x: str(x["question_id"])):
        query_id = str(raw_item["question_id"])
        if query_id.endswith("_abs"):
            skipped_abstention += 1
            continue
        session_ids = raw_item["haystack_session_ids"]
        dates = raw_item["haystack_dates"]
        sessions = raw_item["haystack_sessions"]
        if not (len(session_ids) == len(dates) == len(sessions)):
            raise ValueError(f"{query_id}: haystack arrays have different lengths")
        documents_by_id: dict[str, dict[str, str]] = {}
        for session_id, date, session in zip(session_ids, dates, sessions):
            text = " ".join(
                str(turn.get("content") or "")
                for turn in session
                if str(turn.get("role") or "").lower() == "user"
            )
            document_id = str(session_id)
            # Match the official harness's ID-keyed corpus semantics: repeated
            # session IDs collapse, with the later occurrence winning.
            documents_by_id[document_id] = {
                "_id": document_id,
                "title": "",
                "text": text,
                "source_date": _iso_longmemeval_date(str(date)),
            }
        documents = sorted(documents_by_id.values(), key=lambda x: x["_id"])
        relevant = sorted({str(value) for value in raw_item["answer_session_ids"]})
        items.append(
            {
                "_id": query_id,
                "text": str(raw_item["question"]),
                "question_date": _iso_longmemeval_date(str(raw_item["question_date"])),
                "documents": documents,
                "relevant": relevant,
            }
        )
        qrels.extend((query_id, document_id, 1) for document_id in relevant)
        docs_count += len(documents)
    target = SETS / name
    item_count = _write_jsonl(target / "items.jsonl", items)
    qrels_count = _write_qrels(target / "qrels.trec", sorted(qrels))
    result = _metadata(
        name,
        "xiaowu0162/longmemeval-cleaned (LongMemEval-S)",
        [source_path],
        {"docs": docs_count, "queries": item_count, "qrels": qrels_count},
    )
    result["skipped_abstention_queries"] = skipped_abstention
    result["granularity"] = "one corpus per question; one document per session; user turns only"
    return result


def _iso_locomo_date(value: str) -> str:
    # LoCoMo timestamps have no timezone. Treat them as UTC so historical
    # recency implementations receive a deterministic, timezone-aware clock.
    match = re.fullmatch(
        r"\s*(\d{1,2}):(\d{2})\s*([AaPp][Mm])\s+on\s+(\d{1,2})\s+([A-Za-z]+),\s*(\d{4})\s*",
        value,
    )
    if match is None:
        return ""
    hour, minute, meridiem, day, month_name, year = match.groups()
    month = LOCOMO_MONTHS.get(month_name.title())
    if month is None:
        return ""
    hour_value = int(hour)
    if not 1 <= hour_value <= 12:
        return ""
    hour_value %= 12
    if meridiem.lower() == "pm":
        hour_value += 12
    try:
        parsed = dt.datetime(
            int(year),
            month,
            int(day),
            hour_value,
            int(minute),
            tzinfo=dt.timezone.utc,
        )
    except ValueError:
        return ""
    return parsed.isoformat(timespec="seconds")


def convert_locomo() -> dict[str, Any]:
    name = "locomo"
    (source_path,) = _need([RAW / "locomo" / "locomo10.json"])
    payload = json.loads(source_path.read_text(encoding="utf-8"))
    if not isinstance(payload, list):
        raise ValueError(f"{source_path}: expected a JSON array")

    items: list[dict[str, Any]] = []
    queries: list[dict[str, Any]] = []
    qrels: list[tuple[str, str, int]] = []
    docs_count = 0
    skipped_category_5 = 0
    dropped_zero_evidence = 0
    dropped_evidence_ids = 0
    date_parse_failures = 0
    documents_with_unparsed_source_date = 0
    category_counts = {category: 0 for category in LOCOMO_CATEGORIES}

    for raw_item in sorted(payload, key=lambda row: str(row["sample_id"])):
        sample_id = str(raw_item["sample_id"])
        conversation = raw_item["conversation"]
        session_numbers = sorted(
            int(key.removeprefix("session_"))
            for key in conversation
            if key.startswith("session_") and key.removeprefix("session_").isdigit()
        )
        documents: list[dict[str, Any]] = []
        dialogue_ids: set[str] = set()
        session_dates: dict[int, str] = {}
        for session_number in session_numbers:
            date_key = f"session_{session_number}_date_time"
            session_key = f"session_{session_number}"
            source_date = _iso_locomo_date(str(conversation.get(date_key) or ""))
            session_dates[session_number] = source_date
            if not source_date:
                date_parse_failures += 1
            for turn in conversation[session_key]:
                dialogue_id = str(turn["dia_id"])
                if dialogue_id in dialogue_ids:
                    raise ValueError(f"{sample_id}: duplicate dia_id {dialogue_id}")
                dialogue_ids.add(dialogue_id)
                document_id = f"{sample_id}__{dialogue_id}"
                text = f"{turn.get('speaker') or ''}: {turn.get('text') or ''}"
                caption = str(turn.get("blip_caption") or "")
                if caption:
                    text += " " + caption
                documents.append(
                    {
                        "_id": document_id,
                        "title": "",
                        "text": text,
                        "aliases": [],
                        "tags": [],
                        "source_date": source_date,
                    }
                )
                if not source_date:
                    documents_with_unparsed_source_date += 1

        item_queries: list[dict[str, Any]] = []
        for query_index, qa in enumerate(raw_item["qa"]):
            category = int(qa["category"])
            if category == 5:
                skipped_category_5 += 1
                continue
            if category not in LOCOMO_CATEGORIES:
                raise ValueError(f"{sample_id}__q{query_index}: unsupported category {category}")
            relevant: set[str] = set()
            for raw_evidence in qa.get("evidence") or []:
                evidence = str(raw_evidence)
                if evidence not in dialogue_ids:
                    dropped_evidence_ids += 1
                    continue
                relevant.add(f"{sample_id}__{evidence}")
            if not relevant:
                dropped_zero_evidence += 1
                continue
            query_id = f"{sample_id}__q{query_index}"
            query = {
                "_id": query_id,
                "text": str(qa.get("question") or ""),
                "category": category,
            }
            queries.append(query)
            item_queries.append({**query, "relevant": sorted(relevant)})
            qrels.extend((query_id, document_id, 1) for document_id in sorted(relevant))
            category_counts[category] += 1

        if item_queries:
            clock = session_dates[session_numbers[-1]] if session_numbers else ""
            items.append(
                {
                    "_id": sample_id,
                    "question_date": clock,
                    "documents": sorted(documents, key=lambda row: str(row["_id"])),
                    "queries": item_queries,
                }
            )
        docs_count += len(documents)

    target = SETS / name
    conversation_count = _write_jsonl(target / "items.jsonl", items)
    query_count = _write_jsonl(target / "queries.jsonl", queries)
    qrels_count = _write_qrels(target / "qrels.trec", sorted(qrels))
    result = _metadata(
        name,
        "snap-research/locomo data/locomo10.json",
        [source_path],
        {
            "conversations": conversation_count,
            "docs": docs_count,
            "queries": query_count,
            "qrels": qrels_count,
        },
    )
    result["license"] = "CC BY-NC 4.0"
    result["yazan"] = "codex"
    result["model"] = "gpt-5.6-sol"
    result["granularity"] = "one corpus per conversation; one document per dialogue turn"
    result["query_index_base"] = 0
    result["skipped_category_5_queries"] = skipped_category_5
    result["dropped_zero_evidence_queries"] = dropped_zero_evidence
    result["dropped_evidence_ids"] = dropped_evidence_ids
    result["date_parse_failures"] = date_parse_failures
    result["documents_with_unparsed_source_date"] = documents_with_unparsed_source_date
    result["category_counts"] = {str(key): value for key, value in category_counts.items()}
    result["category_names"] = {str(key): value for key, value in LOCOMO_CATEGORIES.items()}
    result["category_names_status"] = "unverified; upstream README was not available locally"
    result["rrf_clock"] = "last indexed (dialogue-bearing) session date_time of each conversation"
    result["status"] = "ready"
    _write_jsonl(target / "manifest.jsonl", [result])
    return result


CONVERTERS = {
    **{name: (lambda selected=name: convert_trmteb(selected)) for name in TRMTEB},
    **{name: (lambda selected=name: convert_beir(selected)) for name in BEIR},
    "scifact-tr-saoud": convert_saoud,
    "longmemeval-s": convert_longmemeval,
    "locomo": convert_locomo,
}


def _select(value: str) -> list[str]:
    selected = list(ALL_SETS) if value == "all" else [part.strip() for part in value.split(",") if part.strip()]
    unknown = [name for name in selected if name not in CONVERTERS]
    if unknown:
        raise SystemExit(f"unknown set(s): {', '.join(unknown)}")
    return selected


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sets", default="all", help="all or a comma-separated set list")
    args = parser.parse_args()
    SETS.mkdir(parents=True, exist_ok=True)
    manifest_path = SETS / "manifest.json"
    if manifest_path.exists():
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    else:
        manifest = {"datasets": {}}
    manifest["format_version"] = 1
    manifest["yazan"] = "codex"
    manifest["model"] = "gpt-5.6-sol"
    manifest["raw_metadata"] = {}
    for metadata_name in ("hf-revisions.txt", "sha256.txt"):
        metadata_path = RAW / metadata_name
        if metadata_path.is_file():
            manifest["raw_metadata"][metadata_name] = metadata_path.read_text(encoding="utf-8")
        else:
            print(f"missing raw file: {metadata_path.relative_to(ROOT)}", file=sys.stderr)
    selected_sets = _select(args.sets)
    for name in selected_sets:
        try:
            record = CONVERTERS[name]()
        except MissingRawFile as exc:
            print(f"{name}: missing raw file: {exc}")
            if name == "locomo":
                _write_jsonl(
                    SETS / name / "manifest.jsonl",
                    [{"name": name, "status": "skipped", "reason": f"missing raw file: {exc}", "yazan": "codex", "model": "gpt-5.6-sol"}],
                )
            else:
                manifest["datasets"][name] = {"name": name, "status": "skipped", "reason": f"missing raw file: {exc}"}
            continue
        except Exception as exc:
            print(f"{name}: skipped: {type(exc).__name__}: {exc}")
            if name == "locomo":
                _write_jsonl(
                    SETS / name / "manifest.jsonl",
                    [{"name": name, "status": "skipped", "reason": f"{type(exc).__name__}: {exc}", "yazan": "codex", "model": "gpt-5.6-sol"}],
                )
            else:
                manifest["datasets"][name] = {"name": name, "status": "skipped", "reason": f"{type(exc).__name__}: {exc}"}
            continue
        record["status"] = "ready"
        if name != "locomo":
            manifest["datasets"][name] = record
        counts = record["counts"]
        if name == "locomo":
            print(
                f"{name}: conversations={counts['conversations']} docs={counts['docs']} "
                f"queries={counts['queries']} qrels={counts['qrels']} "
                f"dropped_evidence_ids={record['dropped_evidence_ids']} "
                f"dropped_zero_evidence_queries={record['dropped_zero_evidence_queries']} "
                f"skipped_category_5_queries={record['skipped_category_5_queries']}"
            )
        else:
            print(f"{name}: docs={counts['docs']} queries={counts['queries']} qrels={counts['qrels']}")
    non_locomo_selected = [name for name in selected_sets if name != "locomo"]
    if non_locomo_selected:
        temporary = manifest_path.with_suffix(".json.tmp")
        temporary.write_text(json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        os.replace(temporary, manifest_path)
        print(f"manifest: {manifest_path.relative_to(ROOT)}")
    if "locomo" in selected_sets:
        print(f"manifest: {(SETS / 'locomo' / 'manifest.jsonl').relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
