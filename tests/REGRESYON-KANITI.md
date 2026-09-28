# Regression evidence (SPEC-3.1.0 F7-3 / B10)

Red run of the process-boundary regression oracles against the OLD exe, recorded before any fix lands.

## What was run

- Tree: `surum/3.1.0` wave 0, HEAD `8b5d534` plus this lane's new test files (no product code changed).
- Old exe (defect oracle): `<eski-exe>/oom.exe`
  - sha256 `oom.exe` 4b041a0f142d820780acaa0a6f59631f809f2ecac2fadb40cc789f214b456fec
  - sha256 `oom.dll` 277b301cebdbda33cc1c05e308969d0f16c20da26f15d807e6ed822594be81b0
- SDK: dotnet 9.0.312, Windows 11, Git Bash. Date: 2026-09-28.
- Oracle class: `tests/Oom.Tests/Kabul/Regresyon/RegresyonKabul.cs`, trait `Kabul=Regresyon` (SPEC R11).
- Every test goes through `KabulHarness` only: exe from `OOM_KABUL_EXE`, a `KabulVaultBuilder` vault under the harness root, the harness's own `OOM_LOCALAPPDATA`, `OOM_FAKE_NOW` via the harness (R1). No product API is called.
- Where a command needs the model (sweep/flush, compile), a `claude.cmd` shim in the test's own scratch dir answers with a fixed JSON result; while it is alive PATH is only that dir plus the system dir, so no real claude can be reached. Sweep roots point at the test's scratch transcripts, never `~/.claude/projects`.

## Command

```
dotnet build Oom.sln -c Release --no-restore
OOM_KABUL_EXE="<eski-exe>/oom.exe" \
  dotnet test Oom.sln -c Release --no-build --filter "Kabul=Regresyon" \
  --logger "trx;LogFileName=regresyon-eski-exe.trx" --results-directory <scratch>/trx
```

- Build: 0 warnings, 0 errors.
- Exit code: 1.
- Console: `Başarısız! - Başarısız: 8, Başarılı: 0, Atlanan: 0, Toplam: 8`.

## trx summary

- `outcome=Failed` · total 8 · executed 8 · passed 0 · failed 8 · error 0 · timeout 0 · aborted 0 · notExecuted 0.
- Harness errors: 0. Every failure is raised by the test's final `Assert.True` (lines 43, 57, 73, 95, 128, 159, 188, 221); the trx contains no Timeout/FileNotFound/Json/NullReference/InvalidOperation exception. The two fixture preconditions (A2-03 first sweep, A2-06 100 retries) passed.

## Failed tests and assertion messages (verbatim, stdout tail omitted)

- **A1-01** · context --json ≤ 8000 karakter ve ≤ 8500 UTF-8 bayt (dokunulmamış capChars 16000)
  - `A1-01: context --json text is 18779 chars / 20194 UTF-8 bytes on the real-size fixture with the untouched capChars 16000; SPEC F1-1/B4/B5 require ≤ 8000 chars and ≤ 8500 bytes.`
- **A2-02** · Kurallar.md tam metni oturuma birebir ulaşır
  - `A2-02: the full Kurallar.md text (2312 chars) is not verbatim in what the session receives. SessionStart additionalContext is 18805 chars (limit 10000, above it only a 2000-char preview arrives); present anywhere in the raw hook output: True. SPEC B3: Kurallar is never cut.`
- **A2-02** · Duzeltmeler.md tam metni oturuma birebir ulaşır
  - `A2-02: the full Duzeltmeler.md text (1414 chars) is not verbatim in what the session receives. SessionStart additionalContext is 18805 chars (limit 10000, above it only a 2000-char preview arrives); present anywhere in the raw hook output: True. SPEC B3: Düzeltmeler is never cut.`
- **A5-03** · Journal üstte 09-27, altta 09-17 → Son Journal 09-27'yi seçer
  - `A5-03: [Son Journal] must pick the newest date (09-27, top of the file), not the last heading (09-17, bottom). 09-27 present: False, 09-17 present: True. Journal section as rendered: [Hafıza — Son Journal] ## 2026-09-17 — kabul-journal-en-eski Sonradan alta eklenmiş eski girdi; sentetik journal gövdesi.`
