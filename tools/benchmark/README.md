---
yazan: codex
model: gpt-5.6-sol
---

# Historical retrieval benchmark

This directory benchmarks every historical `retrieve.py` checkpoint against pre-downloaded public BEIR-format datasets, LongMemEval-S, and LoCoMo. Dataset conversion and scoring use the dedicated benchmark virtual environment; version extraction and retrieval use only Python's standard library. No script makes a network request.

## Reproduce

Run these commands from the repository root in PowerShell. The first sequence is the complete matrix intended for the operator:

```powershell
$benchPython = "$env:USERPROFILE/.local/venvs/benchmark/Scripts/python.exe"
& $benchPython tools/benchmark/setler.py
python tools/benchmark/surum_cikar.py
python tools/benchmark/kos.py --versions all --sets all --modes bm25,rrf --limit 100 --repeat 3
& $benchPython tools/benchmark/skorla.py
```

For an isolated LoCoMo run that does not write to the default `.out` tree:

```powershell
$benchPython = "$env:USERPROFILE/.local/venvs/benchmark/Scripts/python.exe"
& $benchPython tools/benchmark/setler.py --sets locomo
python tools/benchmark/kos.py --versions V9 --sets locomo --modes bm25 --repeat 1 --out-dir .out-locomo-smoke
& $benchPython tools/benchmark/skorla.py --sets locomo --out-dir .out-locomo-smoke
```

For a fast end-to-end smoke run:

```powershell
$benchPython = "$env:USERPROFILE/.local/venvs/benchmark/Scripts/python.exe"
& $benchPython tools/benchmark/setler.py --sets scifact-tr,scifact-tr-saoud,longmemeval-s
python tools/benchmark/surum_cikar.py
python tools/benchmark/kos.py --versions V1,V9 --sets scifact-tr-saoud --modes bm25 --repeat 1
& $benchPython tools/benchmark/skorla.py
```

`setler.py` accepts `--sets all|name,...`. A missing raw input prints `missing raw file`, marks only that dataset as skipped, and does not fail conversion of the others. `kos.py` additionally accepts `--max-queries N` for smoke tests. Its `--repeat` setting repeats searches only; run rankings always come from repeat 1. `kos.py` and `skorla.py` accept `--out-dir`; relative paths are resolved from `tools/benchmark`, and the backward-compatible default remains `.out`. Each `kos.py` invocation merges its results into the existing `kosu-manifest.json`: records with the same `(set, version, mode)` are replaced, other records remain, and the previous invocation is retained in `history`. The runner refuses to merge when extracted checkpoint hashes differ; `--allow-mixed-versions` explicitly overrides that guard. `skorla.py --sets all|name,...` can limit scoring to sets present in that run manifest.

If a manifest is missing or incomplete while its run and latency artifacts are intact, reconstruct it without rerunning retrieval:

```powershell
python tools/benchmark/kos.py --rebuild-manifest --out-dir .out
```

Rebuild scans matching `runs/<set>__<version>__<mode>.trec` and `latency/<set>__<version>__<mode>.json` pairs, counts distinct run query IDs, restores Profile-A RRF `n/a` rows and identical-checkpoint `reused_from` markers, and merges them with any existing manifest. Existing records win on matching keys during rebuild. The command prints total, per-set, and `n/a` record counts.

`surum_cikar.py` finds the legacy repository by its required V1 commit among Git repositories at the current drive root. If it is stored elsewhere, set `BENCHMARK_LEGACY_REPO` to that repository before running the extractor.

The runner clears `BEYIN_RETRIEVAL` and all historical RRF tuning environment variables while benchmarking, then restores the caller's environment. It therefore uses checkpoint defaults and leaves `BEYIN_RRF_LEGACY_MULTIPLIER` unset. LongMemEval searches are evaluated at each item's `question_date`; LoCoMo searches use the last indexed (dialogue-bearing) session timestamp of that conversation. These clocks freeze V2/V4's wall-clock-dependent legacy recency score and make reruns deterministic.

## Outputs

- `.data/sets/<set>/corpus.jsonl`, `queries.jsonl`, and `qrels.trec` are normalized BEIR datasets. LongMemEval-S instead has `items.jsonl`, with an independent session corpus for every question, plus `qrels.trec`. LoCoMo has one `items.jsonl` row and independent corpus per conversation, a flat category-bearing `queries.jsonl`, and `qrels.trec`.
- `.data/sets/manifest.json` records sources, raw-file SHA-256 values, copied Hugging Face revision/checksum manifests, counts, and license status. LoCoMo's record is isolated in `.data/sets/locomo/manifest.jsonl`; the runner merges it at read time so preparing LoCoMo alone does not rewrite the existing normalized-set manifest.
- `.versions/V1` through `.versions/V9` contain extracted checkpoint files. `.versions/manifest.json` records every file hash, identical groups, API profiles, and distinct representatives.
- `.out/runs/<set>__<version>__<mode>.trec` is a six-column TREC run. BM25 uses `1000-rank` because SQLite `bm25()` is lower-is-better. RRF uses the historical hit score because it is higher-is-better.
- `.out/latency/<set>__<version>__<mode>.json` contains query IDs, every per-query latency for every repeat, index times, and aggregate p50/p95 search latency.
- `.out/kosu-manifest.json` records parameters, Python/SQLite/FTS5 and machine information, checkpoint hashes, skips, Profile-A RRF `n/a` records, and identical-checkpoint reuse.
- `.out/skor/skor.csv` is the long-form ranx score table. `.out/skor/RAPOR.md` has one effectiveness table per set, ranx comparisons with paired Student t-test and Fisher randomization p-values plus Holm correction, latency, and the SciFact-TR continuity check.

