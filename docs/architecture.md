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
│                                  notes.db, calls.jsonl, retrieve-session-*.json,
│                                  hookin-*.json
├── daily\                      <- machine-written daily logs
├── knowledge\                  <- machine-compiled knowledge base
│   ├── index.md                <- compact root map (rootmap.py)
│   ├── index-full.md           <- full article table (compile.py)
│   ├── hubs\<id>.md            <- topic hubs (rootmap.py)
│   ├── concepts\*.md           <- atomic concept articles (compile.py)
│   └── log.md                  <- compile run log (compile.py)
├── .stage\compile-stage-*\     <- transient compile staging, mode 0700
├── .stage\karantina\           <- held content: directive-shaped, and sema\ for
│                                  notes that missed the frontmatter schema
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
| `UserPromptSubmit` | `retrieve.py hook` (retired `memory-retrieve.ps1` wrapper) | 5 s | Gated BM25 retrieval, inject top 3 notes |
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

`compile.py` and `ingest.py` make the same check in `main()`. `retrieve.py hook`
(§7.2) checks it too, in Python rather than PowerShell — the live
`UserPromptSubmit` hook calls `retrieve.py hook` directly, so the guard has to
live in the entry point it actually reaches, not only in the retired
`memory-retrieve.ps1` wrapper. The one place the variable is deliberately
removed is `flush.maybe_trigger_compile()`, which pops it
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
3. **Read the transcript.** Select the **oldest uncommitted contiguous range**, up
   to `MAX_TURNS = 30` turns and the effective live-flush bound. Claude and Antigravity retain
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
6. **Validate the shape.** `validate_summary()` requires those five sections,
   exactly once each and in contract order. It is tolerant about everything the
   parser does not read: a preamble before the first section is dropped, `###`
   is normalised to `##`, and bold, trailing whitespace and closed-ATX hashes
   are stripped from the heading text. A missing section, a reordered one, or a
   contract heading that appears before `Bağlam` is still fatal, and a
   malformed summary is not appended. (The strict version rejected 6 of 20
   summaries on 2026-09-08 — the same sessions passed on retry — because the
   model decorated the headings it had otherwise produced correctly.) A
   rejected summary is written verbatim to `.state/red/<session>-<ts>.md`
   (newest 50 kept) and that path is named in the delivery ledger's
   `flush:rejected` line, so a rejection can be read rather than guessed at.
7. **Redact outbound.** `secret_guard.redact()` again, over the summary.
8. **Append and commit the range.** `daily/YYYY-MM-DD.md` is created with a
   `# Günlük Log` header if absent, then a `### Oturum (HH:MM)` block is appended — suffixed
   `, compaction öncesi` for a `PreCompact` flush. The old 60-second
   duplicate guard is gone; see the range state below for what replaced it.
9. **Maybe trigger compile.** See below.

**Delivery ledger and contiguous range commits (Astra A1-1C).** Python first
records `flush:started` with its PID. Every chunk then appends one bounded line
— successes included — appends one bounded line to
`.state/flush-teslimat.jsonl` (rotates to `.1` past 2 MB), carrying a reason
code: `flush:ok`, `flush:missing-transcript`, `flush:unreadable-transcript`,
`flush:no-turns`, `flush:no-new-turns`, `flush:rejected`, `flush:bos`, or
`flush:append-failed`; a third failure of the same chunk becomes `flush:parked`.
Ledger lines carry `pid`, `turns_committed`, `range`, fragment offsets when
applicable, and `red` — the path of the stored raw output — on a schema
rejection. Every reason except `flush:started`, `flush:ok`, and `flush:no-new-turns`
also raises a `health.json` warning, so a missing or unreadable transcript is
no longer silent. The hook still exits 0 in every case — this is visibility,
not failure.

The existing per-session state JSON now stores committed half-open ranges as
`kapsanan: [[a,b], ...]`; `last_turn_index` remains as the contiguous-prefix
compatibility cursor, and an old scalar cursor is read as `[0,cursor)`. A flush
advances only across the exact range whose daily block landed. Each block keeps
the provenance session marker and adds a deterministic `flush-range` comment
with `tur:a-b` or `parca:turn:start-end/total`. Before append, that marker is
checked across daily files, so a crash after append but before state write is
recovered without a duplicate block. A turn larger than the character cap is
split and its durable offset is stored in `parca_siniri` until the final
fragment commits the turn. A
session with nothing new past the cursor calls no model and records
`flush:no-new-turns`. `PreCompact`, `SessionEnd`, and the timed sweep all use
this same oldest-first path.

One process drains at most `BEYIN_FLUSH_MAX_CHUNKS` chunks (default 3) and stops
after `BEYIN_FLUSH_MAX_SECONDS` wall-clock seconds (default 180); an incomplete
sweep stamp remains eligible on the next hourly run. Three failures of the same
marker write `basarisiz_parca`/`parked`, do not advance the range, and surface in
health and the delivery ledger. `BEYIN_FLUSH_MAX_TURNS` overrides `MAX_TURNS`;
`BEYIN_FLUSH_MAX_CHARS` overrides the character cap and sits behind the older
`BEYIN_FLUSH_CHUNK_CHARS` for backward compatibility. Both degrade to the
shipped default on invalid input.

### 4.3 The evening trigger

`flush.maybe_trigger_compile()` runs after a successful flush:

- opens the clock gate at 18:00 local time **or**, earlier in the day, once a
  *recorded successful* compile is at least `BEYIN_COMPILE_MIN_INTERVAL_HOURS`
  (20 h) behind — a machine that is only awake during the day used to never
  compile at all (Master, 2026-09-07). With no successful run on record the
  evening rule alone applies, so a fresh install does not compile at 09:00.
  `BEYIN_COMPILE_EVENING_HOUR` moves the evening hour, `BEYIN_FAKE_HOUR`
  overrides the hour for tests and `BEYIN_FAKE_NOW` the whole clock;
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

### 4.4 The timed sweep — `flush.py --tara`

`SessionEnd` is a courtesy, not a guarantee: long-lived app sessions may never
deliver it, and Windows hooks can stop after a few hours. The hourly sweep makes
the write path independent of session end.

