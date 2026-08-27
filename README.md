# Origin of Memory

[![tests](https://github.com/Capslockiller/origin-of-memory/actions/workflows/ci.yml/badge.svg)](https://github.com/Capslockiller/origin-of-memory/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**Origin of Memory is a persistent memory system — a "second brain" — for
[Claude Code](https://claude.com/claude-code).** It gives ordinary Claude Code
sessions durable, cross-project memory: every session is automatically
summarised into a daily log, a nightly compiler distils those logs into a linked
Markdown knowledge base, and hooks inject the relevant notes back into every new
session and every prompt. Nothing depends on the model remembering to write its
own memory files. The vault is plain Markdown on your disk (Obsidian-compatible),
there are no API keys, no vector database and no external services.

The system runs natively on Windows: PowerShell hooks plus Python 3.12 using only
the standard library.

---

## How it works

```
  session ends                conversation about to compact
  (SessionEnd)                        (PreCompact)
       |                                    |
       +----------------+-------------------+
                        v
                 flush-launch.ps1        detaches in under a second
                        v
                     flush.py            claude -p --model haiku
              reads the transcript, writes a five-section summary
              secret_guard.py redacts credential patterns in and out
                        v
                daily/YYYY-MM-DD.md      written by the machine, not by you
                        |
        (after 18:00, once per day, only if a daily log changed)
                        v
                    compile.py           claude -p --model sonnet
        runs inside an isolated staging copy of knowledge/ + one daily log
        writes only knowledge/concepts/**, index-full.md, log.md
        every change is re-validated before promotion into the live vault
                        v
        +---------------+---------------+
        v                               v
   rootmap.py                      retrieve.py build
   knowledge/index.md              .state/notes.db
   (compact root map)              (SQLite FTS5 index)
   knowledge/hubs/*.md
        |                               |
        v                               v
  session-start.ps1              memory-retrieve.ps1
  SessionStart:                  UserPromptSubmit:
  companion memory +             BM25 over the prompt ->
  root map, 16k char budget      top 3 full notes injected
```

Two properties are load-bearing:

- **The hook does the retrieval, not the model.** Claude is never handed a search
  tool and asked to go look things up — agents under-call such tools. Selection
  happens in the hook, before the model sees the turn.
- **Hooks are registered at user level.** The brain writes from every project and
  reads in every project, not only inside the vault folder.

## Features

- **Automatic session capture.** `SessionEnd` and `PreCompact` both flush; a
  conversation that gets compacted mid-session is not lost.
- **Nightly knowledge compilation.** Daily logs become atomic concept articles
  under `knowledge/concepts/`, cross-linked with a written justification for each
  link, plus a single-row-per-article table in `knowledge/index-full.md`.
- **Per-prompt retrieval.** SQLite FTS5 with `bm25(notes, 8, 6, 3, 1)` — title,
  aliases, tags, body — returns the top 3 full notes, capped at 1,500 characters
  per note and 4,500 in total. Trivial prompts (under 12 characters) and slash
  commands are skipped; a per-session ledger prevents re-injecting the same note.
- **Root map layer.** `rootmap.py` keeps `knowledge/index.md` under a 4,000
  character budget as a topic map into `knowledge/hubs/*.md`, with the full table
  kept separately. Every concept is verified to be covered by a hub before
  publication.
- **Compile isolation.** The compiler never edits the live vault. It works in a
  0700 staging tree at `<vault>/.stage/compile-stage-*`; the resulting file
  manifest is diffed, deletions and out-of-scope writes raise a policy error, and
  only allowed paths are promoted atomically.
- **Secret redaction.** `secret_guard.py` rewrites credential patterns to
  `[SIR:<pattern>]` on the way into and out of the summariser, and scans compiler
  output at the promotion gate.
- **History backfill.** `ingest.py` imports past conversations from Claude Code
  archives (`~/.claude/projects`), Codex rollouts (`~/.codex/sessions`),
  claude.ai export ZIPs and a Google Takeout Gemini archive.
- **First-class Turkish.** See [Turkish support](#turkish-support).
- **MCP memory server.** A stdlib-only local MCP server exposes `memory_search`
  and the root map to any MCP-capable client — Claude Desktop included, on the
  free plan — read-only, over stdio. Setup: [docs/mcp.md](docs/mcp.md).
- **Health check skill.** `beyin doktor` reports hook wiring, script presence,
  daily-log freshness and last compile status in a single table.

## Measured results

Measured on the author's own corpus (roughly 500 concept notes) against a gold
set of 130 real historical questions — 125 scored, 5 held back as canaries.
These numbers are honest but they are one corpus, one language mix and one
person's question distribution; treat them as an existence proof, not a
benchmark. Methodology: [docs/evaluation.md](docs/evaluation.md).

| Metric | Before | After |
| --- | --- | --- |
| recall@3 (judge-free, gold note in top 3) | 0% | **83%** (104/125) |
| recall@5 | 0% | **84%** (105/125) |
| Retrieval hook latency, p95 | — | **347 ms** |
| Compiler input base per call | 152.8K chars | **56.1K chars** (−63%) |

The 0% baseline is not a rhetorical device: before this work the read-side hook
was registered at project scope for a folder where no sessions were ever opened,
so no memory reached any session at all.

## Quickstart

<!-- yazan: codex · gpt-5.6-sol -->
The setup wizard is the primary path. Press Enter for Recommended, review its
auto-detected plan, then press Enter once more to install:

```powershell
git clone https://github.com/Capslockiller/origin-of-memory.git
cd origin-of-memory
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1
```

The two-screen path detects Claude Code, Ollama, LM Studio, llama.cpp, vLLM,
hardware, Documents redirection, and Claude Desktop MCP configuration. Choose
`Custom` only when you want to override a detected default.

| Preset | Capture and compile | Flush / ingest | Read access |
| --- | --- | --- | --- |
| `cloud` | Claude Code hooks + Claude compile | Claude | Hooks; optional MCP / clipboard |
| `hybrid` | Claude Code hooks + Claude compile | Antigravity, Ollama, or OpenAI-compatible | Hooks; optional MCP / clipboard |
| `local` | Claude Code hooks + Claude compile | Antigravity or a local endpoint | MCP + clipboard by default; hooks too |
| `lite` | None — no automatic capture, no compile | Detected local backend or import-only mode | MCP + clipboard; memory comes from export ZIPs |

`local` still needs the `claude` CLI for hooks and compile. `lite` does not use
Claude Code: it has no automatic capture or nightly compile.

<!-- yazan: codex · gpt-5.6-sol -->
- **Guided local models.** The wizard detects Ollama, LM Studio, llama.cpp, and
  vLLM. It ranks verified Ollama tags by hardware fit, can install Ollama after
  consent, and prints manual GUI instructions for LM Studio.

For an agent-driven auto-detected run, use `-Recommended` and always report the
dry-run confirmation screen before running for real:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Recommended -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Recommended
```

For an explicitly authored reproducible plan, `-Answers` remains available:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Answers C:\path\to\plan.json -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Answers C:\path\to\plan.json
```

The plan contract is:

```json
{"preset":"cloud|hybrid|local|lite","vault":"<path>","backend":"claude|antigravity|ollama|openai-compat|none","backend_env":{"BEYIN_*":"<value>"},"mcp":true,"skills":["beyin-doktor"],"force":false,"install_runtime":false,"pull_models":[]}
```

See [the setup-wizard contract](docs/setup-wizard.md) for validation rules and
filled examples. `-DryRun` prints every install, environment, and MCP action and
writes nothing.

To reverse project registrations and optionally remove copied runtime files,
dry-run the safe uninstaller first. It backs up every edited file and never
touches `daily/`, `knowledge/`, companion files, or other vault content:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1 -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1
```

### Direct installer

`install.ps1` remains the lower-level standalone path and keeps its original
default: all skills and all six hooks.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -VaultPath C:\path\to\vault -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -VaultPath C:\path\to\vault
```

Use `-Force` to overwrite scripts and hooks during an upgrade. Use
`-SkillFilter beyin-doktor,beyin-ice-aktar` to install only selected skills.

The installer:

1. Creates the vault directory if it does not exist (it asks first).
2. Copies `scripts/` and `hooks/` into `<vault>\.claude\`, the vault skeleton
   into `<vault>\`, and `skills/` into `<user>\.claude\skills\`.
3. Copies `template/hub-config.example.json` to
   `<vault>\.claude\scripts\hub-config.json` — edit this to define your own topic
   hubs; it is never overwritten once it exists.
4. Registers six hooks in `<user>\.claude\settings.json`, backing the file up
   first and skipping registrations that already exist.
5. Warns if Python 3.12+ or the `claude` CLI is not on `PATH`.

What happens next:

- In `cloud`, `hybrid`, and `local`, your next Claude Code session starts with the memory block
  injected and each prompt pulls up to three relevant notes.
- The first `daily/YYYY-MM-DD.md` appears when that session ends.
- After 18:00, the first session end that finds changed daily content detaches a
  compile run; `knowledge/` appears after it finishes.
- In `lite`, import export ZIPs and use MCP or the clipboard bridge; automatic
  capture and compile are intentionally absent.
- To backfill history first, run `python scripts/ingest.py status` and then the
  `claude`, `codex`, `web` or `gemini` subcommands.

Detailed pipeline: [docs/architecture.md](docs/architecture.md).

**For agents:** point a coding agent at [INSTALL-AGENT.md](INSTALL-AGENT.md) and
it can run this whole install for you, prerequisites and verification included.
Agents working *inside* this repository should read
[AGENTS.md](AGENTS.md).

## Skills

<!-- yazan: codex · gpt-5.6-sol -->
The wizard asks about every skill; `beyin-doktor` and `beyin-ice-aktar` default
to yes, and the rest default to no. Direct `install.ps1` still copies all skills
unless `-SkillFilter` is supplied. Two of them are
part of the mechanism: `beyin-doktor` (health check for the pipeline) and
`beyin-ice-aktar` (processes a claude.ai export ZIP into the vault).

The other four — `companion`, `orchestration`, `codex-fleet`,
`gece-vardiyasi` — are **genericized copies of the author's working
set**: delegation policy, an overnight draft-only shift protocol, and a Codex CLI
operating manual. They are published because the
patterns are useful, not because they are right for you, and several assume
tools you may not have installed.

See [skills/README.md](skills/README.md) for what each one does and what
"genericized" means here.

`skills/companion` is a **structure example**, not an identity — see below.
[template/rules.example.md](template/rules.example.md) is a matching example of
the persistent-rules file the session hook injects.

## Requirements

- **Windows.** The hooks are PowerShell; there is no POSIX path in this
  repository. See [Limitations](#limitations).
- **Python 3.12+**, standard library only. No third-party packages are installed
  or required at runtime. Set `BEYIN_PYTHON` if the interpreter you want is not
  the first `python` on `PATH`.
- **Claude Code CLI** on `PATH`. By default, model calls go through `claude -p`
  on your existing subscription — flush uses Haiku, compile uses Sonnet.
  - **No subscription?** Claude Code is not part of the free claude.ai plan,
    but it also runs on a pay-as-you-go
    [Anthropic API key](https://platform.claude.com/) (`ANTHROPIC_API_KEY`).
    Typical cost for this system's background calls is on the order of a few
    dollars per month, depending on session volume.
  - **Free-tier background calls (optional).** Set
    `BEYIN_MODEL_BACKEND=antigravity` and the background summarisation calls —
    flush and ingest — run through Google's **Antigravity CLI** (`agy`) instead
    of `claude -p`. Install it from the
    [official Antigravity CLI install page](https://antigravity.google/docs/cli)
    (it is not an npm package), then run `agy` interactively once to sign in;
    headless calls reuse those cached credentials, and there is no documented
    API-key environment variable. Honest limits:
    - Claude Code itself is **still required** — the hooks, the session
      lifecycle and the transcripts all come from it. This backend only
      replaces the model that writes the summaries.
    - **Nightly compile still runs on `claude`.** Compile needs the model to
      write files in an isolated staging tree, and `agy` has no per-invocation
      permission scoping — only a user-global allow-list or a blanket
      auto-approval flag, which this repository refuses to ship. In
      `antigravity` mode compile keeps using `claude` when that binary is on
      `PATH`, and otherwise fails loud with
      `antigravity-backend-unsupported:compile`. Advanced, manual, off by
      default: you may add a scoped `"write_file(<staging>/)"` rule to your own
      `~/.gemini/antigravity-cli/settings.json` allow-list — that is your
      decision, and the repository does not make it for you.
    - Free-tier quota is limited. Third-party reports put it around 20 agent
      requests per day with a roughly five-hour refresh; Google does not
      publish these numbers, so treat them as **unverified**.
    - Summary quality on Gemini models is **unmeasured** — the prompt contract
      and the schema validator were tuned against Claude.
    - Model slugs: `BEYIN_AGY_MODEL_FAST` (default `gemini-3.5-flash-medium`,
      the only slug the docs show) and `BEYIN_AGY_MODEL_SMART` (no default —
      pick one from `agy models`; unset degrades to the fast model with a
      warning in health state). `BEYIN_AGY_BIN` overrides the binary name.
    - Note: Google's older **Gemini CLI was retired on 2026-06-18**; `agy` is
      its successor. `BEYIN_MODEL_BACKEND=gemini` is accepted as a deprecated
      alias for `antigravity` and warns.
  <!-- yazan: codex · gpt-5.6-sol -->
  - **Fully local background calls (optional).** Set
    `BEYIN_MODEL_BACKEND=ollama` to send flush and ingest summaries to a local
    Ollama server. This has zero cloud cost; the machine running Ollama bears
    the compute cost. Set `BEYIN_OLLAMA_MODEL_FAST` to an installed model slug.
    `BEYIN_OLLAMA_MODEL_SMART` is optional and falls back to the fast model with
    a warning; `BEYIN_OLLAMA_URL` defaults to `http://localhost:11434`.
    Compile is text-tool mode and is refused just as it is on Antigravity:
    `claude` is used when present, otherwise the run fails loud with
    `ollama-backend-unsupported:compile`. See
    [Local model backends](docs/local-models.md) for model, hardware, and context
    guidance.
  <!-- yazan: codex · gpt-5.6-sol -->
  - **Other local servers (optional).** Set
    `BEYIN_MODEL_BACKEND=openai-compat` for LM Studio, llama.cpp's
    `llama-server`, vLLM, or another local OpenAI-compatible chat endpoint.
    `BEYIN_OPENAI_URL` and `BEYIN_OPENAI_MODEL_FAST` are required;
    `BEYIN_OPENAI_MODEL_SMART` and `BEYIN_OPENAI_KEY` are optional. Compile uses
    `claude` when present and otherwise fails loud with
    `openai-compat-backend-unsupported:compile`.
  - **Clipboard bridge.** Users of consumer web chat on any provider can inject
    the root map and capped top-three memory notes manually:

    ```powershell
    python .claude\scripts\context_pack.py "<question>" --clip
    ```

    Paste the copied block above the question. `--no-map` omits the root map;
    `-k N` selects one to five notes. The project deliberately does **not**
    automate consumer web UIs: that is fragile and conflicts with providers'
    terms.
- **SQLite with FTS5.** Retrieval builds a virtual table with
  `CREATE VIRTUAL TABLE notes USING fts5(...)`. Most CPython builds for Windows
  ship FTS5 enabled, but not all do. Check before installing:

  ```powershell
  python -c "import sqlite3; sqlite3.connect(':memory:').execute('CREATE VIRTUAL TABLE t USING fts5(a)'); print('fts5 ok')"
  ```

  If that raises `sqlite3.OperationalError`, retrieval will not build; the rest of
  the pipeline still works.
- **Obsidian** is optional. The vault is plain Markdown with wikilinks, so
  Obsidian opens it, but nothing in the pipeline depends on it.

## Turkish support

Turkish is a first-class target, not an afterthought, and the design decisions are
deliberate:

- **Dual-form indexing.** Every word of at least three characters is indexed both
  in its raw folded form and, when longer than five characters, as a five-character
  prefix. Queries are tokenised through exactly the same function, so index and
  query always agree.
- **Explicit i-folding.** Turkish dotted/dotless I (`I` `ı` `İ` `i`) is folded
  through an explicit translation table before `casefold()`, never through
  locale-dependent `lower()`/`upper()`. `turkish_fold()` in both `retrieve.py` and
  `rootmap.py` is the single definition.
- **No stemmer, on purpose.** Snowball's Turkish stemmer over-stems badly, collapsing
  unrelated words into one root. Fixed-length truncation plus the raw form was
  measurably safer than a stemmer that merges distinct concepts.
- The shipped summariser and compiler prompts write Turkish articles. If you want
  another language, edit `build_flush_prompt()` in `scripts/flush.py` and
  `COMPILE_PROMPT` in `scripts/compile.py`; the retrieval layer is
  language-agnostic apart from the Turkish folding, which is harmless for other
  Latin-script languages.

## Writing your own companion protocol

`session-start.ps1` injects a *companion memory* layer alongside the generated
root map: it looks for a directory matching `*850-Companion` in the vault and
reads `Last-Session.md`, `Threads.md`, `Kurallar.md` (persistent rules),
`Journal.md` and points the model at `Core.md`.

**Those files are not shipped.** The personal identity layer — who your assistant
is to you, how it addresses you, what rules it must never break — is yours to
write, and a generic template would be worse than nothing. The mechanism only
requires that:

- the directory name ends with `850-Companion`;
- `Last-Session.md` has `## Session:` headings and a `## Previous` boundary;
- `Threads.md` has an `## Active` section (with `### ` items and `**Status:**`
  lines) and a `## Closed` boundary;
- `Kurallar.md` and `Journal.md` are plain Markdown — the first 60 lines and the
  last `##` entry respectively are what get injected.

Missing files are simply skipped. The machine layer (`daily/`, `knowledge/`,
retrieval) works whether or not you write a companion layer.

`skills/companion/SKILL.md` documents this shape with placeholder content, and
[template/rules.example.md](template/rules.example.md) is an example rules file
in the form the hook expects.

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
- **Compiler cost grows with the corpus.** The root-map layer cut the per-call
  base by 63%, but the duplicate-check registry still scales with the number of
  concepts.
- **Nightly compile is single-machine.** The trigger claim is a local file; two
  machines sharing one synced vault can both compile.

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

## Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).
Release history is in [CHANGELOG.md](CHANGELOG.md); security policy and threat
model in [SECURITY.md](SECURITY.md).

## License

MIT. See [LICENSE](LICENSE).
