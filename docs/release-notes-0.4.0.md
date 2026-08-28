# Release notes — 0.4.0

The release where the project stopped being a terminal.

## A real installer

`Setup.exe`, built with Inno Setup: the familiar Back / Next / Cancel window,
an Add/Remove Programs entry, a desktop shortcut. **Per-user — it never asks
for administrator rights and installs nothing system-wide.** `kur.ps1` remains
the installation authority; the wizard collects answers, writes a plan and
hands it over, and detection calls the existing code rather than a copy of it.

The Ready page itemises exactly what will happen before anything happens, and
the vault default deliberately avoids a Documents folder redirected into
OneDrive — a local-first memory vault silently landing in a cloud-synced folder
is wrong, and the wizard says so on screen.

Three defects were found by compiling it and then actually opening it, none of
which a compiler or a static test could see: `{userprofile}` is not an Inno
constant; `dontcopy` ignores `DestDir`, so the probe tree had to be assembled in
code; and with `DisableDirPage` the `{app}` constant is not initialised while a
custom page is on screen. Compiling is not evidence.

**The output is unsigned**, so Windows shows a SmartScreen prompt. The wizard
never tries to bypass it, and the documentation says so plainly.

## Local Brain

A window instead of a terminal. It opens from a shortcut through a console-free
launcher built with the inbox `csc.exe`, and runs on a loopback server bound to
`127.0.0.1` with a single-use token, exact Host and Origin checks and a strict
CSP. The page makes no external request of any kind.

**Health** is the live form of what `beyin doktor` reports, read through
`durum.py --json` because that contract was already documented as stable for
exactly this. **Today** shows the day's sessions, the last flush and the last
compile, and says so plainly when the day is empty. Four operations — doctor,
compile, index rebuild, watcher sweep — each confirm before running and stream
over SSE, so closing the browser drops the view and not the work.

**Nothing in the panel deletes anything.** There is no delete route, no general
filesystem route and no user-supplied command surface, and a test greps the
server for file-removal primitives so the guarantee cannot rot quietly.

## Fixed

The browser wizard shipped in 0.3.0 could not talk to its own API: the CSP set
`default-src 'none'` with no `connect-src`, and the API demanded an `Origin`
header and a JSON content type on every route — a browser sends neither on a
same-origin GET. Both were written to a spec no browser obeys, and the Python
tests could not see either, because urllib sends what a test hands it while a
browser decides for itself.

An idle wizard also never exited: the shutdown deadline was armed only after an
operation completed, so an abandoned window held a loopback port indefinitely.
One run outlived its 600 second grace by hours.

The preset captions still claimed Claude powers capture, which the watcher made
false in 0.3.0. A correction has to reach every copy.

## Still open, on purpose

- The panel's Local models tab is not in this release.
- Compile, index rebuild and watcher actions exist but were not exercised
  against real memory during review; only the doctor was run end to end.
- The installer's Python remediation and local-runtime install paths were not
  exercised.
- Tool-free compile and hybrid retrieval both remain unmeasured, so both remain
  off by default.
