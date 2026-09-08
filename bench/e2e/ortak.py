#!/usr/bin/env python3
"""Shared paths and data loading for the end-to-end LoCoMo benchmark.

yazan: codex
model: gpt-5.6-sol
"""

from __future__ import annotations

from collections import defaultdict
from datetime import datetime, timezone
import importlib.util
import json
import math
from pathlib import Path
import random
import re
import sys
from types import ModuleType
from typing import Any, Iterable


MODEL_ID = "gpt-5.6-sol"
E2E_DIR = Path(__file__).resolve().parent
BENCHMARK_DIR = E2E_DIR.parent
REPO_ROOT = BENCHMARK_DIR.parent.parent
RAW_PATH = BENCHMARK_DIR / ".data" / "raw" / "locomo" / "locomo10.json"
SET_DIR = BENCHMARK_DIR / ".data" / "sets" / "locomo"
ITEMS_PATH = SET_DIR / "items.jsonl"
PROMPTS_PATH = BENCHMARK_DIR / ".data" / "raw" / "mem0-protocol" / "prompts.py"
WORK_DIR = BENCHMARK_DIR / ".e2e"
VAULTS_DIR = WORK_DIR / "vaults"
SUBSET_PATH = WORK_DIR / "subset-300.json"

_SESSION_KEY = re.compile(r"^session_(\d+)$")
_QUESTION_ID = re.compile(r"^(?P<sample>.+)__q(?P<index>\d+)$")
_PROMPTS_MODULE: ModuleType | None = None
_RETRIEVE_MODULE: ModuleType | None = None


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, 1):
            if not line.strip():
                continue
            value = json.loads(line)
            if not isinstance(value, dict):
                raise ValueError(f"{path}:{line_number}: expected JSON object")
            rows.append(value)
    return rows


