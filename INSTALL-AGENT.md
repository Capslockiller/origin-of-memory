# INSTALL-AGENT.md

**You are an agent installing a persistent memory system for your user.** This
file is self-contained: point yourself at it, follow it top to bottom, and the
user ends up with a working second brain for Claude Code. You do not need to
read the rest of the repository first.

What you are installing: PowerShell hooks plus Python scripts that summarise
every Claude Code session into a daily log, compile those logs nightly into a
linked Markdown knowledge base, and inject the relevant notes back into every
session and every prompt.

**Ask before you install.** Confirm the vault path with the user before running
anything that writes. This installer edits their user-level Claude Code
settings; that is not a change to make on your own initiative.

---

## Step 1 — Verify prerequisites

Run all four checks and report the results as a table before continuing. Do not
proceed past a failure — tell the user what is missing and stop.

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

| Check | Required | If it fails |
| --- | --- | --- |
| Windows | Yes | **Stop.** Point the user at the upstream project for macOS/Linux: <https://github.com/avenoxai/avenoxbeyin> |
| Python 3.12+ | Yes | Have the user install Python 3.12+ from python.org. If they have an interpreter elsewhere, set `BEYIN_PYTHON` to its full path. |
| `claude` CLI | Yes | The pipeline calls `claude -p` for summarising and compiling. Without it, nothing is written. |
| FTS5 probe prints `fts5 ok` | For retrieval | If it raises `sqlite3.OperationalError`, per-prompt retrieval will not work. Everything else still does. Tell the user, and let them decide whether to continue. |

Two notes worth passing on:

- There is **no additional cost**. Model calls run on the user's existing Claude
  subscription: a small Haiku call per session end, one Sonnet compile per day.
- **No API keys, no external services.** Everything is local files.

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
git clone https://github.com/OWNER/origin-of-memory.git
cd origin-of-memory
```

<!-- Replace OWNER with the actual account hosting this repository. -->

## Step 4 — Dry run first

Never run the installer for the first time without `-DryRun`. It prints every
action and writes nothing.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -VaultPath "<VAULT PATH>" -DryRun
```

Read the output with the user. You should see:

- `[CREATE]` for the vault directory if it does not exist
- `[COPY]` lines for `scripts/`, `hooks/`, `skills/` and the vault template
- `[REGISTER]` lines for six hooks
- `[BACKUP]` for the existing `settings.json`, if the user has one
- A summary with `Mode: DRY RUN`

If you see `[SKIP] Hook already registered`, a previous installation exists.
That is fine — the installer is idempotent.

**Before the real run, decide about skills.** `skills/` is copied wholesale into
`<user>\.claude\skills\`, and it contains more than the memory mechanism:
`companion`, `orchestration`, `codex-fleet`, `gece-vardiyasi`, `gptpro` and
`gptpro-handoff` are genericized copies of the author's working set. Show the
user [skills/README.md](skills/README.md) and delete the directories they do not
want **before** the real run. The two to keep in any case are `beyin-doktor` and
`beyin-ice-aktar`.

## Step 5 — Install

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -VaultPath "<VAULT PATH>"
```

Add `-Force` only when upgrading an existing installation and the user wants
scripts and hooks overwritten. `-Force` does **not** overwrite the user's
`hub-config.json` or their vault content.

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

Exit code is 0. Read the summary lines (`Planned actions`, `Writes completed`,
`Existing items skipped`) back to the user.

## Step 6 — Configure the topic hubs

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

```powershell
# Six hook registrations should be present:
Get-Content "$env:USERPROFILE\.claude\settings.json"

# Scripts and hooks landed:
Get-ChildItem "<VAULT PATH>\.claude\scripts", "<VAULT PATH>\.claude\hooks"
```

Expect `SessionStart`, two `UserPromptSubmit`, two `SessionEnd`, one
`PreCompact`.

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
   `orchestration`, `codex-fleet`, `gece-vardiyasi`, `gptpro`,
   `gptpro-handoff` were copied.

4. **Keep or delete the content.** `daily/` and `knowledge/` are the user's
   notes, in plain Markdown. Deleting them destroys the memory; that is the
   user's decision alone, and never yours. Leave them unless explicitly asked.

5. `<vault>\.stage\` and `<vault>\.import\` are transient and safe to delete.

---

Further reading, once the install is done: [README.md](README.md) for the
overview, [docs/architecture.md](docs/architecture.md) for how the pipeline
works, [SECURITY.md](SECURITY.md) for what the redaction layer does and does
not catch.
