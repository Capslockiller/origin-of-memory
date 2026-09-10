---
yazan: codex, opus
model: gpt-5, opus-5
---

# Installation

Origin of Memory 2.0 is still under rebuild. This page is an implementation record of what `oom install` does today, not a claim that installation is production-ready: the clean Windows acceptance chain (`Y-069`) is still red in the 2026-09-09 measurement.

## Requirements

For a fully qualified vault path, `Install.Run` checks all prerequisites before writing:

- Windows 10 build 19041 or newer;
- a `claude.exe`, `claude.cmd`, or `claude.bat` entry resolvable from `PATH`;
- SQLite FTS5 support through `Microsoft.Data.Sqlite`.

The running program must be a published file named `oom.exe`; otherwise the binary-copy step fails. The project currently builds with .NET 9 and targets .NET 10 LTS after the D1 SDK transition.

## What `oom install` does

Given `<vault>`, the installer:

1. Creates `<vault>\.oom\`, `.oom\claude-config\`, and `.oom\quarantine\`.
2. Creates `%LOCALAPPDATA%\oom\<vault-hash>\` with `state.db`, `backup\`, and `logs\`.
3. Applies a current-user-only Windows ACL to `claude-config`, `quarantine`, the state root, and `backup`.
4. Writes missing `vault.json`, `oom.json`, and `hub-config.json`, preserving existing files.
5. Writes `claude-config\settings.json` as `{}` when it is missing — an empty file is the isolation spec 6.6 asks for: no hooks, no plan mode, no skills. An existing file (which may carry the credential link) is left alone.
6. Copies the running `oom.exe` into `<vault>\.oom\oom.exe` when it is not already there.
7. Creates the SQLite schema with WAL, a 5-second busy timeout, and schema version 1.
8. Merges the four user-level Claude hooks into `%USERPROFILE%\.claude\settings.json`, after copying the old settings file into the local state backup directory. The merge replaces only entries whose command names `oom.exe`; another tool's hook registered under the same event is carried over untouched.
9. Registers `OdenaOS Memory Sweep` by writing `Sweep.BuildScheduledTaskXml` to a temporary file and invoking `schtasks.exe /Create /TN ... /XML ... /F`.
10. Adds an `oom` entry to the standard or MSIX Claude Desktop `claude_desktop_config.json`.
11. Attempts the Start-menu shortcut carrying AppUserModelID `OdenaStudio.OriginOfMemory`, then the Event Log source.
12. Runs `Doctor` and keeps its findings on `Install.Health`.

`InstallResult.Registrations` now lists only what actually happened. Every entry is appended at the moment its step succeeds, so the result can no longer claim a registration the machine refused.

## Hooks

The four registrations come from `HookTemplates.Build`; `Install` consumes that template rather than restating it, so the two cannot drift. All four are user-level commands containing an absolute path to `<vault>\.oom\oom.exe`; none calls PowerShell or Python.

| Event | Command | Timeout |
| --- | --- | ---: |
| `SessionStart` | `"<vault>\.oom\oom.exe" context` | 15 s |
| `UserPromptSubmit` | `"<vault>\.oom\oom.exe" retrieve --hook` | 5 s |
| `SessionEnd` | `"<vault>\.oom\oom.exe" flush --reason sessionend` | 15 s |
| `PreCompact` | `"<vault>\.oom\oom.exe" flush --reason precompact` | 15 s |

The flush commands still carry no `--session <id>`, which spec 5 asks for. The template belongs to lane B; `Install` writes it as it is and the gap is recorded in `progress.md` as a `Blocked-by:` line. `HookTemplates.LaunchDetached` already passes both `--session` and `--reason` to the detached child, so only the registered command string is short.

## Companion file markers

`context` does not read a companion file whole. Three of the five sections are cut out of the file by a heading, and a file that carries the text but not the heading produced an empty section with no explanation (clean-Windows Sandbox run, gate 7). The markers are part of the contract:

| Section | File | Marker | What is taken |
| --- | --- | --- | --- |
| Son Oturum | `Last-Session.md` | `## Session:` | from the first such line, 49 lines |
| Aktif Threadler | `Threads.md` | `## Active` | after it, every `### ` line and every line carrying `**Status:**`, 12 lines |
| Kurallar | `Kurallar.md` | — | the first 60 lines |
| Düzeltmeler | `Duzeltmeler.md` | — | the first 30 lines |
| Son Journal | `Journal.md` | `## ` | from the **last** `## ` heading, 10 lines |

