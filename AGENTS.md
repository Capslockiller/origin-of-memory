# Origin of Memory 2.0 — agent notes

- `main` is the 2.0 line: one C# exe (`src/Oom`), xUnit tests (`tests/Oom.Tests`), Python
  measurement tools in `bench/` (not shipped). The v0 Python/PowerShell mechanism lives on
  branch `v0` (last release tag `v0.7.0`); read it, never copy it.
- The build contract is the owner's 2.0 spec (vault: `Origin-of-Memory/2.0/Spec-v2.0-baglayici.md`).
  The scar inventory (`Origin-of-Memory/2.0/Yaralar.md`) is the input to lane S: every scar is a
  test that is red before the code exists.
- Lanes: S, A, B, C, D, E (spec §12). One worktree per lane, one commit per lane, subject
  `2.0(<lane>): <what>`. Lanes never touch another lane's files.
- Code, identifiers and comments in English; every user-visible string in Turkish.
- Dependencies are the two named in the spec (`Microsoft.Data.Sqlite`, xunit). Adding one is a
  spec change.