`flush.py --tara` walks `~/.claude/projects/**/*.jsonl`
(`BEYIN_CLAUDE_PROJECTS`, or `--projects-dir`, overrides the root), derives the
session id from each filename, and runs **the same per-session path as the
hook** — it builds the hook payload in memory (`session_id`,
`transcript_path`, `cwd` from the transcript's first record, reason `tara`) and
calls `_flush_once`. Not every `.jsonl` under that root is a session: subagent
transcripts (`<session-id>/subagents/agent-*.jsonl`) and the pipeline's own
`claude -p` transcripts (project directory name contains `stage-compile`) are
counted as `disarida` and never flushed. The entry is dated by the transcript's
last turn — the file stamp, then the sweep moment, are only fallbacks — so a
sweep hours later still writes the session into its own day and hour.
"Nothing changed → do nothing" is enforced by cheap gates before any model
is reachable:

1. **File stamp.** `.state/flush-tara.json` keeps `son_tarama_ts` and a
   `{mtime, size}` per transcript. An unchanged stamp is skipped without the
   file being opened. `--since-hours` (default 8, `0` lifts it) additionally
   ignores anything older than the window — but **only for a transcript that
   already has a stamp**: one the sweep has never seen is flushed however old
   it is, so a sweep that runs late cannot drop the sessions it exists to
   rescue. The window still keeps a *re-scan* of the archive cheap.
2. **Range state.** A transcript whose stamp moved but whose turns did not
   (tool results, metadata) has no uncovered range and
   records `flush:no-new-turns` — no model call.
3. **Quiet window.** A transcript whose last turn is younger than
   `BEYIN_TARA_SESSIZLIK_DK` (20 minutes) is deferred, unless its oldest
   uncommitted turn is at least `BEYIN_TARA_AZAMI_ERTELEME_SAAT` (4 hours) old.
   A bounded drain stamps `complete:false`, so an unchanged backlog remains
   eligible next hour.

A session already being flushed by a live hook holds its per-session lock; the
sweep takes that lock **non-blocking**, records `flush:locked` and moves on
rather than queueing. A transcript is stamped only on a settled outcome
(`flush:ok`, `flush:no-new-turns`, `flush:no-turns`, `flush:bos`); a rejected
summary, a failed append or a locked session leaves the stamp alone so the next
sweep retries it. One bad transcript is counted, never fatal.

The sweep closes by calling `maybe_trigger_compile()` once, running the
independent reconciliation below, and appending one
summary line to the delivery ledger:
`{ts, reason:"tara", taranan, degisen, ozetlenen, atlanan, disarida, hatali}`. The same
line is printed to stdout. `--dry-run` performs the walk and the cursor check
and writes nothing at all — no lock file, no state, no ledger, no model — which
is how the change was measured against the live archive before it shipped
(1,749 transcripts, 17 changed in 8 h, 16 to summarise).

Registration is `hooks/zamanli-flush-kur.ps1` (idempotent; `-Durum`, `-Kaldir`):
a `OdenaOS-Flush` scheduled task starting at the next full hour, repeating every
hour, running `flush-launch.ps1 -Tara` **only when the user is logged on** —
the sweep needs the user's own Ollama service, which does not exist in a service
session — start-when-available, 30-minute execution limit, no battery
restrictions.

**Authoritative reconciliation (Astra A1-3R).** `flush.py --mutabakat`, also run
at the end of every sweep, walks the same candidates and exclusions. For every
transcript with at least `BEYIN_MUTABAKAT_MIN_TURNS` user turns (default 5), it
writes `.state/mutabakat.json` with the last turn time, `kapsanan`, uncovered
turn count, and whether any daily file carries the session marker. Each
uncovered session produces one ID-deduplicated
`warn:kapsanmayan-oturum:<id>:<uncovered>/<total>` health line. It also compares
post-launch `hooks/.state/hook-girdi.jsonl` records older than ten minutes with
the PID-bearing delivery ledger; unmatched ingress is reported as
`warn:teslimat-eslesmedi:<n>`. `durum.py` displays the uncovered-session count
and age of the oldest unprocessed source.

### 4.5 Model backend dispatch

Every model call in the system — flush summarize, ingest summarize, compile
distill — goes through one function, `claude_runner.run_claude()`. That function
is also the backend switch, and the place every call is timed and accounted for
(§5.8). The exact CLI flag surface and HTTP endpoints each
backend was built against are in
[docs/compatibility.md](compatibility.md); versions beyond those are untested.

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
runs on the Claude backend; see §4.5 for why the optional Antigravity backend
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

One block in this prompt is **not** untrusted data: the binding-corrections
block described in §5.9, assembled by `_duzeltme_girdisi()` and placed
immediately before the daily so the correction is read before the material that
produced the error. It is absent — and the prompt byte-identical to the previous
version — whenever there is no pending correction.

The root map (about 4 KB) plus a compact `name | aliases` registry replaced
sending the full index on every call. That is the 63% input-base reduction.

#### The bounded duplicate-check registry

The root map is a fixed ~4 KB, but the registry was one row per concept for the
whole corpus, so compile input still grew linearly with the knowledge base.
`build_compact_registry()` now sends only the rows a run can plausibly collide
with:

1. **Hub-scoped.** The daily body is run through `rootmap.assign_memberships()`
   as a probe concept — the same hub-matching logic the root map uses, imported
   rather than reimplemented — and the concepts belonging to the matched hubs are
   selected. A concept in an unrelated hub is not sent.
2. **Recency margin.** The `BEYIN_REGISTRY_RECENT` most recently updated concepts
   (default 50) are added regardless of hub, because a duplicate is most likely
   against something written recently, and hub assignment is imperfect.
3. **Hard ceiling.** `BEYIN_REGISTRY_MAX_ROWS` (default 400) caps the result, so
   input is bounded even when one hub holds the entire corpus.

When rows are dropped the prompt says so on its first line — `registry
truncated: N of M rows shown, selected by topic and recency` — so the model knows
its dedupe view is partial rather than assuming a name it cannot see is free.
The same event is recorded in health as the warning
`warn:registry-truncated:<shown>/<total>`.

Measured on a synthetic 1000-concept fixture (five hubs, 200 concepts each, two
aliases per concept):

| Registry | Rows | Characters |
| --- | --- | --- |
| Before — one row per concept | 1000 | 67,800 |
| After — daily matching one hub | 237 | 15,806 |
| After — daily matching the largest hub | 241 | 17,216 |
| After — every concept in the matched hub (ceiling binds) | 400 | 27,194 |

That is a 76.7% reduction in the typical case, and the last row is the point:
past 400 eligible rows the registry stops growing, so compile input no longer
tracks corpus size. `RegistrySelection` (`text`, `total_rows`, `shown_rows`,
`truncated`) is what the compiler reads to decide whether to warn.

Both variables are read through `_bounded_env_int()`, which clamps to ≥ 0 and
falls back to the default on a non-integer value.

### 5.3 Promotion gates

After the model returns, nothing is trusted:

1. `_manifest()` walks the staging tree before and after the run.
2. `_validate_manifest_diff()` raises `PolicyError` on any deletion, any type
   change, any new directory outside `knowledge/concepts/**`, and any changed file
   that is not `knowledge/index-full.md`, `knowledge/log.md`, or a `.md` under
   `knowledge/concepts/`. No changed file at all raises `NoChangesError`, which is
   not a failure but is **not** a consumed daily either — see §5.4.
3. `DIRECTIVE_SHAPED` runs over every staged file that would be promoted. A hit
   means that file is quarantined and the **whole run is refused**: nothing from
   this daily is promoted. See
   [SECURITY.md](../SECURITY.md#quarantine--the-compilers-three-directive-shaped-gates)
   for all three gates and the manual release path.
4. `secret_guard.scan()` runs over the output. A hit raises `PolicyError` and
   fails the whole run — a secret is never a per-file problem.
5. `sema.validate_concept()` runs over every staged **concept note** (not
   `index-full.md`, not `log.md`). A note that misses the frontmatter schema goes
   to `.stage/karantina/sema/` with a sidecar naming the problems, and health
   records `schema-invalid:<file>`. A note written under a subdirectory
   (`knowledge/concepts/<sub>/x.md`) is refused the same way with the single
   problem `nested-path:<file>`: every reader — `retrieve.build_index()`,
   `rootmap.load_concepts()`, `concepts_manifest_hash()` — uses the
   non-recursive `concepts/*.md` glob, so promoting it would publish the note
   into invisibility. Either rejection refuses the whole run. See §5.7.
6. `_validate_live_destination()` re-checks each promoted path against the
   allow-list, resolves it, confirms it lands inside `knowledge/` and refuses a
   nested concept path a second time.
7. Publication is **all or nothing**. `_promote_changes()` is two-phase: it first
   copies every live target aside as `<dest>.bak-<run id>` and writes the new
   content next to it as `<dest>.tmp-<run id>`, then renames the temporaries into
   place in a second loop. A failure in either phase restores the destinations it
   had already renamed from their backups, deletes the temporaries and re-raises,
   so a half-published compile never reaches the vault. The backups outlive the
   rename loop: `_finalize_promotion()` drops them only once the run is
   committed, which is what lets §5.5 undo a promotion after the fact. The
   recorded live baseline digest still detects a live file that changed
   underneath the run.

The staging tree is removed afterwards, including on failure —
`.stage/karantina/` is not part of it and survives.

Rejection is per daily, not per file. A run that produces one held note and two
clean siblings promotes nothing: the clean siblings are a partial reading of a
source the compiler is about to read again, and the daily that produced the held
note is exactly what a retry needs. Promoting the clean half **and** consuming
the daily — the behaviour before this layer — destroyed the only source the held
note could have been rebuilt from.

### 5.4 Run bookkeeping

`compile-state.json` holds `ingested` (daily filename → SHA-256), `cursor`,
`last_run`, `last_status`, a run history, `quarantined` (content SHA-256 →
`{source_file, quarantined_at, quarantine_file}`), and the rejection ledger
`rejected` / `parked` (both daily filename → `{digest, reasons, ts, attempts}`,
`parked` additionally carrying `reason`). Older state files predate the last two
keys; `load_state()` fills them in from `_default_state()` and treats a
wrong-typed value as absent, so no state file needs migrating. Corrupt state is
quarantined rather than overwritten. At most `DEFAULT_MAX_CALLS = 3` daily logs
are processed per run. A successful run writes an **empty** health error,
clearing any stale failure flag — otherwise the health check keeps reporting a
crash that was fixed days ago.

**A daily is marked `ingested` only when the run promoted every note it
produced.** Any other ending — a quarantined output, a schema or `nested-path`
rejection, or an empty answer — records the daily in `rejected` with its reason
slugs and an attempt count, and re-presents it on the next compile. The run
itself is still `ok` (`ok:no-changes`, `ok:output-quarantined`,
`ok:schema-invalid`); it is the daily, not the run, that is unfinished. At
`MAX_REJECT_ATTEMPTS = 3` attempts on the same digest — `MAX_NO_CHANGE_ATTEMPTS
= 2` for an empty answer, which is even less likely to change on a third look —
the entry moves to `parked` with run status and health **warning**
`parked:<reason>`. Parked is not ingested: the daily is simply no longer worth a
model call. Editing it produces a new digest and a fresh chance.

`changed_daily_logs()` skips a file whose digest is in `quarantined` or matches a
`parked` entry, so a held daily is not retried every night. Keying on content
rather than filename is what makes an edited file eligible again with no separate
release step.

### 5.5 Post-compile regeneration

These run **before** the daily is marked ingested, on the promoted files, in
order:

1. `rootmap.regenerate()` — see below.
2. `retrieve.build_index()` — rebuilds the FTS5 database, unless
   `concepts_manifest_hash()` shows the concept set is unchanged, in which case
   the rebuild is skipped loudly (`skip:index-rebuild:concepts-unchanged`).

A failure in either is **not** a warning about a finished run: the run did not
finish. `_rollback_promotion()` puts every promoted file back from the backups
§5.3 kept, `_record_failure()` records `fail:rootmap-regen-failed` or
`fail:retrieve-rebuild-failed`, and the daily stays queued. A promotion the index
never saw is invisible to every reader, so consuming its source first is how a
run could silently lose knowledge. `rootmap.regenerate()` publishes its own
output the same two-phase way (`_publish()`): the root map and the hubs it
promises land together or not at all.

`context_bridge.refresh()` runs after the state write and stays warning-only —
it is a mirror of the map, not the map.

### 5.6 The machine-identified compile lock

`.state/compile.lock` is held with an OS-level exclusive lock
(`_lock_exclusive`), which settles concurrency **on one machine**. It says
nothing about a vault synced across machines by Drive, Dropbox or git, where two
installs can reach their evening trigger independently and both compile.

The lock file therefore also carries JSON identifying its owner:

```json
{"machine": "<host>-<16 hex>", "pid": 4812,
 "started_at": "2026-08-28T02:10:00+03:00", "hostname": "<host>"}
```

`_claim_machine_lock()` reads it before taking ownership:

- **Same machine, or no previous owner** → claim it and overwrite the metadata.
  Behaviour is unchanged from before this layer existed.
- **Another machine, still live** → refuse. `write_health_skip()` records
  `skip:compile-locked-by:<machine>` and the run returns 0 without compiling.
- **Another machine, older than `BEYIN_COMPILE_LOCK_TTL_MIN`** (default 120
  minutes) → break it, but loudly: the health warning
  `warn:stale-compile-lock-broken:<machine>` names the previous owner, so a
  machine that dies mid-compile is visible rather than silently overridden.

The machine id comes from `_machine_identity()`: the hostname, sanitised to
`[A-Za-z0-9_.-]`, plus a random 16-hex suffix generated once and stored in
`.state/machine-id` (mode `0600`, written with `O_EXCL` so a race cannot produce
two ids). It deliberately carries no user identity beyond the hostname. The
suffix is what makes two machines that happen to share a hostname distinguishable.

This is cooperative rather than a distributed lock — it can only work if the sync
tool has actually propagated the lock file — and it is documented as partial in
[SECURITY.md](../SECURITY.md).

### 5.7 `sema.py` — the frontmatter schema gate

The compiler's prompt has always described the concept-note schema, but until
this gate nothing enforced it. `rootmap.load_concepts` reads frontmatter with
`.get()` and falls back to defaults, so a note with a broken or missing block
entered the index quietly, its title degraded to the filename and its tags
empty. The result looked fine and ranked badly.

`sema.validate_concept(text, path) -> list[str]` returns a list of problems;
empty means valid. It checks that the frontmatter block is present and
parsable, that there are no duplicate keys, that `title` is a non-empty string,
that `created` and `updated` are real `YYYY-MM-DD` dates, that `tags`, `aliases`
and `sources` are lists, and that the body after the block is non-empty.
Messages carry the filename the way the rest of the pipeline reports
(`key-missing:<file>:created`, `date-invalid:<file>:updated`, `body-empty:<file>`),
so a problem list stays readable wherever it is copied.

Three **optional** keys were added for the correction layer (§5.9). Absent, they
are not a problem; present, they are checked:

| Key | Value | Written by |
| --- | --- | --- |
| `superseded_by` | an ascii-kebab concept slug, or the literal `duzeltme` | `duzelt.py`, on a `--gecersiz` correction |
| `duzeltildi` | `YYYY-MM-DD` or a full ISO 8601 stamp | `duzelt.py`, when a correction is verified as applied |
| `guven` | `yuksek` \| `orta` \| `dusuk` \| `belirsiz` | `compile.apply_guven()`, and only the value `dusuk` |

A field nothing writes is worse than an absent field — it advertises a guarantee
the system does not keep — so each of the three has exactly one writer.
`sema.guven_for_blocks()` is that writer's rule, as a pure function: a note gets
`guven: dusuk` only when **every** daily block behind it carries the
`kaynak: yerel-8b` marker of the offline fallback summariser, and only when its
`sources` names that daily and nothing else. Until something writes that marker
the path is inert — `guven_for_blocks()` returns `None` and `apply_guven()`
returns immediately.

It lives in its own module rather than in `beyin_ortak.py` for two reasons: it
needs rootmap's frontmatter dialect (`_unquote`, `_inline_list`), and
`beyin_ortak` cannot import `rootmap` without a cycle — putting it there would
have forced a second parser into the repository. `secret_guard.py` is the
existing precedent for a gate rule in its own file.

Its line grammar is deliberately **stricter than rootmap's**. rootmap skips a
frontmatter line it cannot read; a gate that skips is not a gate. The clearest
case is an unterminated inline list — `tags: [a, b` — which rootmap reads as an
empty list and this module refuses as unparsable.

Three boundaries define what this gate is:

- **It stops new damage only.** `retrieve.build_index` and `rootmap` keep their
  tolerant behaviour untouched, so an imperfect corpus keeps indexing and keeps
  being retrieved. Nothing is applied retroactively.
- **It never repairs.** A missing `created` date cannot be recovered, only
  invented, and an invented date is a fabricated fact in permanent memory.
- **The doctor surveys, it does not gate.** `retrieve.py verify` carries
  `schema_checked`, `schema_invalid_count` and up to five `schema_invalid`
  entries with their problems. Those fields never affect `ok`, so a vault full
  of pre-schema notes still verifies green and still exits 0. The survey is
  computed before the index checks, so a missing index still reports it.

### 5.8 Per-call accounting — `.state/calls.jsonl`

Nothing recorded what a model call cost or how long it took, which left the
backend comparison in §4.5 — and the "is a local model worth it" question in
[docs/local-models.md](local-models.md) — with no data behind it.

`claude_runner.run_claude()` now appends one line per call, because it is the
single choke point every model call already passes through and therefore the one
place no caller can opt out of. `_dispatch()` holds the backend switch; the
wrapper around it times the call and hands the numbers to
`beyin_ortak.record_call()`:

```json
{"ts": "2026-08-28T02:10:00+03:00", "backend": "ollama", "component": "flush",
 "model_tier": "haiku", "model_slug": "qwen3:8b", "input_chars": 12000,
 "output_chars": 1800, "input_tokens_est": 3000, "output_tokens_est": 450,
 "duration_ms": 5200, "outcome": "ok", "input_tokens": null, "output_tokens": null,
 "cache_read_tokens": null, "cache_write_tokens": null, "model_actual": "",
 "usage_source": "estimate"}
```

`outcome` is `ok` or the runner's own error string (`claude-timeout`,
`ollama-model-unset`, …). `model_slug` is what the backend actually resolved —
the Claude CLI is given an explicit model id per tier (`CLAUDE_MODEL_IDS`;
`BEYIN_CLAUDE_MODEL_<TIER>` overrides it), because the CLI's own tier aliases
have drifted (`--model haiku` has landed on Sonnet); the local backends map
the tier through their own `resolve_model()`. An unmapped tier records an
empty slug rather than a guess.

**Real usage vs. the chars ÷ 4 estimate.** `input_tokens_est`/
`output_tokens_est` are always the character-count estimate and never claim
to be anything else. `input_tokens`, `output_tokens`, `cache_read_tokens`,
`cache_write_tokens`, and `model_actual` are the provider-reported figures
when the caller has them — currently only the `claude` backend, which runs
`claude -p --output-format json` (instead of the historical `text`) and reads
its `usage`/`modelUsage` blocks, already aggregated across every turn of the
session by the CLI itself. `usage_source` says which: `"session-log"` when
these came from the provider, or the default `"estimate"` when they did not
(local backends, a CLI error, or a reply that failed to parse as the expected
JSON shape) — in which case the four real-usage fields are `null`.
`durum`'s summary shows both: the chars ÷ 4 estimate always, plus a
real-usage table when at least one call in the window has
`usage_source == "session-log"`.

**It is a ledger, not a log.** `record_call()` is handed character *counts*
(and, for the real-usage fields, provider-reported token *counts*), never the
prompt and never the response, so there is no path by which content can reach
the file. That is the signature doing the work rather than a rule someone has
to remember, and a test asserts the field set never grows.

The file is append-only and capped at 5 MB. Past the cap the newest lines that
fit in half of it are kept and rewritten atomically — halving rather than
trimming per line keeps rotation amortised, and the rewrite cuts at a newline so
every kept line still parses. Accounting failures are swallowed the way
`write_health` swallows its own: reporting must never break the call it reports.

The `component` label follows the work, not the wrapper. The ingest family
borrows flush's runner for the default model, so `flush._run_claude()` takes a
keyword-only `component` (default `"flush"`) that `ingest_common` overrides —
otherwise every default-model ingest call would file itself under flush.

**Not covered:** `ingest_common._run_codex()` invokes the Codex CLI directly
rather than through `claude_runner`, so Codex ingest calls do not appear in the
ledger. Backend comparisons that include Codex have to account for that gap.

### 5.9 `duzelt.py` — the correction store the compiler is accountable for

Before this layer, a human who noticed a false sentence in a concept note could
only fix the daily and hope. Nothing named the wrong claim, nothing checked
whether the next rewrite removed it, and a recompiled file was treated as
evidence that it had. The store closes that: **a named correction is an input
the compiler must consume, and application is verified, not assumed.**

**The file contract.** `<vault>/🔮 850-Companion/Duzeltmeler.md`, created on
demand by `duzelt.py ekle`. It is deliberately a plain, line-oriented Markdown
file with a fixed grammar, because a second reader — retrieval, at query time —
parses the same file without the compiler:

```markdown
<!-- duzeltme kavram=<slug> durum=bekliyor ts=<ISO8601> kaynak=<file-or-session> -->
iddia: <the wrong claim, ≤300 characters, one line>
dogru: <the correct statement, ≤300 characters, one line>
not: <optional>
```

One blank line ends a block. Both fields are collapsed to a single line and
capped at 300 characters when written **and** when read, and any `-->` inside
one becomes `->`, so a correction cannot close its own comment. An applied block
is rewritten in place: `durum=uygulandi`, the original `ts` preserved, and
`uygulandi_ts=<ISO8601>` appended to the head. `--gecersiz` adds `gecersiz=evet`
and marks the note obsolete as a whole.

**CLI.**

```bash
python scripts/duzelt.py ekle --kavram <slug> --iddia "..." --dogru "..." \
    --kaynak "Threads.md" [--not "..."] [--gecersiz] [--yeni]
python scripts/duzelt.py liste
python scripts/duzelt.py dogrula
```

`ekle` validates the slug against `knowledge/concepts/` (`--yeni` is the
explicit opt-out for a target that does not exist yet), appends the block with
`durum=bekliyor`, and records `warn:duzeltme-bekliyor:<slug>` in health.

**What the compiler does with it.** `_duzeltme_girdisi()` renders the pending
entries into the "BAĞLAYICI DÜZELTMELER" block (§5.2) — screened first with the
same `DIRECTIVE_SHAPED` detector the untrusted blocks get, per field, so an
entry shaped like an instruction never reaches the prompt. Prompt instruction 11
overrides instruction 6 for these entries: the wrong sentence must not survive
even as a `⚠ çelişki` line.

After the promotion is finalised — the first moment a correction can be checked
against what a reader would actually get — `duzeltme_kapanisi()` runs, in this
order:

1. `duzelt.dogrula()` re-opens any `uygulandi` entry whose claim is back in the
   note, warning `warn:duzeltme-yeniden-acildi:<slug>`. "It was fixed once" is
   not a standing guarantee.
2. `duzelt.uygula_kontrol()` checks every pending entry against the live note
   and closes only those that pass **both** halves: the `iddia` is gone, and the
   `dogru` has landed. It then stamps `duzeltildi` (and `superseded_by` for a
   `--gecersiz` entry) into the note's frontmatter. Anything still open keeps
   `warn:duzeltme-uygulanmadi:<slug>` and appears in `durum.py`'s
   `bekleyen duzeltme` row with the age of the oldest entry.

The comparison is `duzelt.iddia_kalmis_mi()` / `duzelt.dogru_gecmis_mi()`:
Turkish-aware case folding and whitespace collapse, then a normalised substring
match, then a small set of **key tokens** — dates, amounts, case codes — taken
from the text. A claim counts as still present when all of its key tokens appear
together **in one sentence**, which catches a reworded claim without flagging a
note that legitimately kept the fee and dropped the date. A correct statement
counts as landed when all of its key tokens are present and at least 60% of its
content words appear. This is containment, not truth: it cannot establish
booking, payment or negation, and it is not called verification of a fact.

Reading the ledger can never fail a compile. A missing or unreadable file simply
means nothing binds this run, and the prompt is byte-identical to what it would
have been.

### 5.10 Validated provenance — what a session anchor is allowed to claim

`flush.py` writes a `<!-- session:<id> ts:<stamp> source:<kind> -->` anchor into
each daily session block, and the compiler carries it into the notes that block
produced. An anchor is a **claim of provenance**, so it is evidence — not
decoration applied to every file a run happened to touch. It used to be the
latter: `carry_source_anchors()` appended every anchor in the daily to every
concept the run changed, and `restore_source_anchors()` put back, unread, any
anchor the model had removed. The audited result was 84 subagent anchors
(`session:agent-*`) living in six live concepts whose dailies no longer
contained them.

`carry_source_anchors()` now attaches an anchor only when three gates hold:

1. the session id appears in the daily being compiled;
2. it is not a ghost id — an `agent-*`/`subagent-*` transcript is never
   provenance, and stage-compile ids can be passed in `excluded_ids`;
3. the model's own output cites it: either it names the session id (an inline
   anchor it kept, or the id written into `## Kaynaklar`), or, failing that, its
   `## Kaynaklar` cites the daily itself, which authorises that daily's
   non-ghost anchors. A note that names specific sessions gets only those.