`skorla.py` sets `NUMBA_DISABLE_JIT=1` before importing ranx. This avoids a large clean-environment compilation cost on Windows; ranx still supplies every metric and statistical test, and disabling JIT does not change their definitions.

## Protocol and limits

The BEIR sets are scored with ranx using nDCG@10, Recall@10, Recall@100, and MRR@10. LongMemEval-S mirrors the official retrieval harness at session granularity: abstention questions are excluded, each question gets its own ID-keyed corpus, a document is the concatenation of user-role turns in one session, and answer-session IDs are relevant. Repeated session IDs collapse with the later occurrence winning, matching an ID-keyed corpus and satisfying the retriever's unique-name constraint. Its report includes recall-any (`ranx hit_rate`), recall-all (the mean indicator that per-query `ranx recall` equals 1), and nDCG at 1, 3, 5, 10, 30, and 50.

LoCoMo is retrieval-only and uses the official `data/locomo10.json` from Snap Research under CC BY-NC 4.0. Each of the ten conversations gets one FTS5 index. A document is one dialogue turn with a globally scoped `<sample_id>__<dia_id>` ID and `<speaker>: <text>`, followed by the BLIP caption when present. Session timestamps are normalized to ISO-8601 and treated as UTC because the source has no timezone. Category 5 is excluded; other questions with no resolvable evidence are dropped. Query IDs use the zero-based raw QA-list index. The report includes hit rate at 1, 3, 5, and 10; recall at 5 and 10; nDCG at 5 and 10; and MRR at 10, overall and per category. The locally available raw bundle has no upstream README, so the category wording used here—1 single-hop, 2 multi-hop, 3 temporal, 4 open-domain (commonsense)—is explicitly unverified.

The Turkish MTEB datasets are translations, so they do not measure the same language distribution as official English BEIR. SQLite FTS5 BM25 fixes `k1=1.2, b=0.75`; common Anserini BEIR runs use `k1=0.9, b=0.4`, so absolute results are not directly comparable. LongMemEval-S here is English-only.

BM25 tokenizer behavior and weights are identical across all checkpoints. Identical BM25 numbers are therefore expected, and the runner deliberately reuses byte-identical checkpoint groups V1/V3 and V7/V8/V9. RRF differs between V2/V4 and V5+, while Profile A (V1/V3) has no RRF mode.

<!-- yazan: codex · gpt-5.6-sol -->

## E2E LoCoMo

The end-to-end experiment under `e2e/` compares raw-turn BM25, notes produced by the real daily-log → `compile.py` → concept-note pipeline, and full conversation context. All conditions use the same Ollama answerer and Mem0 public J-score judge prompt. Generated vaults, checkpoints, costs, and reports stay under ignored `.e2e/`; condition c automatically uses the fixed seed-42 stratified 300-question subset, while a and b run all 1,531 scored questions.

Run the live experiment from the repository root:

```powershell
python tools/benchmark/e2e/vault_kur.py
python tools/benchmark/e2e/derle.py --backend claude --max-calls 3 --timeout 900
python tools/benchmark/e2e/cevapla.py --condition a --backend ollama --workers 1 --resume
python tools/benchmark/e2e/cevapla.py --condition b --backend ollama --workers 1 --resume
python tools/benchmark/e2e/cevapla.py --condition c --backend ollama --workers 1 --resume
python tools/benchmark/e2e/yargila.py --condition a --backend claude --workers 4 --resume
python tools/benchmark/e2e/yargila.py --condition b --backend claude --workers 4 --resume
python tools/benchmark/e2e/yargila.py --condition c --backend claude --workers 4 --resume
$benchPython = "$env:USERPROFILE/.local/venvs/benchmark/Scripts/python.exe"
& $benchPython tools/benchmark/e2e/skorla_e2e.py
```

Live durations are not yet known. Answer and judge stages checkpoint incrementally and support `--resume`; `derle.py` resumes inherently from compiler state and advances until no changed daily logs remain. Use `--backend fake` on compile, answer, and judge stages for a deterministic offline pipeline test.
