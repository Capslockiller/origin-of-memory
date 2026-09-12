# Changelog

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