The companion directory itself is `context.companionDir` in `oom.json` (default `🔮 850-Companion`). When a file is present but its marker is not, `context` now writes one line to stderr per section — `context: '<bölüm>' boş — dosyada '<marker>' başlığı yok` — and the block is built exactly as before. `Context.CompanionWarnings` returns the same lines for `doctor` and for tests.

## Local backend context window

`backend.local.numCtx` in `oom.json` (default `8192`) is the token context window `Runner.CallLocal` asks Ollama's native `/api/chat` route for — the one route that actually honours it (`/v1/chat/completions` drops the option silently, `Y-115`). Raise it when a daily or a transcript regularly runs into an HTTP 400 "context length" rejection; the prompt itself is also capped to roughly three characters per token of this same window before it is sent, so one oversized daily cannot exceed it. There is no separate `backend.local.fast`/`smart` context split — one window serves both tiers.

## Scheduled task

Install and `Sweep` now share a single XML builder, `Sweep.BuildScheduledTaskXml` (D4: an XML file passed to `schtasks /Create /XML`; no Task Scheduler COM interop). The XML has an eight-hour repetition with **no** `Duration` element (`Y-009`), `StartWhenAvailable` for a missed run, an `InteractiveToken` logon type so the task only runs while the user is signed in, a 30-minute `ExecutionTimeLimit`, and no battery restriction. The second, thinner XML that used to live inside `Install` (and carried `WakeToRun`) is gone.

## Toast registration

The installer creates `Origin of Memory.lnk` under the user's Start-menu Programs directory and stores AppUserModelID `OdenaStudio.OriginOfMemory` on it through `IShellLinkW` and `IPropertyStore` (`Install/ShortcutRegistration.cs`).

If that fails, **installation still succeeds** (D3). The result carries `shortcut:atlandı` instead of `aumid:…`/`shortcut`, a `Warning`/`toast-kaydi-yok` health item is recorded in the ledger and on `Install.Health`, and `Install.ToastRegistered` is false. `Notify` reads the same shortcut: with no AUMID registration it returns `ContextQueued` with `ToastSent` false, so the notification reaches the user only through the next SessionStart line — the fallback spec 6.8 already describes.

The shortcut's presence is the machine's own answer to "can a toast fire here?", which is why `Notify` asks it rather than trusting a flag.

## Event Log

