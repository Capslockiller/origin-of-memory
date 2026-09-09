---
yazan: codex
model: gpt-5
---

# Measurement tools

`bench/` contains Python measurement tools. They are not compiled into, installed with, or required to run `oom.exe`. This is the single language exception in the 2.0 repository: Python measures the product but is not part of the product.

The checked-in programs currently cover two historical experiment families:

- `setler.py`, `surum_cikar.py`, `kos.py`, and `skorla.py` prepare offline datasets, extract v0 retrieval checkpoints, produce TREC runs, and score effectiveness and latency. `skorla.py` uses `ranx`; conversion, extraction, and retrieval use the Python standard library.
- `e2e/` builds LoCoMo fixtures and compares raw-turn retrieval, v0 daily-to-concept compilation, and full-context answering. Claude and Ollama stages are live model calls; `--backend fake` validates plumbing only.

Generated data, extracted versions, runs, and reports stay under ignored `bench/.data/`, `bench/.versions/`, `bench/.out/`, and `bench/.e2e/` paths. The scripts do not ship with the executable.

## Historical retrieval matrix

From the repository root:

```powershell
$benchPython = "$env:USERPROFILE/.local/venvs/benchmark/Scripts/python.exe"
& $benchPython bench/setler.py
python bench/surum_cikar.py
python bench/kos.py --versions all --sets all --modes bm25,rrf --limit 100 --repeat 3
& $benchPython bench/skorla.py
```

Use `--max-queries N` for a smoke run. `kos.py --rebuild-manifest` reconstructs a missing run manifest from matching run and latency artifacts. `--out-dir` selects an alternate output tree; relative paths are resolved from `bench/`.

The normalized datasets and extracted v0 checkpoints must already be available locally. The main matrix itself makes no network request. `tr_beir_kos.py` is a separate SciFact-TR downloader/measurement script and does contact the Hugging Face datasets server.

## LoCoMo end-to-end experiment

The `e2e/` tools still exercise the v0 Python compile/retrieval checkpoints extracted for measurement; they do not call the new C# executable.

```powershell
python bench/e2e/vault_kur.py
python bench/e2e/derle.py --backend claude --max-calls 3 --timeout 900
python bench/e2e/cevapla.py --condition a --backend ollama --workers 1 --resume
python bench/e2e/cevapla.py --condition b --backend ollama --workers 1 --resume
python bench/e2e/cevapla.py --condition c --backend ollama --workers 1 --resume
python bench/e2e/yargila.py --condition a --backend claude --workers 4 --resume
python bench/e2e/yargila.py --condition b --backend claude --workers 4 --resume
python bench/e2e/yargila.py --condition c --backend claude --workers 4 --resume
& $benchPython bench/e2e/skorla_e2e.py
```

Answer and judge stages checkpoint incrementally and support `--resume`. Condition `c` uses the fixed seed-42 stratified 300-question subset; conditions `a` and `b` use all scored questions unless restricted. Fake-backend output is not an accuracy result.

## Acceptance gates

Gate 5 requires the 125-question gold set to be evaluated against the 2.0 index, with recall@3 at least 0.80 and recall@5 at least 0.88. It is measured, and after lane R2 **it passes**: recall@3 0,832 and recall@5 0,888. See "Recall parity (gate 5)" below for both the 2026-09-09 baseline and the R2 measurement.

Gate 10 requires `oom bench --backend local` to measure 30 flush transcripts and five compile dailies, write `bench/results/<date>.json`, and leave the backend lists consistent with the result. The harness exists as `yerel_olcum.py` and has been smoke-run on synthetic input; the `oom bench` CLI subcommand of spec 6.12 still does not exist, and the 30-transcript run over real transcripts has not been made. Gate 10 therefore remains **planned** and must not be reported as passed.

No benchmark result is a default merely because code for the feature exists. Record the dataset revision, parameters, model identity, machine, raw result path, and the acceptance threshold whenever a gate is measured.

## 2.0 harness

`kos20.py` is the 2.0 backend of the retrieval matrix. The v0 harness (`kos.py`) imported extracted `retrieve.py` checkpoints from `bench/.versions/` and ranked in Python; the 2.0 ranking lives in `src/Oom/Retrieve/Retrieve.cs` and has no importable Python surface, so `kos20.py` drives the executable instead:

```bash
python bench/kos20.py                       # full gate 5 measurement
python bench/kos20.py --max-queries 5        # smoke
python bench/kos20.py --probe                # + the 30-prompt hook-gate probe set
```

The run is named `oom-2.0` and is written as a TREC run file to `bench/.out/oom-2.0.run`, in the same format `kos.py` produces, so the two are comparable where a v0 checkpoint still exists locally. **The v0 side was not run in this measurement: `bench/.versions/` is absent from this tree (it is gitignored), so there is no v0 number to compare against and the table below is a 2.0 absolute measurement, not a parity delta.**

Ranking comes from `oom.exe --vault <vault> retrieve --batch <jsonl> --top <k>`, which already existed in `Program.RunRetrieve`; no CLI change was needed for this lane and `src/Oom` was not touched. The batch mode reads `{id, soru}` (or `query`) lines and writes one JSON line per query in input order, sharing one process and one corpus load. That matters: one process start costs ~340 ms, so 130 separate `--query` calls take ~44 s while the batch takes ~3 s (~23 ms per query, corpus load amortised).

The path is read-only over the vault. `Retrieve.Query` parses `<vault>/knowledge/concepts/*.md` into memory and ranks; the served-dedupe table is a process-local static dictionary, so repeated runs do not suppress each other's hits, and `retrieve_served` in the state root was still empty after the full run plus the hook probe.

## Recall parity (gate 5) — measured 2026-09-09

Executable built from `e64566c`; vault `E:\OdenaOS` (542 concept files); gold set `.brief/gold-sorular.jsonl`, 130 rows of which 125 are scored and 5 are `kanarya` negative controls with an empty `gold` by construction and excluded from recall. Raw result: `bench/results/recall-2026-09-09.json`.

| küme | n | recall@3 | recall@5 | MRR@5 |
| --- | ---: | ---: | ---: | ---: |
| **genel** | 125 | **0,696** | **0,744** | 0,667 |
| tek-not | 99 | 0,667 | 0,717 | 0,645 |
| çok-not | 26 | 0,808 | 0,846 | 0,753 |

Thresholds: recall@3 ≥ 0,80 → **FAIL** (−0,104). recall@5 ≥ 0,88 → **FAIL** (−0,136).

Index completeness is not the cause: the state root holds 542 `notes` rows for 542 concept files, and all 154 gold slugs exist as files.

The gap is mostly ordering, not candidate generation. The full-depth recall curve:

| k | 1 | 3 | 5 | 10 | 20 | 50 | 100 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| recall@k | 0,624 | 0,696 | 0,744 | 0,848 | 0,880 | 0,912 | 0,944 |

recall@20 already reaches 0,880 — the gold note is usually retrieved and ranked 6–20. Seven of 125 questions never place the gold note inside the top 100; four of those score it at exactly zero, i.e. no shared token at all. Those seven are short, pronoun-heavy conversational prompts whose words do not occur in the note, and they cap any purely lexical fix at about 0,944.

Latency: batch 3,0 s for 130 queries (~23 ms/query); a single `retrieve --query` process costs ~340 ms, almost all of it process start.

## Recall parity (gate 5) — re-measured 2026-09-09 after lane R2

Same executable source tree, same vault, same gold set, same harness; only `src/Oom/Retrieve/Retrieve.cs` and `src/Oom/Notes/TurkishFold.cs` changed. Raw result: `bench/results/recall-2026-09-09-r2.json`; the pre-change baseline reproduced by this lane is `bench/results/recall-2026-09-09-r2-baseline.json`.

| küme | n | recall@3 | recall@5 | MRR@5 |
| --- | ---: | ---: | ---: | ---: |
| **genel** | 125 | **0,832** | **0,888** | 0,755 |
| tek-not | 99 | 0,808 | 0,879 | 0,733 |
| çok-not | 26 | 0,923 | 0,923 | 0,840 |

Thresholds: recall@3 ≥ 0,80 → **PASS** (+0,032). recall@5 ≥ 0,88 → **PASS** (+0,008).

Per-change measurements, each one applied on top of the one above it and re-measured:

| # | change | recall@3 | recall@5 | MRR@5 | kept |
| --- | --- | ---: | ---: | ---: | --- |
| 0 | baseline (`f8347df`) | 0,696 | 0,744 | 0,667 | — |
| a | real term frequency into BM25 (index tokens no longer de-duplicated) | 0,712 | 0,760 | 0,679 | kept |
| b | content-word query filter, stopwords **and** the ≥ 4 character rule | 0,648 | 0,696 | 0,603 | reverted |
| b1 | stopword filter only, no length rule | 0,720 | 0,760 | 0,676 | kept |
| c | prefix terms at reduced weight (0,7 / 0,5 / 0,35 / 0,2 / 0,1 / 0) | ≤ 0,720 | ≤ 0,752 | ≤ 0,670 | reverted |
| d | length normalisation over prefix-free token counts | 0,832 | 0,888 | 0,755 | reverted (no effect) |
| e | BM25F: one saturation over the row instead of one per field | **0,832** | **0,888** | 0,755 | kept |

Change (e) is the one that moved the number, and it is a correction rather than a tuning. Spec 6.4 names `bm25(notes_fts, 0.0, 8.0, 6.0, 3.0, 1.0)` as the index query, and SQLite's `bm25()` scales the term frequency by the column weight, sums the weighted frequency across columns, and then applies the saturation and the length normalisation **once** against the row. The in-process `Rank` instead saturated each field separately and summed the results, which turned the title weight into an unsaturated 8× multiplier: one common word in a title outscored four rare words in the note that answered the question. Same weights, same `k1` = 1,2 and `b` = 0,75, same tokens — only the order of the operations changed. Document frequency likewise moved from per-field to per-row, and the idf took `bm25()`'s form (`log((N − n + 0,5) / (n + 0,5))`, floored at 1e-6 where it would go negative).

`k1` and `b` were swept and left alone. On 125 questions the best alternatives were worth at most ±0,016 — one or two questions — which is inside the noise of this instrument and is not evidence for changing a documented constant.

Depth curve, before and after:

| k | 1 | 3 | 5 | 10 | 20 | 50 | 100 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline | 0,624 | 0,696 | 0,744 | 0,848 | 0,880 | 0,912 | 0,944 |
| after R2 | 0,672 | 0,832 | 0,888 | 0,912 | 0,936 | 0,944 | 0,968 |

The remaining 14 misses at k=5 split into two kinds. Four questions (`q033`, `q051`, `q108`, `q119`) never place the gold note inside the top 100 and are lexically unreachable — short, pronoun-heavy prompts that share no token with the note ("Gülüm benim anakartım hangi marka model" against a note about a freeze-and-stutter diagnosis). No BM25 parameter reaches them; they need a different signal. The other ten are ordering misses with the gold note at rank 6–78.

## Hook gate — 30-prompt probe set (spec 10.1 #17)

`bench/probe-30.jsonl` is the probe set spec 10.1 #17 asks for and that `bench/` previously lacked: 30 invented prompts in the style of real chat prompts, each carrying whether it *should* inject and why. Eight are genuine memory questions; the other 22 must stay silent. The prompts are written for this file — no vault text, no note body, no copied gold question.

`python bench/kos20.py --probe` runs each prompt through `oom.exe retrieve --hook` and reports three numbers, because the injection count alone can be made perfect by injecting nothing:

| measure | result | target |
| --- | ---: | ---: |
| prompts injected | 8/30 | ≤ 8 |
| real questions that injected | 8/8 | 8/8 |
| false positives among the 22 | 0/22 | 0/22 |
| `kanarya` no-answer rows (gold set) | 0/5 | 0/5 |

Baseline for the same instrument: 5/5 canaries and 20/20 gold questions injected — the gate passed everything. Two defects caused it. The overlap test ran over `hit.Text`, the note body, and a 1500-character body shares two content words with very nearly any prompt; it now runs over the note's identity fields (slug, title, aliases, tags) as spec 6.4 says. And `strictScore` was tested first and short-circuited the overlap test, at 25,0 against raw scores that ran 60–300, so it never bound.

`strictScore` is now the note's score divided by the number of ranked query terms — the mean per-term contribution — because the raw sum grows with the length of the prompt, so one constant over it binds on short prompts and never on long ones. Raw result: `bench/results/recall-2026-09-09-r2-gate.json`.

## Local model measurement (gate 10)

`yerel_olcum.py` implements the three legs of spec 6.12 and its decision rule. The harness is Python under `bench/` rather than a C# `bench` subcommand, because `bench/` is the documented home for tools that measure the product without shipping in it, and because adding a module to `src/Oom` would have put a rebuild and the test-parity obligation inside a measurement lane. **This is a deviation from spec 6.12, which names the command `oom bench --backend local`; that subcommand is still owed.**