- **A2-03** · 'ok' damgalı 4 turluk oturum 44 tura büyür, mtime −10 saat → skipped=0 ve işlenir
  - `A2-03: a 'ok'-stamped session that grew from 4 to 44 turns with mtime −10 h must be processed: want skipped=0, sessions=1 and a daily anchor for turns ≥ 4; got skipped=1, sessions=0, new-turn anchors [].`
- **A2-05** · 3 FILE bloğundan birinde END FILE eksik → 2 not yazılır, exit 2
  - `A2-05: with block 2 of 3 missing '=== END FILE ===', blocks 1 and 3 must still be written and compile must exit 2; got 0/2 intact notes written [], exit 0.`
  - Old stdout: `derleme 2026-09-27.md: rejected · 0 not · 'knowledge/concepts/kuyu-pompasi-ariza-kaydi.md' bloğu '=== END FILE ===' ile kapatılmamış; bütün koşum reddedildi.`
- **A2-06** · 7 günden eski 100 retry + 0 yeni → doctor 'Son 7 gün ret: %0,0'
  - `A2-06: 100 retry rows older than 7 days and 0 recent must render 'Son 7 gün ret: %0,0'; got 'Son 7 gün ret: %100,0'.`
- **V1-02** · Boş state → doctor 'bekleyen' = log.md'de kaydı olmayan gün sayısı
  - `V1-02: with an empty state, 'Bekleyen daily' must equal the days without a knowledge/log.md compile entry (12 dailies − 9 logged = 3), not the raw daily count 12; doctor shows [12].`

## How each oracle is built

- A1-01: default `KabulVaultBuilder` vault (Threads 60 KB, Last-Session 9.7 KB, `capChars` 16000 untouched); counts `text` of `context --json`.
- A2-02: Kurallar.md (41 lines) and Duzeltmeler.md (26 lines) in the real files' line shape, synthetic lines. Runs `context` as the SessionStart hook (JSON payload on stdin) and applies Claude Code's measured rule: a hook string over 10 000 chars reaches the session only as a 2 000-char preview. On the old exe both files are in the raw output but not in what the session receives.
- A5-03: Journal.md with `2026-09-27` on top, `2026-09-22` in the middle, `2026-09-17` at the bottom.
- A2-03: sweep 1 flushes a 4-turn transcript 'ok' (anchor turns:0-3); the transcript grows to 44 turns with mtime now−10 h; sweep 2 must process it.
- A2-05: `compile` on the fixture daily; the model answer has 3 FILE blocks, block 2 without `=== END FILE ===`, then `=== DONE ===`. Slugs share no token, so the F3-5 duplicate rule cannot hide a block.
- A2-06: one sweep at now−10 days over 100 four-turn transcripts; the model answer fails the summary shape, so each records `retry` (precondition: `retry=100`). Then `doctor --json` at now; every string value is scanned, because the table may show only yellow/red rows.
- V1-02: 12 dailies (2026-09-16 … 2026-09-27), `knowledge/log.md` with `compile | <day>.md` entries for 9 of them, no state; `doctor --json`, every string value scanned.

## Control runs (fixture soundness)

A temporary copy of the class (trait changed, not committed) was run against the same old exe with one fixture knob flipped:

- A2-05 with all three blocks closed: old stdout `derleme 2026-09-27.md: ok · 3 not`; both checked notes exist. The note shape is valid; only the missing END FILE makes the old exe drop everything.
- A2-03 with the grown transcript's mtime at now−2 h instead of −10 h: passed. The fake runner and anchors work; only the 8-hour "stamped and old" rule makes the old exe skip.

## O2 set integrity (F2-1, NB-4)

The private O2 set is pinned by SHA-256 in `RetrieveSetKabul.PrivateSetSha256` so a swapped-in
file cannot silently change what F2-1/NB-4 measure; `RetrieveSetKabul.PrivateSet_...` fails
loudly on a mismatch instead of scoring whatever file happens to be at the path.

- File: the private retrieval set (SPEC-3.1.0.md R2's fixed location, outside this repo; path
  redacted here per the L3-privacy hygiene gate — see `OOM_KABUL_EVAL_SET` / `PrivateEval`)
