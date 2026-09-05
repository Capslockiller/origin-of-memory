#!/usr/bin/env python3
"""Judge saved E2E LoCoMo answers with Mem0's public J-score prompt.

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
import shutil
import subprocess
import time
from typing import Any

from ortak import MODEL_ID, WORK_DIR, load_prompts, read_jsonl, write_text_atomic


def _parse_judgment(text: str) -> dict[str, str] | None:
    candidates = [text.strip()]
    candidates.extend(
        match.group(1).strip()
        for match in re.finditer(r"```(?:json)?\s*(\{.*?\})\s*```", text, re.DOTALL | re.IGNORECASE)
    )
    decoder = json.JSONDecoder()
    for offset, character in enumerate(text):
        if character != "{":
            continue
        try:
            value, _end = decoder.raw_decode(text[offset:])
        except json.JSONDecodeError:
            continue
        candidates.append(json.dumps(value))
    for candidate in candidates:
        try:
            value = json.loads(candidate)
        except (json.JSONDecodeError, TypeError):
            continue
        if not isinstance(value, dict):
            continue
        label = str(value.get("label") or "").upper()
        if label not in {"CORRECT", "WRONG"}:
            continue
        return {"label": label, "reasoning": str(value.get("reasoning") or "")}
    return None


def _fake_judge(question: str, gold: str, answer: str) -> dict[str, str]:
    digest = hashlib.sha256(f"{question}\0{gold}\0{answer}".encode("utf-8")).digest()
    label = "CORRECT" if digest[0] < 128 else "WRONG"
    return {
        "label": label,
        "reasoning": "Deterministic offline fake verdict; it is not an accuracy measurement.",
    }


def _claude_judge(system_prompt: str, judge_prompt: str) -> dict[str, str]:
    executable = shutil.which("claude")
    if executable is None:
        return {"label": "unparsed", "reasoning": "claude-cli-missing"}
    command = [executable, "-p", "--model", "claude-haiku-4-5-20251001", "--output-format", "text"]
    environment = os.environ.copy()
    environment["BEYIN_INVOKED_BY"] = "e2e-locomo-judge"
    combined = f"{system_prompt}\n\n{judge_prompt}"
    last_reason = "unparsed"
    for attempt in range(2):
        try:
            result = subprocess.run(
                command,
                input=combined,
                text=True,
                encoding="utf-8",
                errors="replace",
                capture_output=True,
                env=environment,
                timeout=120,
                check=False,
            )
        except subprocess.TimeoutExpired:
            return {"label": "unparsed", "reasoning": "claude-timeout"}
        except OSError as exc:
            return {"label": "unparsed", "reasoning": f"claude-exec-error:{exc.__class__.__name__}"}
        if result.returncode != 0:
            return {"label": "unparsed", "reasoning": f"claude-exit-{result.returncode}"}
        parsed = _parse_judgment(result.stdout)
        if parsed is not None:
            return parsed
        last_reason = f"unparsed-after-attempt-{attempt + 1}"
    return {"label": "unparsed", "reasoning": last_reason}


def _existing_ids(path: Path) -> set[str]:
    if not path.is_file():
        return set()
    return {str(row["qid"]) for row in read_jsonl(path)}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--condition", choices=("a", "b", "c"), required=True)
    parser.add_argument("--backend", choices=("claude", "fake"), default="claude")
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--resume", action="store_true")
    args = parser.parse_args()
    if args.workers < 1:
        raise SystemExit("--workers must be positive")

    answers_path = WORK_DIR / "cevaplar" / f"{args.condition}.jsonl"
    if not answers_path.is_file():
        raise SystemExit(f"missing answers: {answers_path}")
    output = WORK_DIR / "yargi" / f"{args.condition}.jsonl"
    output.parent.mkdir(parents=True, exist_ok=True)
    if not args.resume:
        write_text_atomic(output, "")
    existing = _existing_ids(output) if args.resume else set()
    answers = [row for row in read_jsonl(answers_path) if str(row["qid"]) not in existing]
    if not answers:
        print(f"{args.condition}: 0 pending; checkpoint unchanged")
        return 0

    prompts = load_prompts()

    def process(row: dict[str, Any]) -> dict[str, Any]:
        category = int(row["category"])
        gold = prompts.preprocess_answer(category, str(row["gold"]))
        judge_prompt = prompts.get_judge_prompt(
            category,
            str(row["question"]),
            gold,
            str(row["answer"]),
        )
        started = time.monotonic()
        if args.backend == "fake":
            judgment = _fake_judge(str(row["question"]), gold, str(row["answer"]))
        else:
            judgment = _claude_judge(prompts.JUDGE_SYSTEM_PROMPT, judge_prompt)
        elapsed_ms = int((time.monotonic() - started) * 1000)
        return {
            "yazan": "codex",
            "model": MODEL_ID,
            "judge_backend": args.backend,
            "judge_model": "haiku" if args.backend == "claude" else "deterministic-fake",
            "qid": row["qid"],
            "label": judgment["label"],
            "reasoning": judgment["reasoning"],
            "judge_ms": elapsed_ms,
        }

    completed = 0
    with output.open("a", encoding="utf-8", newline="\n") as handle:
        with ThreadPoolExecutor(max_workers=args.workers) as executor:
            for result in executor.map(process, answers):
                handle.write(json.dumps(result, ensure_ascii=False) + "\n")
                handle.flush()
                completed += 1
                if completed % 25 == 0 or completed == len(answers):
                    print(f"{args.condition}: {completed}/{len(answers)}")
    print(f"checkpoint: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
