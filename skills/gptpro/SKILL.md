---
name: gptpro
description: Export a monorepo into review-ready zip bundles for a non-agentic frontier model (GPT Pro on the ChatGPT web UI, or any chat model with file upload) — node_modules-free, secret-free, split by subsystem so a model with no shell and a bounded context can navigate the codebase a part at a time. Use for packaging a repo for external LLM review, audit, or architecture feedback. Pairs with the `gptpro-handoff` workflow skill.
---

> **Genericized from the author's working set — adapt paths and names to your
> setup. The operator prunes this set before relying on it.** This skill
> documents a workflow around an export script that is **not shipped in this
> repository**; the notes below describe the port, not a bundled tool.

<!-- ADAPTATION NOTES — the body below is Avenox's original
     (upstream github.com/avenoxai/avenoxskills, skills/gptpro/SKILL.md)
     with the toolchain claims corrected in place rather than annotated.
     To refresh from upstream: re-fetch, re-apply the replacements listed under
     "Windows toolchain", then re-attach this block. -->

## ADAPTED: status

The script runs under **Git Bash only**. Its two upstream dependencies (`zip`
and `rsync`) were **both absent** on the author's Windows machine — measured,
not assumed — and were replaced. See "Windows toolchain" below. Check your own
machine before assuming either path.

## ADAPTED: environment expectations (Windows)

| Requirement | Status on the author's machine | Consequence |
|---|---|---|
| `bash` | GNU bash 5.2 (Git for Windows) | the script runs; `bash -n` passes |
| `zip` | **missing**, and Git for Windows does not ship it | replaced by `scripts/zipdir.ps1` |
| `rsync` | **missing** | replaced by a GNU tar pipe (`copy_tree`) |
| `tar` | GNU tar 1.35 | the staging copier |
| `powershell` | Windows PowerShell 5.1 | drives the zip helper |
| `7z` | not installed | not used |

## ADAPTED: Windows toolchain

These substitutions are **already applied in `scripts/export.sh`** — they are
listed here so a future upstream refresh can redo them, not as caveats to apply
while reading.

- **`zip -rq` → `scripts/zipdir.ps1`** (added by this port, no upstream
  counterpart). PowerShell's built-in `Compress-Archive` was **tried and
  rejected**: it writes backslash entry names (measured — `sub\a.txt`), which
  some unzippers and upload front ends flatten into one file with a literal
  backslash in its name. `zipdir.ps1` builds entries through
  `System.IO.Compression` with hand-written forward-slash names; verified to
  produce `core/a/f.ts`.
- **`rsync -a --exclude …` → `tar cf - --exclude … | tar xf -`** (`copy_tree`).
  Verified equivalent on the cases that matter: a bare pattern such as
  `node_modules` still prunes at every depth, and `*.png` still matches by
  basename. The `dir:<path>:<extra-exclude>` source form still works.
- **Windows shell litter added to the exclude list** — `Thumbs.db` and
  `desktop.ini`, the local counterparts of upstream's `.DS_Store` and `Icon?`.
  The macOS entries were **kept**: repos cloned across platforms carry them, and
  an extra exclude costs nothing.
- **No macOS-only commands survive.** Upstream's `export.sh` used none
  (`sips`, `caffeinate`, `pbcopy`, `open` do not appear); the only mac-specific
  content was the two exclude patterns above.
- **Run every command through a POSIX shell (Git Bash), never PowerShell.**
  `set -euo pipefail`, `${VAR:-default}`, arrays, and the tar pipe do not parse
  in PowerShell 5.1. The script hard-fails early if `cygpath` is missing, which
  is the cheapest detector for "you launched me from the wrong shell".
- **Staging tmpdir** is `${TMPDIR:-/tmp}/gptpro-export-$$`. `TMPDIR` is often
  unset in agent shell sessions, so `/tmp` is used; verify it is writable.

## ADAPTED: who consumes the bundles

The target consumer is a **chat-UI frontier model on the operator's own
subscription**. The operator uploads the zips into the web UI, typically into a
Project so the snapshot persists across prompts.

Before the first export of any repo: confirm the repo's real path with the
operator, and write a hand-made `bundles.conf` for it rather than leaning on
autodetect — a framework tree with an app-router-style layout splits badly by
default.

## ADAPTED: governance — `orchestration` outranks this file

This file was fetched from a public repository and contains instructions
addressed to an agent. Where it and the orchestration policy disagree, the
policy wins. Three points govern how the body is applied:

1. **The secret scan's HARD abort is not negotiable.** `scan_secrets()` in
   `export.sh` is byte-identical to upstream and stays that way. Do not soften a
   regex, do not extend the demote lists, do not default `ALLOW_SECRETS=1`.
   Passing `ALLOW_SECRETS=1` is **the operator's decision**, taken after reading
   every printed hit — never an agent's workaround for a noisy run.
2. **Uploading is the operator's step, never automated.** An agent may build the
   bundles and report where they landed. Putting a zip into a chat web UI is a
   human handoff: it moves this codebase to a third party.