- sha256: `33bf01d31c7d990c30238947feaabf5bf629cbf7bda62aede441f7672096464a`
- Measured (Git Bash): `sha256sum "<private set path>"`
- meta.set_id `O2-arama-seti`, meta.author `independent-lane-O2`, 10 positive + 3 negative
  queries, unique ids, every positive has a nonempty `expected` list — checked structurally by
  `RetrieveSetKabul.ValidateFrozenStructure` on every run, in addition to the digest.
- Re-freezing the set (new facts, new digest) is a reviewed step: update both the pinned
  constant and this record together, never one without the other.

## Retrieval set on the old exe (F2-1, F2-5, R2)

Command:

```
OOM_KABUL_EXE="<eski-exe>/oom.exe" \
OOM_KABUL_VAULT="<private vault-kopya path — redacted, see PrivateEval/OOM_KABUL_VAULT>" \
  dotnet test Oom.sln -c Release --no-build --filter "Kabul=RetrieveSet" \
  --logger "trx;LogFileName=retrieveset-eski-exe.trx" --results-directory <scratch>/trx
```

- Exit code 1 · trx total 2 · failed 2 · error 0.
- Private set (O2, frozen, outside the repo; only counts are recorded here, no query text or paths):
  - `F2-1: private set O2-arama-seti: 6/10 positives have an expected path in the top 3; the gate is ≥ 8.`
  - P1–P4 (daily / hand-layer only): 0/4 — matches the R2 baseline.
  - P5–P8 (knowledge/concepts only): 4/4.
  - P9–P10 (both): 2/2, each through its concept path.
  - N1–N3 (negatives, measured, not gated): 3 hits printed for each.
- Synthetic set (`tests/fixtures/retrieve-set.json`, vault generated in `RetrieveSetKabul.cs`):
  - `F2-5: synthetic set kabul-sentetik-arama-seti: 6/10 positives in the top 3 (gate ≥ 8); hard checks failed: [S1 required, E1 first-not Duzeltmeler.md].`
  - S1–S3 (daily only): MISS; S4 (Duzeltmeler only): MISS — printed as `knowledge/concepts/Duzeltmeler.md#1`, a path that does not exist.
  - S5–S8 (concepts only): hit; S9–S10 (both): hit through the concept path.
  - E1 `Eylül`: first hit `knowledge/concepts/Duzeltmeler.md#2`.
  - N1 0 hits, N2 0 hits, N3 2 hits (measured, not gated).

## Release evidence (wave 5 · F7-3 final · F3-7 with B8 · NB-13)

Measured on the real machine on 2026-09-28, tree `surum/3.1.0` at HEAD `da8138d` (wave 4.5), Release build, dotnet 9.0.312, Git Bash.

- Old exe: `<eski-exe>/oom.exe`, sha256 `oom.exe` 4b041a0f142d820780acaa0a6f59631f809f2ecac2fadb40cc789f214b456fec (unchanged since wave 0).
- New exe: `src/Oom/bin/Release/net9.0/oom.exe`, `--version` → `3.1.0+da8138d`; sha256 `oom.exe` ea022df70a1393b3a35025c5731ef12530447c072e330824d35f75911a23ff3f, `oom.dll` 5f2a7c49444e9b3a4cb9323329682ae331588e303c86e12dd557d42a53561560.
- The private Kabul inputs are passed through their environment variables only (`OOM_KABUL_PRIVATE`, `OOM_KABUL_VAULT`, `OOM_KABUL_EVAL_SET`); their paths are private and not recorded here.

### Unfiltered Kabul suite, old exe vs new exe

"Unfiltered Kabul suite" = every test in the `Oom.Tests.Kabul` namespace with no trait filter: `Kabul=Regresyon`, `Kabul=RetrieveSet` and `Kabul=OzelVault` all run.

```
dotnet build Oom.sln -c Release --no-restore
OOM_KABUL_PRIVATE=<private> OOM_KABUL_VAULT=<private> OOM_KABUL_EVAL_SET=<private> \
OOM_KABUL_EXE="<eski-exe>/oom.exe" \
  dotnet test Oom.sln -c Release --no-build --filter "FullyQualifiedName~Oom.Tests.Kabul" \
  --logger "trx;LogFileName=kabul-eski-exe.trx" --results-directory <scratch>/trx
OOM_KABUL_PRIVATE=<private> OOM_KABUL_VAULT=<private> OOM_KABUL_EVAL_SET=<private> \
  dotnet test Oom.sln -c Release --no-build --filter "FullyQualifiedName~Oom.Tests.Kabul" \
  --logger "trx;LogFileName=kabul-yeni-exe.trx" --results-directory <scratch>/trx
```

