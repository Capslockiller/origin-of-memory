# Contributing

## Ground rules

- Every behavioural change comes with a test. Tests live in `tests/Oom.Tests`; regression tests are named `Y-<number>` and the number is assigned when the change is merged. Acceptance-level tests for a whole command live under `tests/Oom.Tests/Kabul` and drive a real `oom.exe` through `KabulHarness`.
- No comments in source outside `tests/`. Names and tests carry the meaning; if a line needs a comment, rewrite the line.
- Nothing leaves the machine except through the `claude` CLI call in `Runner`; `Guards` only folds invisible/control characters out of the prompt that goes out and refuses a directive only in what comes back (`flush`/`compile` model output). A change that adds a network call is a design discussion first.
- Keep the executable small. A feature the owner has not asked for is not added.

## Workflow

1. Open an issue describing the behaviour you want changed and how you would measure it.
2. Branch from `main`, make the change, run `dotnet test Oom.sln -c Release`.
3. Open a pull request. CI runs build and tests on Windows.

## Commit messages

`<version>: <what changed>` — one line, imperative, no trailing period. Example: `3.0: flush.mode dilim`.

## Test seams

A test never touches the real `%LOCALAPPDATA%\oom`, `~/.claude/*` or `~/.codex/*`. Four environment variables change what `oom` itself does and exist only for tests and acceptance harnesses; never set them in a real environment:

- **`OOM_LOCALAPPDATA`** — when set, replaces `%LOCALAPPDATA%` as the root the state directory (`oom\<vault-hash>\`: the state database with the search index, and the files next to it) is written under; the root map itself lives in the vault's `knowledge/`. Every Kabul/Scar test that runs `oom` (in-process or as a child process) sets this to an isolated temp directory instead of mutating the real profile.
- **`OOM_FAKE_NOW`** — an ISO-8601 timestamp (`DateTimeOffset` round-trip format, `O`) that pins "now" for the process that reads it, so a test can assert against a fixed date instead of `DateTimeOffset.Now`. This is a deliberate, permanent test seam (not dead code, not a production code path) — `tests/Oom.Tests/Kabul/KabulHarness.cs` sets it on every process-boundary run that needs a fixed clock. `oom` reads it from its environment every time it asks for the current time (an unparsable value is ignored and the real clock is used) and never writes it; nothing in normal use sets it.
- **`OOM_USERPROFILE`** — when set, replaces the user-profile root in exactly two places: the `.claude\settings.json` whose hooks `oom doctor` validates, and the home `oom kit status`/`kit install` (and `doctor`'s kit rows) compare against and install into (`.claude\`, `.agents\`). `sweep.roots`' `%USERPROFILE%` is expanded from the real `USERPROFILE` and does not follow it.
- **`OOM_TEST_ROOT`** — the directory whose transcripts `sweep` treats as its own mechanism artifacts and skips without a diagnostic (default: the OS temp directory). Setting it moves that exclusion elsewhere; a whitespace-only value or a bare drive root such as `C:\` stops the command with an error instead of excluding everything.

Two more are read only by the test project, never by `oom`:

- **`OOM_KABUL_EXE`** — an absolute path to the `oom.exe` a Kabul (acceptance) test should drive, in place of the one MSBuild copies beside the test assembly's own output. Point it at an old build (e.g. a tagged release) to prove a defect reproduces there and a fix does not, or at a freshly built exe to run the same acceptance suite against it. See `tests/Oom.Tests/Kabul/KabulHarness.cs` for the resolution order.
- **`OOM_SCAR_ROOT`** — where Scar tests create their scratch fixtures (default: a `scar-root` folder beside the test assembly), kept apart from `OOM_TEST_ROOT` so a fixture transcript never lands inside the root `sweep` excludes.

`OOM_INVOKED_BY` is not a test seam but `oom`'s runtime recursion guard (README → Environment variables); `KabulHarness` removes it from every child process it starts so a leaked value cannot turn a guarded command into a silent exit 0.

## Private acceptance data

Some acceptance tests read real personal data that never belongs in a public repository. That data lives outside this checkout entirely, named only through three environment variables:

- **`OOM_KABUL_PRIVATE`** — path to a private JSON file of forbidden terms and private path roots, scanned across every git-tracked file by `tests/Oom.Tests/Kabul/PublicHygieneKabul.cs` before anything ships.
- **`OOM_KABUL_EVAL_SET`** — path to a frozen, private retrieval query set (real personal facts and their expected notes), read by `tests/Oom.Tests/Kabul/RetrieveSetKabul.cs`.
- **`OOM_KABUL_VAULT`** — path to the real vault the private query set above was frozen against, so the set's expectations can be re-measured against the vault they describe.

None of these tests skip when the variable is unset: each fails loudly, naming the missing environment variable, by design (a silently-skipped privacy gate is worse than no gate). They carry `[Trait("Kabul", "OzelVault")]`; the interim and CI gates filter this trait out, and only a local run that has exported the three variables above is expected to exercise them. The repository itself never contains the private files, the personal facts they hold, or a hard-coded fallback path to them.