A note that cites nothing gets no anchor. Provenance *coverage* therefore falls,
visibly — from a false "every changed note is sourced" to a true, smaller
number. That is the intended trade.

`restore_source_anchors()` no longer restores wholesale. Sentence-level
attribution does not exist yet, so it takes the conservative rule: an anchor the
model dropped comes back only if its session id is still named somewhere in the
rewritten note; the rest are dropped and counted as
`info:capa-dusuruldu:<slug>:<n>` in health's skip list.

**Migration.** The historical anchors are retired, not deleted:

```bash
python scripts/compile.py --capa-temizle [--vault-root <path>] [--id <session>] [--dry-run]
```

Every `session:agent-*` anchor (plus any id named with `--id`) is removed from
the note's active block and recorded in a single
`<!-- gecmis-capalar: session:<id> ts:<stamp> source:<kind>; ... -->` line. That
shape does not match `retrieve.SESSION_ANCHOR`, so the history is readable and
inactive: it can no longer be returned as provenance, and it cannot come back
through a rewrite, a restore or an index rebuild. The migration is idempotent
and prints one line per file. Run it against a copy first — it rewrites concept
notes in place.

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

- **Protected sections, never truncated:** a `needs_reflection` warning if one is
  pending (then deleted); up to 49 lines of `Last-Session.md` from `## Session:`
  to `## Previous`; up to 12 `### `/`**Status:**` lines from the `## Active`
  region of `Threads.md`; and the first 60 lines of `Kurallar.md`.