def write_text_atomic(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{id(text)}.tmp")
    temporary.write_text(text, encoding="utf-8", newline="\n")
    temporary.replace(path)


def write_json_atomic(path: Path, value: Any) -> None:
    write_text_atomic(
        path,
        json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
    )


def load_raw() -> list[dict[str, Any]]:
    value = json.loads(RAW_PATH.read_text(encoding="utf-8"))
    if not isinstance(value, list):
        raise ValueError(f"{RAW_PATH}: expected JSON list")
    return value


def raw_by_sample() -> dict[str, dict[str, Any]]:
    return {str(row["sample_id"]): row for row in load_raw()}


def load_items() -> list[dict[str, Any]]:
    return read_jsonl(ITEMS_PATH)


def load_prompts() -> ModuleType:
    global _PROMPTS_MODULE
    if _PROMPTS_MODULE is not None:
        return _PROMPTS_MODULE
    spec = importlib.util.spec_from_file_location("e2e_mem0_prompts", PROMPTS_PATH)
    if spec is None or spec.loader is None:
        raise ImportError(f"cannot load {PROMPTS_PATH}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    _PROMPTS_MODULE = module
    return module


def load_retrieve() -> ModuleType:
    """Load the repository's current retrieve.py with its sibling imports."""
    global _RETRIEVE_MODULE
    if _RETRIEVE_MODULE is not None:
        return _RETRIEVE_MODULE
    scripts_dir = REPO_ROOT / "scripts"
    module_name = "e2e_current_retrieve"
    spec = importlib.util.spec_from_file_location(module_name, scripts_dir / "retrieve.py")
    if spec is None or spec.loader is None:
        raise ImportError(f"cannot load {scripts_dir / 'retrieve.py'}")
    sys.path.insert(0, str(scripts_dir))
    try:
        module = importlib.util.module_from_spec(spec)
        sys.modules[module_name] = module
        spec.loader.exec_module(module)
    finally:
        try:
            sys.path.remove(str(scripts_dir))
        except ValueError:
            pass
    _RETRIEVE_MODULE = module
    return module


def parse_locomo_date(value: str) -> datetime:
    for pattern in ("%I:%M %p on %d %B, %Y", "%I:%M %p on %d %b, %Y"):
        try:
            return datetime.strptime(value, pattern).replace(tzinfo=timezone.utc)
        except (TypeError, ValueError):
            continue
    raise ValueError(f"unrecognized LoCoMo date: {value!r}")


def human_date(value: str) -> str:
    parsed = parse_locomo_date(value)
    return f"{parsed.strftime('%B')} {parsed.day}, {parsed.year}"


def iso_date(value: str) -> str:
    return parse_locomo_date(value).isoformat(timespec="seconds")


def sorted_sessions(entry: dict[str, Any]) -> list[dict[str, Any]]:
    conversation = entry["conversation"]
    sessions: list[dict[str, Any]] = []
    for key, turns in conversation.items():
        match = _SESSION_KEY.fullmatch(key)
        if match is None or not isinstance(turns, list):
            continue
        date_text = str(conversation.get(f"{key}_date_time") or "")
        sessions.append(
            {
                "key": key,
                "number": int(match.group(1)),
                "date_text": date_text,
                "date": parse_locomo_date(date_text),
                "turns": turns,
            }
        )
    return sorted(sessions, key=lambda row: (row["date"], row["number"]))


def turn_text(turn: dict[str, Any], *, caption_label: bool = False) -> str:
    speaker = str(turn.get("speaker") or "Unknown")
    text = str(turn.get("text") or "")
    caption = str(turn.get("blip_caption") or "")
    rendered = f"{speaker}: {text}"
    if caption:
        rendered += f" [Image caption: {caption}]" if caption_label else f" {caption}"
    return rendered


def question_rows() -> list[dict[str, Any]]:
    """Join normalized scored queries to gold answers by raw QA-list index."""
    raw = raw_by_sample()
    rows: list[dict[str, Any]] = []
    for item in load_items():
        sample_id = str(item["_id"])
        entry = raw[sample_id]
        for query in item["queries"]:
            qid = str(query["_id"])
            match = _QUESTION_ID.fullmatch(qid)
            if match is None or match.group("sample") != sample_id:
                raise ValueError(f"unexpected normalized question id: {qid}")
            raw_index = int(match.group("index"))
            qa = entry["qa"][raw_index]
            category = int(query["category"])
            if str(qa["question"]) != str(query["text"]):
                raise ValueError(f"question mismatch for {qid}")
            if int(qa["category"]) != category:
                raise ValueError(f"category mismatch for {qid}")
            rows.append(
                {
                    "qid": qid,
                    "sample_id": sample_id,
                    "category": category,
                    "question": str(query["text"]),
                    "gold": str(qa["answer"]),
                    "relevant": [str(value) for value in query.get("relevant", [])],
                    "question_date": str(item["question_date"]),
                }
            )
    return rows


def _stratified_counts(rows: Iterable[dict[str, Any]], size: int) -> dict[int, int]:
    by_category: dict[int, int] = defaultdict(int)
    for row in rows:
        by_category[int(row["category"])] += 1
    total = sum(by_category.values())
    if size > total:
        raise ValueError(f"subset size {size} exceeds {total} questions")
    exact = {category: size * count / total for category, count in by_category.items()}
    counts = {category: math.floor(value) for category, value in exact.items()}
    remainder = size - sum(counts.values())
    order = sorted(exact, key=lambda category: (-(exact[category] - counts[category]), category))
    for category in order[:remainder]:
        counts[category] += 1
    return counts


def ensure_subset(size: int = 300, seed: int = 42) -> dict[str, Any]:
    rows = question_rows()
    expected_counts = _stratified_counts(rows, size)
    if SUBSET_PATH.is_file():
        payload = json.loads(SUBSET_PATH.read_text(encoding="utf-8"))
        qids = payload.get("qids") if isinstance(payload, dict) else None
        if (
            isinstance(qids, list)
            and len(qids) == size
            and len(set(qids)) == size
            and all(isinstance(qid, str) for qid in qids)
        ):
            return payload
        raise ValueError(f"invalid existing subset file: {SUBSET_PATH}")

    rng = random.Random(seed)
    by_category: dict[int, list[str]] = defaultdict(list)
    order = {str(row["qid"]): index for index, row in enumerate(rows)}
    for row in rows:
        by_category[int(row["category"])].append(str(row["qid"]))
    chosen: list[str] = []
    for category in sorted(by_category):
        chosen.extend(rng.sample(by_category[category], expected_counts[category]))
    chosen.sort(key=order.__getitem__)
    payload = {
        "yazan": "codex",
        "model": MODEL_ID,
        "seed": seed,
        "size": size,
        "population": len(rows),
        "category_counts": {str(key): expected_counts[key] for key in sorted(expected_counts)},
        "qids": chosen,
    }
    write_json_atomic(SUBSET_PATH, payload)
    return payload


def selected_questions(condition: str, max_questions: int | None = None, subset: bool = False, sample_ids: set[str] | None = None) -> list[dict[str, Any]]:
    rows = question_rows()
    if condition == "c" or subset:
        wanted = set(ensure_subset()["qids"])
        rows = [row for row in rows if row["qid"] in wanted]
    if sample_ids:
        rows = [row for row in rows if row["sample_id"] in sample_ids]
    if max_questions is not None:
        rows = rows[:max_questions]
    return rows


def vault_path(sample_id: str) -> Path:
    return VAULTS_DIR / sample_id

