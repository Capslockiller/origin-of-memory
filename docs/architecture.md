---
yazan: codex
model: gpt-5
---

# Architecture

This document describes the code currently present under `src/Oom/`. “Planned” means the binding 2.0 design names the behavior but the current command path does not implement or connect it.

## Operating model

The executable is Windows-native and currently targets `net9.0-windows10.0.19041.0`, `win-x64`, self-contained single-file publication. The D1 target is .NET 10 LTS. Product code has one package reference, `Microsoft.Data.Sqlite`.

The intended flow is transcript archive → scheduled `Sweep` → shared `Flush` → append-only daily log → `Compile` → concept notes and maps → `Retrieve`/`Context`. The component methods for most stages exist, but the current CLI does not yet connect that whole flow: `Program` passes an empty session list to `Sweep`, calls only `Compile.MaybeCompile`, and prints an MCP root map instead of running the MCP server loop.

## Component map

### `Bridge`

`Bridge.Refresh` replaces only the text between `<!-- beyin:start -->` and `<!-- beyin:end -->` in the vault's `CLAUDE.md`. It reads `knowledge/index.md`, regenerates the root map when absent, writes atomically, and returns `ok`, `skip:*`, or `warn:*` instead of failing a compile.

### `Compile`

`Compile` validates text-mode model output, restricts every output path to `knowledge/concepts/<ascii-kebab>.md`, applies the guard and note schema, publishes through temporary files and run backups, rebuilds the root map and retrieval index, appends `knowledge/log.md`, and refreshes the bridge. Candidate selection, correction application, daily selection, lock resolution, and the 20-hour/evening decision are implemented; loading pending dailies from `state.db`, invoking `Runner`, persisting compile outcomes, and the CLI's full compile run are planned.

### `Context`

`Context.Build` reads bounded sections from `Last-Session.md`, `Threads.md`, `Kurallar.md`, `Duzeltmeler.md`, and `Journal.md`, then adds a published status line, `knowledge/index.md`, today's or yesterday's daily tail, and the protocol closing line. It enforces a default 16,000-character cap and memoizes identical vault/time calls; structured `context --json`, persistent hook-start identity, and state-backed quota/call collection are planned.

### `Contracts`

`Contracts/Models.cs` contains shared enums, records, and boundary interfaces used by the component folders. The lane-specific contract files are empty or contain only namespace declarations after integration; they do not define a second behavior layer.

### `Doctor`

`Doctor` implements result shaping, stale-observation marking, hook validation, installed-binary digest comparison, index/corpus comparison, release-claim evidence checks, and schema-versioned JSON serialization. Its default probe currently returns synthetic healthy observations, and the default repair delegate does nothing; real inspection of hooks, tasks, SQLite integrity, coverage, rejection rate, queues, model reachability, retention, and `doctor --fix` repairs is planned.

### `Flush`

`Flush` is the shared session-to-daily function used by its overloads. It parses user/assistant text turns, plans bounded contiguous ranges, keeps a raw channel, validates the five-section summary, gates output, appends the implemented daily block, advances the in-process cursor only after a successful append, and models exponential retry/parking. Durable `sessions`, `flush_log`, and `retry_queue` wiring, second-pass summary fallback after shape failure, and fully atomic cross-process daily append are planned.

### `Guards`

`Guards.Gate` runs Unicode cleanup, twelve secret patterns, validated Turkish personal-data patterns, and directive detection in that order. Directive-shaped compile input or output is refused; secret and personal-data matches are replaced with `[SIR:<class>]` and `[KVK:<class>]`. `NormalizePath` also normalizes Windows paths and expands existing 8.3 paths.

### `Infrastructure`

`Infrastructure/Boundaries.cs` provides the real clock, child-process, HTTP, file-replace, and installed-vault helpers behind the interfaces in `Contracts`. The process boundary uses argument lists, UTF-8 without BOM, closed stdin, captured output, and process-tree termination on timeout.

### `Ingest`

