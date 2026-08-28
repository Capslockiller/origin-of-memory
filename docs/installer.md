---
yazan: codex
model: gpt-5.6-sol
---

# Windows Setup.exe

The native Windows installer is defined in
`installer/origin-of-memory.iss`. It is an Inno Setup installer with the normal
Back, Next, Cancel, installation-progress, and Finish experience.

## Build

Install Inno Setup yourself, then build from the repository root:

```powershell
winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\build.ps1
```

`build.ps1` looks for `ISCC.exe` on `PATH`, under both Program Files locations,
and under the usual per-user LocalAppData location. It never installs Inno
Setup. A successful build produces `installer/output/Setup.exe`.

## What Setup does

The page order is Welcome, read-only System check, conditional Python repair,
Vault location, Setup preset, conditional Local model, Ready, Installing, and
Finish. Each choice page asks for one decision and preselects the detected
recommendation.

System detection is not duplicated in Pascal Script. A child Windows
PowerShell process loads the function definitions from `kur.ps1` without
running its interactive entry point, then calls `Get-AutoDecision`. That call
continues to use `donanim.py`, `model_oneri.py`, and `kurulum_plani.py`.

Setup serializes the accepted answers into a complete plan JSON and runs:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Answers <plan.json>
```

`kur.ps1` remains responsible for strict validation, installation, runtime
work, environment values, MCP registration, and next-step guidance. Setup does
not bundle Python and does not recreate those rules.

When Python is absent or older than 3.12, the Python page offers winget, the
official python.org installer, or no action. Winget is preferred when present.
The fallback downloads Python 3.12.10 over HTTPS, verifies its Windows
Authenticode signer, and runs it silently with `InstallAllUsers=0`; it does not
request elevation. The manual option installs nothing and restricts the safe
continuation to the cloud preset until Python is available.

If Windows Documents is redirected (commonly to OneDrive), the existing plan
engine recommends `%USERPROFILE%\brain`, not the redirected Documents folder.
The Vault page states this in one line. If detection is unavailable, the same
local user-profile default is used conservatively.

The program files go under LocalAppData and `PrivilegesRequired=lowest` keeps
Setup per-user. Inno Setup registers the Add/Remove Programs entry. Its
uninstall phase calls `uninstall.ps1` with the saved vault-specific uninstall
plan before Inno removes the program files. The Finish page offers the desktop
shortcut and the local how-it-works document.

## What Setup does not do

- It does not install anything system-wide or request administrator rights.
- It does not bundle Python.
- It does not bypass Windows SmartScreen or weaken Windows security settings.
- It does not delete vault memory during uninstall; `uninstall.ps1` preserves
  memory content according to its existing policy.

The generated `Setup.exe` is unsigned. Windows SmartScreen may warn because the
publisher has no code-signing certificate or reputation. Review the warning
and obtain the installer from the project source; do not bypass or disable
SmartScreen.

## Unverified until a successful compile

The task started with `ISCC.exe` reported as unavailable. A later build-helper
preflight unexpectedly found Inno Setup 6.7.3 in its per-user LocalAppData
location. That invocation stopped during Pascal compilation and did not produce
`Setup.exe`; no further compile or installer run was performed. The following
therefore remain explicitly unverified:

- successful end-to-end Inno Setup compilation after the corrected UTF-8 file
  writer, and any later Pascal Script type/syntax diagnostics;
- actual `Setup.exe` creation, launch, page order, Back/Next/Cancel behavior,
  conditional-page visibility, control layout, DPI scaling, and progress UI;
- pre-install extraction and child-PowerShell detection on a clean Windows VM;
- winget success, winget-to-python.org fallback, download progress,
  Authenticode verification, silent per-user Python installation, and PATH
  refresh after installation;
- plan JSON creation and the real `kur.ps1 -Answers` run from packaged files;
- local-runtime installation and model download behavior;
- OneDrive/redirected-Documents UI behavior on an affected Windows account;
- Add/Remove Programs metadata, the desktop shortcut, both Finish checkboxes,
  and the complete `uninstall.ps1` hand-off;
- unsigned SmartScreen behavior on representative Windows versions.

The Python tests only validate static installer invariants and source-file
completeness. They cannot replace the first compile and clean-VM install test.

## What Windows will do the first time

The output is unsigned, and a freshly compiled binary has no reputation, so
expect two prompts and know the difference:

- **SmartScreen** — "Windows protected your PC". Click *More info* to see the
  publisher and run it anyway. This is reputation, not a detection.
- **Defender's "send a sample?"** — cloud protection asking permission to upload
  the file to Microsoft for analysis. It is not a quarantine and nothing was
  found. Sending it is harmless for this project (the code is MIT and carries no
  secrets) and it helps the hash build reputation, but it does upload a file to
  a third party, so it is the operator's call.

Neither is bypassed by this project and neither should be. Do not add a Defender
exclusion to silence them; the honest fixes are code signing, which costs money,
or accepting the prompt.
