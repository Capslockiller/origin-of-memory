# Installation in full

> Türkçe: [install.tr.md](install.tr.md) · Quickstart:
> [../README.md](../README.md#quickstart)

The three-command quickstart in the README is the whole story for most people.
This page is everything underneath it: the presets, the non-interactive paths,
the lower-level installer, what the installer actually writes, and how to take
it back off.

The plan file's validation rules and auto-decision logic live in
[setup-wizard.md](setup-wizard.md); this page is the operational side.

---

## The wizard

```powershell
git clone https://github.com/Capslockiller/origin-of-memory.git
cd origin-of-memory
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1
```

Two screens. The first detects Claude Code, Ollama, LM Studio, llama.cpp, vLLM,
your hardware, Documents redirection and Claude Desktop's MCP configuration, and
proposes a preset. The second shows the resulting plan before anything is
written. Press Enter twice for the recommended path; choose `Custom` only when
you want to override something it detected.

The wizard also ranks verified Ollama tags by hardware fit, can install Ollama
after explicit consent, and prints manual GUI instructions for LM Studio rather
than pretending it can automate a desktop app.

## Presets

| Preset | Capture and compile | Flush / ingest | Read access |
| --- | --- | --- | --- |
| `cloud` | Claude Code hooks + Claude compile | Claude | Hooks; optional MCP / clipboard |
| `hybrid` | Claude Code hooks + Claude compile | Antigravity, Ollama, or OpenAI-compatible | Hooks; optional MCP / clipboard |
| `local` | Claude Code hooks + Claude compile | Antigravity or a local endpoint | MCP + clipboard by default; hooks too |
| `lite` | None — no automatic capture, no compile | Detected local backend or import-only mode | MCP + clipboard; memory comes from export ZIPs |

`local` still needs the `claude` CLI for hooks and compile. `lite` does not use
Claude Code at all: it has no automatic capture and no nightly compile.

## Non-interactive paths

For an agent-driven, auto-detected run. Always report the dry-run confirmation
screen before running for real:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Recommended -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Recommended
```

For an explicitly authored, reproducible plan:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Answers C:\path\to\plan.json -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Answers C:\path\to\plan.json
```

The plan contract is:

```json
{"preset":"cloud|hybrid|local|lite","vault":"<path>","backend":"claude|antigravity|ollama|openai-compat|none","backend_env":{"BEYIN_*":"<value>"},"mcp":true,"skills":["beyin-doktor"],"force":false,"install_runtime":false,"pull_models":[]}
```

Validation rules and filled examples: [setup-wizard.md](setup-wizard.md).
`-DryRun` prints every install, environment and MCP action and writes nothing.

## The direct installer

`install.ps1` is the lower-level standalone path and keeps its original default:
all skills and all six hooks.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -VaultPath C:\path\to\vault -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -VaultPath C:\path\to\vault
```

Use `-Force` to overwrite scripts and hooks during an upgrade. Use
`-SkillFilter beyin-doktor,beyin-ice-aktar` to install only selected skills.

What it does, in order:

1. Creates the vault directory if it does not exist (it asks first).
2. Copies `scripts/` and `hooks/` into `<vault>\.claude\`, the vault skeleton
   into `<vault>\`, and `skills/` into `<user>\.claude\skills\`.
3. Copies `template/hub-config.example.json` to
   `<vault>\.claude\scripts\hub-config.json` — edit this to define your own
   topic hubs; it is never overwritten once it exists.
4. Registers six hooks in `<user>\.claude\settings.json`, backing the file up
   first and skipping registrations that already exist.
5. Warns if Python 3.12+ or the `claude` CLI is not on `PATH`.

It never copies `.state`, `.stage`, `.import`, or any `.db` / `.lock` / `.pyc`
file, which is what makes `-Force` safe on an existing vault: your notes, index
and health state are not program files and are left alone.

## What happens next

- In `cloud`, `hybrid` and `local`, your next Claude Code session starts with the
  memory block injected, and each prompt pulls up to three relevant notes.
- The first `daily/YYYY-MM-DD.md` appears when that session ends.
- After 18:00, the first session end that finds changed daily content detaches a
  compile run; `knowledge/` appears once it finishes.
- In `lite`, import export ZIPs and use MCP or the clipboard bridge; automatic
  capture and compile are intentionally absent.
- To backfill history first, run `python scripts/ingest.py status` and then the
  `claude`, `codex`, `web` or `gemini` subcommands.

To check the state of the pipeline at any point:

```powershell
python scripts/durum.py
```

…or ask a Claude Code session for `beyin doktor`, which reports the same picture
as a 🟢/🟡/🔴 table.

## Upgrading

`git pull`, then re-run `install.ps1` with `-Force` (or the wizard with the same
preset). There is no migration step. The details, including what happens to the
FTS index: [release-notes-0.2.0.md](release-notes-0.2.0.md#upgrading-from-010).

## Uninstalling

Dry-run the safe uninstaller first. It backs up every edited file and never
touches `daily/`, `knowledge/`, companion files, or other vault content:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1 -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1
```

It reverses project registrations and, only when separately approved, removes
the copied runtime files.

## For agents

Point a coding agent at [../INSTALL-AGENT.md](../INSTALL-AGENT.md) and it can
run this whole install, prerequisites and verification included. Agents working
*inside* this repository should read [../AGENTS.md](../AGENTS.md) instead.
