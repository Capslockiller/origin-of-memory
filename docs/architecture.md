# Architecture

File-level walkthrough of the memory pipeline: which hook fires what, what each
script produces, how the compiler is isolated from the live vault, and how the
recursion guard keeps the system from feeding on itself.

Overview and quickstart: [../README.md](../README.md).

---

## 1. Layout after installation

<!-- yazan: codex · gpt-5.6-sol -->
`kur.ps1` is the interview/plan layer over this layout. Interactive runs collect
a `cloud`, `hybrid`, `local`, or `lite` plan before writing; agents pass the
same strict JSON contract through `-Answers`. The wizard delegates copying and
normal hook registration to `install.ps1`, then applies user-scope `BEYIN_*`
variables and an optional non-clobbering Claude Desktop MCP merge. Lite skips
hook registration, automatic capture, and compile.

`install.ps1 -VaultPath <vault>` produces three destinations:

```
<vault>\
├── .claude\
│   ├── hooks\                  <- hooks\*.ps1
│   │   └── .state\             <- session_start_time, prompt_count, needs_reflection
│   └── scripts\                <- scripts\*.py
│       ├── hub-config.json     <- from template\hub-config.example.json, written once
│       └── .state\             <- compile-state.json, ingest state, health.json,
│                                  notes.db, retrieve-session-*.json, hookin-*.json
├── daily\                      <- machine-written daily logs
├── knowledge\                  <- machine-compiled knowledge base
│   ├── index.md                <- compact root map (rootmap.py)
│   ├── index-full.md           <- full article table (compile.py)
│   ├── hubs\<id>.md            <- topic hubs (rootmap.py)
│   ├── concepts\*.md           <- atomic concept articles (compile.py)
│   └── log.md                  <- compile run log (compile.py)
├── .stage\compile-stage-*\     <- transient compile staging, mode 0700
├── .import\                    <- claude.ai export ZIPs you drop in
└── <anything>850-Companion\    <- your own companion memory layer (not shipped)

<user>\.claude\
├── settings.json               <- six hook registrations (backed up before write)
└── skills\                     <- skills\beyin-doktor, skills\beyin-ice-aktar
```

Scripts resolve the vault as `Path(__file__).resolve().parent.parent.parent` —
two levels above `scripts/`. The `.claude/scripts/` placement is therefore load
bearing; moving the scripts changes where the vault is believed to be.

## 2. Hook registration

All six registrations live in `<user>\.claude\settings.json` — user scope, not
project scope. This is the difference between a brain that writes everywhere and
reads in one folder, and one that does both everywhere.

| Event | Script | Timeout | Job |
| --- | --- | --- | --- |
| `SessionStart` | `session-start.ps1` | 15 s | Inject companion memory + root map + today's log |
| `UserPromptSubmit` | `prompt-counter.ps1` | 5 s | Count prompts; nudge every 15th |
| `UserPromptSubmit` | `memory-retrieve.ps1` | 5 s | BM25 retrieval, inject top 3 notes |
| `SessionEnd` | `flush-launch.ps1 -Reason sessionend` | 15 s | Detach `flush.py` |
| `SessionEnd` | `session-end.ps1` | 10 s | Raise `needs_reflection` if memory was not updated |
| `PreCompact` | `flush-launch.ps1 -Reason precompact` | 15 s | Detach `flush.py` before compaction |

Each hook returns JSON on stdout in Claude Code's
`hookSpecificOutput.additionalContext` shape, or exits 0 silently. Every hook sets
`$ErrorActionPreference = 'SilentlyContinue'`: a broken memory system must never
break the session it is attached to.

The project-scoped `.claude/settings.json` inside the vault is intentionally left
empty of hook registrations, so hooks do not fire twice when a session is opened
in the vault itself.

## 3. `BEYIN_INVOKED_BY` — the recursion guard

The pipeline calls `claude -p` to do its own summarising and compiling. Those
subprocesses are themselves Claude Code sessions, which would fire the same hooks,
which would flush a transcript, which would call `claude -p` again.

`scripts/claude_runner.py` sets `BEYIN_INVOKED_BY=beyin-scripts` in the child
environment for every model call. Every hook script tests it on its first
executable line and exits 0:

```powershell
if ($env:BEYIN_INVOKED_BY) { exit 0 }
```

`compile.py` and `ingest.py` make the same check in `main()`. The one place the
variable is deliberately removed is `flush.maybe_trigger_compile()`, which pops it
from the environment before launching `compile.py` — that launch happens from a
flush that may itself have been a child, and the compiler must be allowed to run.

## 4. Write path

### 4.1 `SessionEnd` / `PreCompact` → `flush-launch.ps1`

Reads the hook payload from stdin, writes it to
`.claude/scripts/.state/hookin-<pid>-<random>.json` as BOM-less UTF-8 (Python
parses it as strict JSON), resolves a Python interpreter — `BEYIN_PYTHON`, then
`python`, then `py -3` — and launches `flush.py -X utf8 --hook-input <path>
--reason <sessionend|precompact>` with `Start-Process -WindowStyle Hidden`. It
returns immediately; the summariser is not on the session-teardown critical path.

### 4.2 `flush.py`

1. **Load and validate.** Reads the hook input file, repairing invalid JSON escape
   sequences. Refuses hook-input paths it did not manage, and sweeps files older
   than an hour.
2. **Lock per session.** An exclusive lock keyed on `session_id`
   (`fcntl.flock`, or `msvcrt.locking` on Windows). A second flush for the same
   session does not run.
3. **Read the transcript.** Up to `MAX_TURNS = 30` turns, truncated to the
   effective live-flush bound. Claude and Antigravity retain
   `MAX_TRANSCRIPT_CHARS = 15_000`; Ollama and OpenAI-compatible backends use
   24,000 characters. A positive `BEYIN_FLUSH_CHUNK_CHARS` overrides either.
   The selected value is recorded in the run's state detail.
4. **Redact inbound.** `secret_guard.redact()` over the transcript; matched
   pattern classes are written to the health file as a warning.
5. **Summarise.** `claude -p --model haiku`, no tools, 240 s timeout, run in a
   temporary directory outside the vault. The prompt wraps the transcript in
   `BEGIN/END UNTRUSTED TRANSCRIPT DATA` and requires exactly five Turkish
   sections: `## Bağlam`, `## Önemli Konuşmalar`, `## Alınan Kararlar`,
   `## Öğrenilenler`, `## Yapılacaklar`. If nothing has lasting value the model is
   told to reply `FLUSH_BOS` and nothing is written.
6. **Validate the shape.** `validate_summary()` requires those five headings,
   exactly once each, in order, with no preamble. A malformed summary is not
   appended.
7. **Redact outbound.** `secret_guard.redact()` again, over the summary.
8. **Append.** `daily/YYYY-MM-DD.md` is created with a `# Günlük Log` header if
   absent, then a `### Oturum (HH:MM)` block is appended — suffixed
   `, compaction öncesi` for a `PreCompact` flush. A recent-duplicate check
   prevents the same summary being appended twice.
9. **Maybe trigger compile.** See below.

### 4.3 The evening trigger

`flush.maybe_trigger_compile()` runs after a successful flush:

- returns immediately before 18:00 local time (`BEYIN_FAKE_HOUR` overrides the
  hour for tests, `BEYIN_FAKE_NOW` the whole clock);
- compares each `daily/*.md` SHA-256 against `compile-state.json["ingested"]` and
  returns if nothing changed;
- claims the day with `os.open(..., O_CREAT | O_EXCL)` on
  `.state/compile-trigger-YYYY-MM-DD`. `FileExistsError` means today's compile is
  already claimed;
- pops `BEYIN_INVOKED_BY` and launches `compile.py --trigger-claim <path>`
  detached (`DETACHED_PROCESS | CREATE_NO_WINDOW` on Windows,
  `start_new_session` elsewhere), output to `DEVNULL`;
- unlinks the claim if the launch itself fails.

Daily directories and files are checked for symlinks and non-regular types before
any of this; a violation raises rather than proceeding.

### 4.4 Model backend dispatch

