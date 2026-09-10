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

Gate 10 requires `oom bench --backend local` to measure 30 flush transcripts and five compile dailies, write `bench/results/<date>.json`, and leave the backend lists consistent with the result. The command now exists in the product (`src/Oom/Bench/Bench.cs`, lane P3) and has been run against `qwen3:8b` on synthetic input; the 30-transcript run over real transcripts has not been made and leg (b) has never run. Gate 10 therefore remains **planned** and must not be reported as passed.

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

Executable built from `e64566c`; vault `<vault>` (542 concept files); gold set `.brief/gold-sorular.jsonl`, 130 rows of which 125 are scored and 5 are `kanarya` negative controls with an empty `gold` by construction and excluded from recall. Raw result: `bench/results/recall-2026-09-09.json`.

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

<!-- yazan: codex · gpt-6 -->
## Retrieval gate — re-measured 2026-09-10 after lane GATE (Y-110)

**Fact:** the requested 542-note reference is now a **550-note** live corpus at `<vault>`. The historical 542-note snapshot was unavailable; no arbitrary eight notes were removed to manufacture that count. Both fresh baseline (`fdf4b1f`) and final runs used the same 550 concept files, the existing `.brief/gold-sorular.jsonl` (125 scored questions + five canaries), and a byte-identical copy of the 19-note TRIBUN slice under `.brief/hafiza-obegi`. Its eight positives and one negative come from `bench/vm/.out/hafiza-obegi/OKU.md` in the read-only evidence checkout. Raw result: `bench/results/recall-2026-09-10-gate.json` (both phases, executable/gold hashes, machine, per-file corpus hashes, query terms, document frequencies, scores, overlaps and per-query decisions).

**Fact — metric contract:** gate 5 is the existing `kos20.py` **gateless Query** recall measurement, not hook recall. Every candidate changes only the gate; the same measured rankings feed all candidate decisions. Do not interpret the gate-5 column below as post-gate recall. The actual hook metric is included separately so filtering losses remain visible. Recall follows the existing harness: a question succeeds when at least one gold slug occurs in the first k candidates; rejected candidates are not replaced from lower ranks.

| candidate rule | gate 5 @3 / @5 (550) | hook @3 / @5 (550) | slice hook @3, /8 | negative injections, /6 |
| --- | ---: | ---: | ---: | ---: |
| baseline: mean ≥ 1, overlap ≥ 3 | 0.832 / 0.888 | 0.432 / 0.440 | 5 | 0 |
| score ≥ top score × 0.50 | 0.832 / 0.888 | 0.488 / 0.496 | 8 | 2 |
| score ≥ top score × 0.25 | 0.832 / 0.888 | 0.488 / 0.496 | 8 | 2 |
| **mean ≥ strictScore × min(1, N/542)** | **0.832 / 0.888** | **0.432 / 0.440** | **8** | **0** |
| mean ≥ strictScore × min(1, ln(1+N)/ln(543)) | 0.832 / 0.888 | 0.432 / 0.440 | 7 | 0 |
| identity overlap / content terms ≥ 0.50, mean ≥ 1 | 0.832 / 0.888 | 0.048 / 0.048 | 5 | 0 |
| fixed mean ≥ 0.50 | 0.832 / 0.888 | 0.488 / 0.496 | 7 | 2 |
| fixed mean ≥ 0.25 | 0.832 / 0.888 | 0.488 / 0.496 | 7 | 2 |
| fixed mean ≥ 0.10 | 0.832 / 0.888 | 0.488 / 0.496 | 8 | 2 |
| fixed mean ≥ 0 | 0.832 / 0.888 | 0.488 / 0.496 | 8 | 2 |

