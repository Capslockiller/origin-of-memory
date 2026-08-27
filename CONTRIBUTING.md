# Contributing

Thanks for considering a contribution to Origin of Memory.

## Running tests

Tests are written with `pytest` and live under `scripts/tests/`. Pytest
configuration is defined in `pyproject.toml` (`testpaths = ["scripts/tests"]`),
so you can run the full suite from the repo root:

```powershell
python -m pytest
```

No extra flags or working-directory tricks are needed — just make sure
you're in the repository root when you run the command.

## Code style

- **Stdlib-only, runtime-wise.** The Python code under `scripts/` must not
  depend on third-party packages at runtime. `pytest` is a dev/test-only
  dependency; nothing under `scripts/` should `import` anything outside the
  Python 3.12 standard library.
- **Bilingual naming is intentional.** You'll find a mix of Turkish and
  English identifiers, file names, and comments throughout the codebase
  (e.g. `flush`, `ingest`, alongside Turkish domain terms). This is a
  deliberate choice, not an inconsistency to "fix" — please match the
  existing convention in the file/module you're editing rather than
  translating it wholesale.
- Keep functions small and testable; prefer pure functions in `scripts/`
  that are easy to exercise from `scripts/tests/`.

## Platform notes

This project is **Windows-native**. The `hooks/` directory contains
PowerShell scripts that must stay compatible with **Windows PowerShell 5.1**
(not just PowerShell 7+) — avoid syntax or cmdlets that only exist in newer
PowerShell editions. `install.ps1` is also expected to run under 5.1.

## Submitting a pull request

Before opening a PR, please make sure:

- [ ] `python -m pytest` passes locally with no failures.
- [ ] No personal data (real names, private paths, tokens, machine-specific
      identifiers, etc.) is included in code, tests, fixtures, or commit
      messages.
- [ ] A secret scan of your diff comes back clean — no API keys, tokens,
      passwords, or credentials of any kind.
- [ ] New behavior has accompanying tests in `scripts/tests/`.

Small, focused pull requests are easier to review than large ones — if your
change touches multiple unrelated areas, consider splitting it up.
