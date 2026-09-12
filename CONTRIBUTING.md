# Contributing

## Ground rules

- Every behavioural change comes with a test. Tests live in `tests/Oom.Tests`; regression tests are named `Y-<number>` and the number is assigned when the change is merged.
- No comments in source. Names and tests carry the meaning; if a line needs a comment, rewrite the line.
- Nothing leaves the machine except through the egress gate; a change that adds a network call is a design discussion first.
- Keep the executable small. A feature the owner has not asked for is not added.

## Workflow

1. Open an issue describing the behaviour you want changed and how you would measure it.
2. Branch from `main`, make the change, run `dotnet test Oom.sln -c Release`.
3. Open a pull request. CI runs build and tests on Windows.

## Commit messages

`<version>: <what changed>` — one line, imperative, no trailing period. Example: `3.0: flush.mode dilim`.