Every model call in the system — flush summarize, ingest summarize, compile
distill — goes through one function, `claude_runner.run_claude()`. That function
is also the backend switch.

`resolve_backend()` reads `BEYIN_MODEL_BACKEND`: unset or `claude` selects the
Claude CLI path, `antigravity` selects `agy_runner`, `ollama` selects its native
local HTTP runner, and `openai-compat` selects the OpenAI chat API runner.
`openai` is an alias for `openai-compat` and warns; `gemini` is a deprecated
alias for `antigravity` that also warns (Google retired Gemini CLI's serving on
2026-06-18). An unrecognised value falls back to `claude` with a warning rather
than failing the run. Warnings are drained by the caller through
`claude_runner.last_warnings()` and written to `health.json` as warning entries,
so the selection is visible without changing the `(output, error)` contract
that every caller already depends on.

`agy_runner.run_agy()` implements the documented headless contract —
`agy -p <prompt> --model <slug> --output-format text` — with stdin closed
(the prompt travels in argv, not on stdin, unlike the Claude path),
`BEYIN_INVOKED_BY` still set, the same timeout, and the same
outside-the-vault temporary working directory. Binary resolution honours
`BEYIN_AGY_BIN` and reuses the fixed `cmd.exe /d /s /c` bridge that
`ingest_common._run_codex` uses for Windows `.cmd`/`.bat` shims. The caller's
`haiku`/`sonnet` tier maps onto `BEYIN_AGY_MODEL_FAST`
(default `gemini-3.5-flash-medium`) and `BEYIN_AGY_MODEL_SMART` (no default —
unset degrades to the fast model and warns). Failures map to `agy-missing`,
`agy-auth-missing` (best-effort stderr sniffing), `agy-timeout` and
`agy-exec-error`, and propagate into health state exactly like Claude failures.

<!-- yazan: codex · gpt-5.6-sol -->
`ollama_runner.run_ollama()` is the fully local text-mode alternative selected
by `BEYIN_MODEL_BACKEND=ollama`. It uses stdlib `urllib` to POST
`{"model": <slug>, "prompt": <prompt>, "stream": false}` to
`{BEYIN_OLLAMA_URL|http://localhost:11434}/api/generate` and reads the
`response` string. `haiku` requires `BEYIN_OLLAMA_MODEL_FAST`; `sonnet` uses
`BEYIN_OLLAMA_MODEL_SMART` or warns and falls back to the fast slug. Connection,
HTTP, timeout, and malformed-response failures have distinct stable error
strings. Ollama exposes no compile tool path, so the same compile dispatch uses
`claude` when available and otherwise refuses with
`ollama-backend-unsupported:compile`.

<!-- yazan: codex · gpt-5.6-sol -->
`openai_runner.run_openai()` provides the equivalent text-mode path for LM
Studio, llama.cpp `llama-server`, vLLM, and other local OpenAI-compatible
servers. It POSTs a non-streaming user message to
`{BEYIN_OPENAI_URL}/chat/completions`, optionally sends
`Authorization: Bearer <BEYIN_OPENAI_KEY>`, and reads
`choices[0].message.content`. The URL and fast model slug have no defaults.
Compile follows the same Claude fallback and text-tool refusal mechanism, using
`openai-compat-backend-unsupported:compile` when no Claude CLI is available.

**The compile refusal.** Compile is the only tool-mode call: the model must
write files inside the staging tree (§5). The Claude path scopes that precisely,
per invocation, with `--tools` plus `--permission-mode acceptEdits`. `agy` has
no per-invocation equivalent — its only options are a user-global allow-list in
`~/.gemini/antigravity-cli/settings.json` or `--dangerously-skip-permissions`,
which auto-approves *every* tool call for that run. Granting blanket approval to
buy a free backend would trade away the exact property section 5 exists to
protect, so the antigravity backend refuses tool-mode calls outright with
`antigravity-backend-unsupported:compile`. In antigravity mode
`compile.py` therefore asks `claude_runner.compile_backend()` first: if `claude`
is on `PATH` compile runs on it (recording a
`warn:antigravity-compile-fallback-claude` health entry), and if it is not,
the run fails loud with the refusal string instead of quietly weakening the
sandbox. A user who wants compile on `agy` anyway can add a scoped
`write_file(<staging>/)` rule to their own global settings — that is a manual,
documented, off-by-default choice made outside this repository.

