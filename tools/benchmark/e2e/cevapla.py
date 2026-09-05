#!/usr/bin/env python3
"""Answer LoCoMo questions under raw, compiled, or full-context memory.

yazan: codex
model: gpt-5.6-sol
"""

from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import re
import time
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
import uuid

from ortak import (
    MODEL_ID,
    WORK_DIR,
    human_date,
    load_items,
    load_prompts,
    load_retrieve,
    raw_by_sample,
    read_jsonl,
    selected_questions,
    sorted_sessions,
    turn_text,
    vault_path,
    write_text_atomic,
)


OLLAMA_URL = "http://localhost:11434/api/generate"
OLLAMA_MODEL = "qwen3:30b-a3b-instruct-2507-q4_K_M"
PER_NOTE_CAP = 1_500
TOTAL_BODY_CAP = 4_500
_MEM0_DATED_LINE = re.compile(
    r"\((?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday), "
    r"(?P<month>[A-Za-z]+) 0?(?P<day>\d{1,2}), (?P<year>\d{4})\)"
)


def _prompt_timestamp(value: str) -> str:
    """Feed Mem0's formatter a naive ISO value; its offset path falls back to YYYY-MM-DD."""
    return value[:19] if len(value) >= 19 else value


def _answer_prompt(prompts: Any, question: str, memories: list[dict[str, str]], reference_date: str) -> str:
    prompt = prompts.get_answer_generation_prompt(
        question,
        memories,
        reference_date=reference_date,
    )
    return _MEM0_DATED_LINE.sub(
        lambda match: f"({match.group('month')} {int(match.group('day'))}, {match.group('year')})",
        prompt,
    )


class MemoryProvider:
    def __init__(self, condition: str, sample_ids: set[str]):
        self.condition = condition
        self.retrieve = load_retrieve()
        self.items = {str(row["_id"]): row for row in load_items()}
        self.raw = raw_by_sample()
        self.db_paths: dict[str, Path] = {}
        if condition == "a":
            self._build_raw_indexes(sample_ids)
        elif condition == "b":
            self._build_compiled_indexes(sample_ids)

    def _build_raw_indexes(self, sample_ids: set[str]) -> None:
        root = WORK_DIR / "indexes" / "raw-turns"
        root.mkdir(parents=True, exist_ok=True)
        for sample_id in sorted(sample_ids):
            item = self.items[sample_id]
            notes = [
                self.retrieve.ConceptNote(
                    name=str(row["_id"]),
                    title=str(row.get("title") or ""),
                    aliases=tuple(str(value) for value in row.get("aliases", [])),
                    tags=tuple(str(value) for value in row.get("tags", [])),
                    body=str(row.get("text") or ""),
                    source_date=str(row.get("source_date") or ""),
                )
                for row in item["documents"]
            ]
            target = root / f"{sample_id}.db"
            temporary = root / f".{sample_id}.{uuid.uuid4().hex}.tmp"
            self.retrieve._create_database(temporary, notes)
            os.replace(temporary, target)
            self.db_paths[sample_id] = target

    def _build_compiled_indexes(self, sample_ids: set[str]) -> None:
        for sample_id in sorted(sample_ids):
            vault = vault_path(sample_id)
            concepts = vault / "knowledge" / "concepts"
            if not concepts.is_dir() or not any(concepts.glob("*.md")):
                raise FileNotFoundError(
                    f"no compiled notes for {sample_id}; run derle.py first"
                )
            state_dir = WORK_DIR / "indexes" / "compiled-notes" / sample_id
            report = self.retrieve.build_index(vault_root=vault, state_dir=state_dir)
            self.db_paths[sample_id] = Path(report["db_path"])

    def reference_date(self, sample_id: str) -> str:
        sessions = sorted_sessions(self.raw[sample_id])
        if not sessions:
            return "2023"
        return human_date(sessions[-1]["date_text"])

    def memories(self, row: dict[str, Any]) -> tuple[list[dict[str, str]], list[str]]:
        sample_id = str(row["sample_id"])
        if self.condition == "a":
            return self._raw_memories(sample_id, str(row["question"]))
        if self.condition == "b":
            return self._compiled_memories(sample_id, str(row["question"]))
        return self._full_context(sample_id)

    def _raw_memories(self, sample_id: str, question: str) -> tuple[list[dict[str, str]], list[str]]:
        hits = self.retrieve.search(
            question,
            limit=10,
            db_path=self.db_paths[sample_id],
            mode="bm25",
        )
        documents = {str(row["_id"]): row for row in self.items[sample_id]["documents"]}
        memories = [
            {
                "memory": documents[hit.name]["text"],
                "created_at": _prompt_timestamp(str(documents[hit.name]["source_date"])),
            }
            for hit in hits
        ]
        return memories, [hit.name for hit in hits]

    def _compiled_memories(self, sample_id: str, question: str) -> tuple[list[dict[str, str]], list[str]]:
        hits = self.retrieve.search(
            question,
            limit=5,
            db_path=self.db_paths[sample_id],
            mode="bm25",
        )
        memories: list[dict[str, str]] = []
        identifiers: list[str] = []
        used_body = 0
        concepts = vault_path(sample_id) / "knowledge" / "concepts"
        for hit in hits:
            remaining = TOTAL_BODY_CAP - used_body
            if remaining <= 0:
                break
            body = hit.body[: min(PER_NOTE_CAP, remaining)]
            note = self.retrieve.read_concept(concepts / f"{hit.name}.md")
            memories.append(
                {
                    "memory": f"{hit.title}\n{body}",
                    "created_at": _prompt_timestamp(note.source_date),
                }
            )
            identifiers.append(hit.name)
            used_body += len(body)
        return memories, identifiers

    def _full_context(self, sample_id: str) -> tuple[list[dict[str, str]], list[str]]:
        memories: list[dict[str, str]] = []
        identifiers: list[str] = []
        for session in sorted_sessions(self.raw[sample_id]):
            memory = "\n".join(turn_text(turn) for turn in session["turns"])
            memories.append(
                {
                    "memory": memory,
                    "created_at": session["date"].replace(tzinfo=None).isoformat(timespec="seconds"),
                }
            )
            identifiers.append(f"{sample_id}__{session['key']}")
        return memories, identifiers


