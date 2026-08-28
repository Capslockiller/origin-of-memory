---
yazan: codex
model: gpt-5.6-sol
---

# Browser setup wizard: phase one

## What exists

kur-gui.ps1 is a Windows PowerShell 5.1 launcher and minimal HTTP server. It
needs neither Python nor administrator access to open the wizard. It binds a
raw TcpListener to 127.0.0.1 on an operating-system-selected port and serves
only gui/kur.html plus an explicit API route list.

The one implemented screen is **System check**. It is read-only. GET
/api/detect starts a child Windows PowerShell process which loads the detection
functions from the existing kur.ps1 without running that script's interactive
entry point, then calls Get-DecisionContext. Consequently the existing
donanim.py and model_oneri.py calls, command probes, Documents resolution, and
Claude Desktop config detection remain the source of truth.

If Python is absent, the launcher still starts. It uses kur.ps1's existing
Find-PythonCommand check, reports Python as Required, and marks the Python-backed
checks Unavailable until Python exists; it does not invent replacement probes.

Detection runs independently of the browser connection. Its start, output,
result, and completion become sequence-numbered JSON Server-Sent Events.
GET /api/events accepts Last-Event-ID and replays retained events after that
number. Responses are deliberately short-lived so the single-threaded raw TCP
server is never held by one connection; the page reconnects while the child
operation continues.

## Security boundary

- The listener is IPv4 loopback only: never a wildcard, never a firewall rule.
- A 256-bit token travels in the URL fragment (which HTTP does not send), is
  exchanged once, and becomes a SameSite=Strict; HttpOnly session cookie.
- Every API route validates the exact loopback Host and Origin, JSON media type,
  method, and (except the token exchange) session cookie. There are no CORS
  headers and there is no filesystem-serving path.

The HTML response also sets a restrictive Content Security Policy. The page is
one self-contained file with inline CSS/JavaScript and makes no external
requests. Unknown, encoded-parent, and parent-directory request targets are
404s because only exact route strings are accepted.

## Run

From the repository root:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\kur-gui.ps1

The launcher first tries msedge.exe with its app argument set to the generated
URL. If Edge cannot be found or launched, it asks Windows to open the URL in the
default browser. If that also fails, it prints the complete URL for manual
opening. Failures are reported rather than hidden. -NoBrowser is available for
automated/headless testing and prints a GUI_READY URL.

After an operation finishes, the server remains available for a short grace
period and then exits. The Quit button requests shutdown. If an operation is in
flight, shutdown waits for it; closing the browser merely disconnects event
polling and does not terminate the child.

## Explicit phase-one limits

Phase one does not collect choices, build a plan, show a confirmation screen,
install Python, install the memory system, or enable Next/Back navigation.

The authenticated POST /api/install plumbing can launch the existing
kur.ps1 -Answers plan.json child and stream its stdout/stderr as events, but the
page has no control that calls it. This route proves the process boundary; it is
not yet a user flow.

Phase two must add the remaining mouse-driven screens, validation and plan
generation, an exact plan review/confirmation step, installation consent,
progress presentation for installer events, retry/recovery states, and working
Next/Back navigation. It must keep kur.ps1 as the installation authority.
