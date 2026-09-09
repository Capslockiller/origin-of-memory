<!-- yazan: codex · gpt-5 -->

# Contributing

Origin of Memory 2.0 is a Windows-native C#/.NET rebuild. Changes are accepted against the 2.0 line only when their behavior is covered by a scar, acceptance measurement, or an explicit specification change.

## Build and test

Run from the repository root:

```powershell
dotnet build Oom.sln -c Release
dotnet test Oom.sln -c Release
```

The test suite uses xUnit. Python under `bench/` is measurement tooling and is not part of the shipped executable.

## Scar-first rule

The historical failure inventory is represented in `tests/Oom.Tests/Scars/` as `Y-001` through `Y-099`. Every scar has exactly one xUnit `Fact`.

1. Add or update the scar test before implementation.
2. Confirm that the test is red for the missing behavior.
3. Implement only the behavior needed by the owning component.
4. Run the full solution and report the exact passed, failed, skipped, and total counts.

A release note, code review, or explanation is not evidence that a scar is closed; the mapped test must be green.

## Lane discipline

Work is divided into isolated lanes:

| Lane | Ownership |
| --- | --- |
| S | `tests/Oom.Tests/Scars/**` |
| A | `State`, `Runner`, `Guards`, `Notes`, `Program` |
| B | `Context`, `Retrieve`, `Flush`, `Sweep`, hook templates |
| C | `Compile`, `RootMap`, `Bridge` |
| D | `Ingest`, `Doctor`, `Notify`, `Mcp`, `Save`, `Install` |
| E | `README.md`, `README.tr.md`, `SECURITY.md`, `CHANGELOG.md`, `CONTRIBUTING.md`, `docs/**`, `bench/` documentation |

Use one worktree per lane. Do not edit another lane's files. Each lane produces one commit with this subject form:

```text
2.0(<lane>): <what>
```

## Language and files

- Code, identifiers, and comments are English.
- Every user-visible string is Turkish.
- Text boundaries are UTF-8 without BOM; BOM is tolerated only on input.
- The product is Windows-native. Do not add PowerShell scripts or a POSIX runtime path.
- Never commit personal paths, credentials, vault content, generated state, benchmark data, or build output.

## Dependencies

The product dependency is `Microsoft.Data.Sqlite`; xUnit is the test framework. Adding a product dependency or changing the approved dependency set requires a specification change. Keep model, process, HTTP, and transcript-format boundaries isolated in their owning component.

## Line budgets

Tests are excluded from these binding limits. `src/Oom/` must remain at or below 7,500 C# lines.

| Component | Maximum lines |
| --- | ---: |
| `Compile` | 800 |
| `Flush` | 600 |
| `Sweep` | 350 |
| `Retrieve` | 600 |
| `State` | 500 |
| `Runner` | 450 |
| `Guards` | 400 |
| `Context` | 250 |
| `RootMap` | 400 |
| `Ingest` including both parsers | 700 |
| `Doctor` | 450 |
| `Mcp` | 300 |
| `Install` | 450 |
| All remaining production C# | 750 |

A component over budget does not merge. Raising a budget is a specification change.

## Pull requests

Keep a change inside one responsibility boundary. Include the mapped scar or acceptance evidence, the full test result line, and a concise `[Unreleased]` changelog entry for user-visible behavior. Do not hide a failing command behind a pipeline whose last process can replace its exit code.
