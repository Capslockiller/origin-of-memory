# INSTALL-AGENT.md

**You are an agent installing a persistent memory system for your user.** This
file is self-contained: point yourself at it, follow it top to bottom, and the
user ends up with a working second brain for Claude Code. You do not need to
read the rest of the repository first.

What you are installing: PowerShell hooks plus Python scripts that summarise
every Claude Code session into a daily log, compile those logs nightly into a
linked Markdown knowledge base, and inject the relevant notes back into every
session and every prompt.

**Ask before you install.** Confirm the preset, vault path, backend, MCP choice,
skills, and environment variables with the user before running anything that
writes. The primary agent path is a reviewed plan passed to `kur.ps1 -Answers`.
Cloud, hybrid, and local edit user-level Claude Code settings; none of those
changes are yours to infer.

---

## Step 1 — Verify prerequisites

Run the checks relevant to the chosen preset and report the results as a table
before continuing. Do not proceed past a required failure — tell the user what
is missing and stop. Lite does not require the `claude` CLI.

```powershell
# 1. Windows. The hooks are PowerShell; there is no supported POSIX path.
[System.Environment]::OSVersion.VersionString

# 2. Python 3.12 or newer.
python --version
# If "python" is not found, try:
py -3 --version

# 3. Claude Code CLI on PATH.
claude --version

# 4. SQLite FTS5 — retrieval will not build without it.
python -c "import sqlite3; sqlite3.connect(':memory:').execute('CREATE VIRTUAL TABLE t USING fts5(a)'); print('fts5 ok')"
```

<!-- yazan: codex · gpt-5.6-sol -->
| Check | Required | If it fails |
| --- | --- | --- |
| Windows | Yes | **Stop.** Point the user at the upstream project for macOS/Linux: <https://github.com/avenoxai/avenoxbeyin> |
| Python 3.12+ | Yes | Have the user install Python 3.12+ from python.org. If they have an interpreter elsewhere, set `BEYIN_PYTHON` to its full path. |
| `claude` CLI | Cloud / hybrid / local | Hooks and compile require it. Lite deliberately has neither. Claude Code needs a paid subscription (Pro or higher) **or** a pay-as-you-go Anthropic API key set as `ANTHROPIC_API_KEY` — it is not part of the free claude.ai plan. |
| `agy` CLI (Antigravity) | Optional | Only if the user wants the background summarising calls (flush + ingest) on Google's free tier instead of `claude -p`. Install it from the official Antigravity CLI install page (not npm), run `agy` once interactively to sign in, then set `BEYIN_MODEL_BACKEND=antigravity`. The `claude` CLI is still required for hooks, sessions and the nightly compile. Free-tier quota is limited (third-party reports say ~20 agent requests/day — unverified), and summary quality on Gemini models is unmeasured. |
| Local model server | Optional | Only if the user wants zero-cloud-cost flush and ingest summaries. For Ollama, set `BEYIN_MODEL_BACKEND=ollama` and `BEYIN_OLLAMA_MODEL_FAST`; Ollama defaults to `http://localhost:11434`. For LM Studio, llama.cpp `llama-server`, vLLM, or another OpenAI-compatible server, set `BEYIN_MODEL_BACKEND=openai-compat`, `BEYIN_OPENAI_URL`, and `BEYIN_OPENAI_MODEL_FAST`. Smart-model slugs and `BEYIN_OPENAI_KEY` are optional. Nightly compile still needs `claude` and is refused when that binary is absent. See [local model backends](docs/local-models.md). |
| FTS5 probe prints `fts5 ok` | For retrieval | If it raises `sqlite3.OperationalError`, per-prompt retrieval will not work. Everything else still does. Tell the user, and let them decide whether to continue. |

Two notes worth passing on:

- On a subscription there is **no additional cost**: model calls run on the
  user's existing Claude plan — a small Haiku call per session end, one Sonnet
  compile per day. Without a subscription, the same calls run on a
  pay-as-you-go `ANTHROPIC_API_KEY` for roughly a few dollars a month.
- **No external services.** Everything is local files; no key is required
  beyond the Claude authentication the user already has.

### Optional — copy memory context into web chat