- **Elastic sections:** the last `##` entry of `Journal.md` plus nine lines (then
  any future emitted Threads body), followed by the first 150 lines of
  `knowledge/index.md` (the root map) and the last 25 lines of today's `daily/`
  file, falling back to yesterday's.

The total, including its measurement line, is capped at 16,000 characters.
Journal and then a Threads body yield before the root map or daily tail. The root
map retains at least `BEYIN_ACILIS_INDEKS_TABAN` characters (default `800`) and
the daily tail retains at least `BEYIN_ACILIS_DAILY_TABAN` characters (default
`800`) whenever that source is at least that long; an invalid value uses the
default. A truncated section carries a `[not: ... kirpildi ...]` notice and the
injection ledger records `kirpik` plus `kirpildi` section names.

The reflection warning remains first. `[ZAMAN]` and the optional quota line are
placed after the daily tail so the memory prefix is stable between otherwise
identical starts. The ledger additionally records payload `session_id`, `cwd`,
and `cift`. `BEYIN_INVOKED_BY` remains the deterministic helper-session guard.
Desktop helper starts have no documented payload discriminator, so a cheap
fallback treats a same-second record with the same non-empty `cwd` as duplicate:
it injects only `[ZAMAN]` and a duplicate notice, and logs `cift: true`.
The companion directory is found by globbing `*850-Companion`; if it is absent
those sections are simply empty.