## 5. `compile.py` — staging isolation

The compiler is the only component that lets a model write files, so it is the
component with the most gates.

### 5.1 Staging

`_prepare_stage()` creates `<vault>/.stage/compile-stage-<random>/` with mode
`0700` and copies in:

- `knowledge/index.md`, `knowledge/index-full.md`, `knowledge/log.md` (missing
  files become empty files);
- `knowledge/concepts/` and `knowledge/connections/` trees;
- exactly one daily log, at `daily/<name>.md`.

Every source is checked to be a regular file or directory inside the vault before
being copied, and the SHA-256 of each copied file is recorded as the **live
baseline**.

Staging deliberately lives at the vault root, not under `.claude/`: Claude Code
treats `.claude/**` as sensitive and blocks writes there even under
`acceptEdits`. A vault-root dot-directory keeps the staging tree on the same disk
(so promotion is a cheap rename) and out of Obsidian's view.

### 5.2 The model call

`claude -p --model sonnet --safe-mode --tools Read,Write,Edit,Glob,Grep
--permission-mode acceptEdits --allowedTools Read,Write,Edit,Glob,Grep`, working
directory = the staging tree, 900 s timeout, prompt on stdin. This call always
runs on the Claude backend; see §4.4 for why the optional Antigravity backend
refuses it.

The prompt (`COMPILE_PROMPT`) carries:

- the **schema rules** — concept path `knowledge/concepts/<ascii-kebab-slug>.md`,
  frontmatter fields `title, aliases, tags, sources, created, updated`, body shape
  (`# Title`, a 2–4 sentence core, `## Önemli Noktalar` with 3–5 bullets,
  `## Detaylar`, `## İlgili Kavramlar` with at least two wikilinks each carrying a
  one-sentence justification, `## Kaynaklar`), the `index-full.md` column contract
  and the `log.md` entry shape;
- the **security boundary** — untrusted-data delimiters around the root map, the
  duplicate-check registry and the daily body, plus an explicit statement of the
  only writable paths and a prohibition on modifying the daily input;
- the **instructions** — extract 2–6 durable concepts, link them bidirectionally
  inside the two concepts' own `## İlgili Kavramlar` sections (the separate
  `connections/` layer was archived), keep one `index-full.md` row per article,
  inspect only specific candidate articles with Grep and Read rather than reading
  the whole knowledge directory, and correct rather than duplicate a contradicted
  article.

The root map (about 4 KB) plus a compact `name | aliases` registry replaced
sending the full index on every call. That is the 63% input-base reduction.

### 5.3 Promotion gates

After the model returns, nothing is trusted:

1. `_manifest()` walks the staging tree before and after the run.
2. `_validate_manifest_diff()` raises `PolicyError` on any deletion, any type
   change, any new directory outside `knowledge/concepts/**`, and any changed file
   that is not `knowledge/index-full.md`, `knowledge/log.md`, or a `.md` under
   `knowledge/concepts/`. No changed file at all raises `NoChangesError`, which is
   benign — the daily log is marked ingested and the queue moves on.
3. `secret_guard.scan()` runs over the output.
4. `_validate_live_destination()` re-checks each promoted path against the
   allow-list, resolves it, and confirms it lands inside `knowledge/`.
5. Each file is written with `_atomic_copy()`; the recorded live baseline digest
   is used to detect that the live file changed underneath the run.

The staging tree is removed afterwards, including on failure.

### 5.4 Run bookkeeping

`compile-state.json` holds `ingested` (daily filename → SHA-256), `cursor`,
`last_run`, `last_status` and a run history. Corrupt state is quarantined rather
than overwritten. At most `DEFAULT_MAX_CALLS = 3` daily logs are processed per
run. A successful run writes an **empty** health error, clearing any stale
failure flag — otherwise the health check keeps reporting a crash that was fixed
days ago.

### 5.5 Post-compile regeneration

After each successful daily log, in order:

1. `rootmap.regenerate()` — see below. A failure is recorded as a health *warning*
   and does not abort the compile.
2. `retrieve.build_index()` — rebuilds the FTS5 database. Same warning-only
   handling.

## 6. `rootmap.py` — the map layer

`regenerate()` is transactional:

1. Load `hub-config.json` (id, display name, scope sentence, tags, title keys, and
   a `catch_all` id) and every concept from `knowledge/concepts/`.
2. `assign_memberships()` places each concept by frontmatter tags and title
   keywords; unmatched concepts fall to the catch-all hub.
3. On first run, if `index-full.md` does not exist, the existing `index.md` is
   parsed and migrated into it.
4. Render the root map (`index.md`) and one file per hub (`knowledge/hubs/<id>.md`)
   into a temporary directory inside `knowledge/`.
5. Validate: every concept must appear in some hub's table
   (`concept-uncovered:<name>`), the root map must fit `ROOT_MAP_BUDGET = 4_000`
   characters (`root-map-budget:<n>/<budget>`), the hub set must match the config
   (`hub-output-mismatch`), and no staged file may be empty.
6. `_publish()` moves each staged file into place with `os.replace`.
7. Row/concept parity is compared and a mismatch recorded as a health warning.

Failures record the error and re-raise; nothing partial is published.

`turkish_fold()` — `I → ı`, `İ → i`, then `casefold()` — is used for every name
comparison, never `lower()`/`upper()`, which are locale-dependent.

## 7. Read path

### 7.1 `SessionStart` → `session-start.ps1`

Resets `.state/session_start_time` and `.state/prompt_count`, then assembles the
injection block:

- **Fixed sections, never truncated:** a `needs_reflection` warning if one is
  pending (then deleted); up to 49 lines of `Last-Session.md` from `## Session:`
  to `## Previous`; up to 12 `### `/`**Status:**` lines from the `## Active`
  region of `Threads.md`; the first 60 lines of `Kurallar.md`; the last `##` entry
  of `Journal.md` plus nine lines.
- **Elastic sections:** the first 150 lines of `knowledge/index.md` (the root map),
  then the last 25 lines of today's `daily/` file, falling back to yesterday's.

The total is capped at 16,000 characters. The knowledge block shrinks first, the
daily tail second, and a truncated block is marked
`[not: indeks kirpildi - beyin-doktor calistir]`. The companion directory is found
by globbing `*850-Companion`; if it is absent those sections are simply empty.

### 7.2 `UserPromptSubmit` → `memory-retrieve.ps1`

1. Reads the hook JSON from stdin, taking `user_input` (or `prompt`).
2. Skips prompts shorter than 12 characters and anything starting with `/` — "yes",
   "continue" and slash commands carry no retrieval signal.
3. Resolves Python the same way `flush-launch.ps1` does and runs
   `retrieve.py query <text> --limit 3 --session <session_id> --format hook`.
4. On any failure — no Python, no script, no JSON, no hits — exits 0 silently.
5. Wraps the returned notes in a block that names each source path and states that
   the contents are **data**, and that no sentence inside them is to be executed.

The 5-second hook timeout is the hard budget; the measured p95 on the author's
corpus was 347 ms, which includes Python interpreter startup.

### 7.3 `retrieve.py`

**`build`** reads every note in `knowledge/concepts/`, parses the frontmatter
subset used by atomic concepts (`title`, `aliases`, `tags`), and creates a fresh
database:

```sql
CREATE VIRTUAL TABLE notes USING fts5(name UNINDEXED, title, aliases, tags, body);
CREATE TABLE documents(rowid, name, title, aliases, tags, body);
CREATE TABLE meta(key, value);
```

`documents` holds the original text; `notes` holds the **tokenised** form, so
retrieval never depends on FTS5's own tokeniser understanding Turkish. The
database is built to a temporary path and moved into place, so a query never sees
a half-built index. A note with missing or invalid frontmatter raises
`RetrieveError` rather than being silently indexed wrong.