- Build: 0 warnings, 0 errors.
- Old exe: exit 1 · 137 s · console `Başarısız! - Başarısız: 84, Başarılı: 81, Atlanan: 0, Toplam: 165`.
  - trx: total 165 · executed 165 · passed 81 · failed 84 · error 0 · timeout 0 · aborted 0 · notExecuted 0.
  - All 84 failures are assertion failures: 44 carry the test's own assertion message, 21 `Assert.Contains()`, 9 `Assert.Equal()`, 5 `Assert.NotEqual()`, 4 `Assert.DoesNotContain()`, 1 `Assert.DoesNotMatch()`.
  - Build or harness errors: 0. No failure message names a `System.*` or `Microsoft.*` exception, and no failure's top stack frame is in `KabulHarness`, `KabulVaultBuilder` or `FakeClaude`; every one is raised in the test method.
  - Failed per class: RegresyonKabul 8 (all 8 wave-0 oracles), ContextKabul 13, CompileKabul 11, DoctorKabul 10, CliKabul 6, FlushKabul 5, RetrieveKabul 4, LegacyStateKabul 4, RunnerKabul 4, SweepKabul 4, ConfigLoadErrorKabul 3, ContextNoticeKabul 3, RetrieveSetKabul 2, DocsKabul 2, VaultPathKabul 2, DeadCodeCutKabul 1, HarnessSmokeTests 1, TestRootKabul 1.
- New exe: exit 0 · 57 s · console `Başarılı! - Başarısız: 0, Başarılı: 165, Atlanan: 0, Toplam: 165`.
  - trx: total 165 · executed 165 · passed 165 · failed 0 · error 0.

The whole solution, same private env (the runsettings file excludes only the `Category=IntentionalRed` canary):

```
dotnet test Oom.sln -c Release --no-build --logger "trx;LogFileName=tum-yeni-exe.trx" --results-directory <scratch>/trx
dotnet test Oom.sln -c Release --no-build --filter "Kabul!=Regresyon&Kabul!=RetrieveSet&Category!=IntentionalRed"
```

- No filter: exit 0 · `Başarısız: 0, Başarılı: 370, Atlanan: 0, Toplam: 370`; trx failed 0 · error 0.
- Interim gate (R11): exit 0 · `Başarısız: 0, Başarılı: 360, Atlanan: 0, Toplam: 360`.

### Real compile on a scratch copy of the vault copy (F3-7 with B8)

- The private vault copy was copied (`cp -a`, `du -s` equal) to a scratch directory `<workspace>/derleme-deneme/vault`; `OOM_LOCALAPPDATA` pointed at an empty `<workspace>/derleme-deneme/localappdata`. The vault copy itself and the live vault were never compiled.
- Pending dailies, derived by script and not by oom: `daily/*.md` whose name has no `## … compile | <day>.md` heading in `knowledge/log.md`, with an empty state (so no day is `ingested`):

```
grep -oE '^##.*compile \| [^ ]+\.md[[:space:]]*$' knowledge/log.md | sed -E 's/.*compile \| ([^ ]+\.md).*/\1/' | tr -d '\r' | sort -u > logged.txt
ls daily | grep '\.md$' | sort > dailies.txt
comm -23 dailies.txt logged.txt | wc -l
```

- 192 dailies · 176 logged days · **16 pending** (B8: 16 at the snapshot). The five B8 days 08-21, 08-26, 08-27, 09-03 and 09-04 are all logged, so none of them is pending.
- `oom --vault <scratch vault> compile --dry-run` agreed: `bekleyen daily=16 · bu koşumda=3 · kavram=846`, exit 0, and the state directory stayed empty (0 entries) after it.
- Runs: `maxDailiesPerRun` is 3, so `oom --vault <scratch vault> compile` was repeated until the dry run showed 0 pending. Model: the Claude subscription through the `claude` CLI, one campaign.

