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

## Versioning

This project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
from `0.1.0` onward. The public line is the git tag and the
[CHANGELOG](CHANGELOG.md) heading — nothing else. Internal planning documents
sometimes use milestone names like "v0.5"; **those are not versions** and must
never be written into a tag, a changelog heading or a release title.

While the major version is `0`, the minor number carries breaking changes and
the patch number carries fixes — the usual pre-1.0 reading of semver.

**What counts as breaking here.** The project ships no library API, so the
contract is the surface an installed vault and its hooks depend on:

- **The hook contract.** The event names registered in
  `<user>\.claude\settings.json`, the arguments each hook is invoked with, and
  the stdout shape a hook writes back into a session. Removing a hook, renaming
  a script under `hooks/`, or changing what a hook prints are breaking.
- **The plan-JSON shape.** The `-Answers` plan consumed by `kur.ps1`
  (see [docs/setup-wizard.md](docs/setup-wizard.md)). Removing a field, making
  an optional field required, or changing a field's accepted values is
  breaking. Adding an optional field with a default is not.
- **Environment variable names.** Every `BEYIN_*` variable is a public knob.
  Renaming or removing one is breaking; a deprecated alias that still works and
  warns (as `BEYIN_MODEL_BACKEND=gemini` does) is not.
- **Vault layout.** The directory names and file locations an installed vault
  is expected to have — `daily/`, `knowledge/concepts/`, `knowledge/hubs/`,
  `.claude/scripts/`, `.state/`. Moving one of these, or changing a file format
  in a way that an existing vault cannot be read through, is breaking.

Things that are deliberately **not** breaking: the on-disk shape of `.state/`
health and bookkeeping files (they are rebuilt), the FTS index schema (a stale
index is detected and rebuilt), prompt wording, and anything under `docs/`.

Every user-visible change gets a CHANGELOG entry under `[Unreleased]`, grouped
Added / Changed / Fixed / Security, one line each, naming the env var or file it
touches. Releases are cut by the maintainer: the `[Unreleased]` block becomes a
dated version heading, a fresh empty `[Unreleased]` is left behind, and the
compare links at the bottom of the file are updated.

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