The Event Log source lives under `HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\oom`. Reading that key needs no elevation; creating it does. So the installer reads first, creates only when the process is already elevated, and otherwise **skips it silently** and records an `Info`/`event-log-atlandi` item saying that `logs\` suffices — spec 6.11's own fallback. `event-log:oom` appears in `Registrations` only when the source really exists. The installer never elevates itself.

## `--uninstall`

`oom install --uninstall` reverses the install and **never deletes evidence**.

Removed outright (all of them reproducible from the release or from the vault):

- `.oom\oom.exe`, `vault.json`, `oom.json`, `hub-config.json`;
- `.oom\claude-config\`;
- the four `oom.exe` hook entries, the scheduled task, the MCP entry, the Start-menu shortcut and its AUMID, and the Event Log source when this process may remove it.

Moved, not deleted:

- `<vault>\.oom\quarantine\` → `%LOCALAPPDATA%\oom\backup\<vault-hash>-uninstall-<ts>\quarantine\`
- `%LOCALAPPDATA%\oom\<vault-hash>\` (state.db, backup, logs) → `%LOCALAPPDATA%\oom\backup\<vault-hash>-uninstall-<ts>\state\`

The archive path is returned in `Registrations` as `kanıt:<path>`. A move is used, with a file-by-file copy as the cross-volume fallback. `daily\` and `knowledge\` are not touched at all, and neither is the Companion directory. Deleting the archive is a decision for the operator, not for the uninstaller.

## `--from-v0`

`oom install --from-v0` runs the spec 13 migration. Every step is idempotent and reports one line; `oom install --from-v0 --dry-run` runs the identical code path, writes nothing, and prints the plan.

Order of the twelve steps, each of which reports `kaynak yok` when there is nothing to do:

1. **Backup gate.** When the vault is a git work tree, `git stash create` makes a commit object without touching the working tree and `refs/oom/v0-<ts>` keeps it alive; otherwise only the v0-owned files plus `.state` and `__pycache__` directories are copied into `backup\v0-<ts>\`. Either way a `RECOVERY.txt` marker is written and verified — nothing else runs until it exists. A vault path containing `backup-fails` returns before any write (`Y-074`).
2. `compile-state.json` → `daily_ingest` (`ingested`, `rejected`, `parked`, `quarantined` become the `status` column).
3. `flush-<sha256>.json` state files → `sessions` (transcript paths come from `mutabakat.json`).
4. `flush-tara.json` → `sweep_stamps` (epoch `mtime` becomes an ISO string; an incomplete stamp becomes `partial`).
5. `calls.jsonl` → `calls`.
6. `.stage\karantina\` → `<vault>\.oom\quarantine\` plus a `quarantine` row per file, keyed by the file's SHA-256.
7. `red\` rejected summaries → `retry_queue`, one row per session id parsed out of the file name.
8. `mutabakat.json` → one `coverage` row whose `uncovered_json` lists the sessions v0 never summarised; spec 6.3 makes the next sweep prioritise exactly that list, so those sessions are queued for the first ingest rather than replayed here.
9. The **six** v0 hook commands (`session-start`, `prompt-counter`, `memory-retrieve`, `flush-launch -Reason sessionend`, `session-end`, `flush-launch -Reason precompact`) are removed from the user `settings.json` **by exact string match** against v0's `powershell -NoProfile -ExecutionPolicy Bypass -File "<vault>\.claude\hooks\<script>"` form. Anything else in the file survives. The four 2.0 hooks are written by the normal install step.
10. The v0 scheduled task `OdenaOS-Flush` is deleted; `OdenaOS Memory Sweep` is registered by the normal install step. Any other scheduled task whose command points into `<vault>\.claude\scripts` is reported as `dokunulmadı, elle karar` and is not changed.
11. Only the explicit v0 ownership manifest (sourced from predecessor commit `fa9e41f`) plus `.state` and `__pycache__` directories is moved into `backup\v0-<ts>\claude-scripts\` and `…\claude-hooks\`. Other files remain byte-for-byte at their original paths and are listed as `korunan (v0 dışı): …`; a tree that still contains such files remains in place (`Y-111`).
12. `daily\`, `knowledge\` and the Companion directory are not touched.

Re-running the migration is safe: the keyed tables use `INSERT OR REPLACE`, `calls` and `coverage` are guarded by the first timestamp they would insert, and every file-moving step reports `kaynak yok` once its source is already in the backup.

Rebuilding `notes.db`, `index-full.md` and `hubs\` (also part of spec 13) is not done by the migration: those are derived artefacts and `oom doctor --fix` / the first compile regenerate them.

## `--adopt`

`oom install --vault <vault> --adopt` is for a vault whose `daily/` and `knowledge/` were copied in from another, already-compiled vault (not migrated with `--from-v0`) and then given a clean `oom install`. The fresh `state.db` knows nothing about that history, so every copied daily reads as uncompiled and `oom sweep`/`oom compile` would recompile all of them, duplicating the concepts already in `knowledge/`.

`--adopt` runs no other install step. It reads the newest `created`/`updated` frontmatter stamp across every valid note in `knowledge/concepts/`, then marks every `daily/*.md` at or before that stamp as `adopted` in `daily_ingest` — a status distinct from a real `ingested` compile, so a forced recompile (clearing the row by hand, or `--fix`) can still run. A daily newer than the stamp is left alone and stays pending, because `knowledge/` has not caught up to it yet. Nothing under `daily/` or `knowledge/` is read for anything but that one timestamp, and nothing there is written or deleted. The same call also writes the vault's own `installed_at` stamp (`vault_meta` table) if one is not already recorded — the window `oom doctor`'s coverage numbers (Y-118) measure from.

## Verification

The acceptance sequence is unchanged: install on a clean Windows 11 VM, create a session, run `oom sweep`, verify an anchored daily block, force compile time with `OOM_FAKE_NOW`, verify concept/root-map/index output, then verify retrieval in a new session. That chain is still red in the scar suite (`Y-069`).