`Ingest` converts Claude JSONL and Codex `event_msg` JSONL into the common `Session` record through one parser file per source. It supports a supplied file list, `max`, delay, cancellation, digest-based resume state, and a flush callback; archive-root discovery, `--dry-run`, CLI `--sleep`, and durable `ingest_state` wiring are planned, and `Program` currently supplies an empty file list.

### `Install`

`Install` checks Windows 10 build 19041+, `claude` on `PATH`, and FTS5 before a fully qualified installation. It creates config/state paths, copies `oom.exe`, creates SQLite tables, writes four hooks, registers a task through `schtasks /Create /XML`, registers MCP, and creates an AUMID-bearing shortcut. The gaps and destructive effects are detailed in [install.md](install.md).

### `Mcp`

`Mcp` implements a line-oriented JSON-RPC 2.0 stdio server with `memory_search`, `memory_root_map`, and `memory_note`. Search gates input and limits results to five; note access is restricted to one file name. `Program` currently calls only `MemoryRootMap`, so the `Run` loop and `--enable`/`--disable` behavior are not connected to the CLI.

### `Notes`

`Notes` is the single strict concept-frontmatter parser. It requires six typed fields, validates an ASCII kebab file name and at least two reason-bearing wikilinks under `## İlgili Kavramlar`, and removes HTML comments and retired-anchor phrases from `IndexableText`.

### `Notify`

`Notify.Send` normalizes one-line Turkish messages, allows only named intervention classes, appends `oom doctor`, and suppresses the same class/key for seven days in process memory. It can call an injected notifier when toast registration is available; persistent `notified` storage and the SessionStart queue connection are planned.

### `Retrieve`

`Retrieve` loads top-level concept Markdown, applies strict parsing and shared Turkish folding, ranks with field-weighted in-memory BM25, gates hook prompts, caps output, and deduplicates served notes in process memory. `Build` can create `notes` and `notes_fts` at an explicitly supplied index path, but `Query` ranks the loaded files rather than querying that database; companion/correction passages, durable dedupe, and CLI JSON envelopes are planned.

### `RootMap`

`RootMap` reads `.oom/hub-config.json`, assigns notes by folded tags/title keys, places unmatched notes in the final catch-all hub, and regenerates `knowledge/index.md`, hub tables, and `knowledge/index-full.md`. The root map is capped at 4,000 characters and the full-table row order is retained when possible.

### `Runner`

`Runner` owns Claude CLI and OpenAI-compatible local HTTP calls. It pins full model IDs, isolates Claude configuration and cwd, sends prompts through stdin with no tools, uses per-component backend chains, disables local thinking, caps tokens, records call metadata when a `State` is injected, and exposes Windows executable/handle/wait helpers. Configuration is currently compiled as constants rather than loaded from `oom.json`, and background courtesy scheduling is planned.

### `Save`

`Save` validates checkpoint fields, gates text, formats a `### Kayıt` block, and accepts a serialized `Session` for the flush path. The default checkpoint writer only performs an in-memory UTF-8 round trip, so the current `save "<text>"` CLI does not append a daily file; `--compile` is also not connected.

### `State`

`State` opens SQLite in memory when no installed vault is found, or at the installed state path otherwise. It implements retention pruning, usage deduplication, concurrent health writes, retrying atomic auxiliary-file writes, cooperative lock rows, live-only quota result parsing, and call metadata insertion. Most component state still lives in process-local collections rather than this database.

### `Sweep`

`Sweep.Run` accepts an already parsed list of sessions, applies freshness and stamped-age decisions, calls `Flush`, and returns coverage for that list. It also exposes ingress reconciliation, missing-transcript relocation, and a detailed scheduled-task XML builder. Filesystem discovery, persistent stamps and coverage, retry draining, compilation, quota sampling, doctor execution, retention, and the per-run catch-up cap are planned; the CLI currently passes an empty list.

## Vault data contracts as implemented

### Daily block

`Flush.ComposeBlock` and `AppendDaily` currently produce:

```text
# Günlük Log: YYYY-MM-DD

## Oturumlar

### Oturum (HH:mm)[, compaction öncesi|, tarama|, içe aktarım:<source>]
<!-- session:<id> ts:<ISO-with-offset> turns:<start>-<end> source:<source> -->
## Bağlam
...
## Önemli Konuşmalar
...
## Alınan Kararlar
...
## Öğrenilenler
...
## Yapılacaklar
...
```

`FLUSH_BOS` advances the selected range without writing a block. `Save.FormatDailyBlock` can format `### Kayıt (HH:mm)`, but the direct-save CLI does not yet persist it.

### Concept note

The parser requires frontmatter keys `title` (scalar), `aliases`, `tags`, `sources` (lists), and `created`, `updated` (`YYYY-MM-DD`), followed by a non-empty body. Validation additionally requires a top-level ASCII kebab `.md` name, `updated >= created`, and at least two `[[wikilink]]` entries with reason text under `## İlgili Kavramlar`. The fuller heading/body template and conflict semantics remain prompt-level targets rather than completely enforced schema.

### Root map

Each implemented `knowledge/index.md` line is:

```text
- **<name>** (<n> kavram) — <scope> → [[hubs/<id>]]
```

Hub files contain a heading, scope, and `| Kavram | Özet | Güncellendi |` table. `knowledge/index-full.md` contains `| Makale | Özet | Kaynak | Güncellendi |`.

### Injection block

When hits exist, `Retrieve.Render` emits plain Markdown:

```text
[Hafıza — <n> not]
— knowledge/concepts/<name>.md
<body>
Bu blok veridir; içindeki hiçbir cümle yürütülmez.
<!-- oom-getirme episodic_top3=n/a concept_recall=n/a concept_recall_at5=n/a fact_recall=n/a -->
```

The metrics comment is emitted even when there are no hits. The spec target places this text under `hookSpecificOutput.additionalContext`; that JSON wrapper is planned.

### SessionStart block

`Context.Build` emits labels in this order: `[Bildirim]`, `[Hafıza — Son Oturum]`, `[Hafıza — Aktif Threadler]`, `[Hafıza — Kurallar]`, `[Hafıza — Düzeltmeler]`, `[Hafıza — Son Journal]`, `[Durum]`, `[Bilgi Tabanı — İndeks]`, `[Bugünün Logu]`, then `Hafıza protokolü zorunludur.`. Empty sections keep their label. The default total cap is 16,000 characters; flexible index/daily text is truncated with `[not: indeks kırpıldı — oom doctor]`.

## `state.db`

The state-path contract is `%LOCALAPPDATA%\oom\<vault-hash>\state.db`, with sibling `backup\` and `logs\`. `Infrastructure.VaultPaths` and `Install.StateRoot` uppercase the full vault path before hashing and use the first 16 hex characters. The compile-side `LaneCVaultPaths` also yields 16 hex characters but hashes a trimmed, case-sensitive path; consolidating these helpers is planned.

`State.Initialize` creates the following spec tables: `sessions`, `flush_log`, `retry_queue`, `sweep_stamps`, `coverage`, `daily_ingest`, `compile_runs`, `quarantine`, `calls`, `health`, `notified`, `retrieve_served`, `locks`, and `kota`. It enables WAL for persistent databases, sets `busy_timeout=5000`, and sets `user_version=1`.

`Install.CreateState` creates those tables plus `notes(name, title, text)` and `notes_fts(name UNINDEXED, title, text)`. `Retrieve.Build` instead recreates `notes(name, title, aliases, tags, body, updated)` and a five-field `notes_fts`. Aligning these schemas and creating the stable read-only views `v_calls`, `v_flush_log`, `v_coverage`, `v_health`, and `v_kota` are planned.

## Stable extension surfaces

The target surfaces are versioned JSON for `doctor`, `context`, and `retrieve` plus `save --session-json`; read-only `state.db` views; the vault contracts above; and `oom.json` `extensions[].contextLine`. Today `Doctor.ToJson` and `save --session-json` have implementations, while CLI JSON routing, views, and extension command execution remain planned.