### 7.2 `UserPromptSubmit` → `retrieve.py hook`

<!-- yazan: codex · gpt-5.6-sol -->

The live hook now calls `retrieve.py hook` directly (D1), reading the hook JSON
from stdin itself rather than going through `retrieve.py query` from a
PowerShell wrapper. `run_hook_stdin()`:

1. Reads `user_input` (or `prompt`) and `session_id` from the hook JSON.
2. Exits with nothing (`skip:internal`) when `BEYIN_INVOKED_BY` is set — the
   recursion guard used to live only in the retired PS wrapper, so every
   `claude -p` the compiler/flush/benchmark spawned got personal concept notes
   injected.
3. Skips prompts shorter than `HOOK_MIN_PROMPT_LEN` (12) characters
   (`skip:short`) and anything starting with `/` (`skip:slash`).
4. Skips machine-shaped input and non-memory intent (`skip:intent`,
   `prompt_hafiza_ister()`): JSON/hook envelopes, the four recognised XML-style
   payload tags, fewer than three content words, fenced code, or a first word
   naming a tool/edit.
5. Compares Companion mtimes with the index metadata and runs the hand-only
   `yenile` transaction before searching when any changed.
6. Runs the relevance gate (below) and returns `None` (silent) whenever the
   gate produces no notes, or when the index itself is missing or corrupt.