| Run | Pending before | Dailies sent | ok · notes | rejected | Exit | Duration |
|---|---|---|---|---|---|---|
| 1 | 16 | 3 | 1 · 5 | 2 | 2 | 334 s |
| 2 | 15 | 3 | 2 · 14 | 1 | 2 | 397 s |
| 3 | 13 | 3 | 2 · 8 | 1 | 2 | 311 s |
| 4 | 11 | 3 | 3 · 16 | 0 | 0 | 264 s |
| 5 | 8 | 3 | 2 · 14 | 1 | 2 | 559 s |
| 6 | 6 | 3 | 2 · 16 | 1 | 2 | 526 s |
| 7 | 4 | 3 | 2 · 11 | 1 | 2 | 355 s |
| 8 | 2 | 2 | 1 · 5 | 1 | 2 | 307 s |
| 9 | 1 | 1 | 1 · 9 | 0 | 0 | 156 s |
| 10 | 0 | — | — | — | — | dry run only |

- Totals: 9 compile runs · 3209 s · 24 model calls · 16 `ok` · 8 `rejected` · 98 notes written · 0 `parked`, 0 `partial`, 0 runner errors.
- After the last run: dry run `bekleyen daily=0 · bu koşumda=0`, exit 0; one more `oom compile` printed `derleme: atlandı`, exit 0.
- State (`daily_ingest`, opened read-only): 16 rows, all `ingested`. Exactly the 16 script-derived days gained a new `compile |` heading in `knowledge/log.md` (393 → 409 headings; the set of newly logged days equals the derived pending set).
- Files: 91 new files in `knowledge/concepts` (846 → 937), 5 existing concept notes updated, 0 deleted; the rest of the change is `knowledge/log.md`, `knowledge/index.md`, `knowledge/index-full.md` and 11 hub files. `diff -rq` of everything outside `knowledge/` against the vault copy: no difference.

Checks:

- **Near-duplicate slugs: 0.** Token Jaccard on `-` tokens (the rule `Compile.SlugJaccard` applies) over the 91 new slugs: new–new pairs ≥ 0.6: 0; new–existing pairs (91 × 846) ≥ 0.6: 0; highest value measured 0.364. No block was held back as `kopya:` in this campaign. For reference, the 846 pre-existing slugs already contain 46 pairs ≥ 0.6 among themselves; that is legacy content, not produced by this run.
- **The five B8 days: 0 new files.** 08-21, 08-26, 08-27, 09-03 and 09-04 have 0 `daily_ingest` rows, their `compile |` heading counts in `knowledge/log.md` are unchanged (1, 3, 1, 1, 1 before and after), and none was sent to the model, so no note was written for them.
- **A fully rejected day is visible, with exit 2.** 8 of the 24 model answers were rejected whole: the day printed `derleme <day>.md: rejected · 0 not · reddedildi: '<path>' bloğu '=== END FILE ===' ile kapatılmamış · …` (51 block rejections listed in total), and every run that contained one exited 2. Each such day stayed pending and compiled `ok` on a later run (6 days after one retry, 1 day after two).
- Observation for the driver, not changed here: in every rejected answer ALL blocks lacked `=== END FILE ===`, i.e. the model dropped the closing marker wholesale in 8 of 24 calls. The retry path recovered every day, but the rate is worth a look at the compile prompt's format instruction.

### Diff size since the audited code (NB-13, R8)

```
git diff --stat 0f6b983..HEAD -- src/
git diff --stat 0f6b983..HEAD -- tests/
git diff --stat 0f6b983..HEAD -- docs/
git diff --stat 0f6b983..HEAD -- . ':!src/' ':!tests/' ':!docs/'
git diff --shortstat 0f6b983..HEAD
```

| Scope | Files | Insertions | Deletions |
|---|---|---|---|
| `src/` | 43 | 3691 | 1786 |
| `tests/` | 59 | 12639 | 1947 |
| `docs/` | 1 | 40 | 0 |
| rest (`.gitattributes`, CI workflow, CHANGELOG, CONTRIBUTING, README, SECURITY, `kit/manifest.json`, `tools/kit/manifest-hash.ps1`) | 8 | 405 | 52 |
| **total** | 111 | 16775 | 3785 |

`HEAD` here is `da8138d`; this lane's only change (this file) is not in the count. R8: reported, no numeric ceiling.