Unless the row explicitly replaces overlap, identity overlap remains ≥ 3. `mean` is `hit.Score / QueryTerms(prompt).Length`, including distinct folded prefix terms; identity overlap counts distinct content terms of length ≥ 4 on slug/title/aliases/tags, never the body. The negative failures of the permissive rules are q089 and q129; the slice negative stays empty throughout. Gate 5 thresholds ≥ 0.80 / 0.88 pass on the current live corpus. Baseline → final slice hook recall is 5/8 → 8/8 at both @3 and @5, and all eight gold notes remain rank 1. The live corpus hook remains 54/125 at @3 and 55/125 at @5, with 0/5 canaries injected. **A requirement for post-gate 0.80 / 0.88 would not be met by this change or by the measured baseline.**

**Diagnosis:** BM25F IDF is `ln((N-df+0.5)/(df+0.5))`, floored at `1e-6` when nonpositive. Common topic words in a small topic slice therefore contribute almost nothing. S2/S3/S4 have mean scores 0.652047 / 0.896436 / 0.246233 despite correct rank-1 answers and identity overlaps 3 / 5 / 9. The old score threshold rejects them before overlap is checked. This reproduces the 5/8 hook result in the read-only `bench/vm/.out/kiyas/oom/T3-getirme.log` evidence.

**Decision / inference:** linear scaling is the only measured candidate meeting all three observed criteria. It changes the score gate to `strictScore × min(1, max(1,N)/542)`; at N=19 its default is 0.035055. The 542 reference is a calibration constant tied to the historical gate-5 instrument, not a new ranking parameter. For N≥542 the old threshold applies exactly. Installer defaults remain strictScore=1.0 / minOverlap=3. The six negatives constrain this decision; they do not establish general false-positive safety on other small corpora.

**Execution and limits:** `bench/gate/Gate.csproj` references the shipped C# project and invokes its `Rank`, `Query`, `ShouldInject` and `Hook` methods; reflection exposes the tokenizer, identity surface and intent reason for candidate analysis, without reimplementing BM25F. Python applies the candidate predicates to those measured scores. The final run asserts that the selected predicate matches every actual `Hook` result and that pre/post Query names **and scores** are identical on both corpora. Manifest checks confirm concept files were unchanged during and between runs. The published CLI batch also measured the live vault; its slice launch is blocked by `UnauthorizedAccessException` creating the Windows profile state directory, so that slice uses the actual C# `Query`/`Hook` entry points with no state path. End-to-end CLI hook execution in this sandbox and an exact historical 542-note rerun remain unmeasured.

Reproduce from the worktree (offline packages already populated by the previous run):

```powershell
$env:DOTNET_CLI_HOME = "$PWD/.brief/local"
$env:NUGET_PACKAGES = "$PWD/.nuget/packages"
$env:TEMP = "$PWD/.brief/tmp"
$env:TMP = $env:TEMP
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = "$PWD/.brief/gate/bundle"
dotnet restore bench/gate/Gate.csproj --source .nuget/packages --packages .nuget/packages -p:NuGetAudit=false
dotnet build bench/gate/Gate.csproj -c Release --no-restore
dotnet publish src/Oom/Oom.csproj -c Release --no-restore -o publish/win-x64
python bench/gate_measure.py --phase baseline # before the gate edit, retain this snapshot
# Apply Y-110, then repeat build/publish.
python bench/gate_measure.py --phase final
dotnet test tests/Oom.Tests/Oom.Tests.csproj -c Release --no-restore
```

**Tests:** reference baseline supplied by the owner: 123 total / 116 passed / 7 known reds. This sandbox reproduced 123 / 111 / 12 before the change (`.brief/baseline-isolated-tests.txt`) and 124 / 112 / 12 after (`.brief/gate/final-tests.trx`). Seven scar reds remain Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098. Five pre-existing sandbox failures are `Gate12RetrieveJsonCarriesItsSchema`, `Gate12ContextJsonCarriesItsSchema`, `Gate12DoctorJsonCarriesItsSchema`, `Gate12ExtensionContextLineIsExecuted`, `Gate12FailingExtensionAddsNothing` (CLI exit -532462766). This continuation's baseline TRX additionally contains the deliberate `IntentionalRed` seed because an explicit filter overrode the default exclusion; exclude that seed for the comparable 123/111/12 totals. Y-110 fails with an empty hook before the edit (`.brief/gate/red-y110.trx`) and passes after (`.brief/gate/green-y110.trx`); it also checks unrelated and floor-IDF-only rejection, dedupe and explicit strictScore. Release build/publish passed. No new failure entered the comparable set.

