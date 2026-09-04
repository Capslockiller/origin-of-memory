#!/usr/bin/env python3
"""Score benchmark TREC runs with ranx and write CSV/Markdown reports."""

# yazan: codex · gpt-5.6-sol

from __future__ import annotations

import argparse
import csv
import importlib.util
import json
import math
import os
from pathlib import Path
import sys
from typing import Any

# ranx's eager Numba compilation is disproportionately expensive on the clean
# Windows benchmark venv. This changes execution strategy, not metric code.
os.environ["NUMBA_DISABLE_JIT"] = "1"

import numpy as np
from ranx import Qrels, Run, compare, evaluate


ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent
SETS_DIR = ROOT / ".data" / "sets"
VERSIONS_DIR = ROOT / ".versions"
OUT = ROOT / ".out"
SCORE_DIR = OUT / "skor"
GENERAL_METRICS = ("ndcg@10", "recall@10", "recall@100", "mrr@10")
LONG_K = (1, 3, 5, 10, 30, 50)
LONG_RANX_METRICS = tuple(
    metric
    for cutoff in LONG_K
    for metric in (f"hit_rate@{cutoff}", f"recall@{cutoff}", f"ndcg@{cutoff}")
)
LOCOMO_METRICS = (
    "hit_rate@1",
    "hit_rate@3",
    "hit_rate@5",
    "hit_rate@10",
    "recall@5",
    "recall@10",
    "ndcg@5",
    "ndcg@10",
    "mrr@10",
)
LOCOMO_CATEGORIES = {
    1: "multi-hop",
    2: "temporal",
    3: "open-domain",
    4: "single-hop",
}


