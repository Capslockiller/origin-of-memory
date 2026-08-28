<!--
Thanks for the pull request. Small and focused reviews faster than large and
sweeping; if this touches several unrelated areas, consider splitting it.
Conventions: CONTRIBUTING.md · repository rules for agents: AGENTS.md
-->

## What this changes

<!-- One or two sentences. What was wrong or missing, and what this does about
     it. Link the issue if there is one. -->

## Why

<!-- The reasoning, not a restatement of the diff. If this is a behaviour
     change, say what breaks and for whom. See "Versioning" in CONTRIBUTING.md
     for what counts as breaking here. -->

## Checklist

- [ ] `python -m pytest` passes locally with no failures.
- [ ] New behaviour has tests under `scripts/tests/`.
- [ ] **No personal data** — no real names, usernames, email addresses,
      absolute paths from a real machine, private project names, or vault
      content in code, tests, fixtures, docs or commit messages.
      (`AGENTS.md` has the grep gate.)
- [ ] **Secret scan of the diff is clean** — no API keys, tokens, passwords or
      credentials of any kind.
- [ ] Docs updated where behaviour changed — README (both languages if it is
      user-facing), the relevant page under `docs/`, and any env var added or
      renamed.
- [ ] A `CHANGELOG.md` entry under `[Unreleased]`, in the right group
      (Added / Changed / Fixed / Security), one line, naming the env var or
      file it touches.
- [ ] Runtime code under `scripts/` imports **only** the Python 3.12 standard
      library.
- [ ] Any PowerShell added stays compatible with **Windows PowerShell 5.1**.

## Anything the reviewer should know

<!-- Trade-offs you made, things you were unsure about, follow-up work you are
     deliberately leaving out. -->
