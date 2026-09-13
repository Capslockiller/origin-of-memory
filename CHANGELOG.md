# Changelog

## 3.0.3 — 2026-09-13

### Added
- A clock on every prompt: `nudge` prints the time, when the session started, the gap since the last message and the prompt count; `context` prints when the last session ended.
- Daily notes start with frontmatter (`type: daily`, `date`, `source`). Concept notes carry `type: concept` and `hub`; `doctor --fix` migrates existing concepts and appends the hub link. Hub files carry `type: hub`.
- The compile prompt lists the hub ids and a tag vocabulary (hub-config tags plus the forty most frequent); more than one tag outside it is a health warning.
- `doctor` reports orphan concepts, concepts without a hub, and notes outside the schema.
- Compile keeps the reason a daily was rejected; a failed `claude` call records its stdout and stderr.
- Flush reads the compact summary Claude Code writes when a conversation is compacted and puts it before the raw-turn slice, under its own character budget. A session that holds only a compact summary still flushes.

### Fixed
- Codex rollouts in the current `response_item` format parse; developer and injected blocks are skipped. Until now every Codex session was unreadable to sweep.
- The hub configuration lives at `.oom/hub-config.json`; without it every concept fell into the catch-all hub.
- The nudge reminder is English. `sessions` gained `last_prompt_ts`; an older state database is set aside and rebuilt.
- Sweep and flush read Codex rollouts through the Codex parser; stamps marked unreadable by the old parser are retried.
- Hub membership matches whole tags and the note's name and title. A short tag such as `vr` no longer matches inside a word such as `kavram`, which had put every concept into one hub.

## 3.0.2 — 2026-09-12

### Changed
- The runner calls `claude` with the user's own login. The separate `.oom/claude-config` directory is gone; it needed its own `/login` after every fresh install and, without one, every summary failed with exit code 1 (Y-011).

## 3.0.1 — 2026-09-12

### Fixed
- Hook commands are written with forward slashes and carry `--vault <path>`. Claude Code runs hooks through bash on machines where bash is the shell; a backslash path was swallowed there and none of the four hooks ever ran (Y-309). Re-run `oom --vault <vault> install` to rewrite them.
- `--help` lists what each command does.

## 3.0.0 — 2026-09-12

A smaller program with no history attached. Everything the owner had not asked for was removed, then every line of prose in the repository was deleted and rewritten from zero.

### Removed
- Commands `bench` and `ingest`; the machine-scope installer, the state migration ladder, quota tracking, the Ollama backend, secret masking, notifications and the `calls` ledger.
- Single-file, self-contained and trimmed publish. `dotnet build` output is the deliverable.
- All previous documentation, release notes, audit reports, scar ledger and code comments. Old GitHub releases and tags remain as they were.
- Test Y-125, which checked the deleted scar ledger.

### Changed
- Source 12,770 → 7,300 lines; tests 261 → 105; state database 16 → 9 tables in one `CREATE` script, no `user_version`.
- A state database with an older schema is set aside as `state.db.eski-<timestamp>` and rebuilt; it is never migrated.
- `doctor` reads coverage from the sweep's own health row and says it is unmeasured before the first sweep.
- `install` writes only the project: `.oom/vault.json`, `.oom/oom.json` and four hooks in `.claude/settings.json`.
- Every model call leaves through one egress gate; `retrieve` no longer runs at session start, only on request or over MCP.
- Target framework net9.0.
- License: PolyForm Noncommercial 1.0.0.

### Added
- `nudge` — the UserPromptSubmit hook counts prompts and, every `nudgeEvery` prompts, asks the session to record what it learned. `reflectionMinPrompts` sets the minimum before a reflection is expected.
- `flush.mode` — `dilim` summarises only the last `flush.sliceTurns` turns of a session and skips sub-agent transcripts (`subagents/`, `agent-*.jsonl`); `tam` summarises every turn. Default `dilim`, 30 turns.
- Tests Y-300 to Y-308 over the behaviour above.
