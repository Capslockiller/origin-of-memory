---
yazan: codex
model: gpt-5.6-sol
---

# Local Brain operations panel

## What it is

`LocalBrain.exe` opens a three-tab graphical operations window without showing a
console. The launcher starts `beyin.ps1` hidden; PowerShell owns the local server
and any running child operation, so closing the browser does not stop work that
is already in progress.

The source-tree fallback treats the repository root as the vault. A deployed
copy uses `BEYIN_VAULT` and the scripts under `<vault>\.claude\scripts`. Python
resolution follows the rest of the project: `BEYIN_PYTHON`, `python`, then
`py -3`.

## The three tabs

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

<!-- yazan: codex · gpt-5.6-sol -->
**Local models** presents four evidence blocks in a fixed order:

1. **This computer** runs `python scripts/donanim.py --json`. RAM, CPU identity
   and core counts, GPU identity and VRAM, model-store path, and free disk are
   displayed directly from that JSON contract. The panel does not probe the
   hardware independently.
2. **What fits** passes that captured probe to
   `python scripts/model_oneri.py --json --probe-json <probe>`. It preserves the
   returned ranking, model tag, `size_gb`, verdict (`fits-gpu`, `tight`,
   `cpu-ok`, or `no-fit`), and `why` text. The panel has no second catalogue.
3. **Installed models** calls Ollama's loopback-only
   `GET {BEYIN_OLLAMA_URL}/api/tags`; the default base is
   `http://localhost:11434`. Names, byte sizes, modification times, and digests
   come from Ollama. If the command is absent or the API is unreachable, the
   tab distinguishes not installed from installed-but-not-running and does not
   display an invented empty inventory. Non-loopback URLs and HTTP redirects
   are refused, so these direct requests cannot escape the local machine.
4. **Active backend** imports `claude_runner` and uses `resolve_backend`,
   `compile_backend`, and the runners' model resolution to show the next text
   backend, compile fallback, and resolved fast/smart slugs.

If Python is unavailable, the hardware, recommendations, resolved backend, and
smoke test explicitly become unknown and explain why; zero is never substituted
for a missing measurement. Ollama inventory remains an independent, truthful
loopback check.

## Operations

Every button presents a browser confirmation before sending its authenticated
request. Output and the final exit status stream as sequence-numbered SSE events
with replay:

- **Run the doctor** runs `durum.py --json`.
- **Compile now** runs `compile.py`.
- **Rebuild the index** runs `retrieve.py build` for the configured vault.
- **Watcher sweep once** runs `watcher.py --once`.

The Local models actions use the same confirmation and SSE event stream:

- **Pull** confirms the exact model, catalogue size, and 1.5× disk preflight.
  The server re-runs `donanim.py` and `model_oneri.py`, refuses an unknown model,
  unknown free space, or insufficient space with the measured free/required GB,
  then streams Ollama `/api/pull` NDJSON. `completed` and `total` are shown as
  bytes. **Cancel pull** closes only the request process; it removes nothing, so
  Ollama's partial download remains resumable.
- **Switch backend** first shows the exact
  `BEYIN_MODEL_BACKEND=<claude|antigravity|ollama|openai-compat>` change and its
  storage location. Only after confirmation does PowerShell write the Windows
  **User** environment (`HKCU\Environment`) and update the panel process. The
  response and SSE completion event repeat the new value and exit status, so a
  setting read by the next pipeline process never changes behind the owner.
- **Try it** confirms the selected backend and fast/smart tier, then sends one
  fixed short prompt through `claude_runner.run_claude`. It records no history
  and displays one answer, wall-clock latency, resolved model slug, backend, and
  exit status. This is a smoke test, not a chat surface.

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

The panel has no delete route, button, file-removal command, model-removal API,
or user-supplied
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

- Shortcut creation and installer wiring are separate work.
- Model installation/runtime installation is not exposed; a stopped or absent
  Ollama is reported rather than simulated or silently installed.
- Model deletion is deliberately absent because the panel's hard safety boundary
  permits no route or command that removes data. There is also no cleanup,
  quarantine release, note removal, or vault-entry removal operation.
- The smoke test is not a chat: it has no editable prompt, conversation history,
  multi-turn state, or remote browser request.
