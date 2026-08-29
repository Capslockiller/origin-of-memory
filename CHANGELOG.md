# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

<!-- yazan: claude · fable-5 -->
- Recency no longer multiplies the fused score. The old post-fusion multiplier
  was scale-blind (a 1.6% band multiplied by up to 4x), so freshness could bury
  a strong match. Recency now contributes only as an RRF channel with a single
  pre-declared weight (`BEYIN_RRF_RECENCY_CHANNEL_WEIGHT`); the former
  behaviour stays reachable for comparison runs via
  `BEYIN_RRF_LEGACY_MULTIPLIER=1`.
- Sealed the retrieval default after a 125-question gold-set run: `bm25`
  recall@3 83.2% vs `rrf` 69.6% (McNemar p=0.003). `bm25` stays the default
  and the fused path's default candidacy is closed until a redesign measures
  better on the same set. The verdict and the reproduction command live in
  `docs/retrieval.md` §7.
- The Ollama runner now sends `think: false` and a `num_predict` cap
  (`BEYIN_OLLAMA_THINK` / `BEYIN_OLLAMA_NUM_PREDICT`): qwen3's thinking mode is
  on by default and was consuming the whole token budget before any answer.

### Added

<!-- yazan: claude · fable-5 -->
- Three measurement tools, all stdlib-only: `tools/olc_baslangic.py`
  (cold-start breakdown of the retrieval hook), `tools/tr_beir_kos.py`
  (SciFact BEIR anchor, EN and TR legs), `tools/a4_kapi.py` (the two-mode
  compile-gate harness and its report table).

### Fixed

<!-- yazan: claude · opus-5 -->
- `kur.ps1` handed `model_oneri.py` the hardware probe as a native command
  argument. Windows PowerShell re-splits the quotes inside a JSON blob, so
  argparse saw garbage and exited 2 — reproduced here with a 504-character
  probe before anything was changed. The probe now travels through a temporary
  file that is removed afterwards, and `model_oneri.py` gained `--probe-file`
  beside the existing `--probe-json`. This surfaced while chasing an install
  failure on someone else's machine; it is a real defect and a plausible cause,
  but that log has not been read, so it is not claimed as *the* cause.

## [0.4.1] - 2026-08-28

### Added

<!-- yazan: claude · opus-5 -->
- **Local models tab** in the panel. Backend independence was built but not
  usable: switching to a local model meant editing an environment variable in a
  terminal. The tab shows the machine's hardware and the ranked recommendations
  from `donanim.py` and `model_oneri.py`, the installed inventory from Ollama's
  own `/api/tags`, and which backend the next pipeline run will actually use.
  Three actions, each confirming first: **pull** streams Ollama's real completed
  and total bytes and refuses up front with a number when the disk cannot hold
  the model; **switch** names the exact `BEYIN_MODEL_BACKEND` value and the
  exact place it will be stored before writing it, because nothing a live
  pipeline reads may appear behind the owner's back; **try** sends one fixed
  prompt through the existing runners and reports the answer, model and latency.
  When Ollama is unreachable the tab says so rather than showing an empty
  inventory that reads like a measurement. Model deletion is deliberately
  absent — the panel deletes nothing.
- **`Setup.cmd` and `Local Brain.cmd`** at the repository root, so someone who
  downloaded the source zip can double-click instead of knowing to run a
  PowerShell script. `Setup.cmd` starts the graphical wizard and falls back to
  the terminal one; both only launch what already exists.
- The built installer is now **attached to the release** as
  `OriginOfMemory-Setup-<version>.exe`, with its SHA256 in the release notes —
  an unsigned binary people are asked to double-click should at least be
  verifiable.

## [0.4.0] - 2026-08-28

### Added

<!-- yazan: claude · opus-5 -->
- **A real Windows installer** (`installer/origin-of-memory.iss`, built with Inno
  Setup). The familiar setup window with Back / Next / Cancel, an Add/Remove
  Programs entry and a desktop shortcut. Per-user: it never asks for
  administrator rights and installs nothing system-wide. `kur.ps1` stays the
  installation authority — the wizard collects answers, writes a plan and hands
  it over. Detection is the existing code, not a reimplementation. The vault
  default deliberately avoids a OneDrive-redirected Documents folder, and the
  Ready page itemises exactly what will happen before anything does. The output
  is **unsigned**, so Windows will show a SmartScreen prompt; the wizard never
  tries to bypass it. See [`docs/installer.md`](docs/installer.md).