7. Wraps the returned notes in a block that names each source path and states
   that the contents are **data**, and that no sentence inside them is to be
   executed.

The 5-second hook timeout is the hard budget; the measured p95 on the author's
corpus was 347 ms, which includes Python interpreter startup. `retrieve.py
query <text> --limit 3 --session <session_id> --format hook` (the raw ranking,
without the gate) remains available for callers — `context_pack.py` and the
MCP `memory_search` tool — that ask an explicit question and want every hit
regardless of overlap.

**The relevance gate (Astra A3/A4).** Before this gate the hook injected on
almost every prompt: query tokens were OR-joined, `--min-score` defaulted to
0, and the only skips were the length and slash checks above — measured
against the live 527-note index, all 30 probe prompts injected. A candidate
now survives only when enough distinct content words of the prompt
(`gate_tokens()`, folded, stopword-filtered, at least `GATE_MIN_TOKEN_LEN` (4)
characters) occur in the candidate note's own title/aliases/tags — 2 overlaps
for at most 6 content words, 3 above that; the body is deliberately excluded —
or its score clears
a strict escape-hatch threshold. The BM25 score alone cannot do this
filtering: on the measured corpus, memory-worthy top-1 hits scored
11.4–37.7 and junk hits scored 6.0–20.9, ranges that overlap almost
completely. The environment variables below tune retrieval through
`hook_result()`:

| Variable | Default | Effect |
|---|---|---|
| `BEYIN_RETRIEVE_MIN_SCORE` | `0.0` (off) | Floor on positive `-bm25()` relevance before the gate runs |
| `BEYIN_RETRIEVE_STRICT_SCORE` | `25.0` | A hit at or above this score is admitted even with no token overlap |
| `BEYIN_EL_KATMANI_ORAN` | `1.35` | Hand-first authority threshold relative to the best concept hit |
| `BEYIN_EL_KATMANI_DOSYALARI` | `Last-Session.md,Threads.md` | Companion files allowed to take hand-first authority |
| `BEYIN_EL_KATMANI_ONCELIK` | `2` | Cap on hand passages placed ahead of the concepts; `0` means no cap |

Path-like chunks, `.py`/`.md`/`.ps1` names, drive paths, hexadecimal ids of at
least seven characters, and generic Turkish caller words are removed before
overlap. This prevents hook envelopes and incidental path text from becoming
retrieval evidence.

A gated-off candidate set returns `skip:score` when nothing scored at all and
`skip:token-overlap` when hits existed but none passed the gate; a successful
injection is logged `inject`. `context_pack.py` and `memory_search` opt out of
the gate entirely (`require_overlap=False`) and keep the raw ranking.

**Query-aware dedup.** The per-session ledger used to key solely on the note's
name, so a note suppressed for one question could never answer a materially
different later one. Suppression now keys on `<query_signature>:<note>` —
`query_signature()` hashes the prompt's folded token set — so the same note
can be re-shown for a genuinely different question in the same session.

### 7.3 `retrieve.py`

**`build`** reads every note in `knowledge/concepts/` plus the hand layer
(`Last-Session.md`, `Threads.md`, `Journal.md` in `*850-Companion`). Concept
frontmatter supplies `title`, `aliases`, `tags`, and `superseded_by`. Hand files
split at `##`/`###`; oversized sections split at Markdown boundaries into
≤1,200-character passages. Wikilinks and bold lead words become tags. It then
creates a fresh schema-version-3 database:

```sql
CREATE VIRTUAL TABLE notes USING fts5(name UNINDEXED, title, aliases, tags, body);
CREATE TABLE documents(rowid, name, title, aliases, tags, body, source_date,
                       source, source_file, heading, superseded_by);
CREATE TABLE meta(key, value);
```

`documents` holds the original text; `notes` holds the **tokenised** form, so
retrieval never depends on FTS5's own tokeniser understanding Turkish. The
database is built to a temporary path and moved into place, so a query never sees
a half-built index. `meta` records the three hand-file mtimes. **`yenile`**
(`refresh` alias) replaces only hand rows in a transaction; `hook` invokes it
automatically on mtime drift. A note with missing or invalid frontmatter raises
`RetrieveError` rather than being silently indexed wrong.