The installer copies but does not register the clipboard bridge. Run it
manually (or bind the wrapper to a shortcut) and paste the result above a
question in any provider's consumer web chat:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "<VAULT PATH>\.claude\hooks\pano-kopru.ps1" "<question>"
# Direct form, including optional --no-map and -k 1..5 flags:
python "<VAULT PATH>\.claude\scripts\context_pack.py" "<question>" --clip
```

The bridge does not automate consumer web UIs; such automation is fragile and
conflicts with providers' terms.

## Step 2 — Choose the vault path

Ask the user where the vault should live. Guidance:

- Any directory. It can be new, or an existing Obsidian vault.
- Prefer a path **without** non-ASCII characters if you can — it avoids a whole
  class of shell quoting problems later.
- It must not be inside the cloned repository.
- If the user syncs it (Drive, Dropbox, git), tell them plainly: this vault will
  accumulate summaries of everything discussed in their Claude Code sessions.
  Point them at `SECURITY.md` before they sync it anywhere shared.

Example: `C:\Users\<user>\Documents\brain`

## Step 3 — Clone

```powershell
git clone https://github.com/Capslockiller/origin-of-memory.git
cd origin-of-memory
```


## Step 4 — Build and review the plan

<!-- yazan: codex · gpt-5.6-sol -->
Create the answers JSON outside the repository. The file is the entire
non-interactive contract: every field is required, and `kur.ps1` will not ask a
follow-up question. Use exactly one example below, replacing `<VAULT PATH>` and
backend details after the user approves them.

### Cloud example

```json
{"preset":"cloud","vault":"<VAULT PATH>","backend":"claude","backend_env":{"BEYIN_VAULT":"<VAULT PATH>","BEYIN_MODEL_BACKEND":"claude"},"mcp":false,"skills":["beyin-doktor","beyin-ice-aktar"],"force":false}
```

### Hybrid example

```json
{"preset":"hybrid","vault":"<VAULT PATH>","backend":"antigravity","backend_env":{"BEYIN_VAULT":"<VAULT PATH>","BEYIN_MODEL_BACKEND":"antigravity"},"mcp":false,"skills":["beyin-doktor","beyin-ice-aktar"],"force":false}
```

Before the real hybrid run, remind the user to launch `agy` once interactively
and complete login. Hybrid may instead use the Ollama or OpenAI-compatible
backend fields shown in [the full plan contract](docs/setup-wizard.md).

### Local example

```json
{"preset":"local","vault":"<VAULT PATH>","backend":"ollama","backend_env":{"BEYIN_VAULT":"<VAULT PATH>","BEYIN_MODEL_BACKEND":"ollama","BEYIN_OLLAMA_MODEL_FAST":"qwen3:8b"},"mcp":true,"skills":["beyin-doktor","beyin-ice-aktar"],"force":false}
```

`qwen3:8b` is a starting-point suggestion, not a measured recommendation.
Local may instead use OpenAI-compatible `BEYIN_OPENAI_URL` and
`BEYIN_OPENAI_MODEL_FAST` fields.

### Lite example

```json
{"preset":"lite","vault":"<VAULT PATH>","backend":"none","backend_env":{"BEYIN_VAULT":"<VAULT PATH>"},"mcp":true,"skills":["beyin-doktor","beyin-ice-aktar"],"force":false}
```

State the lite limitation without euphemism: no automatic capture and no
compile. The user feeds memory with export ZIPs and reads it through MCP or the
clipboard bridge.

Show [skills/README.md](skills/README.md) and put only approved skill directory
names in `skills`. The two core defaults are `beyin-doktor` and
`beyin-ice-aktar`; the four generic working-set skills default off in the
interactive wizard.

## Step 5 — Dry run, then execute the same plan

Never run a first install without `-DryRun`. It prints install, `setx`, and MCP
actions and writes nothing.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Answers "<ANSWERS JSON>" -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Answers "<ANSWERS JSON>"
```

Read the dry-run output with the user. Expect `[COPY]`, hook `[REGISTER]` lines
except in lite, `[DRYRUN][SETX]`, MCP merge-or-snippet output when selected, and
`[DONE] preset=<name> mode=dry-run`. If an existing hook or MCP entry is
reported as `[SKIP]`, the merge is idempotent.

Set `force` to `true` only for an approved upgrade. It overwrites installed
scripts/hooks but never the user's `hub-config.json` or vault content.

### Manual fallback