- **Local Brain, the operations panel** (`beyin.ps1`, `gui/panel.html`,
  `LocalBrain.exe`). A window that shows whether the system is alive and runs
  its operations, opened from a shortcut rather than a terminal. Health is the
  live form of what `beyin doktor` reports, read from `durum.py --json` because
  that contract was already documented as stable for exactly this; Today shows
  the day's sessions, the last flush and the last compile. Four operations —
  doctor, compile, index rebuild, watcher sweep — each confirming first and
  streaming over SSE, so closing the browser drops the view and not the work.
  **Nothing in the panel deletes anything**, and a test greps the server for
  file-removal primitives so that guarantee cannot rot quietly. The launcher is
  a console-free C# binary built with the inbox `csc.exe`; its icon is an
  original placeholder, deliberately not anyone else's mark. See
  [`docs/panel.md`](docs/panel.md).

### Added

<!-- yazan: claude · opus-5 -->
- **Graphical setup wizard, phase one** (`kur-gui.ps1`, `gui/kur.html`). The
  terminal wizard is hard to follow, so the setup surface is moving to a
  browser page driven by a loopback server. The server is PowerShell rather
  than Python because the wizard's own job includes installing Python — a
  wizard that needs Python cannot run on the machine it is meant to fix. A raw
  `TcpListener` on `127.0.0.1` binds unprivileged; `HttpListener` was avoided
  because HTTP.sys URL reservations can demand elevation elsewhere. A
  single-use 256-bit token travels in the URL fragment, so it never reaches the
  server or a log, and is exchanged once for a `SameSite=Strict; HttpOnly`
  cookie; every API call must also carry an exact `Host` and `Origin`. The page
  makes no external request of any kind. Detection is not reimplemented — the
  System Check screen calls what `kur.ps1` already probes — and progress rides
  a sequence-numbered SSE stream with replay, so closing the browser drops the
  event stream without killing the work. **Installation is not yet driven by
  the UI**; see [`docs/gui-wizard.md`](docs/gui-wizard.md) for what phase two
  must add.

## [0.3.0] - 2026-08-28

### Fixed

<!-- yazan: claude · opus-5 -->
- A settle-window check shared by `ingest_claude`, `ingest_codex` and the
  watcher skipped files whose mtime landed a fraction ahead of the clock, even
  when the window was zero and therefore meant "disabled". A skipped candidate
  is never classified, so its rejection vanished and the suite failed
  intermittently while passing in isolation. The window must now be positive to
  filter anything.

### Added

<!-- yazan: codex · gpt-5.6-sol -->
- **Hookless transcript watcher** (`scripts/watcher.py`). Session capture was the
  last memory surface that required Claude Code lifecycle hooks; a quiet
  15-minute scanner now reuses the Claude/Codex ingest adapters, the shared
  summarisation path, and `ingest-state.json` watermarks, so the hook becomes a
  latency optimisation instead of a dependency. A minimal named-folder adapter
  accepts finalized `.md` and `.jsonl` sessions. The watcher holds the existing
  per-session flush lock and rechecks canonical daily anchors before every model
  call, allowing hook and watcher capture to coexist without duplicate records.
  Antigravity capture remains explicitly unimplemented because its official
  documentation establishes only a local conversation-ID cache, not an on-disk
  transcript layout; guessing a path would turn data loss into a silent success.
  No scheduler is registered—the owner still controls that operating-system
  decision. See [`docs/watcher.md`](docs/watcher.md).