**Scoped authority.** Search computes ordinary BM25 for both layers. A hand hit
moves before concepts only when it clears three measured tests: it *outscores*
the best concept by `BEYIN_EL_KATMANI_ORAN` (default `1.35` — a margin, not a
fraction), it comes from a file in `BEYIN_EL_KATMANI_DOSYALARI` (default
`Last-Session.md,Threads.md`, so narrative `Journal.md` prose is searchable but
never hand-first), and it fits under `BEYIN_EL_KATMANI_ONCELIK` (default 2).
Every other hand hit ranks after the concepts. See
[`retrieval.md`](retrieval.md) for the I3 measurement table behind those
defaults. Emitted hand passages carry
`[el katmanı · <file> › <heading> · <date>]`.

**Correction exclusion.** `Duzeltmeler.md` and its grammar belong to `duzelt.py`
(§5.9); retrieval parses it with `duzelt.ayristir()` rather than a second regex
of its own, and `duzelt` is imported lazily so an install with no ledger never
loads it. A `durum=bekliyor` target is never emitted. A `durum=uygulandi` target
is emitted again only when `duzelt.iddia_kalmis_mi()` says the wrong sentence is
actually gone from the body about to be injected — the same accountability test
that lets lane B close the block, so a stale index or a returned claim keeps the
note hidden. A concept with `superseded_by:` is never emitted either. The
block's `dogru:` line replaces its target at ≤300 characters under
`[düzeltme · <slug>]`, and every exclusion is ledgered as `exclude:correction`.
Retired ghost-anchor history (`<!-- gecmis-capalar: ... -->`, §5.10) is stripped
by `strip_session_anchors()` at build and at query time, so it never becomes a
searchable token or reaches a session as context.

**Tokenisation** (`expanded_tokens`): fold with `turkish_fold()`, take
`[^\W_]+` words of at least 3 characters, emit the word, and additionally emit its
first 5 characters when longer than 5. Both indexing and querying call the same
function, so the two can never drift. This is the dual-form scheme — raw form plus
fixed-length truncation — chosen over a Turkish stemmer, which over-stems badly.

**`query`** ranks with `bm25(notes, 0.0, 8.0, 6.0, 3.0, 1.0)`: bm25() weights
are positional over every column including the UNINDEXED `name`, so the
leading `0.0` is required to keep title=8, aliases=6, tags=3, body=1 landing
on the right columns — a four-weight call silently shifts them all one column
left. `--min-score` (`query` subcommand) applies a floor on the positive
`-bm25` relevance; the `hook` subcommand reads the same floor from
`BEYIN_RETRIEVE_MIN_SCORE` and layers the relevance gate on top (§7.2).
Results are capped at `PER_NOTE_CAP = 1_500` characters per passage and
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
and returns 0. The `min_turns` gate in `summarize_session()` compares against
`len(session.turns)`, the session's real turn count, not the count
`format_turns()` returns — that count is capped to what survived the character
cap (§4.2), so a short session that happened to hit the cap used to be
misclassified `bos`.

Backfilled sessions land in `daily/` exactly like live ones, marked with a suffix
naming the source and summariser, and are then compiled by the same nightly path.

## 9. Health and diagnosis

Scripts write a shared `.state/health.json` through `write_health()`. An empty
error string means healthy; the warning flag preserves history rather than
overwriting it. The `beyin doktor` skill (installed to
`<user>\.claude\skills\beyin-doktor`) renders hook wiring, script presence,
interpreter and CLI availability, daily-log freshness, last compile status and
the quarantine count as a single table.

### 9.1 `durum.py` — the health summary command

```powershell
python scripts/durum.py [--json] [--state-dir <path>] [--temizle-uyarilar]
```

It reads `health.json`, `ingest-health.json`, `compile-state.json` and
`last-flush.json` from the state directory and prints one table: component, last
status, last run, last error or skip, quarantine count. **The exit code is always
0** — this is a reporting surface, not a gate, and a missing or corrupt state
file produces `unknown` rows rather than a failure. Like every other entry point
it returns immediately when `BEYIN_INVOKED_BY` is set.

`health.json["warnings"]` keeps up to 20 historical entries (`write_health()`,
§9 above) and a healthy run never clears them, so a live alarm and a
three-week-old one look identical unless something ages them. `durum.py` does
that ageing itself, without touching the writer: each warning's age is
computed from its own `ts` when the entry is a `{"message"/"warning"/"text":
..., "ts": ...}` object, else from the health file's top-level `ts`; an entry
older than 24 h is flagged `eski` (Turkish "stale") in both the table (an
`eski` column) and `--json` (`"eski": true`). `now` is injectable everywhere
this math happens (`summarize_warnings`, `temizle_uyarilar`, and the `now=`
already on `build_summary`), so tests use fixed clocks instead of the wall
clock.

`--temizle-uyarilar` rewrites `health.json`, dropping only the warnings aged
`eski` and keeping every other key byte-shape-compatible with what
`write_health()` writes (same `json.dumps(..., indent=2)` via
`beyin_ortak._atomic_write_json`, same temp-file + `os.replace` swap). It is a
no-op — file untouched, mtime unchanged — when nothing is old or the file is
absent; it prints a one-line result and exits 0 without printing the rest of
the report.

**`bekleyen kaynak` — the pending-compile queue (Astra A14).** A `bekleyen
kaynak: <count> daily uncompiled (oldest <name>)` line prints below the
warnings table: how many `daily/*.md` files have a SHA-256 that does not match
`compile-state.json["ingested"]`, and the oldest one by filename. Quarantined
and parked entries are excluded from the count — this is a read-only report
surface, so a file already flagged elsewhere is not double-counted as
pending. `--json`'s `bekleyen` object carries `count`, `oldest` and the full
`files` list. Zero pending prints `bekleyen kaynak: none pending`.

