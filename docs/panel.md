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

<!-- yazan: claude · sonnet -->
Below the tabs and above the operation buttons, the **Nezaket** card shows the
A7 politeness gate's current decision (Meşgul/Serbest and its reason) and the
deferred-operation queue, polled from the page every 10 s
(`GET /api/nezaket`) with no new persistent process. Checking rows and
pressing "Seçilenleri çalıştır" (`POST /api/action/nezaket-serbest`) never
drops work: if another operation is already running it responds
`409 operation_in_progress` before touching the queue at all, and otherwise
it releases exactly the **first** checked id and starts it, leaving every
other checked id queued untouched. The card shows a note naming how many
selected records are still queued when the response's `remaining_selected`
is greater than zero — check the button again once the running operation
finishes to release the next one. See [nezaket.md](nezaket.md) for the gate
itself.

<!-- yazan: claude · sonnet -->
Below the Nezaket card, the **Pasaport** card is F4 part 2's approval
surface, polled every 5 s (`GET /api/pasaport`): the clipboard listener's
status (running/stopped, last event) from its heartbeat file, the single
pending `[ODENA-DONUS]` candidate — if any — rendered read-only as its
surviving bullet units, dropped-duplicate count, and warnings, with a short
form of its `raw_hash` so the reviewer sees exactly what they are about to
approve, and the aggregated ISTEK "kör nokta haritası" (blind-spot map).
"Onayla → günlüğe" (`POST /api/action/pasaport-onayla` with `{ raw_hash }`)
writes the candidate to the daily log and spawns compile — the same
409-before-anything-starts rule as every other operation, streamed through
the same SSE `operation-output` log with its own `PASAPORT-SONUC ` result
marker (compile's own stdout is inherited, exactly like Kaydet's spawn).
"Reddet" runs synchronously (it never touches the daily log or compile, so
it needs no operation slot) and "Panodan al" is the manual fallback when
the listener is not running, reading the clipboard once
(`pano_izleyici.py --once`) through the normal SSE operation path. Both
approve and reject refuse a stale `raw_hash` — a candidate a newer paste
already replaced — rather than silently acting on the wrong text. The
listener itself is spawned hidden as beyin.ps1's child when the panel
starts (`BEYIN_PASAPORT_IZLEYICI=off` disables it) and stopped in the
panel's single shutdown path, covering both an explicit quit and idle
timeout; it is never restarted if it exits on its own. See
[pasaport.md](pasaport.md) for the parser, the gate pipeline, and the
listener itself.

<!-- yazan: claude · sonnet -->
Above the tabs, the **Kaydet** card is the golden path: type into the
textarea, add an optional title, and press "Kaydet" (`POST
/api/action/kaydet` with `{ metin, baslik }`). The note text travels to
`kaydet.py --stdin --json` over stdin, never as a command-line argument, so
it never reaches a process listing or shell history. Like every other
operation it runs through the same SSE `operation-output` stream and the
same 409-before-anything-starts rule, and the button disables itself while
any operation — including its own — is in flight. The draft lives only in
the textarea's own value; the panel never writes it to `localStorage`, and
a successful `202` clears both fields immediately, since the note is
already durably on disk by the time the operation starts — Kaydet writes
before it ever spawns the compile that follows. See
[kaydet.md](kaydet.md) for the full save-then-compile contract, including
why that compile bypasses the A7 nezaket gate.

<!-- yazan: claude · sonnet -->
Below Pasaport, the **Kokpit** card is F5 part 2's face on the tower
(`scripts/kule.py`): a lane meter and count-per-status row read straight
from `kule/durum.json` (polled every 5 s, `GET /api/kule`, guarded against
stacking two in-flight requests), an "İş ver" form
(`POST /api/action/kule-is-ver` with `{ tur, model, prompt, cwd?, izlenen?,
izin? }`, prompt piped to `kule.py` over stdin) whose submit button disables
itself locally while its own request is in flight — deliberately **not**
wired into the shared SSE `setActive()` sweep, since this is not an SSE
operation — and a job list (last 20) with per-row Log/İptal/Diff/Onayla/
Reddet actions. None of Kokpit's routes touch the panel's single
operation-at-a-time slot: kule runs its own multi-lane background worker,
so `/api/action/kule-is-ver`/`kule-onayla`/`kule-reddet`/`kule-iptal`/
`kule-vscode` all answer synchronously instead of streaming through SSE.
"VS Code'da aç" on a waiting-approval job's diff runs
`code --diff <once> <sonra>` when `code` is found on `PATH` (or under the
usual `LOCALAPPDATA`/`ProgramFiles` install locations); when it isn't, the
same stored diff text is shown read-only instead — the panel never
recomputes a diff either way. The tower child is spawned hidden when the
panel starts and stopped (`dur` file, then a graceful wait, then `Kill()`)
in the same single shutdown path as the pasaport listener
(`BEYIN_KULE=off` disables it). See [kokpit.md](kokpit.md) for the full job
lifecycle, the diff-approval flow, and the route contract.

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

- ~~Shortcut creation and installer wiring are separate work.~~ Done since
  v0.4.1: the installer creates the desktop shortcut and the `Local Brain.cmd`
  entry point (see `docs/installer.md`).
- Model installation/runtime installation is not exposed; a stopped or absent
  Ollama is reported rather than simulated or silently installed.
- Model deletion is deliberately absent because the panel's hard safety boundary
  permits no route or command that removes data. There is also no cleanup,
  quarantine release, note removal, or vault-entry removal operation.
- The smoke test is not a chat: it has no editable prompt, conversation history,
  multi-turn state, or remote browser request.
