---
yazan: codex
model: gpt-5.6-sol
---

# Local model backends

Local backends handle text-only flush and ingest summaries. They do not replace
Claude Code capture, and nightly compile still needs `claude` because compile
uses scoped file-writing tools. A lite install works without Claude Code but has
no automatic capture or nightly compile.

## Runtime detection

`scripts/donanim.py` probes every runtime independently and never treats an
optional runtime failure as fatal. TCP checks open only a connection, use a
200 ms timeout, and make no HTTP or model request.

| Runtime | Binary/install probe | TCP probe | Reported endpoint | Backend |
| --- | --- | --- | --- | --- |
| Ollama | `ollama` on PATH | `127.0.0.1:11434` | `http://127.0.0.1:11434` | `ollama` |
| LM Studio | `lms` on PATH or `%LOCALAPPDATA%\Programs\lm-studio` | `127.0.0.1:1234` | `http://127.0.0.1:1234/v1` | `openai-compat` |
| llama.cpp | `llama-server` on PATH | `127.0.0.1:8080` | `http://127.0.0.1:8080/v1` | `openai-compat` |
| vLLM | none on Windows | `127.0.0.1:8000` | `http://127.0.0.1:8000/v1` | `openai-compat` |

Each JSON row has `name`, `detected_by`, `endpoint`, and `backend`.
`detected_by` is `binary`, `port`, `both`, or `null`. vLLM detection usually
means a WSL or forwarded server; the wizard never offers to install vLLM.

Run the probes directly with:

```powershell
python scripts/donanim.py
python scripts/donanim.py --json
python scripts/model_oneri.py
python scripts/model_oneri.py --json
```

The wizard recommends an already-running runtime before an installed but stopped
one. Ties use Ollama, LM Studio, llama.cpp, then vLLM.

## Ollama

Set `BEYIN_MODEL_BACKEND=ollama` and `BEYIN_OLLAMA_MODEL_FAST` to a verified tag.
`BEYIN_OLLAMA_MODEL_SMART` and `BEYIN_OLLAMA_URL` are optional. The runner uses
Ollama's native `/api/generate` endpoint.

The verified catalogue remains:

| Tag | File size |
| --- | ---: |
| `qwen3:4b` | 2.5 GB |
| `qwen3:8b` | 5.2 GB |
| `qwen3:14b` | 9.3 GB |
| `qwen3:30b` | 19 GB |
| `gemma3:4b` | 3.3 GB |
| `gemma3:12b` | 8.1 GB |
| `gemma3:27b` | 17 GB |

The estimator labels candidates `fits-gpu`, `tight`, `cpu-ok`, or `no-fit`.
Recommended setup chooses the first `fits-gpu` or `cpu-ok` candidate. These are
memory estimates, not speed promises.

If Ollama is absent, Custom can install it with `winget`. Model pulls are a
separate opt-in with a 1.5× disk-space preflight. `-DryRun` never installs or
downloads anything.

## LM Studio

Install the GUI from <https://lmstudio.ai/download>, load a model, and enable
the local server in its Developer/Server tab. The wizard prints this URL but
does not download or launch a GUI installer. Configure:

```text
BEYIN_MODEL_BACKEND=openai-compat
BEYIN_OPENAI_URL=http://127.0.0.1:1234/v1
BEYIN_OPENAI_MODEL_FAST=<identifier shown in the Server tab>
```

The model name cannot be known safely without HTTP. The wizard never lists
models in dry-run or unattended recommended mode.

## llama.cpp

Start `llama-server` with the intended model, then use:

```text
BEYIN_MODEL_BACKEND=openai-compat
BEYIN_OPENAI_URL=http://127.0.0.1:8080/v1
BEYIN_OPENAI_MODEL_FAST=<served model identifier>
```

The runner appends `/chat/completions` to this base URL, matching the OpenAI
chat-completions API exposed by the server.

## vLLM

Expose its OpenAI-compatible server on port 8000 and configure:

```text
BEYIN_MODEL_BACKEND=openai-compat
BEYIN_OPENAI_URL=http://127.0.0.1:8000/v1
BEYIN_OPENAI_MODEL_FAST=<served model identifier>
```

Windows-native support is unlikely, so detection is described as probably WSL
or remote/forwarded. There is no wizard install action.