**`info:registry-selection` demotion.** `warn:registry-truncated:<n>/<total>`
is telemetry compile.py writes on **every** successful bounded duplicate-check
selection (README's "Compiler input is now bounded, not free" limitation),
not a sign that anything is wrong. `durum.py` now renders it as
`info:registry-selection:<n>/<total>` in the table and excludes it from the
warning count (`warnings: N (+M info)` when `M` such entries exist) — the
translation is display-layer only, so `health.json`'s own raw
`warn:registry-truncated:` string is unchanged on disk.

Below that table it summarises the last 7 days of `.state/calls.jsonl` (§5.8):
calls per backend with median and p95 duration, and estimated tokens per
component.

```text
model calls (last 7 days): 9 (8 ok, 1 failed)

backend | calls | median ms | p95 ms
--------+-------+-----------+-------
claude  | 6     | 610       | 2400
ollama  | 3     | 5200      | 15300

component | calls | in tokens (est) | out tokens (est)
----------+-------+-----------------+-----------------
flush     | 5     | 15475           | 2250

real usage (provider-reported, 6 of 9 calls):

component | real calls | in tokens | out tokens | cache read | cache write
----------+------------+-----------+------------+------------+------------
flush     | 6          | 812       | 1930       | 998000     | 500
```

An absent or empty ledger prints `model calls (last 7 days): none recorded`
rather than an empty grid, and a line that will not parse costs that one call
rather than the report. p95 is nearest-rank, the same convention
`retrieve.benchmark` uses. The token columns in the first grid are the
ledger's chars ÷ 4 estimates — the `(est)` in the header is not decoration.
The real-usage grid only appears when at least one call in the window has
`usage_source == "session-log"` (§5.8) — its counts are provider-reported,
summed only over those calls.

`--json` emits the same data in the shape the future TUI health tab will consume,
so treat it as a stable contract:

```json
{
  "schema_version": 1,
  "rows": [
    {
      "component": "flush",
      "last_status": "ok",
      "last_run": "2026-08-28T10:10:00+03:00",
      "last_error_or_skip": "unknown",
      "quarantine_count": 1
    }
  ],
  "warnings": [
    {"message": "fail:rootmap-regen-failed", "ts": 1756289400,
     "age_seconds": 3600, "eski": false}
  ],
  "bekleyen": {"count": 2, "oldest": "2026-09-04.md", "files": ["2026-09-04.md", "2026-09-05.md"]},
  "calls": {
    "window_days": 7,
    "total_calls": 9,
    "ok_calls": 8,
    "failed_calls": 1,
    "real_usage_calls": 6,
    "backends": [
      {"backend": "claude", "calls": 6, "median_ms": 610, "p95_ms": 2400}
    ],
    "components": [
      {"component": "flush", "calls": 5,
       "input_tokens_est": 15475, "output_tokens_est": 2250,
       "real_usage_calls": 3, "input_tokens_real": 812,
       "output_tokens_real": 1930, "cache_read_tokens_real": 998000,
       "cache_write_tokens_real": 500}
    ]
  }
}
```

Shape rules, so a consumer can rely on them:

- `rows` is always present and always holds exactly three rows, in the order
  `flush`, `compile`, `ingest`, whether or not any state file exists.
- `warnings` is always present, one entry per `health.json["warnings"]` item in
  order, and empty rather than absent when there are none. `ts`/`age_seconds`
  are `null` when no timestamp (own or top-level) could be resolved.
- `bekleyen` is always present: `count` (int), `oldest` (filename or `null`)
  and `files` (the full pending list, possibly empty).
- `calls` is always present. Its `backends` and `components` lists are sorted by
  call count descending, then by name, and are empty when nothing was recorded —
  an empty ledger is not an absent key.
- Every field is a string except `quarantine_count`, which is an integer.
- Unknown values are the literal string `"unknown"`, never `null` or absent.
- `last_run` is an ISO-8601 local-offset timestamp when known. Epoch seconds in
  the source files are converted; strings are passed through unchanged.
- `quarantine_count` is `len(compile-state.json["quarantined"])`. It is
  compile-owned and **repeated on every row** rather than being per-component, so
  the table has one column instead of a footnote. Read it once, not three times.
- Add fields in a later version rather than renaming or reordering these, and
  raise `schema_version` when the meaning of an existing field changes.

### 9.2 `kota.py` / `kota_hiz.py` — quota bands and the `bilinmiyor` band

```powershell
python scripts/kota.py            # one line, for SessionStart injection
python scripts/kota.py --detay    # multi-line breakdown
python scripts/kota.py --json     # machine-readable
```

`kota.py` reads official quota percentages for each window (Claude's OAuth
usage endpoint, then the statusline cache, then a local spend tally; Codex's
own `rate_limits` field) and `kota_hiz.py` turns each into a forward-looking
band from `R = yanma / sürdürülebilir` (burn rate over the sustainable rate to
the reset).

**Stale-source handling (Astra A8).** Every percentage rides on a server-side
observation that itself has an age — the moment the OAuth cache was written,
or the Codex rollout file's mtime. Past `BEYIN_KOTA_BAYAT_DK` minutes (default
`120`) the window's band becomes `bilinmiyor` ("unknown") instead of whatever
the stale percentage would otherwise imply, and the line prints
`[? bayat <n>dk]`. `bilinmiyor` sorts between "dikkat" (attention) and a
closed/rationed band in the manager selection — an unrationed window still
wins management, but `bilinmiyor` is never read as "serbest" (free). A window
past its reset time needs a fresh observation to be called free; without one
it is `bilinmiyor` too, where it used to report `serbest` unconditionally. An
unchanged observation (same `gozlem` + `used` + `resets_at`) is no longer
appended to `.state/kota-orneklem.jsonl` a second time — that duplicate
appending under a fresh timestamp was what produced a synthetic Δused = 0 and
a false "R 0.0 · serbest" reading. Older sample lines have no `gozlem` field;
the reader ignores unknown fields, so the format stays backward compatible.

### 9.3 `harcama_defteri.py` — the spend ledger, v2

```powershell
python scripts/harcama_defteri.py --topla            # update the ledger
python scripts/harcama_defteri.py --topla --yeniden  # rebuild from scratch
python scripts/harcama_defteri.py --ozet             # print a summary
```

Reads `usage` blocks out of `~/.claude/projects/**/*.jsonl` and Codex's
`total_token_usage` counters to tally model spend. The ledger file records
`surum` (schema version) `2`: a Claude Code transcript re-emits the same
assistant message across streaming updates, retries and compaction rewrites,
and every copy carries its own `usage` block, so a `surum 1` ledger counted
some responses two or three times. Each usage record is now keyed by
`message.id` (falling back to `requestId`, then `uuid`), keeping the **last**
record seen for that key; a key already recorded from an earlier file (in
sorted path order) is skipped and counted separately as a cross-file
duplicate. The daily breakdown is built from each record's own timestamp
(converted to the local calendar day) rather than the session's last
timestamp, so a session crossing midnight splits correctly across days. A
`surum 1` ledger is rebuilt rather than inherited. `--yeniden` forces a full
rebuild; `BEYIN_HARCAMA_DEFTERI` overrides the ledger's path, for read-only
measurement runs against a copy.

## 10. Tests

`scripts/tests/` runs under `pytest` (configured in `pyproject.toml` via
`testpaths`). Coverage includes compile state handling, flush summary shape and
silent-skip behaviour, the retrieval index, root-map generation, the secret guard,
the frontmatter schema gate and its read-only survey (`test_sema_gate.py`), the
per-call ledger and its content-leak guard (`test_call_ledger.py`), each ingest
source, and the model-backend dispatch (`test_agy_backend.py`, fully mocked — no
test ever launches a real CLI).

`conftest.py` holds one autouse fixture: it redirects `claude_runner.STATE_DIR`
to a temporary directory for every test. Accounting is on by default and no
caller can opt out, so without it a mocked call would append to the developer's
own `scripts/.state/calls.jsonl`.

```powershell
python -m pytest
```