## Local model measurement (gate 10)

**`oom bench` is authoritative.** Spec 6.12 names the measurement `oom bench --backend claude|local`, and since lane P3 that command exists in the product (`src/Oom/Bench/Bench.cs`). It is the measurement of record: it grades the model through the shipped path — `Flush` produces the summary and validates its five-section shape, `CompilePrompt` builds the compile prompt and `Compile.ValidateOutputPaths` judges the answer — so the grader cannot drift from the code it grades. `yerel_olcum.py` stays as the offline cross-check: a second, independent implementation in Python that can be run without a build, and whose `--check-drift` guard re-reads the C# files and refuses to measure when the strings it copied no longer appear verbatim. If the two disagree, `oom bench` is right and the Python copy is stale.

Neither writes into the vault. `oom bench` builds its `Flush` with no vault path, no state database and no rejection directory, so no daily block, `flush_log` row or `calls` row comes out of a measurement; the only file it produces is the results JSON. Its records carry the file name, the verdict, the reason and the duration — never the summary or the note text.

```bash
oom --vault <vault> bench --backend local --transcripts 30 --dailies 5 \
    --transcript-dir "%USERPROFILE%\.claude\projects" --daily-dir "<vault>\daily"
oom --vault <vault> bench --backend local --dry-run    # resolve inputs, call nothing, write nothing
python bench/yerel_olcum.py --transcripts 3 --dailies 2    # offline cross-check
```

`--transcripts` defaults to 30 and `--dailies` to 5; without `--transcript-dir` the first `sweep.roots` entry is used and without `--daily-dir` the vault's own `daily\`. `--out` moves the results file, `--judge` runs leg (b), `--dry-run` lists the inputs and calls nothing.

Synthetic run of `oom bench`, `qwen3:8b` via Ollama at `http://localhost:11434/v1`, 3 synthetic Claude Code transcripts and 2 synthetic dailies (invented text, no vault or transcript material). Raw result: `bench/results/oom-bench-sentetik-2026-09-09.json`. The Python harness's own smoke on comparable input is `bench/results/yerel-2026-09-09.json`. **A synthetic run decided nothing** — see the real run below.

### Real run — 2026-09-10, `<vault>`, real transcripts and real dailies

`local.smart` in `<vault>\.oom\oom.json` names `qwen3:14b`, which was not installed on the measuring machine at run time (`qwen3:8b`, `qwen3:30b-a3b-instruct-2507-q4_K_M` and `odena-8b:latest` were). The live vault's `oom.json` was never edited — read-only, as spec 4 requires. The measurement ran against a copy of the vault (config, the isolated `claude-config` credential directory, and a read-only junction onto `knowledge\`) with only `backend.local.smart` changed to `qwen3:30b-a3b-instruct-2507-q4_K_M`, an installed model, so the mismatch is resolved honestly instead of silently: the model actually asked is the model actually reported, and every byte anyone reads back out of `<vault>` (daily files, `.oom\oom.json`, the state DB) is unchanged (`.brief/gate10-vault-once.txt` / `-sonra.txt` are byte-identical apart from unrelated background sweep/ingest activity already running on the machine).

```bash
oom --vault <vault-copy> bench --backend local --transcripts 30 --dailies 5 \
    --transcript-dir "%USERPROFILE%\.claude\projects" --daily-dir "<vault>\daily" --judge \
    --out bench/results/gate10-2026-09-10.json
```

