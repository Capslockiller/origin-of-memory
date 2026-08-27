---
yazan: codex
model: gpt-5.6-sol
---

# Local model backends

Local backends replace the model used for text-only flush and ingest summaries.
They do not replace Claude Code itself, and nightly compile still uses `claude`
when available because compile requires scoped file-writing tools. Without that
binary, compile is refused rather than sent to a text-only local endpoint.

## Runtimes

| Runtime | Backend | Required settings | API path |
| --- | --- | --- | --- |
| Ollama | `ollama` | `BEYIN_OLLAMA_MODEL_FAST`; optional `BEYIN_OLLAMA_MODEL_SMART` and `BEYIN_OLLAMA_URL` | Native `/api/generate` |
| LM Studio | `openai-compat` | `BEYIN_OPENAI_URL`, `BEYIN_OPENAI_MODEL_FAST`; optional smart model and key | OpenAI-compatible `/chat/completions` |
| llama.cpp `llama-server` | `openai-compat` | Same as LM Studio | OpenAI-compatible `/chat/completions` |
| vLLM | `openai-compat` | Same as LM Studio | OpenAI-compatible `/chat/completions` |

`BEYIN_OPENAI_URL` deliberately has no default: common servers use different
ports, and guessing could send memory data to the wrong process. Set the base
URL including any prefix the server needs, such as `http://localhost:1234/v1`.
Set `BEYIN_OPENAI_KEY` only when the server expects a Bearer token; dummy tokens
are accepted by some local servers. `BEYIN_MODEL_BACKEND=openai` is a supported
alias for `openai-compat`, but records a warning so the canonical name remains
visible.

<!-- yazan: codex · gpt-5.6-sol -->
## Hardware probe and model recommendations

Run the standard-library-only tools directly to inspect the full result:

```powershell
python scripts/donanim.py
python scripts/donanim.py --json
python scripts/model_oneri.py
python scripts/model_oneri.py --json
```

The stable probe JSON keys are `ram_gb`, `cpu` (`name`, `physical_cores`,
`logical_cores`), `gpus` (`name`, `vram_gb`, `source`), `free_disk_gb`,
`model_store`, `commands`, `os_build`, and `notes`. Every field is independent:
an unavailable value is `null` and a reason is added to `notes` instead of
aborting the result. `OLLAMA_MODELS` selects the model-store path; the default
is `%USERPROFILE%\.ollama\models`.

GPU memory detection does not trust `Win32_VideoController.AdapterRAM`: that
field is 32-bit and can misreport cards above 4 GB. The order is NVIDIA
`nvidia-smi`, the cross-vendor display-class registry QWORD
`HardwareInformation.qwMemorySize`, then `AdapterRAM` as a warned last resort.

The embedded catalogue contains only these verified Ollama tags and file sizes:

| Tag | File size |
| --- | ---: |
| `qwen3:4b` | 2.5 GB |
| `qwen3:8b` | 5.2 GB |
| `qwen3:14b` | 9.3 GB |
| `qwen3:30b` | 19 GB |
| `gemma3:4b` | 3.3 GB |
| `gemma3:12b` | 8.1 GB |
| `gemma3:27b` | 17 GB |

The estimator uses `need_GB = file_size_GB × 1.2 + 1.0` and labels each model
`fits-gpu`, `tight`, `cpu-ok`, or `no-fit`. These are memory-fit estimates, not
benchmarks or speed promises. Context length, GPU offload, runtime settings, and
workload can change real behaviour. Test summaries against your own material.

## Guided Ollama setup

In an interactive `hybrid` or `local` wizard run with the `ollama` backend,
`kur.ps1` prints the probe and ranked fit table. After explicit confirmation it
can:

1. Install Ollama through
   `winget install --id Ollama.Ollama -e --accept-source-agreements --accept-package-agreements`.
2. If `winget` is unavailable, download the official
   `https://ollama.com/download/OllamaSetup.exe` and run it with
   `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`. These are widely reported
   InnoSetup flags but are not officially documented by Ollama. Windows
   SmartScreen may prompt; the wizard warns in advance and never bypasses it.
3. Refresh the current process PATH from Machine and User values, then verify
   `ollama --version`.
4. Require model-store free space equal to 1.5 times the selected catalogue file
   size, then stream `ollama pull <tag>`. Ollama pulls are resumable.

The default install is per-user under `%LOCALAPPDATA%\Programs\Ollama` and does
not need administrator rights. Use `HTTPS_PROXY`, not `HTTP_PROXY`, when Ollama
must reach its registry through a proxy. `-DryRun` skips this entire step and
cannot install or download anything.

## Context window and flush chunking

Local models often expose a smaller usable context than hosted models. When
`BEYIN_MODEL_BACKEND` resolves to `ollama` or `openai-compat`, live flushes use a
24,000-character transcript bound (roughly 6,000 tokens), leaving room in an 8k
context for instructions and output. Claude and Antigravity retain the project's
existing bound.

Set `BEYIN_FLUSH_CHUNK_CHARS` to a positive integer to override the live-flush
bound for any backend. Invalid values are ignored, a
`warn:flush-chunk-invalid:<value>` warning is recorded, and the existing Claude
or local default is used for the selected backend. The effective value is
included in each flush run's state detail for diagnosis.
