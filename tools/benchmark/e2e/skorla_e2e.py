#!/usr/bin/env python3
"""Score E2E LoCoMo judgments and run exact McNemar comparisons.

yazan: codex
model: gpt-5.6-sol
"""

from __future__ import annotations

import csv
import io
from itertools import combinations
from typing import Any

from scipy.stats import binomtest

from ortak import MODEL_ID, WORK_DIR, ensure_subset, load_prompts, question_rows, read_jsonl, write_text_atomic


CONDITIONS = ("a", "b", "c")
CONDITION_NAMES = {
    "a": "raw-turns",
    "b": "compiled-notes",
    "c": "full-context",
}


def _load_condition(condition: str) -> tuple[dict[str, dict[str, Any]], dict[str, dict[str, Any]]]:
    answers_path = WORK_DIR / "cevaplar" / f"{condition}.jsonl"
    judgments_path = WORK_DIR / "yargi" / f"{condition}.jsonl"
    answers = {} if not answers_path.is_file() else {
        str(row["qid"]): row for row in read_jsonl(answers_path)
    }
    judgments = {} if not judgments_path.is_file() else {
        str(row["qid"]): row for row in read_jsonl(judgments_path)
    }
    return answers, judgments


def _score_row(
    condition: str,
    scope: str,
    category: int | None,
    expected_qids: set[str],
    answers: dict[str, dict[str, Any]],
    judgments: dict[str, dict[str, Any]],
    *,
    status_override: str = "",
) -> dict[str, Any]:
    qids = [
        qid
        for qid in expected_qids
        if qid in answers
        and qid in judgments
    ]
    expected = len(expected_qids)
    labels = [str(judgments[qid].get("label") or "unparsed") for qid in qids]
    correct = labels.count("CORRECT")
    wrong = labels.count("WRONG")
    unparsed = len(labels) - correct - wrong
    n = len(labels)
    status = status_override or ("complete" if n == expected else "incomplete")
    return {
        "yazan": "codex",
        "model": MODEL_ID,
        "row_type": "score",
        "scope": scope,
        "condition": condition,
        "comparison": "",
        "category": "overall" if category is None else str(category),
        "n": n,
        "expected": expected,
        "correct": correct,
        "wrong": wrong,
        "unparsed": unparsed,
        "j_score": "" if n == 0 else f"{100.0 * correct / n:.6f}",
        "n01": "",
        "n10": "",
        "discordant": "",
        "p_value": "",
        "status": status,
    }


def _mcnemar(
    left: str,
    right: str,
    scope: str,
    allowed: set[str],
    data: dict[str, tuple[dict[str, dict[str, Any]], dict[str, dict[str, Any]]]],
) -> dict[str, Any]:
    left_answers, left_judgments = data[left]
    right_answers, right_judgments = data[right]
    shared = sorted(
        allowed
        & set(left_answers)
        & set(right_answers)
        & set(left_judgments)
        & set(right_judgments)
    )
    n01 = sum(
        str(left_judgments[qid].get("label")) != "CORRECT"
        and str(right_judgments[qid].get("label")) == "CORRECT"
        for qid in shared
    )
    n10 = sum(
        str(left_judgments[qid].get("label")) == "CORRECT"
        and str(right_judgments[qid].get("label")) != "CORRECT"
        for qid in shared
    )
    discordant = n01 + n10
    p_value = 1.0 if discordant == 0 else float(
        binomtest(min(n01, n10), discordant, p=0.5, alternative="two-sided").pvalue
    )
    return {
        "yazan": "codex",
        "model": MODEL_ID,
        "row_type": "mcnemar",
        "scope": scope,
        "condition": "",
        "comparison": f"{left}-{right}",
        "category": "overall",
        "n": len(shared),
        "expected": len(allowed),
        "correct": "",
        "wrong": "",
        "unparsed": "",
        "j_score": "",
        "n01": n01,
        "n10": n10,
        "discordant": discordant,
        "p_value": f"{p_value:.10g}",
        "status": "complete" if len(shared) == len(allowed) else "incomplete",
    }


