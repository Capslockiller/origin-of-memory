<!-- yazan: codex · gpt-5 -->

# Origin of Memory 2.0 — agent notes

- `main` is the 2.0 line: one C# executable project (`src/Oom`), xUnit tests (`tests/Oom.Tests`), and Python measurement tools in `bench/` that are not shipped. The v0 Python/PowerShell mechanism lives on branch `v0` (last release tag `v0.7.0`); read it, never copy it.
- The binding build contract is the owner's 2.0 specification. Its scar inventory is represented by the 99 tests under `tests/Oom.Tests/Scars/`; each scar is red before its implementation exists.
- Components are folders under `src/Oom/`; shared public records and boundary interfaces are under `src/Oom/Contracts/`, with platform implementations under `src/Oom/Infrastructure/`.
- Lanes S, A, B, C, D, and E use separate worktrees and one commit per lane. The subject is `2.0(<lane>): <what>`, and lanes do not edit one another's files.
- Code, identifiers, and comments are English; every user-visible string is Turkish.
- The product project references `Microsoft.Data.Sqlite`; tests use xUnit and the .NET test infrastructure. Adding a product dependency is a specification change.