If the wizard cannot run, keep the direct installer as the fallback. It installs
all skills and registers all hooks by default; environment variables and MCP
registration must then be handled manually.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -VaultPath "<VAULT PATH>" -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -VaultPath "<VAULT PATH>"
```

What it does:

1. Copies `scripts/` → `<vault>\.claude\scripts\` and `hooks/` →
   `<vault>\.claude\hooks\`.
2. Copies `skills/` → `<user>\.claude\skills\`.
3. Copies the vault skeleton (`daily/`, `knowledge/`) into the vault, never
   overwriting anything that exists.
4. Copies `template\hub-config.example.json` →
   `<vault>\.claude\scripts\hub-config.json`, once. This file defines the topic
   hubs of the knowledge base and is meant to be edited.
5. Backs up `<user>\.claude\settings.json`, then registers six hooks in it.
6. Warns if Python 3.12+ or `claude` is missing.

Exit code is 0. Read the install summary plus the wizard's preset-specific
"What happens next" block back to the user.

## Step 6 — Configure the topic hubs

This step applies to cloud, hybrid, and local. Lite has no compiler; its
import/retrieval path can still use existing knowledge content, but it will not
generate topic hubs automatically.

Open `<vault>\.claude\scripts\hub-config.json` with the user. The shipped
example defines Work Projects, Health, Learning, Finances and a Daily Life
catch-all. Each hub has:

- `id` — the filename under `knowledge/hubs/`
- `ad` — display name
- `kapsam` — a one-line scope description
- `tags` / `title_keys` — how concepts are routed into this hub
- `catch_all` (top level) — the id that takes everything unmatched

Array order controls root-map order. Editing this is optional but worthwhile;
the defaults are generic on purpose.

## Step 7 — Verify the installation

### Immediately

For cloud, hybrid, and local:

```powershell
# Six hook registrations should be present:
Get-Content "$env:USERPROFILE\.claude\settings.json"

# Scripts and hooks landed:
Get-ChildItem "<VAULT PATH>\.claude\scripts", "<VAULT PATH>\.claude\hooks"
```

Expect `SessionStart`, two `UserPromptSubmit`, two `SessionEnd`, one
`PreCompact`.

For lite, confirm instead that no hooks were registered and that
`<vault>\.claude\scripts\mcp_server.py`, `retrieve.py`, `ingest.py`, and
`context_pack.py` were copied. If Claude Desktop config was absent, use the
snippet the wizard printed rather than creating or overwriting the config
silently.

### After the user's next session

Have the user open Claude Code **in any project** (the hooks are user-level, so
it does not need to be the vault), exchange at least a few messages, then close
the session. Then check:

| Where | What appears | Meaning |
| --- | --- | --- |
| `<vault>\daily\<today>.md` | A `# Günlük Log` header and a `### Oturum (HH:MM)` block with five Turkish sections | The flush worked end to end |
| `<vault>\.claude\hooks\.state\session_start_time` | A recent Unix timestamp | `SessionStart` fired |
| `<vault>\.claude\hooks\.state\prompt_count` | A number | `UserPromptSubmit` fired |
| `<vault>\.claude\scripts\.state\health.json` | Ideally absent or an empty error | No component has failed |

The daily log is written by a detached background process a few seconds after
the session ends. If it is not there immediately, wait, then re-check.

**The knowledge base does not appear on day one.** `knowledge/` is populated by
the nightly compiler, which runs after 18:00 on the first session end that finds
changed daily content. After that first compile, expect
`knowledge/index.md`, `knowledge/index-full.md`, `knowledge/hubs/*.md`,
`knowledge/concepts/*.md` and `.state/notes.db`. Per-prompt retrieval starts
working once `notes.db` exists.

### Health check

Tell the user they can type **`beyin doktor`** in a Claude Code session at any
time for a single-table status report on hooks, scripts, log freshness and the
last compile.

### Optional — backfill history

If the user has past conversations worth importing:

```powershell
cd "<VAULT PATH>\.claude\scripts"
python ingest.py status                       # reports what is available, writes nothing
python ingest.py claude --dry-run             # Claude Code archives
python ingest.py claude
python ingest.py codex                        # Codex rollouts
python ingest.py web                          # claude.ai export ZIP placed in <vault>\.import\
python ingest.py gemini                       # Google Takeout Gemini archive
```