<!-- yazan: odena · claude-opus-5 -->
- **Context bridge** (`scripts/context_bridge.py`). Per-message injection needs
  a prompt hook and only some hosts offer one; the bridge closes most of that
  gap from the other side. After a successful compile the root map is written
  into a delimited block inside `AGENTS.md`, `GEMINI.md`, and `CLAUDE.md` at the
  vault root, so an agent that never calls a hook still sees what the knowledge
  base holds and how to search it. A file that does not exist is **never
  created** — its existence is the user's consent — and nothing outside the
  markers is ever rewritten. A file whose markers are damaged is left completely
  untouched and reported, because half a marker means a human edited the block
  and guessing where it ended would destroy their text. Identical content is not
  rewritten, headings are demoted one level so the map never outranks its host,
  and the block is secret-scanned before any write. Toggle with
  `BEYIN_CONTEXT_BRIDGE=off`. See [`docs/context-bridge.md`](docs/context-bridge.md).
- **Tool-free compile mode**, behind `BEYIN_COMPILE_MODE=text`
  (`scripts/compile_text.py`). The model returns a delimited file transcript and
  this project writes the staging tree itself, so compile stops being the one
  call that needs `--tools` and `--permission-mode` — and stops being the one
  call only `claude` can serve. Everything downstream is unchanged: the same
  manifest diff, path allowlist, directive quarantine, secret guard, schema gate
  and atomic promotion audit the result, and a parity test proves both modes
  promote identical bytes from identical model output. Refusals are proven by
  mutation testing: forbidden paths, traversal, absolute paths, truncated
  blocks, duplicates, oversized output and leaked credentials each fail the run
  or drop the block. **The default is still `tools`** and stays there until the
  measured gate in the v0.6 plan has run against a real model. See
  [`docs/tool-free-compile.md`](docs/tool-free-compile.md).

### Changed

<!-- yazan: codex · gpt-5.6-sol -->
- Archive ingest now carries canonical session anchors for Claude Code archive,
  Codex rollout and claude.ai web sessions, so imported provenance and recency
  no longer collapse to note frontmatter when the source supplies a genuine
  session ID. Gemini stays deliberately anchors-free because its Takeout-derived
  day chunks have no genuine session identity to claim.
- The compiler snapshots pre-call concept-note anchors and restores only those
  a model rewrite removed before promotion, so event provenance no longer
  depends on the model obeying a preservation instruction; post-call order and
  model-added anchors remain untouched.

- `docs/compatibility.md` now states which memory surface each host actually
  provides, and separates "the only backend that can compile" into "the only one
  that can compile *in tool mode*".

## [0.2.0] - 2026-08-28

### Added

<!-- yazan: claude · opus-5 -->
- **Frontmatter schema gate** at the compiler's promotion path
  (`scripts/sema.py`). The compiler's prompt always described the concept-note
  schema; nothing enforced it, so a note with broken frontmatter entered the
  index with its title degraded to the filename and empty tags. A staged note
  that fails validation is now **not promoted** — it is routed to
  `.stage/karantina/sema/` with a sidecar naming the problems, health records
  `schema-invalid:<file>`, and its clean siblings in the same run still promote.
  Nothing is ever auto-repaired: inventing a missing `created` date would file a
  fabricated fact as permanent memory. The gate stops **new** damage only —
  `retrieve.build_index` and `rootmap` keep their tolerant behaviour, so an
  existing imperfect corpus keeps indexing and keeps being retrieved.
- `beyin doktor` now surveys the live corpus read-only: `retrieve.py verify`
  reports `schema_checked`, `schema_invalid_count` and up to five offending
  notes with their problems. The survey never affects `ok` and never modifies or
  blocks anything — it is a census of what predates the gate.
- **Per-call accounting** in `.state/calls.jsonl`, appended by
  `claude_runner.run_claude()` — the single choke point every model call already
  passes through. One line per call: timestamp, backend, model tier and resolved
  slug, component, character counts, chars ÷ 4 token estimates (`_est` in the
  field name, because that is what they are), duration and outcome. **No prompt
  or response content** — `record_call()` is handed counts rather than strings,
  so content has no path into the file, and a test asserts the field set never
  grows. Append-only, capped at 5 MB, rotated keeping the newest lines.
- `python scripts/durum.py` gained a call-accounting section: the last 7 days
  per backend with median and p95 duration, and estimated tokens per component.
  `--json` adds a `calls` key alongside the unchanged `rows` contract. See
  "Measuring your own setup" in [docs/local-models.md](docs/local-models.md) for
  how to compare backends with it, and why the token figure skews low on
  Turkish text.

