---
yazan: codex
model: gpt-5.6-sol
---

# Setup wizard and plan contract

`kur.ps1` is an interview-first layer over `install.ps1`. Running it without
arguments asks numbered bilingual questions, shows the resulting JSON and a
summary table, then asks one final confirmation before writing.

For automation, `-Answers <file.json>` makes that file the complete plan. This
mode never prompts and exits 0 on success or 1 on a validation/action failure.
`-DryRun` prints every copy, hook, environment, and MCP action without writing.

## Plan schema

The original fields are required. `install_runtime` and `pull_models` are
optional and default to `false` and `[]`; unknown fields are rejected.

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

Validation is deliberately strict and names the failing field. The vault may
exist or be new, but must be a directory and must not be inside this repository.
A non-ASCII path is accepted with a warning. Skill names must match directories
under `skills/`. Backend compatibility is:

| Preset | Valid backend | Notes |
| --- | --- | --- |
| `cloud` | `claude` | Full Claude Code capture and compile |
| `hybrid` | `antigravity`, `ollama`, `openai-compat` | Claude Code capture/compile; selected backend for flush/ingest |
| `local` | `antigravity`, `ollama`, `openai-compat` | Same hooks plus MCP/clipboard-oriented defaults; compile still needs `claude` |
| `lite` | `none` | No hook registration, automatic capture, or compile |

Ollama plans require `BEYIN_OLLAMA_MODEL_FAST`. An explicit `pull_models` list
accepts at most two verified catalogue tags; the first becomes the fast model
and the second becomes the smart model through the plan environment mechanism.
`install_runtime` and model pulls are valid only for Ollama. OpenAI-compatible
plans require `BEYIN_OPENAI_URL` and `BEYIN_OPENAI_MODEL_FAST`.

## Filled plans

Replace `<vault>` with an absolute path. These examples select only the two
core memory skills.

### Cloud

```json
{
  "preset": "cloud",
  "vault": "<vault>",
  "backend": "claude",
  "backend_env": {
    "BEYIN_VAULT": "<vault>",
    "BEYIN_MODEL_BACKEND": "claude"
  },
  "mcp": false,
  "skills": ["beyin-doktor", "beyin-ice-aktar"],
  "force": false
}
```

### Hybrid

```json
{
  "preset": "hybrid",
  "vault": "<vault>",
  "backend": "antigravity",
  "backend_env": {
    "BEYIN_VAULT": "<vault>",
    "BEYIN_MODEL_BACKEND": "antigravity"
  },
  "mcp": false,
  "skills": ["beyin-doktor", "beyin-ice-aktar"],
  "force": false
}
```

Run `agy` once interactively to complete login before relying on this backend.

### Local

```json
{
  "preset": "local",
  "vault": "<vault>",
  "backend": "ollama",
  "backend_env": {
    "BEYIN_VAULT": "<vault>",
    "BEYIN_MODEL_BACKEND": "ollama",
    "BEYIN_OLLAMA_MODEL_FAST": "qwen3:8b"
  },
  "mcp": true,
  "skills": ["beyin-doktor", "beyin-ice-aktar"],
  "force": false
}
```

An OpenAI-compatible local plan uses `backend: "openai-compat"` plus
`BEYIN_OPENAI_URL` and `BEYIN_OPENAI_MODEL_FAST` instead.

### Lite

```json
{
  "preset": "lite",
  "vault": "<vault>",
  "backend": "none",
  "backend_env": {
    "BEYIN_VAULT": "<vault>"
  },
  "mcp": true,
  "skills": ["beyin-doktor", "beyin-ice-aktar"],
  "force": false
}
```

Lite installs ingest, retrieval, MCP, and clipboard components but does not
register hooks. Memory is fed through export ZIPs and read through MCP or the
clipboard bridge; there is no automatic capture or nightly compile.

## Environment and MCP writes

The interactive wizard lists all chosen `BEYIN_*` variables and asks once
before persisting them through the .NET user-scope environment API. It never
uses `setx`, which can truncate long values and damage PATH. Non-interactive
mode treats the plan as that authorization. Secret-like values are redacted.

When MCP is selected, the wizard probes both the standard
`%APPDATA%\Claude\claude_desktop_config.json` and the MSIX-virtualised
`%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude_desktop_config.json`.
It prefers an existing file, backs up every file it writes, and writes the same
merged content to both when both exist. If neither exists, it creates the
standard file and warns that an MSIX install may read the virtual path.

Cheap backend verification (`--version` or a short TCP connection) is performed
only after explicit interactive consent and never makes a model call.
