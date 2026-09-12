# Origin of Memory

[![CI](https://github.com/Capslockiller/origin-of-memory/actions/workflows/ci.yml/badge.svg)](https://github.com/Capslockiller/origin-of-memory/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Capslockiller/origin-of-memory)](https://github.com/Capslockiller/origin-of-memory/releases)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)
[![License: PolyForm Noncommercial](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0.0-blue)](LICENSE)

Persistent memory for Claude Code sessions, kept as plain Markdown in an Obsidian vault.

`oom` is a single .NET 9 console executable. It hooks into Claude Code, summarises each session into a daily log, compiles the daily logs into concept notes, and injects the relevant memory back at the start of the next session. Summaries are produced by the `claude` CLI on the same machine; nothing else is called.

## How it works

1. **Session start** — `context` prints today's log, the knowledge index and the companion files into the session as additional context.
2. **During the session** — `nudge` counts prompts and periodically asks the session to record what it learned.
3. **Session end / compaction** — `flush` summarises the transcript with the fast model and appends a block to `daily/YYYY-MM-DD.md`.
4. **`oom sweep`** — finds transcripts that changed on disk and flushes the ones the hooks missed. Nothing schedules it; run it yourself or from Task Scheduler. `sweep.everyHours` stops it doing the work twice.
5. **`oom compile`** — folds the daily logs into `knowledge/` concept notes with the smart model and rebuilds the root map. Same: run it, it decides whether it is due (`compile.eveningHour`, `minIntervalHours`).
6. **On request** — `retrieve` or the MCP tools search the notes with BM25 and return a bounded excerpt.

Every file it writes is Markdown you can read and edit; the SQLite state is a cache that can be deleted at any time.

## Requirements

- Windows 10/11
- .NET 9 SDK
- [Claude Code](https://docs.anthropic.com/en/docs/claude-code) with the `claude` CLI on `PATH`
- An Obsidian vault, or any folder of Markdown files

## Install

```
dotnet build Oom.sln -c Release
oom --vault <vault> install
```

Run `install` from wherever the build lives; the hooks point at that executable. It writes `<vault>\.oom\vault.json` and `<vault>\.oom\oom.json` (defaults below, never overwritten) and adds four hooks to `<vault>\.claude\settings.json`:

| Hook | Command |
|---|---|
| SessionStart | `oom context` |
| UserPromptSubmit | `oom nudge` |
| SessionEnd | `oom flush --reason sessionend` |
| PreCompact | `oom flush --reason precompact` |

`install --uninstall` removes the hooks. The only file outside the vault is the SQLite state under `%LOCALAPPDATA%\oom\<vault-hash>\`; it is a cache and can be deleted.

## Commands

```
oom [--vault <path>] <command>
```

| Command | What it does |
|---|---|
| `context [--json]` | Prints the session-start block: today's log, the knowledge index, and the companion files |
| `nudge [--session <id>]` | Counts prompts; every `nudgeEvery` prompts reminds the session to record what it learned |
| `flush [--session <id>] [--reason <r>]` | Summarises one session transcript into `daily/YYYY-MM-DD.md` |
| `sweep [--dry-run]` | Finds transcripts under the configured roots and flushes the ones that changed |
| `compile [--dry-run]` | Folds daily logs into `knowledge/` concept notes and the root map |
| `retrieve --query <q> [--json] [--top N] [--batch <file>]` | BM25 search over the notes; returns at most `retrieve.totalChars` |
| `doctor [--fix] [--json] [--quiet]` | Health table: coverage, pending dailies, queue, quarantine, config warnings |
| `save "<text>" \| --session-json` | Appends a record to today's daily directly |
| `mcp` | Read-only MCP server over stdio with `memory_search`, `memory_root_map`, `memory_note` |

## Configuration — `.oom/oom.json`

| Key | Default | Meaning |
|---|---|---|
| `backend.claude.fast` / `.smart` | `claude-haiku-4-5-20251001` / `claude-sonnet-5` | Models used for flush and compile |
| `sweep.roots` | `~\.claude\projects`, `~\.codex\sessions` | Where transcripts are looked for |
| `sweep.everyHours` / `sinceHours` / `minTurns` / `maxSessionsPerRun` | 8 / 8 / 3 / 20 | Sweep cadence and limits |
| `flush.mode` | `dilim` | `dilim`: summarise only the last `flush.sliceTurns` turns and skip sub-agent transcripts. `tam`: summarise every turn |
| `flush.sliceTurns` | 30 | Turns kept per session in `dilim` mode |
| `compile.eveningHour` / `minIntervalHours` / `maxDailiesPerRun` | 18 / 20 / 3 | When and how much to compile |
| `context.companionDir` / `capChars` | `🔮 850-Companion` / 16000 | Companion folder and size cap of the injected block |
| `retrieve.top` / `perNoteChars` / `totalChars` | 3 / 1500 / 4500 | Retrieval limits |
| `nudgeEvery` / `reflectionMinPrompts` | 15 / 5 | Nudge cadence and minimum prompts before a reflection is asked for |
| `mcp.enabled` | true | Whether `oom mcp` serves |

## Vault layout

- `daily/` — one file per day, one block per flushed session, anchored with the session id and turn range
- `knowledge/` — compiled concept notes, hubs, and the root map
- `<companionDir>/` — hand-written files injected at session start (Core, Last-Session, Threads, Journal)
- `.oom/` — `oom.json` and `vault.json`

## Development

```
dotnet build Oom.sln -c Release
dotnet test Oom.sln -c Release
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for how changes are made and [SECURITY.md](SECURITY.md) for reporting a vulnerability.

## License

[PolyForm Noncommercial 1.0.0](LICENSE). Anyone may use, change and share it for noncommercial purposes. Commercial use requires a separate commercial license from Odena Studio: odenastudio@gmail.com.