- Opt-in **hybrid retrieval** (`BEYIN_RETRIEVAL=rrf`): reciprocal rank fusion
  over BM25, tag/alias overlap and recency, with a bounded recency multiplier.
  Default stays `bm25` until the fused path is measured against the gold set —
  the synthetic benchmark suggests it may exceed the 500 ms injection gate on a
  real corpus, and two open ranking questions are documented rather than tuned
  away. See [docs/retrieval.md](docs/retrieval.md).
- **Session anchors**: each flushed daily block carries
  `<!-- session:<id> ts:<ISO8601> -->`, the compiler carries it into the concept
  note's sources, and retrieval strips anchors before injection — so a compiled
  claim can be traced back to the session that produced it.
- **Compile hygiene**: content-hash skip for unchanged daily logs, an index
  rebuild gate keyed on a concepts manifest hash, and a minimum-interval gate on
  the nightly trigger. Skips are recorded in health state as skips, never as
  errors.
- **Epistemic-status preservation** in the compiler prompt: hedged statements
  keep their hedge and date, and a new claim that contradicts an existing note
  is recorded as an explicit conflict line instead of silently overwriting it.
- MCP tools now carry behaviour annotations (read-only, non-destructive,
  idempotent, closed-world) so clients can judge them without guessing.
- `beyin doktor` gained an index-consistency check: what the FTS index should
  contain, recomputed from `knowledge/concepts/` and diffed against `notes.db`.

<!-- yazan: claude · opus-5 -->
- `python scripts/durum.py [--json]` — a one-table health summary (component,
  last status, last run, last error/skip, quarantine count) built from
  `health.json`, `ingest-health.json`, `compile-state.json` and
  `last-flush.json`, always exiting 0, with the JSON shape documented as a stable
  contract for the future TUI health tab.
- CI now runs the suite across Python 3.12 and 3.13 on `windows-latest`, and a
  new [docs/compatibility.md](docs/compatibility.md) states the external CLI flag
  surface and HTTP endpoints this was built against — and that anything beyond
  them is untested.
- `beyin doktor` gained a quarantine check reporting the count and newest entry,
  red when non-empty, with the manual release steps.

<!-- yazan: codex · gpt-5.6-sol -->
- Two-screen, one-Enter recommended setup with deterministic JSON planning,
  `-Recommended` agent automation, visible custom defaults, and seven-step
  maximum custom flow.
- Multi-runtime wizard support for Ollama, LM Studio, llama.cpp, and vLLM,
  including OpenAI-compatible endpoint wiring and consent-safe model handling.

<!-- yazan: codex · gpt-5.6-sol -->
- Fault-tolerant Windows hardware probe (`scripts/donanim.py`) and verified-tag
  Ollama fit recommender (`scripts/model_oneri.py`), with interactive wizard
  guidance, explicit non-interactive install/pull plan fields, disk preflight,
  and dry-run isolation.
- Safe `uninstall.ps1` for exact hook/MCP cleanup and separately approved copied
  files, with per-file backups and an explicit vault-memory preservation rule.

<!-- yazan: codex · gpt-5.6-sol -->
- Interview-first PowerShell 5.1 setup wizard (`kur.ps1`) with strict JSON plans,
  cloud/hybrid/local/lite presets, dry-run-safe user environment actions,
  selected-skill installs, optional non-clobbering Claude Desktop MCP merge,
  and preset-specific verification/next-step output.
- `install.ps1 -SkillFilter` for selected skills while preserving the default
  all-skills standalone behavior; lite wizard runs skip hook registration.
- Backend-aware Gemini ingest bounds for local endpoints: 24,000 characters by
  default or `BEYIN_FLUSH_CHUNK_CHARS`, while Claude, Antigravity, and Codex
  retain the existing full-day payload.

<!-- yazan: codex · gpt-5.6-sol -->
- Optional OpenAI-compatible local backend for LM Studio, llama.cpp
  `llama-server`, vLLM, and similar chat endpoints. It uses stdlib `urllib`,
  requires an explicit URL and fast-model slug, supports an optional Bearer
  token, and preserves distinct connection, HTTP, timeout, and response errors.