def _fake_answer(prompt: str) -> tuple[str, int, int, int]:
    digest = hashlib.sha256(prompt.encode("utf-8")).hexdigest()[:20]
    answer = f"offline-fake-{digest}"
    return answer, len(prompt) // 4, len(answer) // 4, 0


def _ollama_answer(prompt: str, timeout: int, num_ctx: int) -> tuple[str, int, int, int]:
    payload = {
        "model": OLLAMA_MODEL,
        "prompt": prompt,
        "stream": False,
        "options": {"temperature": 0, "num_ctx": num_ctx, "num_predict": 512},
    }
    request = Request(
        OLLAMA_URL,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    started = time.monotonic()
    last_exc: Exception | None = None
    for attempt in range(4):  # transient 5xx/timeouts: retry with backoff
        try:
            with urlopen(request, timeout=timeout) as response:
                value = json.loads(response.read().decode("utf-8"))
            break
        except (HTTPError, URLError, TimeoutError, OSError) as exc:
            last_exc = exc
            time.sleep(15 * (attempt + 1))
    else:
        raise RuntimeError(f"Ollama request failed after 4 attempts: {last_exc}") from last_exc
    wall_ms = int((time.monotonic() - started) * 1000)
    answer = str(value.get("response") or "").strip()
    if "ANSWER:" in answer:
        answer = answer.rsplit("ANSWER:", 1)[-1].strip()
    total_duration = int(value.get("total_duration") or 0)
    duration_ms = total_duration // 1_000_000 if total_duration else wall_ms
    return (
        answer,
        int(value.get("prompt_eval_count") or 0),
        int(value.get("eval_count") or 0),
        duration_ms,
    )


def _answered(path: Path) -> set[str]:
    if not path.is_file():
        return set()
    return {str(row["qid"]) for row in read_jsonl(path)}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--condition", choices=("a", "b", "c"), required=True)
    parser.add_argument("--backend", choices=("ollama", "fake"), default="ollama")
    parser.add_argument("--workers", type=int, default=1)
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--max-questions", type=int)
    parser.add_argument("--timeout", type=int, default=1800)
    parser.add_argument("--num-ctx", type=int, default=32768)
    parser.add_argument("--subset", action="store_true", help="restrict a/b to the fixed 300-question subset")
    parser.add_argument("--sample-ids", default="", help="comma-separated conversation ids to include")
    args = parser.parse_args()
    if args.workers < 1 or args.timeout < 1 or args.num_ctx < 1:
        raise SystemExit("--workers, --timeout, and --num-ctx must be positive")
    if args.max_questions is not None and args.max_questions < 1:
        raise SystemExit("--max-questions must be positive")

    output = WORK_DIR / "cevaplar" / f"{args.condition}.jsonl"
    output.parent.mkdir(parents=True, exist_ok=True)
    if not args.resume:
        write_text_atomic(output, "")
    existing = _answered(output) if args.resume else set()
    questions = [
        row for row in selected_questions(args.condition, args.max_questions, subset=args.subset, sample_ids={x.strip() for x in args.sample_ids.split(",") if x.strip()} or None)
        if row["qid"] not in existing
    ]
    if not questions:
        print(f"{args.condition}: 0 pending; checkpoint unchanged")
        return 0

    provider = MemoryProvider(args.condition, {str(row["sample_id"]) for row in questions})
    prompts = load_prompts()

    def process(row: dict[str, Any]) -> dict[str, Any]:
        memories, retrieved = provider.memories(row)
        prompt = _answer_prompt(
            prompts,
            str(row["question"]),
            memories,
            provider.reference_date(str(row["sample_id"])),
        )
        if args.backend == "fake":
            answer, prompt_count, eval_count, duration_ms = _fake_answer(prompt)
        else:
            answer, prompt_count, eval_count, duration_ms = _ollama_answer(
                prompt, args.timeout, args.num_ctx
            )
        return {
            "yazan": "codex",
            "model": MODEL_ID,
            "answer_backend": args.backend,
            "answer_model": OLLAMA_MODEL if args.backend == "ollama" else "deterministic-fake",
            "qid": row["qid"],
            "sample_id": row["sample_id"],
            "category": row["category"],
            "question": row["question"],
            "gold": row["gold"],
            "retrieved_ids": retrieved,
            "prompt_chars": len(prompt),
            "answer": answer,
            "prompt_eval_count": prompt_count,
            "eval_count": eval_count,
            "duration_ms": duration_ms,
        }

    completed = 0
    with output.open("a", encoding="utf-8", newline="\n") as handle:
        with ThreadPoolExecutor(max_workers=args.workers) as executor:
            for result in executor.map(process, questions):
                handle.write(json.dumps(result, ensure_ascii=False) + "\n")
                handle.flush()
                completed += 1
                if completed % 25 == 0 or completed == len(questions):
                    print(f"{args.condition}: {completed}/{len(questions)}")
    print(f"checkpoint: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