def _compile_cost_rows() -> list[dict[str, str]]:
    path = WORK_DIR / "maliyet-derleme.csv"
    if not path.is_file():
        return []
    with path.open("r", encoding="utf-8", newline="") as handle:
        return list(csv.DictReader(handle))


def _answer_costs(data: dict[str, tuple[dict[str, dict[str, Any]], dict[str, dict[str, Any]]]]) -> list[dict[str, Any]]:
    rows = []
    for condition in CONDITIONS:
        answers, _judgments = data[condition]
        values = list(answers.values())
        rows.append(
            {
                "condition": condition,
                "calls": len(values),
                "prompt_tokens": sum(int(row.get("prompt_eval_count", 0)) for row in values),
                "output_tokens": sum(int(row.get("eval_count", 0)) for row in values),
                "duration_ms": sum(int(row.get("duration_ms", 0)) for row in values),
                "prompt_chars": sum(int(row.get("prompt_chars", 0)) for row in values),
            }
        )
    return rows


def _judge_costs(data: dict[str, tuple[dict[str, dict[str, Any]], dict[str, dict[str, Any]]]]) -> list[dict[str, Any]]:
    return [
        {
            "condition": condition,
            "calls": len(data[condition][1]),
            "duration_ms": sum(int(row.get("judge_ms", 0)) for row in data[condition][1].values()),
        }
        for condition in CONDITIONS
    ]


def _markdown_table(headers: list[str], rows: list[list[Any]]) -> str:
    lines = [
        "| " + " | ".join(headers) + " |",
        "|" + "|".join("---" for _ in headers) + "|",
    ]
    lines.extend("| " + " | ".join(str(value) for value in row) + " |" for row in rows)
    return "\n".join(lines)