- Backend-aware live-flush chunking: Ollama and OpenAI-compatible runs default to
  24,000 transcript characters, `BEYIN_FLUSH_CHUNK_CHARS` overrides any backend,
  and the effective bound is recorded in flush state detail.
- Local model selection, rough hardware tiers, and context guidance in
  `docs/local-models.md`.

- Local **MCP memory server** (`scripts/mcp_server.py`, stdlib-only JSON-RPC
  over stdio): `memory_search` and `memory_root_map` tools plus root-map and
  hub resources, read-only, dual protocol era (`2026-07-28` and the legacy
  `initialize` handshake shipping desktop clients still speak). Registration
  and caveats: `docs/mcp.md`.
<!-- yazan: codex · gpt-5.6-sol -->
- Optional local **Ollama backend** for text-mode flush and ingest calls,
  selected with `BEYIN_MODEL_BACKEND=ollama`. It posts non-streaming generate
  requests through stdlib `urllib`, requires an explicit fast model slug, maps
  transport/protocol failures to stable health strings, and falls back to
  `claude` for compile when available while refusing tool mode otherwise.
- Manual clipboard context bridge: `scripts/context_pack.py` composes the root
  map plus capped BM25 notes and can send UTF-16LE text to `clip.exe`;
  `hooks/pano-kopru.ps1` is an unregistered PowerShell 5.1 wrapper for manual
  use or a user-defined shortcut.
- Optional **Antigravity CLI (`agy`) backend** for the background model calls,
  selected with `BEYIN_MODEL_BACKEND=antigravity`. Default behaviour is
  unchanged: with the variable unset every call still goes through `claude -p`
  with byte-identical arguments. `BEYIN_MODEL_BACKEND=gemini` is accepted as a
  deprecated alias and warns (Google retired Gemini CLI's serving on
  2026-06-18; `agy` is the successor).
  - New `scripts/agy_runner.py` runs the documented headless contract
    `agy -p <prompt> --model <slug> --output-format text` with stdin closed,
    the same timeout and out-of-vault temporary-directory discipline as the
    Claude path, and `BEYIN_INVOKED_BY` still set.
  - Binary resolution: `agy` by default, `BEYIN_AGY_BIN` to override, with the
    fixed `cmd.exe /d /s /c` bridge for Windows `.cmd`/`.bat` shims.
  - Model mapping: `haiku` → `BEYIN_AGY_MODEL_FAST` (default
    `gemini-3.5-flash-medium`, the only slug the official docs show);
    `sonnet` → `BEYIN_AGY_MODEL_SMART`, which has no default and degrades to
    the fast model with a `warn:agy-smart-model-unset:…` entry in health state.
  - Distinct failure strings propagated into health state: `agy-missing`,
    `agy-auth-missing` (best-effort stderr sniffing), `agy-exec-error`,
    `agy-timeout`.
  - **Compile is refused** on this backend with
    `antigravity-backend-unsupported:compile`. Compile is the only tool-mode
    call and `agy` offers no per-invocation permission scoping — only a
    user-global allow-list or `--dangerously-skip-permissions`, which this
    repository does not ship. In `antigravity` mode compile keeps using
    `claude` when that binary is on `PATH` and fails loud otherwise.

### Changed

<!-- yazan: claude · opus-5 -->
- The duplicate-check registry is now bounded instead of one row per concept for
  the whole corpus: hub-scoped to the daily log's topics plus the
  `BEYIN_REGISTRY_RECENT` (default 50) most recently updated concepts, hard-capped
  at `BEYIN_REGISTRY_MAX_ROWS` (default 400), with a one-line truncation notice in
  the prompt and a `warn:registry-truncated:<shown>/<total>` health warning —
  67,800 → 15,806 characters on a synthetic 1000-concept corpus.
- `write_health`, `write_health_skip`, `_atomic_write_json`, `_lock_exclusive` and
  `_sha256` now have one implementation in the new `scripts/beyin_ortak.py`, which
  `flush.py`, `compile.py`, `rootmap.py`, `ingest_common.py` and `retrieve.py`
  import; the module-level names they bind keep `flush._sha256`-style access
  working and are asserted to be the same object.
