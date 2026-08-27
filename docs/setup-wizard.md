---
yazan: codex
model: gpt-5.6-sol
---

# Setup wizard and plan contract

`kur.ps1` defaults to a two-screen recommended installation. The first screen
has one decision, with Enter selecting the recommended path:

```text
Step 1/2 — Choose a path
  1. Recommended setup (auto-detect) [default]
  2. Custom
  3. Show me what you detected first
```

The wizard probes Claude Code, local model runtimes, hardware, the Windows
Documents path, and Claude Desktop configuration. It then shows one confirmation
screen. Each chosen value has a one-line reason:

```text
Step 2/2 — Recommended setup confirmation
  Preset: cloud — Claude Code was detected and no local runtime was found ...
  Vault: C:\Users\<user>\Documents\brain — Documents is not redirected ...
  Backend: claude — No local runtime was detected.
  Model: not needed — No local model is needed for this plan.
  MCP: False — No Claude Desktop config exists ...
  Skills: beyin-doktor, beyin-ice-aktar — ...
  Mode: WRITE
  Plan JSON: {...}
[Enter] Install / [c] Change something / [q] Quit
```

Holding Enter therefore selects recommended, approves the detected plan, and
installs. `c` opens Custom with those auto-values as defaults. `q` exits without
writing. Recommended mode never installs a GUI runtime or pulls a model without
consent.

## Auto-decision rules

The pure function in `scripts/kurulum_plani.py` implements these decisions:

| Claude Code | Local runtime | Preset | Backend |
| --- | --- | --- | --- |
| Detected | Detected | `hybrid` | Preferred detected runtime |
| Detected | None | `cloud` | `claude` |
| None | Detected | `lite` | Preferred detected runtime; no capture or compile |
| None | None | `cloud` | `claude`, with an install-Claude-first note |

An already-running runtime is preferred over one that is merely installed;
ties use Ollama, LM Studio, llama.cpp, then vLLM. MCP defaults on only when an
existing Claude Desktop config is found. The only default skills are
`beyin-doktor` and `beyin-ice-aktar`. The model is the first `fits-gpu` or
`cpu-ok` catalogue recommendation.

The vault defaults to `%USERPROFILE%\Documents\brain`. If Windows reports a
Documents directory different from `%USERPROFILE%\Documents` (the common
OneDrive-redirection case), it uses `%USERPROFILE%\brain` instead.

## Custom flow

Custom mode is at most seven steps: preset, vault, runtime/backend, model,
integrations, verification, and confirmation. Every prompt displays a default
and bare Enter accepts it. Detected values are shown as defaults instead of
being asked as unknowns.

For a hybrid, local, or lite plan, detected runtimes are numbered with a running
runtime preselected. If none is found, the choices are Install Ollama, use LM
Studio, or skip local models. LM Studio prints its download URL and manual GUI
instructions; the wizard does not download or launch its installer. Ollama
model pulls remain separately opt-in.

OpenAI-compatible model names cannot be discovered without HTTP. Custom mode
explains where the runtime displays the name. No HTTP model listing occurs in
dry-run or recommended mode. If no model name is known, the plan prints a
`[TODO]` for `BEYIN_OPENAI_MODEL_FAST` and does not persist an empty value.

## Plan JSON contract

`-Answers <file.json>` is unchanged: the file is the complete non-interactive
plan, there are no prompts, and validation remains strict. The original fields
are required; `install_runtime` and `pull_models` are optional and default to
`false` and `[]`. Unknown fields are rejected.

```json
{
  "preset": "cloud|hybrid|local|lite",
  "vault": "<path>",
  "backend": "claude|antigravity|ollama|openai-compat|none",
  "backend_env": { "BEYIN_*": "<string>" },
  "mcp": true,
  "skills": ["beyin-doktor", "beyin-ice-aktar"],
  "force": false,
  "install_runtime": false,
  "pull_models": []
}
```

Backend compatibility is:

| Preset | Valid backend |
| --- | --- |
| `cloud` | `claude` |
| `hybrid` | `antigravity`, `ollama`, `openai-compat` |
| `local` | `antigravity`, `ollama`, `openai-compat` |
| `lite` | `none`, `ollama`, `openai-compat` |

Ollama plans require `BEYIN_OLLAMA_MODEL_FAST`. `pull_models` accepts at most
two verified catalogue tags and is valid only for Ollama. OpenAI-compatible
`-Answers` plans require non-empty `BEYIN_OPENAI_URL` and
`BEYIN_OPENAI_MODEL_FAST` values.

## Agent and automation mode

`-Recommended` probes, prints the same confirmation screen, and proceeds without
prompts. It is intended for agents and automation. Always review its dry-run
first:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Recommended -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Recommended
```

`-DryRun` prints every copy, hook, environment, runtime, and MCP action without
writing. `-Answers` and `-Recommended` are mutually exclusive.

MCP detection and registration cover both the standard
`%APPDATA%\Claude\claude_desktop_config.json` path and the MSIX-virtualised
path. Existing files are backed up before a real merge. Cheap backend checks use
only a binary version or TCP connection and run only after interactive consent.
