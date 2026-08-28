# AGENTS.md

Instructions for coding agents working **inside this repository**. To install
this system for a user, read [INSTALL-AGENT.md](INSTALL-AGENT.md) instead.

## What this project is

A persistent memory system for Claude Code: PowerShell hooks flush each session
into `daily/YYYY-MM-DD.md`, a nightly Python compiler distils those logs into a
linked Markdown knowledge base, and hooks inject a root map at session start and
the top-3 BM25 matches on every prompt.

Full pipeline, file by file: [docs/architecture.md](docs/architecture.md).

## Commands

```powershell
python -m pytest              # the whole suite; testpaths is set in pyproject.toml
python -m pytest scripts/tests/test_retrieve.py -q
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -VaultPath <path> -DryRun
```

There is no build step, no linter config, and no formatter config. Match the
style of the file you are editing.

## Hard policies

### Standard library only

`scripts/` must run on a bare CPython 3.12+ install. **No third-party imports,
no `pip install`, no new entry in `pyproject.toml`'s dependencies.** `pytest` is
a development dependency and is used only under `scripts/tests/`.

If a task seems to need a package, it needs a different design. Raising this
with the operator is correct; adding the dependency quietly is not.

### Shared helpers live in `scripts/beyin_ortak.py`

`write_health`, `write_health_skip`, `_atomic_write_json`, `_lock_exclusive` and
`_sha256` have **one implementation**, in `scripts/beyin_ortak.py`. `flush.py`,
`compile.py`, `rootmap.py`, `ingest_common.py` and `retrieve.py` import from it.

These were previously copy-pasted into each module, and the duplication was
documented here as deliberate: each script had to stay independently runnable,
and nothing outside the standard library could be imported. Both goals survive a
shared module, because `beyin_ortak.py` ships in the same directory as its
callers and is imported the same way as `claude_runner` or `secret_guard` — it is
not a package, not a dependency, and not an install step. Five copies of an
`fsync`-and-`os.replace` helper was not independence, it was five places to fix
the next bug in.

**Import the names directly, do not re-wrap them:**

```python
from beyin_ortak import _atomic_write_json, _lock_exclusive, _sha256, write_health
```

The leading underscores are kept, and the import is at module level, because a
plain `from … import` binds the name into the importing module — so `flush._sha256`
and `compile._sha256` still resolve, and are the *same object*. Tests and callers
that reach for those names keep working untouched; that compatibility is the
reason the aliases exist, and `test_hardening.py::SharedHelperTests` asserts the
identity so a future refactor cannot quietly fork them again.

If you need module-local behaviour, pass a parameter rather than redefining the
helper. `write_health()` already takes `component` and `health_name` for exactly
this reason — that is how `ingest_common.py` writes to `ingest-health.json`
instead of `health.json`.

### Bilingual naming is intentional

Turkish and English are deliberately mixed and **must not be "cleaned up":**

- Skill trigger phrases (`beyin doktor`, `gece vardiyası`, `beyin içe aktar`)
  are the invocation surface. Translating them breaks the trigger.
- Model-facing prompts (`COMPILE_PROMPT`, `build_flush_prompt`) and the daily
  summary's five section headings are Turkish because the corpus is Turkish.
  The section names are a parsing contract — `validate_summary()` rejects a
  summary whose headings do not match exactly.
- Some identifiers, comments and health strings are Turkish
  (`secret_guard.redact` returns `[SIR:<pattern>]`, `FLUSH_BOS`,
  `hub-config.json` uses `ad` and `kapsam`). These appear in state files and
  generated content; renaming one is a data-format change.
- Documentation is English, with `README.tr.md` as the Turkish mirror. Keep the
  two in step when you change either.

### Fail loud, run quiet

Hooks and background scripts must never break the session they are attached to.
Every entry point catches its own failures, writes a reason through
`write_health()`, and returns 0. A silent no-op with no health entry is a bug.

### Never widen the compiler's write policy

`_is_allowed_output_file` / `_is_allowed_output_directory` in `scripts/compile.py`
are a security boundary — the model runs in a staging tree and only allow-listed
paths are promoted. Do not add paths there without reading
[SECURITY.md](SECURITY.md) first.

### Recursion guard

Every hook and every script entry point exits immediately when
`BEYIN_INVOKED_BY` is set. New entry points must do the same, or the pipeline's
own `claude -p` subprocesses will re-trigger the pipeline.

## Personal-data gate

This repository was extracted from a private vault. **Nothing personal may
re-enter it** — no real names, usernames, absolute paths from a real machine,
project names, email addresses, or vault content.

Run this before committing, from the repository root, with your own identifiers
substituted into the pattern:

```bash
grep -rIn --exclude-dir=.git -iE "<username>|<real name>|<email>|<vault name>|<private project names>" .
```

Zero hits is the gate. Keep your identifier list in your own notes rather than
in this file — writing it here would publish exactly what the gate exists to
keep out. A companion check that needs no list:

```bash
# Absolute Windows paths and home directories should never appear outside
# placeholders. Anything this prints is a candidate leak.
grep -rIn --exclude-dir=.git -E "[A-Za-z]:\\\\Users\\\\[^<]|/home/[a-z]" .
```

Related rules:

- Test fixtures use invented data. Never paste a real transcript, daily log or
  concept note into a test.
- Documentation examples use `<vault>`, `<user>`, `<repo>` placeholders, never a
  real path.
- Measured numbers in the docs come from the author's corpus and are labelled as
  such. Do not add a metric you cannot source.
- The gold evaluation set is deliberately unpublished; see
  [docs/evaluation.md](docs/evaluation.md).

## Layout

| Path | What |
| --- | --- |
| `hooks/*.ps1` | Session hooks, registered at user level by the installer |
| `scripts/*.py` | Flush, compile, root map, retrieval, ingest, secret guard, schema gate |
| `scripts/beyin_ortak.py` | Shared health, locking, atomic-write, hashing and call-ledger helpers |
| `scripts/durum.py` | `python scripts/durum.py [--json]` — one-table health summary |
| `scripts/tests/` | pytest suite |
| `skills/` | Skills copied to `<user>\.claude\skills\`; see [skills/README.md](skills/README.md) |
| `template/` | Vault skeleton, hub config example, rules example |
| `tools/` | One-off migration helpers, not part of the runtime |
| `docs/` | Architecture, compatibility, evaluation, attribution, discoverability |

Contribution process: [CONTRIBUTING.md](CONTRIBUTING.md).
