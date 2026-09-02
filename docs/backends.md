# Requirements and model backends

> Türkçe: [backends.tr.md](backends.tr.md)

What has to be on the machine, and which model writes the summaries. The exact
flag surfaces and endpoints each backend was built against are in
[compatibility.md](compatibility.md); model, hardware and context guidance for
local servers is in [local-models.md](local-models.md).

---

## Requirements

- **Windows.** The hooks are PowerShell; there is no POSIX path in this
  repository. See the README's limitations section.
- **Python 3.12+**, standard library only. No third-party packages are installed
  or required at runtime. Set `BEYIN_PYTHON` if the interpreter you want is not
  the first `python` on `PATH`.
- **Claude Code CLI** on `PATH`. By default, model calls go through `claude -p`
  on your existing subscription — flush uses Haiku, compile uses Sonnet.
- **SQLite with FTS5.** Retrieval builds a virtual table with
  `CREATE VIRTUAL TABLE notes USING fts5(...)`. Most CPython builds for Windows
  ship FTS5 enabled, but not all do. Check before installing:

  ```powershell
  python -c "import sqlite3; sqlite3.connect(':memory:').execute('CREATE VIRTUAL TABLE t USING fts5(a)'); print('fts5 ok')"
  ```

  If that raises `sqlite3.OperationalError`, retrieval will not build; the rest
  of the pipeline still works.
- **Obsidian** is optional. The vault is plain Markdown with wikilinks, so
  Obsidian opens it, but nothing in the pipeline depends on it.

## No Claude subscription?

Claude Code is not part of the free claude.ai plan, but it also runs on a
pay-as-you-go [Anthropic API key](https://platform.claude.com/)
(`ANTHROPIC_API_KEY`). Typical cost for this system's background calls is on the
order of a few dollars per month, depending on session volume.

## Which call goes where

Only two kinds of model call exist in this system, and they have different
requirements:

| Call | What it does | Backends that can run it |
| --- | --- | --- |
| **Flush / ingest** | Reads a transcript, writes a five-section summary | `claude`, `antigravity`, `ollama`, `openai-compat` |
| **Compile** | Writes files inside an isolated staging tree | `claude` only |

Compile is the only call that writes files. It needs per-invocation permission
scoping (`--permission-mode acceptEdits` with a matching `--allowedTools`), and
no other backend here offers that. In every non-`claude` mode, compile keeps
using `claude` when that binary is on `PATH` and otherwise fails loud with
`<backend>-backend-unsupported:compile` rather than running unscoped.

## Free-tier background calls — Antigravity (optional)

Set `BEYIN_MODEL_BACKEND=antigravity` and the background summarisation calls —
flush and ingest — run through Google's **Antigravity CLI** (`agy`) instead of
`claude -p`. Install it from the
[official Antigravity CLI install page](https://antigravity.google/docs/cli) (it
is not an npm package), then run `agy` interactively once to sign in; headless
calls reuse those cached credentials, and there is no documented API-key
environment variable.

Honest limits:

- Claude Code is **no longer required for capture.** Since 0.3.0 the
  [watcher](watcher.md) reads settled transcripts from disk, so the hooks are a
  latency optimisation rather than a dependency, and Codex or a generic folder
  can be the source instead. What Claude Code still uniquely provides is
  **per-message injection** — that needs a prompt-submit hook, and no other host
  offers one. This backend only replaces the model that writes the summaries.
- **Nightly compile still runs on `claude`.** Compile needs the model to write
  files in an isolated staging tree, and `agy` has no per-invocation permission
  scoping — only a user-global allow-list or a blanket auto-approval flag, which
  this repository refuses to ship. In `antigravity` mode compile keeps using
  `claude` when that binary is on `PATH`, and otherwise fails loud with
  `antigravity-backend-unsupported:compile`. Advanced, manual, off by default:
  you may add a scoped `"write_file(<staging>/)"` rule to your own
  `~/.gemini/antigravity-cli/settings.json` allow-list — that is your decision,
  and the repository does not make it for you.
- Free-tier quota is limited. Third-party reports put it around 20 agent
  requests per day with a roughly five-hour refresh; Google does not publish
  these numbers, so treat them as **unverified**.
- Summary quality on Gemini models is **unmeasured** — the prompt contract and
  the schema validator were tuned against Claude.
- Model slugs: `BEYIN_AGY_MODEL_FAST` (default `gemini-3.5-flash-medium`, the
  only slug the docs show) and `BEYIN_AGY_MODEL_SMART` (no default — pick one
  from `agy models`; unset degrades to the fast model with a warning in health
  state). `BEYIN_AGY_BIN` overrides the binary name.
- Note: Google's older **Gemini CLI was retired on 2026-06-18**; `agy` is its
  successor. `BEYIN_MODEL_BACKEND=gemini` is accepted as a deprecated alias for
  `antigravity` and warns.

## Fully local background calls — Ollama (optional)

Set `BEYIN_MODEL_BACKEND=ollama` to send flush and ingest summaries to a local
Ollama server. This has zero cloud cost; the machine running Ollama bears the
compute cost.

- `BEYIN_OLLAMA_MODEL_FAST` — an installed model slug. Required.
- `BEYIN_OLLAMA_MODEL_SMART` — optional; falls back to the fast model with a
  warning.
- `BEYIN_OLLAMA_URL` — defaults to `http://localhost:11434`.
- `BEYIN_OLLAMA_NUM_CTX` — optional context window (tokens) sent as
  `options.num_ctx`. Ollama silently truncates input past its default window
  (~4k on <24 GiB cards), which turns a long transcript into a
  `summary-schema-invalid` flush; `16384` is a measured safe floor for
  flush-sized inputs. Unset keeps the server default, byte-identical request.

Compile is text-tool mode and is refused just as it is on Antigravity: `claude`
is used when present, otherwise the run fails loud with
`ollama-backend-unsupported:compile`. See [local-models.md](local-models.md) for
model, hardware and context guidance.

## Other local servers — OpenAI-compatible (optional)

Set `BEYIN_MODEL_BACKEND=openai-compat` for LM Studio, llama.cpp's
`llama-server`, vLLM, or another local OpenAI-compatible chat endpoint.

- `BEYIN_OPENAI_URL` and `BEYIN_OPENAI_MODEL_FAST` are required.
- `BEYIN_OPENAI_MODEL_SMART` and `BEYIN_OPENAI_KEY` are optional.

Compile uses `claude` when present and otherwise fails loud with
`openai-compat-backend-unsupported:compile`.

## Clipboard bridge

Users of consumer web chat on any provider can inject the root map and capped
top-three memory notes manually:

```powershell
python .claude\scripts\context_pack.py "<question>" --clip
```

Paste the copied block above the question. `--no-map` omits the root map; `-k N`
selects one to five notes. The project deliberately does **not** automate
consumer web UIs: that is fragile and conflicts with providers' terms.

## MCP

A stdlib-only local MCP server exposes `memory_search` and the root map to any
MCP-capable client — Claude Desktop included, on the free plan — read-only, over
stdio. Setup and caveats: [mcp.md](mcp.md).
