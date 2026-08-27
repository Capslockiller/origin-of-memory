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

## Suggested models

These are suggestions, not benchmarks. For a Turkish vault, Turkish-language
quality matters at least as much as headline English scores. Qwen 2.5 or Qwen 3
in the 7–14B range is the first model family to try; Gemma 3 and Llama 3.x are
reasonable alternatives. Test summaries against your own Turkish material
before relying on unattended runs.

## Rough hardware guidance

All figures below are rough guidance, not measurements from this project.

- Floor: 8–16 GB system RAM can run a 4-bit 7–8B model on CPU slowly. Background
  summarisation is latency-tolerant, so this can still be useful.
- Comfortable: about 8 GB VRAM or 16 GB unified memory for typical 7–14B local
  use, depending on quantisation and context allocation.
- Better: about 24 GB VRAM or 32 GB unified memory gives more room for 14–32B
  models and larger context allocations.

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