Always run `--dry-run` first and show the user the plan. Each subcommand makes
one model call per session, so a large history takes time and consumes
subscription usage; `--max-sessions` bounds a run.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| Installer warns "Python was not found" | `python` is not on `PATH` (often the Windows Store stub) | Install Python 3.12+ from python.org, or set `BEYIN_PYTHON` to the interpreter's full path and re-run. `py -3` is used as a fallback. |
| Installer warns "claude CLI was not found" | Claude Code is not on `PATH` | Install the CLI. Hooks are registered either way, but flush and compile silently do nothing without it. |
| FTS5 probe raises `OperationalError` | The Python build lacks FTS5 | Install a CPython build from python.org (they ship FTS5). Until then, retrieval is unavailable; the rest works. |
| No `daily/` file after a session | Several possible causes | Check `<vault>\.claude\scripts\.state\health.json` first — it names the failing component. Then confirm the six hooks are in `<user>\.claude\settings.json`, and that `claude` is on `PATH`. A session with almost nothing in it is skipped on purpose (the summariser returns `FLUSH_BOS`). |
| Hooks do not seem to fire at all | Registered in the wrong settings file, or the session predates the install | They must be in `<user>\.claude\settings.json`, not a project `.claude/settings.json`. Restart Claude Code after installing. |
| Hooks fire twice | A previous project-scoped registration is still present | Remove the duplicate entries from the project's `.claude\settings.json`; keep only the user-level ones. |
| `knowledge/` never appears | The compiler only runs after 18:00, once per day, and only when a daily log changed | Check `.state\compile-state.json` (`last_run`, `last_status`) and `.state\health.json`. A `compile-trigger-<date>` file means the day is already claimed. |
| Retrieval injects nothing | Index not built yet, or the prompt was filtered | `notes.db` only exists after the first compile. Prompts under 12 characters and slash commands are skipped deliberately. Test directly: `python retrieve.py query "<a real question>" --limit 3 --format plain`. |
| Sessions feel slower | Retrieval runs on every prompt | It is budgeted at a 5-second hook timeout; measured p95 on the author's corpus was 347 ms. If it is worse, run `python retrieve.py query "test" --bench`. |
| `health.json` shows `agy-missing` | `BEYIN_MODEL_BACKEND` selects the Antigravity backend but no `agy` binary is on `PATH` | Install the Antigravity CLI from its official install page, or set `BEYIN_AGY_BIN` to the binary's name/full path, or unset `BEYIN_MODEL_BACKEND` to go back to `claude -p`. |
| `health.json` shows `agy-auth-missing` (or repeated `agy-exec-error`) | The cached Antigravity credentials are absent or expired; headless `agy` has no API-key environment variable | Run `agy` once interactively in a terminal and sign in, then retry. If the errors persist, the free-tier quota may be exhausted (third-party reports: ~20 agent requests/day, ~5 h refresh — unverified). |
| `health.json` shows `antigravity-backend-unsupported:compile` | Antigravity mode is on and `claude` is not on `PATH`, so the nightly compile has no backend | Compile needs scoped write permission that `agy` cannot grant per invocation, so it is refused by design. Put `claude` on `PATH` (subscription or `ANTHROPIC_API_KEY`) for the nightly compile. Advanced and off by default: add a scoped `"write_file(<staging>/)"` rule to your own `~/.gemini/antigravity-cli/settings.json` allow-list. |
| `health.json` warns `warn:agy-smart-model-unset:BEYIN_AGY_MODEL_SMART` | No smart model slug is configured for the Antigravity backend | Run `agy models`, pick a stronger slug and set `BEYIN_AGY_MODEL_SMART`. Until then the fast model is used. |
| Everything works but the summaries are in Turkish | By design — the shipped prompts are Turkish | Edit `build_flush_prompt()` in `scripts/flush.py` and `COMPILE_PROMPT` in `scripts/compile.py`. Keep the five section headings consistent with `EXPECTED_SECTIONS`, which is a parsing contract. |

## Uninstall

There is no uninstaller. Removal is manual and reversible:

1. **Unregister the hooks.** Edit `<user>\.claude\settings.json` and remove the
   entries whose `command` points at `<vault>\.claude\hooks\`:
   - `SessionStart` → `session-start.ps1`
   - `UserPromptSubmit` → `prompt-counter.ps1` and `memory-retrieve.ps1`
   - `SessionEnd` → `flush-launch.ps1 -Reason sessionend` and `session-end.ps1`
   - `PreCompact` → `flush-launch.ps1 -Reason precompact`

   The installer left a backup at `settings.json.bak-<timestamp>`; restoring it
   is the cleanest route if nothing else changed since.

2. **Remove the machinery.** Delete `<vault>\.claude\scripts\` and
   `<vault>\.claude\hooks\`. This also removes `.state\` (session counters,
   ingest and compile bookkeeping, `notes.db`).

3. **Remove the skills** you installed from `<user>\.claude\skills\`:
   `beyin-doktor`, `beyin-ice-aktar`, and whichever of `companion`,
   `orchestration`, `codex-fleet`, `gece-vardiyasi` were copied.

4. **Keep or delete the content.** `daily/` and `knowledge/` are the user's
   notes, in plain Markdown. Deleting them destroys the memory; that is the
   user's decision alone, and never yours. Leave them unless explicitly asked.

5. `<vault>\.stage\` and `<vault>\.import\` are transient and safe to delete.

---

Further reading, once the install is done: [README.md](README.md) for the
overview, [docs/architecture.md](docs/architecture.md) for how the pipeline
works, [SECURITY.md](SECURITY.md) for what the redaction layer does and does
not catch.
