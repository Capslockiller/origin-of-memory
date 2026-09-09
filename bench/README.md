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

Gate 5 requires the v0 125-question gold set to be evaluated against the 2.0 index, with recall@3 at least 0.80 and recall@5 at least 0.88. The historical matrix provides retrieval methodology and regression evidence, but the current scripts do not yet run that exact gold set against `src/Oom`; the 2.0 gate remains planned.

Gate 10 requires `oom bench --backend local` to measure 30 flush transcripts and five compile dailies, write `bench/results/<date>.json`, and leave the backend lists consistent with the result. `Program` currently prints a pointer to `bench/`, and no `bench/results/` implementation is present in this tree. Gate 10 therefore remains planned and must not be reported as passed.

No benchmark result is a default merely because code for the feature exists. Record the dataset revision, parameters, model identity, machine, raw result path, and the acceptance threshold whenever a gate is measured.
