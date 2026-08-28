# Origin of Memory

**Persistent, cross-project memory for Claude Code on Windows — sessions are
summarised automatically, compiled into a linked Markdown knowledge base, and
injected back into your next prompt.**

[![tests](https://github.com/Capslockiller/origin-of-memory/actions/workflows/ci.yml/badge.svg)](https://github.com/Capslockiller/origin-of-memory/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> Türkçe: [README.tr.md](README.tr.md)

No API keys, no vector database, no external services. The vault is plain
Markdown on your disk (Obsidian-compatible). PowerShell hooks plus Python 3.12,
standard library only.

## What it looks like

```
  WITHOUT MEMORY                       WITH ORIGIN OF MEMORY
  ───────────────────────────────      ───────────────────────────────
  > why did we drop the stemmer?       > why did we drop the stemmer?

  I don't have context from            [2 notes injected by the hook,
  earlier sessions — could you          before the model saw the turn]
  paste the relevant decision?          · turkce-stemmer-karari.md
                                        · retrieval-tokenizasyon.md

                                       Snowball over-stemmed Turkish —
                                       it collapsed unrelated words
                                       into one root. Dropped for
                                       fixed-length truncation.
```

<!-- Illustration of the mechanism, not a screenshot. If a real terminal
     capture is wanted for the social preview or the README, the orchestrator
     must produce it from an actual session; do not synthesise one. -->

## Quickstart

```powershell
git clone https://github.com/Capslockiller/origin-of-memory.git
cd origin-of-memory
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1
```

Press Enter for the recommended plan, review what it auto-detected, press Enter
again to install. Nothing is written before that second Enter. Presets,
non-interactive plans, the lower-level `install.ps1`, upgrading and uninstalling:
[docs/install.md](docs/install.md).

## Is this for me?

| You are | What you get | What you need | Preset |
| --- | --- | --- | --- |
| A Claude Code user on a subscription | The whole thing: automatic capture, nightly compile, memory injected into every session and every prompt | Windows, Python 3.12+, Claude Code CLI | `cloud` |
| Someone who wants it local | Same pipeline, but session summaries are written by your own model — Ollama, LM Studio, llama.cpp, vLLM or Antigravity. `claude` is still required for the hooks and the nightly compile | The above, plus a local server or the `agy` CLI | `local` or `hybrid` |
| Someone who just wants to search past conversations | Import claude.ai / Codex / Gemini exports and query them from any MCP client or the clipboard bridge. No hooks, no automatic capture, no compile | Windows, Python 3.12+, an MCP-capable client, and a local backend to summarise the imports | `lite` |

## How it works

```
  SessionEnd / PreCompact  ->  flush.py    ->  daily/YYYY-MM-DD.md
                               (haiku)         one file per day

  after 18:00, if a log changed
                           ->  compile.py  ->  knowledge/concepts/*.md
                               (sonnet)        atomic, cross-linked articles
                               in an isolated staging tree; every write
                               re-validated before promotion

                           ->  rootmap.py  ->  knowledge/index.md + hubs/
                           ->  retrieve.py ->  .state/notes.db (FTS5)

  SessionStart      ->  root map + companion memory, 16k char budget
  UserPromptSubmit  ->  BM25 over your prompt, top 3 full notes injected
```

Full annotated pipeline and the complete feature list:
[docs/features.md](docs/features.md). Implementation:
[docs/architecture.md](docs/architecture.md).

## Measured results

Measured on the author's own corpus (roughly 500 concept notes) against a gold
set of 130 real historical questions — 125 scored, 5 held back as canaries.
These numbers are honest but they are one corpus, one language mix and one
person's question distribution; treat them as an existence proof, not a
benchmark. Methodology: [docs/evaluation.md](docs/evaluation.md).

| Metric | Before | After |
| --- | --- | --- |
| recall@3 (judge-free, gold note in top 3) | 0% | **83.2%** (104/125) |
| recall@5 | 0% | **91.2%** (114/125) |
| Retrieval hook latency, p95 | — | **347 ms** |
| Compiler input base per call | 152.8K chars | **56.1K chars** (−63%) |

The 0% baseline is not a rhetorical device: before this work the read-side hook
was registered at project scope for a folder where no sessions were ever opened,
so no memory reached any session at all.

An independent review of the codebase scored it 5/5 on code quality, security,
error handling and dependency health, and 4/5 on architecture, performance and
tests. That was a review of the code, not an endorsement of the project, and its
author has no affiliation with it.

> **Correction, 2026-08-28:** recall@5 was previously published as 84%
> (105/125). That run retrieved three results and labelled the column `top5`. A
> correct re-run at `limit=5` gives 114/125 = 91.2%; recall@3 was unaffected.
> The full explanation is in [docs/evaluation.md](docs/evaluation.md).

## How this differs from the alternatives

The category is *agent memory*. This one differs from most of it in four ways
worth knowing before you choose:

- **Memory is pushed, not searched.** There is no tool the model has to remember
  to call. A hook selects the notes and injects them before the model sees the
  turn — because agents reliably under-call retrieval tools, and a memory that
  only works when the model remembers to look is not memory.
- **The recall number comes from a real corpus.** 125 real historical questions
  against the author's own vault, with the gold set and its limits described in
  full. It is not a public benchmark score, and it is not claimed to transfer.
- **Zero runtime dependencies.** Python 3.12 standard library and PowerShell.
  No embedding model, no vector store, no service to keep running, no key to
  rotate. The whole index is one SQLite file.
- **Windows-native, deliberately.** Not a POSIX tool with a Windows caveat. That
  is also its main limitation — see below.

Lineage, and what this took from where: [docs/attribution.md](docs/attribution.md).

## What you need

- **Windows**, **Python 3.12+** (stdlib only), and the **Claude Code CLI** on
  `PATH`. Model calls go through `claude -p` on your existing subscription by
  default — flush uses Haiku, compile uses Sonnet.
- **SQLite with FTS5.** Most Windows CPython builds have it; check with
  `python -c "import sqlite3; sqlite3.connect(':memory:').execute('CREATE VIRTUAL TABLE t USING fts5(a)')"`.
- No Claude subscription? Claude Code also runs on a pay-as-you-go
  [Anthropic API key](https://platform.claude.com/); the background calls cost
  on the order of a few dollars a month.
- Prefer a different model for the background calls? Antigravity, Ollama, or any
  OpenAI-compatible local server can write the summaries. Compile still needs
  `claude`, and each backend has honest limits worth reading first.

Full requirements, every backend, its environment variables and its limits:
[docs/backends.md](docs/backends.md).

## Limitations

- **Windows only.** The hooks are PowerShell and the file locking falls back to
  `msvcrt` region locks. There is no tested macOS or Linux path here. For those
  platforms, use the upstream project this one derives from:
  [avenoxbeyin](https://github.com/avenoxai/avenoxbeyin) (macOS tested, Linux
  untested).
- **Measured on one corpus.** All numbers in this README come from the author's
  own vault and question set. Your recall will differ. Build your own gold set —
  [docs/evaluation.md](docs/evaluation.md) explains how, and why the statistical
  floor means a personal evaluation only detects large changes.
- **Sensitive-data filtering is not included.** `secret_guard.py` catches
  credential patterns — keys, tokens, connection strings, password assignments.
  It does **not** detect health information, legal status, financial detail or
  third parties who never consented to being written down. If your sessions
  contain that material, it will be summarised, compiled and stored. This is a
  known open gap, described honestly in [SECURITY.md](SECURITY.md).
- **Compiler input is now bounded, not free.** The root-map layer cut the
  per-call base by 63%, and the duplicate-check registry — which used to grow one
  row per concept forever — is now scoped to the daily log's hubs plus the 50
  most recently updated concepts, hard-capped at 400 rows. On a synthetic
  1000-concept corpus that is 67,800 → 15,806 characters. The trade is real: the
  model sees a partial dedupe view, is told so in one line of the prompt, and can
  therefore miss a duplicate that lives outside the selected rows.
- **Cross-machine compile is prevented cooperatively, not guaranteed.** The
  compile lock records which machine holds it, and a run that finds a live lock
  from another machine skips instead of compiling alongside it. It depends on
  your sync tool having propagated the lock file, so a fast enough race can still
  slip through.
- **Web-fetched text that enters a transcript can be summarised into the vault.**
  Untrusted-data delimiters are in place, but there is no exclusion list.

## Attribution

This project is **adapted, not forked, from
[avenoxbeyin v2](https://github.com/avenoxai/avenoxbeyin) by
[Avenox](https://avenox.lol)** (MIT). It was built clean-room from that
project's SPEC-V2, ported to native Windows, and then extended with layers
upstream does not have: user-level hook registration, FTS5 BM25 per-prompt
retrieval, the root-map layer, secret redaction, the ingest family and a gold-set
evaluation. There is no shared code history — hence "adapted", not "forked".

The knowledge-compilation pattern traces to Andrej Karpathy's LLM knowledge-base
gist:
<https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f>

Full lineage and reasoning: [docs/attribution.md](docs/attribution.md).

## Documentation

| | |
| --- | --- |
| [docs/install.md](docs/install.md) | Presets, plans, the direct installer, upgrade, uninstall |
| [docs/features.md](docs/features.md) | Full feature list, Turkish design, the companion layer |
| [docs/backends.md](docs/backends.md) | Requirements and every model backend |
| [docs/architecture.md](docs/architecture.md) | How the pipeline is actually built |
| [docs/retrieval.md](docs/retrieval.md) | Ranking, session anchors, known limits |
| [docs/evaluation.md](docs/evaluation.md) | The gold set, the metric, the correction |
| [docs/mcp.md](docs/mcp.md) · [docs/local-models.md](docs/local-models.md) · [docs/compatibility.md](docs/compatibility.md) | MCP setup, local model guidance, what was tested |

**For agents:** point a coding agent at [INSTALL-AGENT.md](INSTALL-AGENT.md) and
it can run the whole install for you. Agents working *inside* this repository
should read [AGENTS.md](AGENTS.md).

## Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).
Release history is in [CHANGELOG.md](CHANGELOG.md); security policy and threat
model in [SECURITY.md](SECURITY.md).

## License

MIT. See [LICENSE](LICENSE).