**Tokenisation** (`expanded_tokens`): fold with `turkish_fold()`, take
`[^\W_]+` words of at least 3 characters, emit the word, and additionally emit its
first 5 characters when longer than 5. Both indexing and querying call the same
function, so the two can never drift. This is the dual-form scheme — raw form plus
fixed-length truncation — chosen over a Turkish stemmer, which over-stems badly.

**`query`** ranks with `bm25(notes, 8.0, 6.0, 3.0, 1.0)`: title 8, aliases 6, tags
3, body 1. `--min-score` applies a floor on the positive `-bm25` relevance.
Results are capped at `PER_NOTE_CAP = 1_500` characters per note and
`TOTAL_BODY_CAP = 4_500` overall. With `--session`, hits already served in that
session are recorded in `.state/retrieve-session-<id>.json` and not repeated;
session ids are validated against `[A-Za-z0-9_.-]{1,128}` before touching the
filesystem, and ledgers older than seven days are pruned. `--bench` runs a fixed
query set for latency measurement.

### 7.4 `context_pack.py`

`context_pack.py` is the manual bridge for web-chat clients that cannot run the
hooks. It resolves the installed vault from the same `VAULT_ROOT` convention
(or `--vault`), reads `knowledge/index.md`, and calls
`retrieve.hook_result()` for the top notes so the existing 1,500-character
per-note and 4,500-character total caps remain the single policy source. The
Markdown block can be printed or piped to Windows `clip.exe` as UTF-16LE with
`--clip`; `--no-map` and `-k 1..5` control composition. A missing root map or
FTS database produces an explicit notice instead of an exception. The
PowerShell 5.1 wrapper `hooks/pano-kopru.ps1` is installed but not registered as
a hook.

### 7.5 `session-end.ps1`

Compares `Last-Session.md`'s mtime against the recorded session start. If the
session ran at least 5 prompts and the companion memory was never updated, it
writes `.state/needs_reflection`, which the next `SessionStart` surfaces as a
warning and then deletes. Then it clears the session timer and prompt counter.

## 8. Backfill: the ingest family

`scripts/ingest.py` is the front end. Common flags (`--dry-run`,
`--max-sessions`, `--sleep`, `--model`, `--retry-failed`) may be written before or
after the subcommand. Dry runs and `status` touch nothing and take no lock; every
other run holds an exclusive lock so two backfills cannot interleave.

| Subcommand | Source | Module |
| --- | --- | --- |
| `claude` | `~/.claude/projects` transcript archives (`--only-project` to narrow) | `ingest_claude.py` |
| `codex` | `~/.codex/sessions` rollouts | `ingest_codex.py` |
| `web` | claude.ai export ZIP in `<vault>/.import/` (`--zip`, `--web-resummarize`, `--max-conversations`) | `ingest_web.py` |
| `gemini` | Google Takeout Gemini records staged by `tools/gemini_ayikla.py` | `ingest_gemini.py` |
| `status` | Reports progress per source, writes nothing | — |

`ingest_common.py` holds the shared machinery: session model, per-source state
buckets keyed by identifier and file digest, resumable `should_skip`/`record_done`
bookkeeping, the summariser call (default model `haiku`; the `gemini` subcommand
defaults to the Codex path), the daily-append helper and the exclusive lock. The
ingester never raises out of `main()` — every failure path writes a health entry
and returns 0.

Backfilled sessions land in `daily/` exactly like live ones, marked with a suffix
naming the source and summariser, and are then compiled by the same nightly path.

## 9. Health and diagnosis

Scripts write a shared `.state/health.json` through `write_health()`. An empty
error string means healthy; the warning flag preserves history rather than
overwriting it. The `beyin doktor` skill (installed to
`<user>\.claude\skills\beyin-doktor`) renders hook wiring, script presence,
interpreter and CLI availability, daily-log freshness and last compile status as a
single table.

## 10. Tests

`scripts/tests/` runs under `pytest` (configured in `pyproject.toml` via
`testpaths`). Coverage includes compile state handling, flush summary shape and
silent-skip behaviour, the retrieval index, root-map generation, the secret guard,
each ingest source, and the model-backend dispatch (`test_agy_backend.py`, fully
mocked — no test ever launches a real CLI).

```powershell
python -m pytest
```