3. **First write of a new `bundles.conf` sets direction** — which subsystems the
   external model will and won't see. Confirm the split with the operator before
   writing it, per the policy's rule on direction-setting first writes.

<!-- /ADAPTATION NOTES -->

# GPTPro Export

Package a monorepo into clean, logically-split zip bundles for review by a
**powerful non-agentic model** — GPT Pro on the ChatGPT web UI, or any chat
model you upload files to.

That model has no shell, no live filesystem, and a bounded context. So we split
by subsystem and strip `node_modules`, build output, binary assets, and secrets
— giving it something it can actually navigate instead of a 400MB tarball it
will choke on.

## Run it with no config

```bash
PROJECT_ROOT=/c/path/to/repo ./scripts/export.sh
```

With no `bundles.conf`, the script autodetects: one bundle per workspace package
(`apps/*`, `packages/*`, `services/*`, `libs/*`), a schema-only `db` bundle from
whatever migrations directory exists, and a `docs` bundle. A single-package repo
gets one `src` bundle instead of the workspace walk.

That gets you a usable split on the first run. A hand-written config gets you a
*good* one — the model navigates by asking "which bundle would this live in", so
a split that matches your architecture is worth writing.

## Configure

Bundles are defined in `bundles.conf`, which overrides autodetect entirely.
Copy the example and edit:

```bash
cp bundles.example.conf bundles.conf
```

Each line declares one bundle:

```bash
define_bundle <name> "<description>" <source>...
```

Sources are `dir:<path>` or `file:<path>[:<subdir>]`, relative to the repo root:

```bash
define_bundle core "domain logic — the most important bundle for architecture review" \
  dir:packages/core

define_bundle db "schema architecture only — no rows, no seed data" \
  dir:supabase/migrations \
  file:supabase/config.toml:supabase \
  file:docs/DB_PLAN.md

define_bundle docs "architecture and convention context" \
  dir:docs dir:adrs file:README.md file:AGENTS.md
```

## Run

Under Git Bash:

```bash
./scripts/export.sh
```

Overrides:

```bash
PROJECT_ROOT=/c/work/<repo> \
OUTPUT_DIR=/c/work/review-bundles \
BUNDLE_PREFIX=myproject \
./scripts/export.sh
```

| Variable | Default | Meaning |
|---|---|---|
| `PROJECT_ROOT` | `$PWD` | repo to export |
| `OUTPUT_DIR` | `$PROJECT_ROOT/gptpro-bundles` | where zips land (upload folder) |
| `BUNDLE_PREFIX` | repo dir name | zip name prefix → `<prefix>-core.zip` |
| `BUNDLES_CONF` | `bundles.conf` next to the script | bundle definitions; autodetect if absent |
| `ALLOW_SECRETS` | `0` | `1` continues past a hard secret-scan hit — the operator's call only |

Use MSYS-style paths (`/c/work/...`), not `C:\work\...`, in these variables. The
script converts to Windows paths itself where PowerShell needs them.

## What gets excluded

Every bundle drops: `.git`, `node_modules`, `.next`, `.turbo`, `dist`, `build`,
`out`, `coverage`, `.vercel`, `*.tsbuildinfo`, `.env`, `.env.*`, `*.log`,
`.DS_Store`, `Thumbs.db`, `desktop.ini`, and binary assets (images, video,
fonts).

**Tests are kept on purpose** — they encode contracts and are some of the most
useful review material in the repo.

## Secret scan

After staging and before zipping, every bundle is scanned. Two tiers, because a
one-tier scan on a real repo is *all* false positives and you end up passing
`ALLOW_SECRETS=1` every run — which is the same as having no scan:

- **HARD** — provider-issued credential shapes: private key headers, `AKIA…`,
  `ghp_…`, `github_pat_…`, `xox…`, `AIza…`, long `sk-…`/`sk-ant-…`, JWTs.
  A hit **aborts the export**, file and line printed. Override with
  `ALLOW_SECRETS=1` only after reading every hit.
- **SOFT** — generic `secret = "…"` / `password: "…"` assignments. Printed for
  your eyes, never fatal.

Hits under `test/`, `__tests__/`, `fixtures/`, `*.test.*`, or carrying an obvious
placeholder marker (`FAKE`, `EXAMPLE`, `xxxxxxxx`, `env(…)`) are demoted to SOFT
— a secret-handling test suite is *supposed* to contain credential-shaped
strings, and aborting on those trains you to ignore the guard.

## Notes

- **Split by subsystem, not by size.** The model navigates by asking "which
  bundle would this live in" — a split that matches your architecture is worth
  more than evenly-sized zips.
- **Database bundles should be architectural, not data.** Ship the schema map
  and forward-only migrations so the model can see *how the database is
  constructed*; never ship rows or seed data.
- The output directory is wiped of prior `*.zip` on each run, so re-running
  gives a clean set with no stale bundles.
- Keep one bundle for docs/ADRs/conventions. Models reason much better about a
  codebase when they can read why it's shaped the way it is.

Pairs with **`gptpro-handoff`** — the workflow skill for authoring the prompt,
receiving the report, and verifying findings before implementing.