- Model-call timeouts are configurable and backend-aware: `BEYIN_FLUSH_TIMEOUT`,
  `BEYIN_INGEST_TIMEOUT` (both default 240 s, raised to 900 s when the resolved
  backend is `ollama` or `openai-compat`, because local inference is slow) and
  `BEYIN_COMPILE_TIMEOUT` (900 s). An unusable value is ignored with a
  `warn:timeout-invalid:<name>:<value>` health warning, the effective value is
  recorded in each component's state so a timeout is diagnosable, and the
  `claude`/`antigravity` defaults are unchanged. See
  [docs/local-models.md](docs/local-models.md).

### Security

<!-- yazan: claude · opus-5 -->
- Directive-shaped content is now **quarantined instead of noted**: a poisoned
  daily body is copied to `<vault>/.stage/karantina/` with a forensic sidecar and
  is not compiled, a poisoned root map or registry aborts the run with
  `PolicyError("directive-shaped-registry")`, and a poisoned model output is held
  back while its clean siblings still promote — each raising the health *error*
  `quarantine:directive-shaped`, with a documented, deliberately manual release
  path (see [SECURITY.md](SECURITY.md)).
- The compile lock now records `{machine, pid, started_at, hostname}` so a vault
  synced across machines no longer compiles twice: a live lock owned by another
  machine refuses with `skip:compile-locked-by:<machine>`, and a lock older than
  `BEYIN_COMPILE_LOCK_TTL_MIN` (default 120) is broken with a health warning
  naming the previous owner.

### Fixed

<!-- yazan: codex · gpt-5.6-sol -->
- Replaced `setx` persistence with the non-truncating .NET user environment API.
- Claude Desktop MCP registration now handles both standard and MSIX-virtualised
  config paths, backs up every edited file, and keeps dual configs in sync.

## [0.1.0] - 2026-08-27

