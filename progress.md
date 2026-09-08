---
yazan: codex
model: gpt-5
---

Class YazmaYoluScars: 18 tests, compiles, all red
Class OzetleyiciScars: 6 tests, compiles, all red
Class DerleyiciScars: 16 tests, compiles, all red
Class GetirmeScars: 10 tests, compiles, all red
Class KancaScars: 8 tests, compiles, all red
Class KotaScars: 6 tests, compiles, all red
Class DurumDeposuScars: 7 tests, compiles, all red
Class KurulumScars: 13 tests, compiles, 11 red + 2 repository invariants green (Y-068, Y-094)
Class TestDisipliniScars: 8 tests, compiles, 7 red + 1 repository invariant green (Y-081)
Class SurecIsletmeScars: 7 tests, compiles, all red

Ruling: Y-005 uses a lossless raw-transcript channel that preserves tool blocks while the derived summary path follows Spec §6.3 and excludes them.
Ruling: Y-007 follows the scar inventory's stricter unstamped-source rule; `sinceHours` filters only sources that already have a sweep stamp.
Ruling: Y-016 is represented by a six-window benchmark fixture plus a compile promotion assertion because `bench/` belongs to lane E and may not be edited here.
Ruling: Y-067 follows binding Spec §4.1 where unknown configuration keys are reported as warnings, not fatal errors; the test requires the unknown key to be surfaced.
Ruling: Y-068 and Y-081 are repository-invariant tests and pass on the empty 2.0 skeleton as permitted by BRIEF rule 3.
Ruling: Y-083 is tested through a pure cleanup plan contract; the test never performs a destructive worktree operation.
Ruling: Y-086 maps release-claim evidence validation to Doctor because release documents belong to lane E and are do-not-touch for lane S.
Ruling: Y-094 scans the repository for `.ps1`, but applies the no-BOM assertion to lane S-owned Contracts/Scars files; four pre-existing do-not-touch skeleton files have BOM and cannot be edited by this lane.
Ruling: Y-094 excludes generated `.nuget`, `bin`, `obj`, and `.git` contents from the repository invariant scan.
Ruling: No Y-row was merged or left unmapped; Y-001 through Y-099 each has exactly one Fact.

Build: `dotnet build Oom.sln -c Release` — succeeded, 0 warnings, 0 errors.
Test: Başarısız! - Başarısız:    96, Başarılı:     3, Atlanan:     0, Toplam:    99, Süre: 26 ms - Oom.Tests.dll (net9.0)
Passing repository invariants: Y-068, Y-081, Y-094.
Guard grep: `grep -rn "Assert.True(false)\|Skip =" tests/Oom.Tests/Scars` — no matches.
Public contract types: 70 (18 classes, 40 records, 6 interfaces, 6 enums); stub methods: 83.