def _read_qrels(path: Path, query_ids: list[str]) -> dict[str, dict[str, int]]:
    selected = set(query_ids)
    qrels: dict[str, dict[str, int]] = {query_id: {} for query_id in query_ids}
    with path.open("r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, 1):
            parts = line.split()
            if len(parts) != 4:
                raise ValueError(f"{path}:{line_number}: expected four TREC qrels columns")
            query_id, _, document_id, relevance = parts
            if query_id in selected:
                qrels[query_id][document_id] = int(float(relevance))
    missing = [query_id for query_id, judgments in qrels.items() if not judgments]
    if missing:
        raise ValueError(f"{path}: no qrels for {len(missing)} evaluated queries")
    return qrels


def _read_run(path: Path, name: str, query_ids: list[str] | None = None) -> Run:
    selected = None if query_ids is None else set(query_ids)
    values: dict[str, dict[str, float]] = {}
    with path.open("r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, 1):
            parts = line.split()
            if len(parts) != 6:
                raise ValueError(f"{path}:{line_number}: expected six TREC run columns")
            query_id, _, document_id, _, score, _ = parts
            if selected is not None and query_id not in selected:
                continue
            values.setdefault(query_id, {})[document_id] = float(score)
    return Run(values, name=name) if values else Run(name=name)


def _load_query_categories(path: Path, query_ids: list[str]) -> dict[str, int]:
    selected = set(query_ids)
    categories: dict[str, int] = {}
    with path.open("r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, 1):
            if not line.strip():
                continue
            row = json.loads(line)
            query_id = str(row["_id"])
            if query_id not in selected:
                continue
            category = int(row["category"])
            if category not in LOCOMO_CATEGORIES:
                raise ValueError(f"{path}:{line_number}: unsupported LoCoMo category {category}")
            categories[query_id] = category
    missing = sorted(selected - categories.keys())
    if missing:
        raise ValueError(f"{path}: no category for {len(missing)} evaluated queries")
    return categories


def _write_csv(path: Path, rows: list[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=("set", "version", "mode", "metric", "value", "n_queries"))
        writer.writeheader()
        writer.writerows(rows)
    os.replace(temporary, path)


def _markdown_table(headers: list[str], rows: list[list[str]]) -> list[str]:
    def cell(value: str) -> str:
        return str(value).replace("|", "\\|").replace("\n", " ")

    return [
        "| " + " | ".join(map(cell, headers)) + " |",
        "| " + " | ".join("---" if index == 0 else "---:" for index in range(len(headers))) + " |",
        *("| " + " | ".join(map(cell, row)) + " |" for row in rows),
    ]


def _holm(raw_values: list[float]) -> list[float]:
    adjusted = [float("nan")] * len(raw_values)
    finite = sorted((value, index) for index, value in enumerate(raw_values) if math.isfinite(value))
    running = 0.0
    total = len(finite)
    for order, (value, index) in enumerate(finite):
        running = max(running, min(1.0, (total - order) * value))
        adjusted[index] = running
    return adjusted


def _load_tr_helper() -> Any:
    path = REPO / "tools" / "tr_beir_kos.py"
    spec = importlib.util.spec_from_file_location("tr_beir_kos_benchmark_sanity", path)
    if spec is None or spec.loader is None:
        raise ImportError(f"cannot import {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _sanity_mean(run_path: Path, qrels: dict[str, dict[str, int]]) -> tuple[float, float]:
    helper = _load_tr_helper()
    retrieved: dict[str, list[str]] = {query_id: [] for query_id in qrels}
    with run_path.open("r", encoding="utf-8") as handle:
        for line in handle:
            query_id, _, document_id, _, _, _ = line.split()
            if query_id in retrieved:
                retrieved[query_id].append(document_id)
    values = [helper._query_metrics(retrieved[query_id], qrels[query_id]) for query_id in qrels]
    return (
        sum(value[0] for value in values) / len(values),
        sum(value[1] for value in values) / len(values),
    )


def _p_value(report: Any, first: str, second: str, metric: str) -> float:
    payload = report.to_dict()
    return float(payload[first]["comparisons"][second][metric])


def _display_path(path: Path) -> str:
    try:
        return str(path.relative_to(ROOT))
    except ValueError:
        return str(path)


def _resolve_out_dir(value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else ROOT / path


def _select_sets(value: str, available: list[str]) -> list[str]:
    selected = available if value == "all" else [part.strip() for part in value.split(",") if part.strip()]
    unknown = [name for name in selected if name not in available]
    if unknown:
        raise SystemExit(f"unknown set(s) in run manifest: {', '.join(unknown)}")
    return selected


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sets", default="all", help="all or comma-separated sets from the run manifest")
    parser.add_argument("--out-dir", default=".out", help="output directory, relative to tools/benchmark by default")
    parser.add_argument("--tests", default="student,fisher", help="comma-separated ranx stat tests to run (student, fisher)")
    args = parser.parse_args()
    stat_tests = tuple(t.strip() for t in args.tests.split(",") if t.strip())
    out_dir = _resolve_out_dir(args.out_dir)
    score_dir = out_dir / "skor"
    run_manifest_path = out_dir / "kosu-manifest.json"
    version_manifest_path = VERSIONS_DIR / "manifest.json"
    if not run_manifest_path.is_file():
        raise SystemExit(f"missing {_display_path(run_manifest_path)}; run kos.py first")
    if not version_manifest_path.is_file():
        raise SystemExit("missing .versions/manifest.json; run surum_cikar.py first")
    run_manifest = json.loads(run_manifest_path.read_text(encoding="utf-8"))
    version_manifest = json.loads(version_manifest_path.read_text(encoding="utf-8"))
    available_sets = list(dict.fromkeys(record["set"] for record in run_manifest["records"]))
    selected_sets = set(_select_sets(args.sets, available_sets))
    ready = [
        record
        for record in run_manifest["records"]
        if record.get("status") == "ok" and record["set"] in selected_sets
    ]
    if not ready:
        raise SystemExit("run manifest contains no scoreable records")

    score_rows: list[dict[str, Any]] = []
    evaluated: dict[tuple[str, str, str], dict[str, Any]] = {}
    for record in ready:
        set_name, version, mode = record["set"], record["version"], record["mode"]
        latency_path = out_dir / record["latency"]
        run_path = out_dir / record["run"]
        query_ids = json.loads(latency_path.read_text(encoding="utf-8"))["query_ids"]
        qrels_dict = _read_qrels(SETS_DIR / set_name / "qrels.trec", query_ids)
        qrels = Qrels(qrels_dict, name=set_name)
        run = _read_run(run_path, f"{version} {mode}")
        metrics = list(GENERAL_METRICS)
        if set_name == "longmemeval-s":
            metrics = list(dict.fromkeys((*metrics, *LONG_RANX_METRICS)))
        elif set_name == "locomo":
            metrics = list(LOCOMO_METRICS)
        per_query = evaluate(qrels, run, metrics, return_mean=False, make_comparable=True, threads=1)
        means = {metric: float(np.mean(per_query[metric])) for metric in metrics}
        for metric in metrics:
            score_rows.append(
                {"set": set_name, "version": version, "mode": mode, "metric": metric, "value": f"{means[metric]:.12f}", "n_queries": len(query_ids)}
            )
        if set_name == "longmemeval-s":
            for cutoff in LONG_K:
                recall_all = float(np.mean(np.asarray(per_query[f"recall@{cutoff}"]) == 1.0))
                score_rows.append(
                    {"set": set_name, "version": version, "mode": mode, "metric": f"recall_all@{cutoff}", "value": f"{recall_all:.12f}", "n_queries": len(query_ids)}
                )
                means[f"recall_all@{cutoff}"] = recall_all
        category_means: dict[int, dict[str, float]] = {}
        category_query_counts: dict[int, int] = {}
        if set_name == "locomo":
            query_categories = _load_query_categories(SETS_DIR / set_name / "queries.jsonl", query_ids)
            for category in LOCOMO_CATEGORIES:
                category_query_ids = [query_id for query_id in query_ids if query_categories[query_id] == category]
                if not category_query_ids:
                    continue
                category_qrels_dict = {
                    query_id: qrels_dict[query_id]
                    for query_id in category_query_ids
                }
                category_qrels = Qrels(category_qrels_dict, name=f"{set_name} category {category}")
                category_run = _read_run(run_path, f"{version} {mode} category {category}", category_query_ids)
                category_per_query = evaluate(
                    category_qrels,
                    category_run,
                    list(LOCOMO_METRICS),
                    return_mean=False,
                    make_comparable=True,
                    threads=1,
                )
                category_means[category] = {
                    metric: float(np.mean(category_per_query[metric]))
                    for metric in LOCOMO_METRICS
                }
                category_query_counts[category] = len(category_query_ids)
                for metric in LOCOMO_METRICS:
                    score_rows.append(
                        {
                            "set": set_name,
                            "version": version,
                            "mode": mode,
                            "metric": f"category_{category}/{metric}",
                            "value": f"{category_means[category][metric]:.12f}",
                            "n_queries": len(category_query_ids),
                        }
                    )
        evaluated[(set_name, version, mode)] = {
            "qrels": qrels,
            "qrels_dict": qrels_dict,
            "run": run,
            "run_path": run_path,
            "latency_path": latency_path,
            "means": means,
            "n_queries": len(query_ids),
            "metrics": metrics,
            "category_means": category_means,
            "category_query_counts": category_query_counts,
        }

    csv_path = score_dir / "skor.csv"
    _write_csv(csv_path, score_rows)
    report_lines = [
        "---",
        "yazan: codex",
        "model: gpt-5.6-sol",
        "---",
        "",
        "# Retrieval benchmark report",
        "",
        "All effectiveness metrics and per-query values used by statistical tests were computed by ranx 0.3.21. For LongMemEval-S, `recall_all@k` is the required mean of the indicator `ranx recall@k == 1.0`.",
        "",
    ]
    set_names = list(dict.fromkeys(record["set"] for record in ready))
    for set_name in set_names:
        entries = [(key, value) for key, value in evaluated.items() if key[0] == set_name]
        report_lines.extend([f"## {set_name}", ""])
        if set_name == "longmemeval-s":
            columns = [item for cutoff in LONG_K for item in (f"hit_rate@{cutoff}", f"recall_all@{cutoff}", f"ndcg@{cutoff}")]
        elif set_name == "locomo":
            columns = list(LOCOMO_METRICS)
        else:
            columns = list(GENERAL_METRICS)
        display_columns = [
            f"recall_any@{metric.split('@')[1]} (hit_rate@{metric.split('@')[1]})"
            if metric.startswith("hit_rate@")
            else metric
            for metric in columns
        ]
        rows = []
        for (_, version, mode), result in entries:
            rows.append([f"{version} {mode}", *(f"{result['means'][metric]:.6f}" for metric in columns)])
        report_lines.extend(_markdown_table(["version / mode", *display_columns], rows))
        report_lines.append("")
        if set_name == "locomo":
            report_lines.extend(["### Per category", ""])
            category_rows = []
            for (_, version, mode), result in entries:
                for category, category_name in LOCOMO_CATEGORIES.items():
                    if category not in result["category_means"]:
                        continue
                    values = result["category_means"][category]
                    category_rows.append(
                        [
                            f"{version} {mode}",
                            f"{category} — {category_name}",
                            str(result["category_query_counts"][category]),
                            *(f"{values[metric]:.6f}" for metric in LOCOMO_METRICS),
                        ]
                    )
            report_lines.extend(
                _markdown_table(
                    ["version / mode", "category", "queries", *LOCOMO_METRICS],
                    category_rows,
                )
            )
            report_lines.extend(["", "Category mapping (1 multi-hop, 2 temporal, 3 open-domain, 4 single-hop) verified against the official LoCoMo per-category counts (841/282/321/96 in the paper) and task_eval/evaluation.py.", ""])

    report_lines.extend(["## Comparisons", ""])
    distinct = version_manifest["distinct"]
    representative = {row["version"]: row["representative"] for row in version_manifest["versions"]}
    comparisons: list[dict[str, Any]] = []
    seen_comparisons: set[tuple[str, str, str, str]] = set()
    for (set_name, version, mode), target in evaluated.items():
        candidates: list[tuple[str, tuple[str, str, str]]] = []
        baseline_key = (set_name, "V1", "bm25")
        if baseline_key in evaluated and baseline_key != (set_name, version, mode):
            candidates.append(("V1 bm25", baseline_key))
        rep = representative[version]
        if rep in distinct:
            rep_index = distinct.index(rep)
            if rep_index > 0:
                previous_key = (set_name, distinct[rep_index - 1], mode)
                if previous_key in evaluated and previous_key != (set_name, version, mode):
                    candidates.append(("previous distinct", previous_key))
        for relation, control_key in candidates:
            identity = (set_name, f"{version} {mode}", relation, f"{control_key[1]} {control_key[2]}")
            if identity in seen_comparisons:
                continue
            seen_comparisons.add(identity)
            control = evaluated[control_key]
            if target["n_queries"] != control["n_queries"]:
                continue
            metrics = target["metrics"]
            target_name = f"{version} {mode}"
            control_name = f"{control_key[1]} {control_key[2]}"
            for test in stat_tests:
                try:
                    comparison = compare(
                        qrels=target["qrels"],
                        runs=[control["run"], target["run"]],
                        metrics=metrics,
                        stat_test=test,
                        n_permutations=1000,
                        random_seed=42,
                        threads=1,
                        make_comparable=True,
                    )
                except (NotImplementedError, ImportError) as exc:
                    comparisons.append({"set": set_name, "target": target_name, "control": control_name, "relation": relation, "test": test, "metric": "n/a", "p": float("nan"), "error": str(exc)})
                    continue
                for metric in metrics:
                    comparisons.append({"set": set_name, "target": target_name, "control": control_name, "relation": relation, "test": test, "metric": metric, "p": _p_value(comparison, control_name, target_name, metric)})

    for set_name in set_names:
        set_comparisons = [row for row in comparisons if row["set"] == set_name]
        if not set_comparisons:
            report_lines.extend([f"### {set_name}", "", "No requested baseline/previous-distinct pair was available.", ""])
            continue
        for test in stat_tests:
            rows_for_test = [row for row in set_comparisons if row["test"] == test]
            adjusted = _holm([row["p"] for row in rows_for_test])
            for row, value in zip(rows_for_test, adjusted):
                row["holm_p"] = value
        table_rows = []
        for row in set_comparisons:
            raw = "n/a" if not math.isfinite(row["p"]) else f"{row['p']:.6g}"
            corrected = "n/a" if not math.isfinite(row.get("holm_p", float("nan"))) else f"{row['holm_p']:.6g}"
            table_rows.append([row["target"], row["control"], row["relation"], row["test"], row["metric"], raw, corrected])
        report_lines.extend([f"### {set_name}", ""])
        report_lines.extend(_markdown_table(["target", "control", "relation", "test", "metric", "p", "Holm p"], table_rows))
        report_lines.append("")

    report_lines.extend(["## Latency", "", "Search latency excludes index construction. p50/p95 use linear interpolation over all per-query observations across repeats.", ""])
    latency_rows = []
    for (set_name, version, mode), result in evaluated.items():
        payload = json.loads(result["latency_path"].read_text(encoding="utf-8"))
        latency_rows.append([set_name, version, mode, f"{payload['p50_ms']:.3f}", f"{payload['p95_ms']:.3f}", str(payload["repeat"])])
    report_lines.extend(_markdown_table(["set", "version", "mode", "p50 ms", "p95 ms", "repeats"], latency_rows))
    report_lines.extend(["", "## Sanity", ""])
    sanity_key = ("scifact-tr-saoud", "V9", "bm25")
    if sanity_key in evaluated:
        result = evaluated[sanity_key]
        helper_ndcg, helper_recall = _sanity_mean(result["run_path"], result["qrels_dict"])
        ranx_ndcg = result["means"]["ndcg@10"]
        ranx_recall = result["means"]["recall@10"]
        report_lines.extend(
            _markdown_table(
                ["metric", "ranx", "tools/tr_beir_kos.py _query_metrics", "absolute delta"],
                [
                    ["nDCG@10", f"{ranx_ndcg:.12f}", f"{helper_ndcg:.12f}", f"{abs(ranx_ndcg-helper_ndcg):.3g}"],
                    ["Recall@10", f"{ranx_recall:.12f}", f"{helper_recall:.12f}", f"{abs(ranx_recall-helper_recall):.3g}"],
                ],
            )
        )
        print(f"sanity scifact-tr-saoud V9 bm25: ranx nDCG@10={ranx_ndcg:.12f}, helper={helper_ndcg:.12f}, delta={abs(ranx_ndcg-helper_ndcg):.3g}")
        print(f"sanity scifact-tr-saoud V9 bm25: ranx Recall@10={ranx_recall:.12f}, helper={helper_recall:.12f}, delta={abs(ranx_recall-helper_recall):.3g}")
    else:
        report_lines.append("scifact-tr-saoud V9 bm25 was not part of this run.")
    report_lines.append("")
    report_path = score_dir / "RAPOR.md"
    report_path.parent.mkdir(parents=True, exist_ok=True)
    temporary = report_path.with_suffix(".md.tmp")
    temporary.write_text("\n".join(report_lines), encoding="utf-8", newline="\n")
    os.replace(temporary, report_path)
    for (set_name, version, mode), result in evaluated.items():
        if set_name in {"longmemeval-s", "locomo"}:
            print(f"{set_name} {version} {mode}: hit_rate@5={result['means']['hit_rate@5']:.6f}")
        else:
            print(f"{set_name} {version} {mode}: nDCG@10={result['means']['ndcg@10']:.6f}")
    locomo_entries = [(key, value) for key, value in evaluated.items() if key[0] == "locomo"]
    if locomo_entries:
        print("locomo per-category metrics:")
        print("version/mode | category | queries | " + " | ".join(LOCOMO_METRICS))
        for (_, version, mode), result in locomo_entries:
            for category, category_name in LOCOMO_CATEGORIES.items():
                if category not in result["category_means"]:
                    continue
                values = result["category_means"][category]
                metrics_text = " | ".join(f"{values[metric]:.6f}" for metric in LOCOMO_METRICS)
                print(
                    f"{version} {mode} | {category} {category_name} | "
                    f"{result['category_query_counts'][category]} | {metrics_text}"
                )
    print(f"scores: {_display_path(csv_path)}")
    print(f"report: {_display_path(report_path)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