The harness copies the prompt and the validator out of `src/Oom` rather than importing them, so it re-reads those C# files on every run and refuses to measure when the copied strings no longer appear verbatim (`--no-check-drift` disables the guard). A drift failure means the harness is stale, never that the model failed.

```bash
python bench/yerel_olcum.py --transcripts 3 --dailies 2    # synthetic smoke
```

Smoke result, `qwen3:8b` via Ollama at `http://localhost:11434/v1`, 3 synthetic Claude Code transcripts and 2 synthetic dailies under the gitignored `bench/.data/`. Raw result: `bench/results/yerel-2026-09-09.json`.

| leg | n | result | threshold | pass |
| --- | ---: | ---: | ---: | --- |
| (a) five-section flush shape | 3 | 1,000 | 0,95 | yes |
| (b) double-blind judge | 0 | **not run** | 3,5 | — |
| (c) text-mode compile conformance | 2 | 0,500 | 0,95 | no |

Leg (b) is implemented (`judge_pairs` builds the blind A/B pairing) and deliberately not called: it needs Claude reference summaries and Claude judge calls, and this lane spends no Claude quota. Because leg (b) did not run, the `backend.flush` decision of spec 6.12 is **undecided**, not passed.

**A synthetic smoke decides nothing.** n=3 and n=2 are far below the 30 and 5 the spec requires, and the inputs are invented. The one substantive observation is that both compile failures were contract failures rather than truncations (`finish_reason: stop`): `qwen3:8b` omitted `=== END FILE ===` between blocks, and in an earlier run emitted a slug containing Turkish characters, which the `^knowledge/concepts/[a-z0-9-]+\.md$` allowlist rejects.

The full run of spec 6.12 is the owner's decision — it reads real transcripts and real dailies and sends them to the local model. The exact command is in `progress.md` under `## Lane BENCH`.

## Mutation check (gate 9)

`mutate.py` measures acceptance gate 9: six mutants, two per specification boundary, each of which must be killed by at least one test that already exists. It is a measurement tool, not a test — it never edits `tests/` and never leaves the tree mutated.

The boundaries and their mutants live in `bench/mutants.json` as exact `search` -> `replace` string pairs anchored on the current source:

- Spec 6.5-4 — the compile output path allowlist `^knowledge/concepts/[a-z0-9-]+\.md$` in `src/Oom/Compile/Compile.cs`.
- Spec 6.7 — the directive refusal in `Guards.Gate` in `src/Oom/Guards/Guards.cs`.
- Spec 6.6 — the isolated `claude -p` invocation built by `Runner.BuildClaudeRequest` in `src/Oom/Runner/Runner.cs`.

From the repository root:

```powershell
python bench/mutate.py
python bench/mutate.py --list
python bench/mutate.py --only M4-directive-never-refuses
```

The tool refuses to start if `git status --porcelain -- src` is not empty. It runs `dotnet test Oom.sln -c Release --no-restore -v q` once with no mutant to establish the baseline, then applies one mutant at a time to the target file's bytes, runs the suite again, and restores the original bytes in a `finally` block, verifying the restore byte-for-byte. Before it exits it re-checks `git status --porcelain -- src` and `git diff --quiet -- src`; a dirty tree is reported and makes the exit code non-zero. Each mutant's `search` must match its file exactly once — zero or several matches abort the run before anything is written, which is what makes the definitions self-invalidating when the source moves.

A mutant is **killed** when the failing-test count rises above the baseline; the killing tests are the `Y-xxx` scar tests that were green in the baseline and red under the mutant. A mutant that survives is a measurement result, not a failure of the tool: it means no existing test detects that change. A build failure counts as a kill only for a mutant explicitly declared `compile_visible`; for a behavioural mutant it is recorded as an invalid mutant, never as a kill.

Results are written to `bench/results/mutation-<YYYY-MM-DD>.json` (baseline line and failing set, then per mutant: id, file, description, whether it was applied, its test line, killed, killing tests and killing scar ids) and a Markdown summary is printed to stdout. Exit code 0 means every mutant was killed and the tree is clean; 1 means a survivor or a dirty tree; 2 means the run aborted on a safety precondition.

A green result means: for each of the six mutants, at least one test in the existing suite turns red. It does not mean the boundary is fully covered — only the specific mutated properties are measured. Baseline reds are excluded by construction, because a mutant is judged only on failures that are *new* relative to the baseline set.
