---
yazan: codex
model: gpt-5
---

# Installation

Origin of Memory 2.0 is still under rebuild. The installer code exists, but the clean Windows acceptance gate is red in the 2026-09-09 measurement. Use this page as an implementation record, not as a claim that installation is production-ready.

## Requirements

For a fully qualified vault path, `Install.Run` checks all prerequisites before writing:

- Windows 10 build 19041 or newer;
- a `claude.exe`, `claude.cmd`, or `claude.bat` entry resolvable from `PATH`;
- SQLite FTS5 support through `Microsoft.Data.Sqlite`.

The running program must be a published file named `oom.exe`; otherwise the binary-copy step fails. The project currently builds with .NET 9 and targets .NET 10 LTS after the D1 SDK transition.

## What `oom install` currently does

Given `<vault>`, the installer:

1. Creates `<vault>\.oom\`, `.oom\claude-config\`, and `.oom\quarantine\`.
2. Creates `%LOCALAPPDATA%\oom\<vault-hash>\` with `state.db`, `backup\`, and `logs\`.
3. Applies a current-user-only Windows ACL to `claude-config`, `quarantine`, the state root, and `backup`.
4. Writes missing `vault.json`, `oom.json`, and `hub-config.json`, preserving existing files.
5. Copies the running `oom.exe` into `<vault>\.oom\oom.exe` when it is not already there.
6. Creates the SQLite schema with WAL, a 5-second busy timeout, and schema version 1.
7. Merges four user-level Claude hooks into `%USERPROFILE%\.claude\settings.json`, after copying the old settings file into the local state backup directory.
8. Registers `OdenaOS Memory Sweep` by writing XML to a temporary file and invoking `schtasks.exe /Create /TN ... /XML ... /F`.
9. Adds an `oom` entry to the standard or MSIX Claude Desktop `claude_desktop_config.json`.
10. Creates a Start-menu shortcut carrying AppUserModelID `OdenaStudio.OriginOfMemory`.

The result object lists an Event Log registration, but no Event Log API or command is called in `Install.Run`. The installer also does not call `Doctor` at the end and does not populate the isolated Claude configuration with credentials or an empty settings file. Those steps are planned by the 2.0 install contract.

## Hooks

All four registrations are user-level commands containing an absolute path to `<vault>\.oom\oom.exe`; none calls PowerShell or Python.

| Event | Command | Timeout |
| --- | --- | ---: |
| `SessionStart` | `"<vault>\.oom\oom.exe" context` | 15 s |
| `UserPromptSubmit` | `"<vault>\.oom\oom.exe" retrieve --hook` | 5 s |
| `SessionEnd` | `"<vault>\.oom\oom.exe" flush --reason sessionend` | 15 s |
| `PreCompact` | `"<vault>\.oom\oom.exe" flush --reason precompact` | 15 s |

The templates contain the same four events. They do not put a session ID on the flush command; completing the hook-input-to-session binding is planned.

## Scheduled task

The registration mechanism implements D4: an XML file is passed to `schtasks /Create /XML`; Task Scheduler COM interop is not used. The XML produced inside `Install` currently specifies an eight-hour repetition and `WakeToRun`. It does not yet contain the fuller settings implemented separately by `Sweep.BuildScheduledTaskXml`—interactive token, run-if-missed, no battery restriction, 30-minute execution limit, and no repetition-duration element—so consolidating the two XML builders is planned.

## Toast registration

The installer creates `Origin of Memory.lnk` under the user's Start-menu Programs directory and stores AppUserModelID `OdenaStudio.OriginOfMemory` on the shortcut through `IShellLinkW` and `IPropertyStore`. This implements the D3 registration primitive. There is no explicit registry entry; uninstall deletes the shortcut.

If shortcut creation fails, `Install.Run` currently returns failure. The planned D3 behavior is to complete installation without toast and use only the next SessionStart notification line.

## `--uninstall`

`oom install --uninstall` currently removes:

- `.oom\oom.exe`, `vault.json`, `oom.json`, and `hub-config.json`;
- `.oom\claude-config\` and `.oom\quarantine\`;
- the entire `%LOCALAPPDATA%\oom\<vault-hash>\` state root;
- matching `oom.exe` entries from the four user hooks;
- the scheduled task, MCP entry, and Start-menu shortcut.

It does not address the Event Log source and does not touch `daily\` or `knowledge\`. Because it removes the state root and quarantine directory recursively, back up any evidence or local state you need before running it.

## `--from-v0`

`oom install --from-v0` currently creates `%LOCALAPPDATA%\oom\<vault-hash>\backup\v0-<timestamp>\RECOVERY.txt` before the normal install path. A test-only path containing `backup-fails` returns before writes.

The full migration remains planned. The current code does not import `compile-state.json`, flush cursors, sweep stamps, calls, rejected summaries, quarantine, or uncovered-session reconciliation; it does not rebuild v0 indexes; it does not remove the six exact v0 hook lines or `OdenaOS-Flush`; and it does not move legacy `.claude\scripts\` or `.claude\hooks\` into backup. The target contract preserves `daily\`, `knowledge\`, and Companion content and finishes with `oom doctor` and `oom bench` only after those migrations are implemented.

## Verification

After installation is completed in a later integration build, the acceptance sequence is: install on a clean Windows 11 VM, create a session, run `oom sweep`, verify an anchored daily block, force compile time with `OOM_FAKE_NOW`, verify concept/root-map/index output, then verify retrieval in a new session. That chain is not green in the current scar suite (`Y-069`).