First public release. This is the initial extraction of a working system into a
standalone repository, so everything is listed as added. The items below are
scoped as *what this project adds over the upstream
[avenoxbeyin v2](https://github.com/avenoxai/avenoxbeyin) base it derives from* —
see [docs/attribution.md](docs/attribution.md) for the lineage.

### Added

#### Native Windows port

- PowerShell hooks (`hooks/session-start.ps1`, `hooks/prompt-counter.ps1`,
  `hooks/memory-retrieve.ps1`, `hooks/flush-launch.ps1`, `hooks/session-end.ps1`)
  replacing the upstream bash hooks. No WSL, no POSIX shell.
- File locking falls back to `msvcrt` region locks where `fcntl` is unavailable,
  in both `flush.py` and `compile.py`.
- UTF-8 hardening throughout: hooks set `[Console]::OutputEncoding` to UTF-8
  without BOM, hook stdin is persisted as BOM-less UTF-8 for strict JSON parsing
  in Python, Python subprocesses are launched with `-X utf8`, and the installer
  writes `settings.json` with a BOM-less UTF-8 encoder.
- Detached background launch uses Windows `creationflags` (`DETACHED_PROCESS |
  CREATE_NO_WINDOW`) with a POSIX `start_new_session` fallback, so the flush hook
  returns in under a second and no console window flashes.
- `flush-launch.ps1` writes the hook payload to a state file and detaches
  `flush.py`, rather than blocking the session-end path.

#### User-level hook registration

- `install.ps1` registers all six hooks in `<user>\.claude\settings.json`, not in
  a project-scoped settings file. Memory is written **and read** from every
  project on the machine, not only from inside the vault folder.
- Registration is idempotent: an already-present command string is skipped, and
  the existing `settings.json` is backed up to `settings.json.bak-<timestamp>`
  before any write.
- `BEYIN_INVOKED_BY` recursion guard: every hook and every entry-point script
  exits on line one when that environment variable is set, so the pipeline's own
  `claude -p` subprocesses cannot re-trigger the pipeline.

#### FTS5 BM25 per-prompt retrieval

- New `scripts/retrieve.py`: builds a SQLite FTS5 index of every concept note
  (`build`) and answers ranked queries (`query`), with an atomic index rebuild.
- `hooks/memory-retrieve.ps1` on `UserPromptSubmit` injects the top 3 full notes
  for the current prompt. The hook performs the selection; the model is never
  given a search tool, because agents measurably under-call such tools.
- Ranking uses `bm25(notes, 8.0, 6.0, 3.0, 1.0)` over title, aliases, tags and
  body, with an optional `--min-score` floor.
- Injection caps: 1,500 characters per note, 4,500 characters total.
- Per-session dedupe ledger (`.state/retrieve-session-*.json`) prevents
  re-injecting the same note within a session; ledgers older than seven days are
  pruned.
- Prompts under 12 characters and slash commands are skipped as carrying no
  retrieval signal.
- Injected notes are labelled as data, with an explicit instruction that no
  sentence inside them is to be executed.
- Turkish support in the index: explicit dotted/dotless I folding
  (`turkish_fold()`) applied identically at index and query time, dual-form
  tokenisation (raw folded form plus a five-character prefix for words longer
  than five characters), and no stemmer by deliberate choice.
- `--bench` mode with a fixed query set for latency measurement.

#### Root-map layer

- New `scripts/rootmap.py`: regenerates `knowledge/index.md` as a compact topic
  root map under a 4,000 character budget, plus one hub file per topic under
  `knowledge/hubs/`, while the full article table moves to
  `knowledge/index-full.md`.
- Topic hubs are configurable per user through `hub-config.json` (shipped as
  `template/hub-config.example.json`): frontmatter tags and title keywords decide
  membership, unmatched concepts fall to a configured catch-all, and array order
  controls root-map order.
- Publication gates: every concept must be covered by a hub, the root map must fit
  its budget, hub output must match the configured hub set, and every staged file
  must be non-empty — otherwise nothing is published.
- Outputs are written to a temporary directory inside `knowledge/` and published
  with `os.replace`, so a failed run cannot leave a half-written map.
- The compiler now sends the root map plus a compact duplicate-check registry
  instead of the entire index. Measured on the author's corpus, this cut the
  per-call input base by 63% (152.8K → 56.1K characters).
- Index/concept parity mismatches are recorded as a health warning rather than
  silently ignored.

#### Secret redaction

- New `scripts/secret_guard.py` with `redact()` and `scan()`. Patterns are
  deliberately narrow: PEM private keys, AWS/Google/GitHub/Slack/Anthropic/OpenAI
  key formats, JWTs, credentials embedded in URLs, `Bearer` tokens, and
  `password:`/`api_key=` style assignments (tolerating Turkish possessive
  suffixes).
- `flush.py` redacts both the transcript going into the summariser and the summary
  coming out, recording a health warning naming the matched pattern classes.
- `compile.py` scans compiler output at the promotion gate.
- A harmless-value filter skips placeholders (`${VAR}`, `<...>`, `REDACTED`,
  `CHANGEME`, `EXAMPLE`, `ÖRNEK`, …) so free text is not mangled.

#### Compile isolation and policy gates

- The compiler runs `claude -p` inside a `0700` staging tree at
  `<vault>/.stage/compile-stage-*`, holding a copy of `knowledge/` and exactly one
  daily log — the live vault is never the working directory.
- Before promotion, a file manifest diff rejects deletions, type changes and any
  write outside `knowledge/concepts/**`, `knowledge/index-full.md` and
  `knowledge/log.md` (`PolicyError`).
- Daily sources are checked for symlinks and non-regular files before use.
- Untrusted-data delimiters wrap the root map, the registry and the daily body in
  the compile prompt, and the transcript in the flush prompt, with an explicit
  instruction that nothing inside them is an instruction.
- One compile per day is claimed with an `O_EXCL` trigger file; at most three
  daily logs are processed per run.
- A successful run clears the stale health error flag, so the health check cannot
  keep reporting a crash that has since been fixed.

#### Ingest family

- New `scripts/ingest.py` front-end with `claude`, `codex`, `web`, `gemini` and
  `status` subcommands, shared `--dry-run`, `--max-sessions`, `--sleep`,
  `--model` and `--retry-failed` flags, and an exclusive lock so two backfills
  cannot run at once.
- `ingest_claude.py` reads Claude Code transcript archives from
  `~/.claude/projects`.
- `ingest_codex.py` reads Codex rollouts from `~/.codex/sessions`.
- `ingest_web.py` reads claude.ai export ZIPs dropped into `<vault>/.import/`.
- `ingest_gemini.py` plus the one-off `tools/gemini_ayikla.py` extractor handle a
  Google Takeout Gemini archive.
- Resumable state tracking per source, so an interrupted backfill continues where
  it stopped and does not re-summarise finished sessions.

#### Evaluation methodology

- Judge-free binary recall@k over gold note identity as the primary metric, run
  against a corpus snapshot pinned by commit.
- Gold questions are real historical user questions rather than synthetic ones,
  with held-back canary questions.
- Documented statistical floor: a paired comparison needs a net difference of
  roughly 16 questions for p < 0.05 at n = 125.
- First measured run on the author's corpus: recall@3 83% (104/125), recall@5 84%
  (105/125), against a 0% baseline; retrieval hook p95 latency 347 ms.
  *(The recall@5 figure here was later found to be wrong — the run retrieved
  three results and labelled the column `top5`. Corrected in 0.2.0 to
  114/125 = 91.2%; recall@3 was unaffected. See docs/evaluation.md.)*
- Method and how to build your own gold set: [docs/evaluation.md](docs/evaluation.md).
  The author's gold questions are not published — they are personal data.

#### Installer, vault template and parametrised config

- `install.ps1` with `-VaultPath`, `-DryRun` and `-Force`: copies scripts, hooks
  and skills to their destinations, skips `__pycache__`/`.state`/`.stage`/
  `.import` and compiled artefacts, registers hooks, and verifies that Python
  3.12+ and the `claude` CLI are present.
- `template/vault/` skeleton with empty `daily/` and `knowledge/` trees.
- `template/hub-config.example.json` with generic English hub definitions,
  installed once and never overwritten — replacing hardcoded personal topics.
- `BEYIN_PYTHON` environment variable to select an interpreter; `py -3` fallback
  when `python` is not on `PATH`.
- Test-only escape hatches `BEYIN_FAKE_HOUR` and `BEYIN_FAKE_NOW` for the evening
  compile trigger.
- Test suite under `scripts/tests/`, runnable with `pytest`.

#### Agent onboarding and skills

- `INSTALL-AGENT.md`: a self-contained file an agent can be pointed at to
  install the system for its user — prerequisite probes (Windows, Python 3.12+,
  `claude` CLI, SQLite FTS5), dry run, install, verification of the first flush,
  a troubleshooting table and manual uninstall steps.
- `AGENTS.md`: repository conventions for agents working inside the codebase —
  test command, stdlib-only policy, why the bilingual naming is intentional, the
  compiler write-policy boundary, and the personal-data grep gate.
- `skills/README.md` documenting the shipped skill set and the pruning caveat:
  the installer copies the whole directory to the user's skills folder.
- Six genericized skills alongside the two mechanism skills — `companion`
  (structure example for the personal identity layer, placeholder content only),
  `orchestration`, `codex-fleet` and `gece-vardiyasi`. Absolute paths, usernames and project names replaced with
  placeholders; Turkish trigger phrases kept; upstream Avenox credits and
  adaptation notes preserved.
- `template/rules.example.md`: seventeen ranked binding rules in the form the
  session hook injects, fifteen genericized from the author's working ruleset
  and two adopted from upstream.

### Known gaps

- Sensitive-data filtering beyond credential patterns is not implemented; see
  [SECURITY.md](SECURITY.md).
- Web-fetched text that enters a transcript can be summarised into the vault.
  Untrusted-data delimiters are in place, but there is no exclusion list.
- Windows only; no tested macOS or Linux path.

[Unreleased]: https://github.com/Capslockiller/origin-of-memory/compare/v0.4.1...HEAD
[0.4.1]: https://github.com/Capslockiller/origin-of-memory/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/Capslockiller/origin-of-memory/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Capslockiller/origin-of-memory/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Capslockiller/origin-of-memory/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Capslockiller/origin-of-memory/releases/tag/v0.1.0