def main() -> int:
    prompts = load_prompts()
    questions = question_rows()
    all_qids = {str(row["qid"]) for row in questions}
    subset_qids = set(ensure_subset()["qids"])
    categories = {str(row["qid"]): int(row["category"]) for row in questions}
    data = {condition: _load_condition(condition) for condition in CONDITIONS}

    score_rows: list[dict[str, Any]] = []
    for condition in CONDITIONS:
        answers, judgments = data[condition]
        for category in (None, 1, 2, 3, 4):
            full_expected = (
                all_qids
                if category is None
                else {qid for qid in all_qids if categories[qid] == category}
            )
            subset_expected = (
                subset_qids
                if category is None
                else {qid for qid in subset_qids if categories[qid] == category}
            )
            if condition == "c":
                score_rows.append(
                    _score_row(
                        condition,
                        "full",
                        category,
                        full_expected,
                        {},
                        {},
                        status_override="not-run-by-protocol",
                    )
                )
            else:
                score_rows.append(
                    _score_row(condition, "full", category, full_expected, answers, judgments)
                )
            score_rows.append(
                _score_row(condition, "subset-300", category, subset_expected, answers, judgments)
            )

    test_rows = [_mcnemar("a", "b", "full", all_qids, data)]
    test_rows.extend(
        _mcnemar(left, right, "subset-300", subset_qids, data)
        for left, right in combinations(CONDITIONS, 2)
    )
    csv_rows = score_rows + test_rows
    fields = [
        "yazan", "model", "row_type", "scope", "condition", "comparison",
        "category", "n", "expected", "correct", "wrong", "unparsed",
        "j_score", "n01", "n10", "discordant", "p_value", "status",
    ]
    buffer = io.StringIO(newline="")
    writer = csv.DictWriter(buffer, fieldnames=fields, lineterminator="\n")
    writer.writeheader()
    writer.writerows(csv_rows)

    score_dir = WORK_DIR / "skor"
    score_dir.mkdir(parents=True, exist_ok=True)
    write_text_atomic(score_dir / "skor_e2e.csv", buffer.getvalue())

    category_names = prompts.CATEGORY_NAMES
    display_scores = []
    for row in score_rows:
        category = row["category"]
        category_text = "overall" if category == "overall" else f"{category} {category_names[int(category)]}"
        value = "n/a" if row["j_score"] == "" else f"{float(row['j_score']):.2f}%"
        display_scores.append(
            [
                row["scope"],
                f"{row['condition']} {CONDITION_NAMES[row['condition']]}",
                category_text,
                f"{row['n']}/{row['expected']}",
                value,
                row["unparsed"],
                row["status"],
            ]
        )

    display_tests = [
        [
            row["scope"], row["comparison"], row["n"], row["n01"], row["n10"],
            row["discordant"], row["p_value"], row["status"],
        ]
        for row in test_rows
    ]
    compile_costs = _compile_cost_rows()
    answer_costs = _answer_costs(data)
    judge_costs = _judge_costs(data)
    observed_answer_backends = sorted(
        {
            str(row.get("answer_backend") or "unknown")
            for condition in CONDITIONS
            for row in data[condition][0].values()
        }
    )
    observed_judge_backends = sorted(
        {
            str(row.get("judge_backend") or "unknown")
            for condition in CONDITIONS
            for row in data[condition][1].values()
        }
    )
    backend_note = (
        f"Observed answer backends: {', '.join(observed_answer_backends) or 'none'}; "
        f"judge backends: {', '.join(observed_judge_backends) or 'none'}."
    )
    if "fake" in observed_answer_backends or "fake" in observed_judge_backends:
        backend_note += " Fake-backend results validate plumbing only and are not accuracy measurements."

    report = (
        "---\n"
        "yazan: codex\n"
        f"model: {MODEL_ID}\n"
        "---\n\n"
        "# E2E LoCoMo Report\n\n"
        "J-score counts `CORRECT` over every available judgment; `unparsed` is retained in the denominator. "
        "Condition c is intentionally not run on the full 1,531-question set. "
        + backend_note
        + "\n\n"
        "## J-score\n\n"
        + _markdown_table(
            ["Scope", "Condition", "Category", "Judged/expected", "J", "Unparsed", "Status"],
            display_scores,
        )
        + "\n\n## Exact McNemar tests\n\n"
        + _markdown_table(
            ["Scope", "Pair", "Paired", "n01", "n10", "Discordant", "p", "Status"],
            display_tests,
        )
        + "\n\n`p` is the two-sided exact binomial test on discordant pairs (`scipy.stats.binomtest`).\n\n"
        "## Compile cost by conversation\n\n"
        + (
            _markdown_table(
                ["Vault", "Calls", "Input tokens est.", "Output tokens est.", "Duration ms", "Notes", "Quarantined"],
                [
                    [
                        row.get("vault", ""), row.get("calls", ""), row.get("input_tokens_est", ""),
                        row.get("output_tokens_est", ""), row.get("duration_ms", ""),
                        row.get("notes_written", ""), row.get("quarantined", ""),
                    ]
                    for row in compile_costs
                ],
            )
            if compile_costs else "No compile cost file found."
        )
        + "\n\n## Answer cost\n\n"
        + _markdown_table(
            ["Condition", "Calls", "Prompt tokens", "Output tokens", "Prompt chars", "Duration ms"],
            [
                [row["condition"], row["calls"], row["prompt_tokens"], row["output_tokens"], row["prompt_chars"], row["duration_ms"]]
                for row in answer_costs
            ],
        )
        + "\n\n## Judge cost\n\n"
        + _markdown_table(
            ["Condition", "Calls", "Duration ms"],
            [[row["condition"], row["calls"], row["duration_ms"]] for row in judge_costs],
        )
        + "\n"
    )
    write_text_atomic(score_dir / "E2E-RAPOR.md", report)
    print(f"report: {score_dir / 'E2E-RAPOR.md'}")
    print(f"csv: {score_dir / 'skor_e2e.csv'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
