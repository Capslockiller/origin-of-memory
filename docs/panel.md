---
yazan: codex
model: gpt-5.6-sol
---

# Local Brain operations panel

## What it is

`LocalBrain.exe` opens a two-tab graphical operations window without showing a
console. The launcher starts `beyin.ps1` hidden; PowerShell owns the local server
and any running child operation, so closing the browser does not stop work that
is already in progress.

The source-tree fallback treats the repository root as the vault. A deployed
copy uses `BEYIN_VAULT` and the scripts under `<vault>\.claude\scripts`. Python
resolution follows the rest of the project: `BEYIN_PYTHON`, `python`, then
`py -3`.

## The two tabs

**Health** consumes the documented `durum.py --json` contract. Its three rows
show component, status, last run, last error or skip, and the compile-owned
quarantine count. Status uses a word and symbol as well as colour. The lower
tables show the same seven-day call ledger summary: calls, median and p95
duration per backend, plus estimated input and output tokens per component.

**Today** parses `daily/<today>.md` session headings for the time and writer,
using canonical session-source comments and `ingest-state.json` when a heading
needs provenance. `last-flush.json` supplies the last flush. `compile-state.json`
supplies the last compile and confirms whether today's daily file was compiled.
The concept count is the number of concept notes carrying provenance for one of
today's compiled sessions. If there is no session today, the tab says so plainly.

## Operations

Every button presents a browser confirmation before sending its authenticated
request. Output and the final exit status stream as sequence-numbered SSE events
with replay:

- **Run the doctor** runs `durum.py --json`.
- **Compile now** runs `compile.py`.
- **Rebuild the index** runs `retrieve.py build` for the configured vault.
- **Watcher sweep once** runs `watcher.py --once`.

Only one operation runs at a time. The rest of the page remains readable, and
the panel refreshes both tabs when the operation ends.

## Security boundary

The server is a raw `TcpListener` bound to `127.0.0.1:0`. A 256-bit token is
carried only in the URL fragment, exchanged once, and replaced by a
`SameSite=Strict; HttpOnly` cookie. API calls require the exact Host, the exact
Origin when supplied, same-origin fetch metadata when present, the expected
method, JSON content type for bodies, and the session cookie. There is no CORS
header and no general file-serving route.

The HTML is self-contained. Its CSP is `default-src 'none'` with only same-origin
connections and inline page CSS/JavaScript. In particular, `connect-src 'self'`
is present so the panel can reach its own API.

The panel has no delete route, button, file-removal command, or user-supplied
command surface. Its route list is exact. The maintenance programs retain their
existing internal atomic state-file behavior, but the panel never asks any of
them to delete a note, file, or vault entry.

## Build and run

Build the small Windows GUI launcher from the repository root:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\build-launcher.ps1

The build uses only the inbox 64-bit .NET Framework `csc.exe`. It installs
nothing and fails loudly if the compiler is absent. Run `LocalBrain.exe`, or for
headless diagnostics run:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\beyin.ps1 -NoBrowser

`assets/localbrain.ico` is an original programmatic placeholder: a dark circular
field with a copper memory loop and three cream nodes. It does not copy or imitate
another company's mark and is intended to be replaced by final artwork later.

## Not built yet

- The three later tabs are not present; this version deliberately proves only
  Health and Today.
- Shortcut creation and installer wiring are separate work.
- The panel never deletes. There is no cleanup, quarantine release, note removal,
  or vault-entry removal operation in this or any planned tab.