| leg | n | result | threshold | pass |
| --- | ---: | ---: | ---: | --- |
| (a) five-section flush shape | 30 | 0.400 | 0.95 | no |
| (b) double-blind judge | 2 | 2.00 | 3.50 | no |
| (c) text-mode compile conformance | 5 | 0.000 | 0.95 | no |

Raw result: `bench/results/gate10-2026-09-10.json` (full run, all three legs). A flush-and-compile-only sanity pass taken minutes earlier without `--judge` is kept at `bench/results/gate10-2026-09-10-akis-derleme-yalniz.json` for comparison; a standalone `--backend claude` diagnostic pass over the same inputs is at `bench/results/gate10-2026-09-10-claude-backend.json`.

**Leg (a)'s denominator is mostly not the model's fault.** Of the 30 newest files under `%USERPROFILE%\.claude\projects`, 14 were subagent traces (`agent-*.jsonl`, zero summarizable turns by spec 6.3-1's own contract — Bench does not apply Y-112's subagent filter, unlike `ingest`) and 3 more had no new turns to flush; neither class ever reaches the model. Of the 13 transcripts that were actually sent to `qwen3:8b`, 12 produced a conformant five-section summary and 1 failed shape validation — **12/13 = 0.923**, still under the 0.95 threshold but a materially different number from the raw 0.400. The raw 0.400 is what spec 6.12 counts, honestly, but it overstates the model's failure rate roughly three-to-one by counting inputs the model never saw.

**Leg (b) ran on n=2, not 30.** `--judge` paired the local backend's 13 callable flushes against a fresh `claude`-backend reference flush of the same 13 transcripts and asked the Claude `smart` judge to score both blind. Only 2 of the 13 pairs had a non-empty answer on *both* sides — most of the 13 `claude`-side reference flushes did not come back with usable text (a standalone `--backend claude` diagnostic pass was run separately over the same input to see why; see `progress.md` under `## Lane GATE10` for what it found). `RunJudge` silently skips any pair where either side's answer is empty and reports only the pairs it could score (`Table()`'s printed `n` column mirrors `flushCount`, 30, not the actual scored-pair count — a cosmetic mismatch between the console table and the `"why"` field of the JSON, which correctly says `"2 çift"`). The score itself, 2.00/5.00 against a 3.50 threshold, is a genuine double-blind result but on a sample too thin to generalize from.

**Leg (c) failed 5/5, and none of the five was a contract failure this time.** Four dailies were rejected by the local endpoint with HTTP 400 "request exceeds context length" (11 534–21 151 tokens reported by the server) and the fifth — the largest daily — crashed the Ollama `llama-server` child process outright (HTTP 500, `exit status 0xc0000409`) on the first pass and returned the same context-length 400 on the retry. `qwen3:30b-a3b-instruct-2507-q4_K_M`'s own context window is 262 144 tokens (`ollama show`); the failure is not the model's limit but `Runner.CallLocal` (`src/Oom/Runner/Runner.cs`), which sends no `options.num_ctx` in the `/chat/completions` body, so the Ollama server keeps whatever (much smaller) context the instance loaded with. The compile prompt for a real daily plus the real `<vault>` root map and up to 400 registry names routinely runs past that. This is a real, reportable limitation of the local backend's request shape, not a flaw in leg (c)'s grading and not something this measurement lane changed — spec 6.12 measures the shipped path as shipped.

**Decision (spec 6.12): `backend.flush = drop`, `backend.compile = drop`.** Neither axis clears its threshold on the real run. Gate 10 does not pass for `qwen3:8b` (flush) / `qwen3:30b-a3b-instruct-2507-q4_K_M` (compile) on this machine, on this vault, today.

The Python equivalent (`bench/yerel_olcum.py`) was not re-run against the same real inputs in this pass; its own synthetic smoke is `bench/results/yerel-2026-09-09.json` as before.

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