## OpenAI-compatible runner details

`BEYIN_OPENAI_URL` is the base through `/v1`; `scripts/openai_runner.py` appends
`/chat/completions`. `BEYIN_OPENAI_MODEL_SMART` and `BEYIN_OPENAI_KEY` are
optional. A model identifier is mandatory for an explicit `-Answers` plan.
Recommended mode leaves an unknown identifier unset and prints a `[TODO]`
instead of guessing or making an unconsented HTTP request.

Local model live flushes use a 24,000-character transcript bound. Set
`BEYIN_FLUSH_CHUNK_CHARS` to a positive integer to override it. Invalid values
are ignored and recorded as a health warning.

## Timeouts

Local inference is far slower than a hosted API. An 8B model summarising a
15,000-character transcript on CPU can take longer than the 240-second bound the
hosted path uses, and the call is then killed and recorded as a timeout — the
session's content is not summarised, and the daily log simply never gets that
block.

So the **default** depends on the resolved backend:

| Call | `claude` / `antigravity` | `ollama` / `openai-compat` | Variable |
| --- | ---: | ---: | --- |
| flush | 240 s | **900 s** | `BEYIN_FLUSH_TIMEOUT` |
| ingest | 240 s | **900 s** | `BEYIN_INGEST_TIMEOUT` |
| compile | 900 s | 900 s | `BEYIN_COMPILE_TIMEOUT` |

Compile is not raised because it is already at 900 s and always runs on `claude`
— the local backends refuse the tool-mode call. The Codex ingest path takes
`BEYIN_INGEST_TIMEOUT` but never the local bump, since it is its own CLI rather
than a `BEYIN_MODEL_BACKEND` target.

An explicit variable always wins, on every backend:

```text
BEYIN_FLUSH_TIMEOUT=1800
```

The value is seconds, and must be a positive integer. Anything else — a
non-number, `0`, a negative, or an empty string — is **ignored in favour of the
default** and recorded as the health warning
`warn:timeout-invalid:<VARIABLE>:<value>`, because a typo in a variable must
never break the session the hook is attached to. Check for it with
`python scripts/durum.py` if a timeout you set does not seem to apply.

The effective value is recorded where each component keeps its state —
`last-flush.json` and `compile-state.json` carry a `timeout` field, and ingest's
`last_run` carries one — so a `claude-timeout`, `agy-timeout`, `ollama-timeout`
or `codex-timeout` can be read against the bound that produced it instead of
guessing.

If flushes are timing out on a local model, raising the timeout is the first
thing to try; lowering `BEYIN_FLUSH_CHUNK_CHARS` so the model has less to read is
the second.

## Measuring your own setup

Whether a local model is worth it on *your* hardware is not a question this
document can answer for you, so the pipeline records the evidence instead of
guessing. Every model call appends one line to
`<vault>\.claude\scripts\.state\calls.jsonl`: timestamp, backend, model tier and
resolved slug, component, character counts, estimated tokens, duration in
milliseconds, and outcome. No prompt or response text — it is a ledger, not a
log.

```powershell
python scripts/durum.py            # last 7 days, per backend and per component
python scripts/durum.py --json     # same numbers under the "calls" key
```

The practical way to compare: run a week on `claude`, note the median and p95
durations, then switch `BEYIN_MODEL_BACKEND` and run another week. The ledger
keeps both, labelled by backend, and the failure column tells you how often a
local run timed out — usually the number that decides it, not the median.

Two honest caveats about the token figures:

- **They are characters ÷ 4, not a provider count.** The field names say `_est`
  for that reason. No provider is asked what it actually billed, and nothing
  here reads a usage header. Treat the numbers as a consistent yardstick for
  comparing runs to each other, not as an invoice.
- **Turkish tokenizes worse than English, so the estimate skews low.** The 4:1
  ratio comes from English text. Turkish agglutination and non-ASCII characters
  produce more tokens per character, so a Turkish corpus will use more real
  tokens than this estimate suggests. The direction of the error is known; the
  size of it is not measured here.

Durations have no such caveat — they are wall-clock time around the call, which
is exactly what you feel — but remember they include process startup for the CLI
backends and model load time for a cold local server.
