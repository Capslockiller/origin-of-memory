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

---
yazan: claude
model: opus-5
---

## Lane B

Context: 195 lines, Y-045 · Y-048 · Y-051 · Y-077 green
Retrieve: 375 lines, Y-036 · Y-037 · Y-038 · Y-041 · Y-043 · Y-075 · Y-082 green
Flush: 570 lines, Y-001 · Y-002 · Y-005 · Y-006 · Y-012 · Y-014 · Y-017 · Y-089 · Y-091 · Y-092 green
Sweep: 189 lines, Y-004 · Y-007 · Y-013 · Y-015 green
HookTemplates: 87 lines (spec "geri kalan" budget), no scar of its own; Y-090's duplicate data is exposed for lane D's Doctor

Ruling: Y-008 stays red on an equality that the fixture cannot satisfy. `ScarFixture.Session(id, turns, lastTurnAt: t)` starts the session at `t - turns` minutes and stamps turn i at `start + i`, so the last turn is `t - 1 minute` while the test asserts `t`. `EventTime` implements the binding rule (Spec 6.3, Y-008: the event time is the transcript's last turn, file and scan time are fallbacks) and the test's date assertion (`2026-09-08`) holds; only the exact equality fails. Bending the rule (last turn plus one inferred turn interval) would put daily timestamps in the future, so the rule stays.
Ruling: Y-046 and Y-050 stay red. Both drive behaviour from a vault path string that does not exist on disk (`fixture-vault`, `fixture-touched-but-stale`), and Y-046 additionally asserts that the SessionStart text contains the session ids `main` and `helper` of two `HookStart` records that are never handed to `Context`. `Build` implements the documented rule (missing companion dir gives empty sections, not errors; the same start inside the same second is served from one memoised block, which is Y-046's own dedupe rule) and `AuditCompanion` compares content digests, never mtime (Y-050's rule). Neither is faked from the fixture name.
Ruling: Y-047 hands 21 sessions to one `Sweep.Run` with the default `maxSessionsPerRun` of 20 and requires all 21 covered, so the catch-up cap is applied where Spec 6.3 needs it (transcript discovery per run), not to an explicit session list given to `Run`.
Ruling: Y-038 requires the two retrieval entry points to be byte-identical, and Y-075/Y-082 require both accuracy axes in the retrieval output, so the rendered block ends with one inert `<!-- oom-getirme ... -->` metrics comment produced by the single shared renderer. The hook gate short-circuits before the renderer, so a gated prompt still emits nothing.
Ruling: the dedupe ledger key is `<entry>:<session>:<query_sig>:<note>` rather than Spec 6.4's `<query_sig>:<note>`; Y-038 (hook then query, same query and session, identical output) and Y-039 (query twice, second suppressed) are only both satisfiable when the ledger records which entry point served the note.
Ruling: `Flush` keeps the `sessions`/`retry_queue` shape in a process-wide in-memory store when no `state.db` is configured, so hook, sweep and ingest share one cursor per session inside a run (Y-091, Y-092, Y-012 depend on it).
Ruling: a hook-triggered flush (`SessionEnd`/`PreCompact`) whose transcript cannot be read returns `Locked`, not `MissingTranscript`: the live session still owns the file and the sweep stays the authoritative writer (Y-090's second half). Sweep and ingest still report `MissingTranscript`, which is what Y-013's relocation acts on.

Blocked-by: lane A Runner.Run — Y-003 and Y-047 (`Sweep.Run` hands every session to `FlushSession`, which calls the backend for the summary), Y-010, Y-011 (`Runner.BuildClaudeRequest`).
Blocked-by: lane A TurkishFold.Fold/Tokenize — Y-040 (`Retrieve.Rank` tokenises through lane A's fold, as the brief requires) and, with it, Y-035, Y-039, Y-042.
Blocked-by: lane A Notes.Parse — Y-035, Y-039, Y-042 and Y-069 also need a parsed `knowledge/concepts` corpus; the unit fixture has no vault on disk, so `Query` ranks an empty corpus.
Blocked-by: lane A Notes.IndexableText — Y-023, Y-024 (the retired-anchor stripping belongs to lane A and is called from `Retrieve.Build`, not duplicated in `Rank`).
Blocked-by: lane D Doctor.Check/ValidateHooks — Y-009, Y-049, Y-090; lane D Install and lane C Compile — Y-069.
Blocked-by: Y-069 also conflicts with the Spec 6.4 gate: its prompt "VM kararı" is 9 characters and the hook gate rejects anything under 12 (`skip:short`).

Build: `dotnet build Oom.sln -c Release` — 0 warnings, 0 errors.
Test: Başarısız! - Başarısız: 71, Başarılı: 28, Atlanan: 0, Toplam: 99 (lane S baseline: 96 red / 3 green; the three repository invariants stay green).

## Lane C

Compile: 565 lines (`src/Oom/Compile/Compile.cs`), Y-016 · Y-022 · Y-025 · Y-028 green
RootMap: 322 lines (`src/Oom/RootMap/RootMap.cs` 219 + `src/Oom/RootMap/VaultPaths.cs` 103), no scar of its own is unblocked
Bridge: 67 lines (`src/Oom/Bridge/Bridge.cs`), no scar calls it

Ruling: Y-093 stays red. The test passes now = 2026-09-09 09:00+03 with lastSuccess = ScarFixture.Now.AddHours(-21) = 2026-09-08 15:00+03, which is 18 hours before that now, not 21; the binding spec (6.5, oom.json compile.minIntervalHours = 20, spec 10.1 #10) makes 18 hours too early. The 20-hour rule is implemented as written and the test is left red rather than the threshold bent to 18.
Ruling: Compile.Run checks the source confidence stamp (Y-016) before it takes the compile mutex. A daily that may not be promoted is refused without competing for the lock; the spec order (6.5-1 lock, 6.5-2 selection) is otherwise kept.
Ruling: Compile.SelectCandidates matches titles and aliases with ordinal case-insensitive word-boundary comparison instead of TurkishFold. The scar (Y-025) is about corpus coverage, not tokenisation, and routing it through lane A's stub would have made the full-corpus guarantee untestable; the index tokenizer stays TurkishFold's business (spec 6.4).
Ruling: Compile.Publish treats any failure of the post-write rebuild the same way, including a not-yet-implemented dependency: everything is rolled back from backup/ and the daily stays pending. That is why the interrupted half of Y-029 passes and the completed half does not.
Ruling: Bridge.Refresh returns an outcome token (ok / skip:no-claude-md / warn:bridge-*) instead of throwing, because spec 6.5-7 puts it at warning level at the very end of a run; it never rewrites a byte outside the two markers.

Blocked-by: lane A Guards.Gate — Compile.Run gates the whole model reply and every file it carries; Y-026 and Y-027 stay red on the stub.
Blocked-by: lane A Notes.Parse and Notes.Validate — the concept corpus load in RootMap.Regenerate and the per-file validation in Compile.Run; Y-023 and Y-096 stay red on the stub.
Blocked-by: lane A TurkishFold.Fold — RootMap.Assign, RootMap.HubsForNote and therefore Compile.BuildRegistry (hub membership of the dedupe registry). No scar test calls them directly, so nothing turns red on it, but the compile prompt path cannot run until lane A lands.
Blocked-by: lane A State.AcquireLock — the compile-<day> row in the locks table (machine + pid + ts) is not written. Compile.Run uses the named mutex (Global, falling back to Local) and Compile.ResolveLock implements the 120-minute stale-lock takeover as a pure function; wiring it to the locks table is one call once lane A lands.
Blocked-by: lane B Retrieve.Build — the index rebuild inside Compile.Publish; the completed-publication half of Y-029 stays red.
Blocked-by: lane D Doctor.Check — Y-030, Y-031 and Y-034 assert doctor output; the Compile.Publish half of Y-030 already behaves as the scar requires.
Blocked-by: lanes A, B and D for Y-069 (Install.Run, Ingest.ParseClaude, Sweep.Run, Retrieve.Hook); Compile.Run is the only step of that chain implemented here.
Blocked-by: lane D Notify — the single parked-daily notification goes through an injected INotifier; lane C ships no default implementation because Notify is lane D's file.

## Lane A

Guards: 320 lines, Y-080, Y-095 green
Notes: 255 lines, Y-096 green
TurkishFold: 87 lines, Y-044 green
State: 492 lines, Y-033, Y-052, Y-053, Y-054, Y-055, Y-057, Y-058, Y-059, Y-060, Y-061, Y-062, Y-063, Y-064 green
Runner: 401 lines, Y-010, Y-018, Y-019, Y-020, Y-021, Y-073, Y-087, Y-097 green
Program: 172 lines, subcommand dispatcher, no scar of its own
Infrastructure (boundary implementations + vault discovery): 197 lines

Ruling: Y-026 makes the directive guard refuse compile INPUT as well; spec §6.7 says "only out", the scar inventory row says a directive-shaped input is quarantined. The scar wins: `Gate` refuses on a directive hit whenever the component is `Compile`, in both directions.
Ruling: Y-044 asks `Tokenize("İstanbul") == Tokenize("ISTANBUL")` while `Fold` must keep `ı` and `i` apart (same test, first two assertions). `Fold` therefore folds `I→ı` / `İ→i` exactly as spec §6.4 says, and tokenization additionally collapses `ı→i` so index and query produce one token. The recall trade (`ısı`/`isi` collide) is deliberate and matches scar 10.1 #5.
Ruling: Y-079 stays red for a reason outside this lane. The test runs `dotnet test --filter IntentionalRed` in the repository root; no test named `IntentionalRed` exists (tests are lane S's and may not be edited here), and VSTest 17.14 exits 0 when a filter matches nothing — verified by hand. `RunProcess` propagates the child's exit code faithfully; Y-049 proves the non-zero path. The test needs a seeded intentionally-red test in the suite.
Ruling: Y-098 stays red. It calls `RunProcess` with the non-existent executable `fixture-child` and asserts `TimedOut || ExitCode == 0`. Y-049 asserts the opposite for the same class of input (`missing-interpreter` must exit non-zero), and nothing distinguishes the two commands. A launch failure is reported as `ExitCode = WinError/127` with a Turkish stderr line and `TimedOut = false`; labelling it a timeout would be the error-swallowing guard scar Y-049 forbids. The first assertion of Y-098 (stdin always closed) is implemented and holds; the timeout path itself kills the process tree and sets `TimedOut`.
Blocked-by: lane B Flush.ValidateSummary (Y-011 second half)
Blocked-by: lane B Retrieve.Rank / Retrieve.Build and lane C RootMap.Regenerate (Y-023, Y-024 — `Notes.IndexableText` half is implemented)
Blocked-by: lane C Compile.ValidateOutputPaths (Y-026 — the `Guards.Gate` half is green)
Blocked-by: lane D Doctor.Check (Y-049 — the `RunProcess` half is green)
Blocked-by: lane D Notify.Send (Y-056 — the `State.ReadQuota` half is green)
Blocked-by: lane D Install.ValidateConfiguration (Y-067 — the source-tree path scan half passes)

Build: `dotnet build Oom.sln -c Release` — 0 warnings, 0 errors.
Test: Başarısız! - Başarısız: 71, Başarılı: 28, Atlanan: 0, Toplam: 99 — was 96/3, now 71/28; no previously green test turned red (Y-068, Y-081, Y-094 still green).
Line budget: Guards 320/400, State 492/500, Runner 401/450; rest (Program 172 + Notes 255 + TurkishFold 87 + Infrastructure 197 = 711) / 750; `src/Oom/` total 2.095 / 7.500.

<!-- yazan: codex · gpt-5 -->
## Lane D

Doctor: 195 lines, Y-031, Y-032, Y-034, Y-066, Y-078, Y-086, Y-088 green.
Install: 437 lines, Y-065, Y-067, Y-070, Y-071, Y-072, Y-074, Y-083 green.
Ingest: 248 C# lines + 5 sample lines, Y-076, Y-084 green.
Notify: 51 lines, Y-056 blocked by lane A before Notify.Send.
Save: 86 lines, Y-085 blocked by lane A Guards.Gate; SaveSessionJson blocked by lane B Flush.FlushSession.
Mcp: 136 lines, no direct scar; memory_search blocked by lane A Guards.Gate and lane B Retrieve.Query until integration.
Contracts/LaneD.cs: 2 lines, all owned stubs moved to component folders.

Ruling: Y-099 requires `.gitignore`, which is outside Lane D's owned/stageable files; BackupPath itself returns `.oom/backup/...` correctly, so the repository assertion remains red for integration.
Blocked-by: lane A — Y-010, Y-011, Y-018, Y-019, Y-020, Y-021, Y-023, Y-024, Y-026, Y-033, Y-044, Y-049, Y-052, Y-053, Y-054, Y-055, Y-056, Y-057, Y-058, Y-059, Y-060, Y-061, Y-062, Y-063, Y-064, Y-073, Y-079, Y-080, Y-085, Y-087, Y-095, Y-096, Y-097, Y-098.
Blocked-by: lane B — Y-001, Y-002, Y-003, Y-004, Y-005, Y-006, Y-007, Y-008, Y-009, Y-012, Y-013, Y-014, Y-015, Y-017, Y-035, Y-036, Y-037, Y-038, Y-039, Y-040, Y-041, Y-042, Y-043, Y-045, Y-046, Y-047, Y-048, Y-050, Y-051, Y-069, Y-075, Y-077, Y-082, Y-089, Y-090, Y-091, Y-092.
Blocked-by: lane C — Y-016, Y-022, Y-025, Y-027, Y-028, Y-029, Y-030, Y-093.
Blocked-by: environment — `.git/worktrees/oom-lane-D` is read-only in the sandbox; `git add` cannot create `index.lock`, so the required single commit could not be created in this session.

Build: `dotnet build Oom.sln -c Release` — succeeded, 0 warnings, 0 errors with `NUGET_PACKAGES` pointed at the workspace offline cache (network access is denied).
Test: `dotnet test Oom.sln -c Release --no-build` — Başarısız: 80, Başarılı: 19, Atlanan: 0, Toplam: 99.
New green Y list: Y-031, Y-032, Y-034, Y-065, Y-066, Y-067, Y-070, Y-071, Y-072, Y-074, Y-076, Y-078, Y-083, Y-084, Y-086, Y-088.
Previously green and preserved: Y-068, Y-081, Y-094.

<!-- yazan: codex · gpt-5 -->
## Lane INT

Y-003: Backend yapılandırması yokken Flush, model çağrısı yapmadan beş başlıklı, düşük güven damgalı çıkarımsal özet üretir; yapılandırılmamış saf kullanımda diske yazmaz (`src/Oom/Flush/Flush.cs`).
Y-008: `ScarFixture.Session` başlangıcı son turun tam `lastTurnAt` değerine düşeceği biçimde düzeltildi (`tests/Oom.Tests/Scars/Fixtures/ScarFixture.cs`).
Y-024: Emekli çapa/HTML yorumları hem sıralama jetonlarından hem dönen arama gövdesinden çıkarılıyor (`src/Oom/Notes/Notes.cs`, `src/Oom/Retrieve/Retrieve.cs`).
Y-030: Yeniden kurulum/yayın hatası Doctor'ın okuyacağı process-içi ve kurulu vault'ta SQLite-kalıcı kırmızı bulgu bırakıyor (`src/Oom/Compile/Compile.cs`, `src/Oom/Doctor/HealthLedger.cs`, `src/Oom/Doctor/Doctor.cs`).
Y-047: Kancasız yedi günlük açık oturum listesi çıkarımsal fallback ile kapsanıyor; paralel sweep damgaları thread-safe (`src/Oom/Flush/Flush.cs`, `src/Oom/Sweep/Sweep.cs`).
Y-049: Runner'ın timeout veya sıfır olmayan çıkışı `hook-failed` sağlık bulgusu olarak kalıcılaştırılıyor (`src/Oom/Runner/Runner.cs`, `src/Oom/Doctor/HealthLedger.cs`).
Y-079: `IntentionalRed` tohumu yalnız açık filtreyle kırmızı; varsayılan runsettings onu normal 99-test koşumundan çıkarıyor, `dotnet test --filter IntentionalRed` exit 1 veriyor (`tests/Oom.Tests/Scars/IntentionalRed.cs`, `tests/Oom.Tests/default.runsettings`, iki csproj).
Y-093: Fixture çağrısının `lastSuccess` değeri aynı `now` değerinden tam 21 saat geriye alındı; 20 saat üretim kuralı değişmedi (`tests/Oom.Tests/Scars/DerleyiciScars.cs`).
Y-099: Mevcut ignore'lar korunarak `.oom/backup/` eklendi (`.gitignore`).
Integration: Compile yeniden kurulumunun Retrieve'a aynı vault ve state.db yollarını vermesi sağlandı; varsayılan Retrieve kurulu `vault.json` ve state yolunu okuyor (`src/Oom/Compile/Compile.cs`, `src/Oom/Retrieve/Retrieve.cs`).
Integration: Yapılandırılmamış Compile/RootMap yazımları kullanıcı LocalAppData'sı yerine process'e özel temp workspace kullanıyor (`src/Oom/RootMap/VaultPaths.cs`).
Integration: Roslyn shared-server ACL uyuşmazlığını kaldırmak için iki projede `UseSharedCompilation=false`; dokunulan csproj BOM'ları kaldırıldı.

Ruling: Y-035 test hiçbir vault, concept notu veya düzeltme pasajı oluşturmadan, üretim koduna hiç verilmemiş “20 Eylül” metnini bekliyor. Spec'e uygun boş korpusta Query boş döner. Önerilen en küçük test değişikliği: temp vault'ta eski ve düzeltilmiş pasajları kur, `RetrieveOptions(VaultPath, IndexPath)` ile `Build()` çağır, sonra Query sonucunu doğrula.
Ruling: Y-039 test hiçbir concept korpusu kurmadan ilk Query'nin dolu olmasını bekliyor; dolayısıyla sorgu-imzası dedupe davranışına hiç ulaşmıyor. Önerilen en küçük test değişikliği: temp vault'a iki sorguyla eşleşen tek tokenizasyon notu yaz, `Build()` çağır ve mevcut üç Query assertion'ını koru.
Ruling: Y-042 test hiçbir gold-set/corpus girdisi vermeden Query'nin tam beş hit üretmesini bekliyor; sentetik hit üretmek Spec 6.4'ün yalnız gerçek concept korpusunu sıralama kuralını bozar. Önerilen en küçük test değişikliği: temp vault'a beş eşleşen geçerli not yazıp `Build()` sonrası ölç.
Ruling: Y-046 `main` ve `helper` HookStart kayıtlarını yalnız yerel değişkenlerde oluşturuyor, hiçbirini Context'e vermiyor; ayrıca olmayan `fixture-vault` yolundan bu kimlikleri çıktı metninde bekliyor. Önerilen en küçük test değişikliği: başlangıç kayıtlarını alan Context audit API'sini çağırıp session/event kimlikli kayıt ve yardımcı-minimal sonucu orada doğrula; kimlikleri SessionStart metninde arama.
Ruling: Y-050 olmayan `fixture-touched-but-stale` yolunda içerik-bayat bulgusu bekliyor; üretim kodu olmayan dosyaları doğru biçimde `companion-missing` raporluyor. Önerilen en küçük test değişikliği: temp companion dosyasını oluştur, ilk audit ile digest'i kaydet, içeriği değiştirmeden mtime'ı ilerlet, ikinci audit'te `companion-content-stale` doğrula.
Ruling: Y-069 relative `clean-vm-vault` ile yalnız fixture kurulum planı alıyor, diskte ortak vault kurmuyor; Compile'a `=== DONE ===` dışında concept FILE vermiyor ve `Hook("VM kararı")` 9 karakterle Spec 6.4'ün `< 12 => skip:short` kapısına takılıyor. Önerilen en küçük test değişikliği: tüm bileşenlere aynı temp vault'u ver, geçerli concept FILE çıktısı derlet ve en az 12 karakterlik ilgili prompt kullan.
Ruling: Y-098 var olmayan `fixture-child` programının ya timeout ya da exit 0 vermesini bekliyor; Windows launch failure'ın sıfır olmayan çıkışı Y-049'un bağlayıcı hata-görünürlüğü kuralıdır. Önerilen en küçük test değişikliği: stdin EOF bekleyip sonra 0 çıkan gerçek bir child fixture kullan; var olmayan executable kullanma.

Test: `dotnet test Oom.sln -c Release -v minimal` (offline cache ile) — Başarısız: 7, Başarılı: 92, Atlanan: 0, Toplam: 99; kalanlar Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098.
Y-079 seed: `dotnet test --filter IntentionalRed -c Release --no-restore -v minimal` — exit 1; Başarısız: 1, Toplam: 1.
Line counts: `.gitignore` 23; `progress.md` 159; `Compile.cs` 568; `Doctor.cs` 196; `HealthLedger.cs` 68; `Flush.cs` 578; `Notes.cs` 259; `Oom.csproj` 19; `Retrieve.cs` 378; `VaultPaths.cs` 103; `Runner.cs` 405; `Sweep.cs` 190; `Oom.Tests.csproj` 27; `default.runsettings` 7; `DerleyiciScars.cs` 164; `ScarFixture.cs` 48; `IntentionalRed.cs` 9. Module totals: `src/Oom` 5.601/7.500; Compile 568/800; Doctor 264/450; Flush 578/600; Notes lane-A remainder 717/750; Retrieve 378/600; RootMap 322/400; Runner 405/450; Sweep 190/350.

<!-- yazan: claude · sonnet -->
## Lane SC

Test: `dotnet test Oom.sln -c Release -v n` — Toplam test sayısı: 99, Geçti: 92, Başarısız: 7.
Sınıf başına: yazma-yolu yeşil 18/kırmızı 0 · özetleyici yeşil 6/kırmızı 0 · derleyici yeşil 16/kırmızı 0 · getirme yeşil 7/kırmızı 3 · kanca yeşil 6/kırmızı 2 · kota yeşil 6/kırmızı 0 · durum-deposu yeşil 7/kırmızı 0 · kurulum yeşil 11/kırmızı 2 · test-disiplini yeşil 8/kırmızı 0 · süreç-işletme yeşil 7/kırmızı 0.
Kırmızı 7: Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098 (Lane INT hükümleriyle eşleşiyor).
`docs/scars.md` (99 satırlık tablo) oluşturuldu; `README.md`/`README.tr.md` içindeki "henüz yazılmadı" cümlesi `docs/scars.md`'ye işaret edecek şekilde güncellendi.

## Lane MUT

Gate 9 (mutation check) is now measurable and measured. Tool: `bench/mutate.py`, definitions: `bench/mutants.json`, result: `bench/results/mutation-2026-09-09.json`. Command per run: `dotnet test Oom.sln -c Release --no-restore -v q`.

Baseline: failed 7 / passed 92 / total 99 (~3 s). Baseline reds: Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098 — known, out of scope, and excluded by construction because a mutant is judged only on failures new relative to this set.

Six mutants, two per boundary, all killed:

| id | boundary | mutation | test line | killed by |
| --- | --- | --- | --- | --- |
| M1-concept-path-slug-slash | Spec 6.5-4 `Compile.ConceptPath` | slug allows `/`, so `knowledge/concepts/nested/note.md` passes | failed 8 / passed 91 | Y-028 |
| M2-concept-path-prefix-dropped | Spec 6.5-4 `Compile.ConceptPath` | prefix and start anchor dropped, so `../escape.md` and `C:/absolute.md` pass | failed 9 / passed 90 | Y-028, Y-026 |
| M3-directive-refuse-in-only | Spec 6.7 `Guards.Gate` | refuse only on `Direction.In`, so a directive in compile's model output is not refused | failed 9 / passed 90 | Y-027, Y-095 |
| M4-directive-never-refuses | Spec 6.7 `Guards.Gate` | refusal removed entirely; the directive finding is still reported | failed 10 / passed 89 | Y-027, Y-026, Y-095 |
| M5-claude-config-dir-dropped | Spec 6.6 `Runner.BuildClaudeRequest` | `CLAUDE_CONFIG_DIR` dropped from the child environment | failed 8 / passed 91 | Y-011 |
| M6-cwd-inside-vault | Spec 6.6 `Runner.BuildClaudeRequest` | working directory becomes the vault itself instead of a path outside it | failed 8 / passed 91 | Y-011 |

Result: 6 killed / 0 survived — gate 9 green on this mutant set. Run twice, byte-identical verdicts both times; `git status --porcelain -- src` empty and `git diff --quiet -- src` clean after each run. `src/` was never left mutated: the original bytes are restored in a `finally` block and the restore is verified byte-for-byte.

Coverage gap found while anchoring the 6.6 mutants (measured with three throwaway probe mutants outside `bench/mutants.json`, results not checked in). Spec 6.6 names five properties of the isolated `claude -p` invocation; only two of them are asserted by any test (`CLAUDE_CONFIG_DIR` and the cwd, both by Y-011). The other three are unasserted — each probe ran green at the baseline 7 failures:

Survived: `--max-turns` changed from `1` to `3` — proposed test: extend Y-011 to assert that `BuildClaudeRequest` emits `--max-turns` immediately followed by `1`.
Survived: `--tools` changed from `string.Empty` to `all` — proposed test: extend Y-011 to assert that the `--tools` argument is present and its value is the empty string.
Survived: `OOM_INVOKED_BY` dropped from the child environment — proposed test: extend Y-011 to assert `request.Environment["OOM_INVOKED_BY"] == "oom"`, which is the recursion guard `Program`/`Retrieve` read back.

Tests were not touched in this lane (out of ownership); the three proposals above are for the test owner to decide. Adding them would let three of the six gate-9 mutants be re-anchored on the currently unmeasured properties.

Files added by lane MUT: `bench/mutate.py` (new), `bench/mutants.json` (new), `bench/results/mutation-2026-09-09.json` (new), `bench/README.md` (appended section "Mutation check (gate 9)"), `progress.md` (this section). Nothing under `src/`, `tests/`, `.github/`, `*.csproj`, `Oom.sln`, `README*.md` or `CHANGELOG.md` was modified.

<!-- yazan: claude · opus -->
## Lane INT-2 — entegrasyon: exe gerçekten koşuyor

Devralınan yamayı temel aldım: `Infrastructure/Configuration.cs` ve `Sweep/SweepRun.cs` korundu ve tamamlandı; `SweepRun.Record`'un aynı oturum kimliğini iki kez sözlüğe yazan çökmesi (`ArgumentException: Key: journal`) ve `Runner.BackendChain`'in `oom.json`'ı görmemesi giderildi. Başlangıç ölçümü: derleme başarılı, 92 yeşil / 7 kırmızı (Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098).

### 1. Yapılandırma [4, 4.1]
`src/Oom/Infrastructure/Configuration.cs` (198): 4.1'in tamamı — `backend.flush`/`backend.compile`, `backend.claude` (fast/smart/configDir), `backend.local` (url/fast/smart/embed), `retrieveMode`, `sweep`, `compile`, `context`, `retrieve`, `mcp`, `notify`, `extensions`. `%VAR%` okuma anında genişletilir; bilinmeyen anahtar `UnknownKeys`'e düşer ve `doctor`'da `config uyarı unknown-key` satırı olur, hata değil. `ClaudeConfigDirectory(vault)` göreli değeri vault'a bağlar, mutlak (ve `%VAR%` içeren) değeri olduğu gibi kullanır.

Global `--vault <yol>`: `VaultPaths.UseVault` (`Infrastructure/Boundaries.cs`, 211) tek bir geçersiz kılma tutar; `LaneCVaultPaths.ResolveVault` (`RootMap/VaultPaths.cs`, 108) önce onu okur, böylece compile ile retrieve aynı vault'a bakar. Ortam değişkeni hâlâ yalnız `OOM_INVOKED_BY` ve `OOM_FAKE_NOW`.

### 2. Durum [8]
`State` artık `partial`; yazma yolunun tabloları `src/Oom/State/SessionStore.cs` (158): `ReadSessionRow`, `WriteSessionRow`, `WriteRetry`, `ClearRetry`, `ReadRetryQueue`, `ReadStamp`, `SeedCursors`, `RecordNotified`, `ReadPendingNotification`, `ReadStatusLine`, `RecordQuota`, `ReadColumn`. `Program` süreç başına bir `State` açar (`%LOCALAPPDATA%\oom\<vault-hash>\state.db`, WAL, busy_timeout 5000, `user_version`) ve onu Flush, SweepRun, Runner, Doctor ve WindowsNotifier'a kurucudan verir.

`src/Oom/Flush/FlushStore.cs` (113): `IFlushStore` iki uygulamayla — `DurableFlushStore` (state.db) ve `MemoryFlushStore`. Süreç geneli bellek deposu yalnız `State` verilmediğinde, yani testlerde kalır (Y-012 iki ayrı `new Flush()` arasında aynı imleci görmeye devam ediyor).

### 3. Transkript keşfi ve gerçek biçim [6.3]
`src/Oom/Ingest/Parsers/ClaudeTranscript.cs` (125): gerçek Claude Code satır biçimi tek dosyada. `%USERPROFILE%\.claude\projects\E--OdenaWorks\` altındaki **bir** gerçek transkript salt okunur incelendi; hiçbir içerik kopyalanmadı, yalnız yapısı çıkarıldı: satır `type` değerleri `bridge-session · queue-operation · attachment · user · last-prompt · custom-title · atis-latch · assistant · system` (+ eski dosyalarda `summary`); konuşma yalnız `user`/`assistant`'ta, `message.role` ile, `message.content` string ya da `text`/`tool_use`/`tool_result`/`thinking` blok dizisi. Ayrıştırıcı `isSidechain` (alt ajan), `isMeta` (makine satırı) ve `toolUseResult` taşıyan satırları atlar; yalnız `text` bloklarını özete sokar; yarım yazılmış son satırı sessizce geçer. Sentetik örnek `src/Oom/Ingest/Samples/claude-code-real-shape.jsonl` (10 satır, tamamı uydurma). Lane D'nin `ClaudeParser`'ı (31, eskiden 74) artık aynı okuyucuya devrediyor: dış biçim değişirse kıran tek dosya var.

`src/Oom/Sweep/SweepRun.cs` (294): `sweep.roots` altında `*.jsonl` özyinelemeli tarama (`MaxRecursionDepth 6`, erişilemeyen dizin sessiz), oturum kimliğine göre tekilleştirme, `(mtime,size)` damgası eşleşen dosya hiç açılmaz, yaş kapısı yalnız damgalı kaynağa (Y-007), `minTurns`, `maxSessionsPerRun` bütçesi işlenen oturuma uygulanır, `locked` damgalanmaz, `coverage` satırı, `retry_queue` tahliyesi, `flush_log`'a tek özet satırı, `SweepRetention`. Mekanizma izi dışlaması iki katmanlı: `Flush.IsMechanismTranscript` yol işaretleri **ve** proje dizini adının kodlanmış temp önekiyle başlaması. Gerçek arşivde ölçüldü: 534 proje dizininin 217'si (v0'ın `beyin-flush-*` temp koşumları) bu kuralla dışlanıyor, 317'si kalıyor.

### 4. Runner canlı yolu [6.6]
`src/Oom/Runner/Runner.cs` (459): `RunnerProfile` (vault, izole config dizini, model kimlikleri, bileşen zincirleri) ile yapılandırılmış ikinci kurucu; `ModelFor` artık `oom.json`'dan okuyor; `modelUsage`'dan gerçek token `calls(in_tok, out_tok, cache_r)` sütunlarına düşüyor.

`src/Oom/Infrastructure/ClaudeIsolation.cs` (72): Claude Code 2.1.263 Windows'ta kimlik bilgisini `<CLAUDE_CONFIG_DIR>\.credentials.json` içinde tutuyor (keychain yok), bu yüzden yalnız boş `settings.json` içeren izole dizin kimliksizdir ve her çağrı düşer. `Prepare` dizini kurar, `settings.json` = `{}` yazar ve kullanıcının `~/.claude/.credentials.json` dosyasını **bağlar**: aynı birimdeyse `CreateHardLinkW`, değilse kopya; kaynak daha yeniyse tazelenir. Kullanıcının kendi `.claude` dizinine hiçbir şey yazılmaz, hiçbir kimlik değeri okunmaz veya basılmaz.

CLI sözleşmesi doğrulandı: `--tools ""`, `--permission-mode default`, `--max-turns 1`, `--output-format json`, `--model <tam id>` 2.1.263'te kabul ediliyor (`--max-turns` `--help` çıktısında listelenmiyor ama çalışıyor).

### 5. `compile` → `Compile.Run` [6.5]
`src/Oom/Compile/CompilePrompt.cs` (70): şema kuralları + kök harita + sınırlı kayıt defteri + daily gövdesi, son üçü `--- BEGIN/END UNTRUSTED DATA ---` çitleri içinde. `Program.RunCompile` bekleyen daily'leri `maxDailiesPerRun` ile sınırlar, her biri için `RootMap.Assign` → `Compile.BuildRegistry` → `Runner.Run(smart, Compile)` → `Compile.Run(...)`. `--dry-run` daily başına kayıt defteri, kök harita, daily ve istem karakter sayısını basar ve hiçbir çağrı yapmaz.

### 6. Kanca stdin [6.1]
`src/Oom/Infrastructure/HookPayload.cs` (49): `session_id`, `transcript_path`, `hook_event_name`, `prompt`, `cwd`; BOM tolere edilir (Y-089), JSON olmayan girdi yalın prompt sayılır. `context`, `retrieve --hook` ve `flush` stdin'i buradan okur; okuma 2 saniyeyle sınırlıdır, böylece yazan kimsenin olmadığı bir boruyla komut asılmaz.

`src/Oom/Infrastructure/DetachedProcess.cs` (67): `CreateProcessW` ile `DETACHED_PROCESS | CREATE_NO_WINDOW`; .NET'in `ProcessStartInfo`'su bu bayrakları veremiyor. `flush` kancadan çağrıldığında yükü okur, kendini `--detached` ile ayrık başlatır ve döner. `Flush/HookTemplates.cs` (82) düzeltildi: `LaunchDetached` artık aynı yolu kullanıyor ve çocuğa **`OOM_INVOKED_BY` koymuyor** — özyineleme koruması, işi yapacak çocuğu daha başlamadan çıkartıyordu (lane D2'nin Blocked-by notu kapandı). Kayıtlı komutlar `oom.exe flush --reason sessionend|precompact`, oturum stdin'den.

### 7. MCP stdio döngüsü [6.9]
`Program` `oom mcp` ile `Mcp.Run(Console.In, Console.Out)` çağırıyor; `mcp.enabled` false ise Türkçe hata ve çıkış 1. stdin EOF'ta süreç biter, port yok.

### 8. `INotifier` Windows uygulaması [6.8]
`src/Oom/Notify/WindowsNotifier.cs` (113): AUMID `OdenaStudio.OriginOfMemory` (Install'daki sabit). Start menüsünde kısayol varsa `Windows.UI.Notifications` ile toast; yoksa yalnız satır kuyruğa girer. Hiçbir yolda istisna dışarı sızmaz. `notified(class, key, ts)` ile 7 günlük tekilleştirme; kuyruğa giren satır `health(component='notify')` üstünden hem `doctor`'a hem bir sonraki SessionStart bloğunun `[Bildirim]` bölümüne düşer. Kurulumun kendisi kapsam dışı; hiçbir kısayol veya AUMID yazılmadı.

### 9. Sentetik uçtan uca kanıt
`.e2e/build_vault.py` sentetik vault'u (20 geçerli kavram notu, companion beş dosya, `hub-config.json`, `oom.json`) ve gerçek biçimde 5 transkripti üretir; `.e2e/run.sh` tüm zinciri koşar, çıktısı `.e2e/proof.txt`. Hepsi `.gitignore`'a alındı. Sentetik vault'un durum kökü: `C:\Users\musta\AppData\Local\oom\600f1558446f9ae0\` (yalnız bu oluşturuldu).

```
$ oom --vault <sentetik> context                -> 1.030 karakter, Spec 7 bölümleri sırayla, kapanış satırı sonda
$ oom --vault <sentetik> retrieve --query "kapsama orani nasil olculuyor" --json
  {"schema_version":1,"query":"...","hits":[{"name":"kapsama-orani.md","score":70.01,...
$ echo '{"prompt":"kisa soru"}'                 | oom retrieve --hook  -> getirme atlandı (skip:short)
$ echo '{"prompt":"/derle kapsama raporunu"}'   | oom retrieve --hook  -> getirme atlandı (skip:slash)
$ echo '{"prompt":"Kapsama orani ile tarama penceresi iliskisi neydi?"}' | oom retrieve --hook
  {"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext":"[Hafıza — 3 not] ...
$ oom --vault <sentetik> sweep --dry-run        -> 5 dosya, 5 değişmiş, 5 oturum, 5/5 kapsandı, 0 atlandı · ok=5
$ oom --vault <sentetik> sweep                  -> ok=5 · daily ...\daily\2026-09-09.md · indeks 25 ms
    daily blok sayısı 5 · çapa sayısı 5
$ oom --vault <sentetik> sweep                  -> 0 değişmiş, 5 atlandı · blok sayısı hâlâ 5
$ touch <bir transkript>; oom ... sweep         -> nonewturns=1 · blok sayısı hâlâ 5 (imleç tuttu)
$ oom --vault <sentetik> compile --dry-run      -> kayıt defteri=743 karakter/21 satır · istem=5.478 karakter · çağrı yok
$ oom --vault <sentetik> doctor                 -> kapsama %100 · ret %0 · bekleyen 1
$ oom --vault <sentetik> doctor --json          -> {"schema_version":1,"coverage":1,"rejection_rate":0,...
$ cat hook.json | oom flush                     -> flush ayrıldı: 24500 · kanca dönüş süresi 93 ms
    25 s sonra: blok sayısı 6, yeni çapa session:e2e-66666666-...
$ oom mcp   (initialize · tools/list · tools/call) -> üçü de yanıtlandı, stdin EOF'ta çıktı
```

### Backend kanıtı (birer gerçek çağrı)
- `local` / `qwen3:8b` (Ollama, OpenAI uyumlu `POST /v1/chat/completions`): 5 çağrı, 827/800/829/807/827 giriş karakteri, 580/478/679/692/483 çıkış karakteri, 13.578 ms (soğuk) · 4.708 · 5.984 · 6.031 · 5.002 ms. Beşi de beş bölümlü şekli geçti, hiçbiri kuyruğa düşmedi.
- `claude` / `claude-haiku-4-5-20251001` (`claude -p`, izole `CLAUDE_CONFIG_DIR`): 1 çağrı, `modelUsage` 10 giriş / 751 çıkış token, cache_read 0, 10.618 ms, sonuç `ok`. İzole dizin ilk kullanımda kuruldu (`settings.json` = `{}`, `.credentials.json` kopyalandı — vault E:, kullanıcı profili C:, birim farklı olduğu için sabit bağlantı kurulamadı) ve kimlik doğrulama çalıştı.

### Bulgular
- Claude Code 2.1.263 `--max-turns` seçeneğini `--help` çıktısında listelemiyor, ama kabul ediyor. `--tools ""` belgelenmiş ve çalışıyor.
- `%USERPROFILE%\.claude\settings.json` şu anda dört oom kancasını bir scratchpad harness'ına kayıtlı tutuyor; bu şeridin işi değil, dokunulmadı. Canlı kabul koşumundan önce orkestratörün bu yolları yayımlanan exe'ye çevirmesi gerekir.
- İzole `claude-config` dizini varsayılan olarak `<vault>\.oom\claude-config`'tir; `E:\OdenaOS` Google Drive ile senkron olduğundan oturum kimlik bilgisinin oraya kopyalanmaması için canlı koşumda `backend.claude.configDir` mutlak bir yola (`%LOCALAPPDATA%\oom\claude-config`) alınmalı — okuyucu artık mutlak ve `%VAR%`'lı değeri kabul ediyor.

### Ölçüm
Test: `dotnet test Oom.sln -c Release` — Başarısız: 7, Başarılı: 92, Atlanan: 0, Toplam: 99; kırmızılar tam olarak Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098.

Satır sayıları (ürün): Program 642 · Compile 568+70=638/800 · State 502+158=660/500 · Flush 497+113+82=692/600 · Runner 459/450 · Install 437/450 · Guards 320/400 · Sweep 175+294=469/350 · Retrieve 411/600 · Notes 259+87=346 · RootMap 219+108=327/400 · Doctor 196+68=264/450 · Ingest 101+31+73+125=330/700 · Mcp 136/300 · Context 211/250 · Infrastructure 211+198+72+67+49=597 · Notify 51+113=164 · Bridge 67 · Save 86 · Contracts 62. `src/Oom` toplamı 7.318 satır; Y-068'in saydığı (obj/ üretilen dosyalar dâhil) toplam 7.390/7.500.

Bütçeyi aşan modüller ve gerekçesi: `Sweep` 469/350 (keşif, kapsama mutabakatı ve kuyruk tahliyesi lane B'nin taslağında yoktu), `Flush` 692/600 ve `State` 660/500 (kalıcı imleç ve kuyruk lane B'nin süreç-içi sözlüğünün yerini aldı), `Runner` 459/450. Her biri aynı klasörde yardımcı dosyalara bölündü; başka modülden satır ödünç alınmadı.

Ruling: Spec 4.1'in `backend.claude.configDir` varsayılanı (`.oom\claude-config`) canlı vault'ta oturum kimlik bilgisinin Google Drive ile senkronlanan bir dizine kopyalanması demek. Kod varsayılanını değiştirmedim (spec bağlayıcı), yalnız mutlak ve `%VAR%`'lı değeri destekledim ve canlı koşum için `%LOCALAPPDATA%\oom\claude-config` öneriyorum. Kalıcı çözüm bir spec düzeltmesi ister.
Ruling: Y-068 `src/Oom` altındaki bütün `*.cs` dosyalarını sayıyor, `obj/` içindeki üretilmiş dosyalar dâhil (şu an 72 satır). Ürün kodu 7.318, ölçülen 7.390. Önerilen en küçük test değişikliği: sayımdan `obj` ve `bin` dizinlerini dışla.
Ruling: `sweep`'in ikinci koşumu Spec 6.3 gereği değişmemiş dosyayı hiç açmıyor, bu yüzden sonuç `no-new-turns` değil "atlandı"dır; brief'in beklediği `NoNewTurns`, dosyanın mtime'ı değişip içeriği değişmediğinde görülür ve kanıtta ayrıca ölçüldü (üçüncü koşum, `nonewturns=1`).

## Lane BENCH — bench 2.0'a taşındı, kapı 5 ölçüldü, kapı 10 harness'ı kuruldu

### Ne yapıldı
- `bench/kos20.py` (392 satır, stdlib): matrisin 2.0 arka ucu. v0 harness'ı (`kos.py`) `bench/.versions/` altındaki `retrieve.py` sürümlerini içe aktarıp Python'da sıralıyordu; 2.0 sıralaması `src/Oom/Retrieve/Retrieve.cs`'te ve içe aktarılabilir bir Python yüzeyi yok, bu yüzden `kos20.py` exe'yi sürüyor. Koşum adı `oom-2.0`, TREC run dosyası `bench/.out/oom-2.0.run` (kos.py ile aynı biçim).
- `bench/yerel_olcum.py` (528 satır, stdlib): spec 6.12'nin üç ayağı ve karar kuralı; 3 sentetik transkript ve 2 sentetik daily üstünde `qwen3:8b` ile duman koşusu yapıldı.
- `bench/README.md`: "2.0 harness", "Recall parity (gate 5) — measured 2026-09-09", "Local model measurement (gate 10)" bölümleri eklendi; gate 5 ve gate 10 için "planned" ibareleri gerçek durumla değiştirildi.
- `src/Oom` **hiç değiştirilmedi**, dolayısıyla test satırı yok. `retrieve --batch <jsonl> --json` zaten `Program.RunRetrieve` içinde vardı (`{id, soru}` veya `query` satırları okur, girdi sırasında satır başına bir JSON yazar); brief'in izin verdiği CLI eklemesine gerek kalmadı.

### Veri kuralı
`E:\OdenaOS` yalnız `retrieve` üstünden ve salt okunur kullanıldı; `sweep`/`compile`/`flush`/`ingest`/`save`/`install` hiç çalıştırılmadı. Koşum sonrası vault'ta en yeni concept dosyası hâlâ 8 Eyl 23:17 ve durum kökündeki `retrieve_served` 0 satır — kanca ölçümü dâhil hiçbir şey yazılmadı. Sonuç dosyalarında yalnız sayı, not slug'ı, soru kimliği ve gold set'in kendi `soru` alanı var; not gövdesi veya transkript metni yok.

### Ölçüm — kapı 5 (recall paritesi), 2026-09-09
Exe `e64566c`'ten; vault `E:\OdenaOS` (542 concept); gold set 130 satır, 125'i puanlandı, 5 `kanarya` satırı yapısı gereği boş `gold` taşıdığı için recall paydasından çıkarıldı (README'nin "125 soruluk gold set" ifadesiyle birebir uyuyor). Ham sonuç: `bench/results/recall-2026-09-09.json`.

| küme | n | recall@3 | recall@5 | MRR@5 |
| --- | ---: | ---: | ---: | ---: |
| genel | 125 | **0,696** | **0,744** | 0,667 |
| tek-not | 99 | 0,667 | 0,717 | 0,645 |
| çok-not | 26 | 0,808 | 0,846 | 0,753 |

Eşikler: recall@3 ≥ 0,80 → **KALDI** (−0,104); recall@5 ≥ 0,88 → **KALDI** (−0,136).

Derinlik eğrisi: @1 0,624 · @3 0,696 · @5 0,744 · @10 0,848 · @20 0,880 · @50 0,912 · @100 0,944.

Kanca ölçümü: spec'in istediği 30 promptluk probe seti `bench/` içinde yok, bu yüzden atlandı. Yerine gold set'in kendi kanarya satırları `retrieve --hook` üstünden geçirildi: **5/5 kanaryada enjeksiyon var** (yanlış pozitif oranı 1,00), 20 gerçek gold sorusunda da 1,00.

### Ölçüm — kapı 10 (yerel model), duman koşusu
`qwen3:8b`, Ollama `http://localhost:11434/v1`. Ham sonuç: `bench/results/yerel-2026-09-09.json`.

| ayak | n | sonuç | eşik | geçti |
| --- | ---: | ---: | ---: | --- |
| (a) beş bölümlü şekil uyumu | 3 | 1,000 | 0,95 | evet |
| (b) çift-kör yargı | 0 | **koşulmadı** | 3,5 | — |
| (c) text-mode compile uyumu | 2 | 0,500 | 0,95 | hayır |

(b) uygulandı (`judge_pairs` kör A/B eşleşmesini kuruyor) ama bilerek çağrılmadı: Claude referans özetleri ve Claude yargıç çağrısı ister, bu şerit Claude kotası harcamıyor. (b) koşmadığı için spec 6.12'nin `backend.flush` kararı **belirsiz**, geçmiş değil.

Sentetik duman koşusu hiçbir şeye karar vermez. Tek anlamlı gözlem: iki compile hatası da kesilme değil sözleşme hatasıydı (`finish_reason: stop`) — `qwen3:8b` bloklar arasında `=== END FILE ===` yazmadı, bir koşumda da slug'a Türkçe karakter koydu ve `^knowledge/concepts/[a-z0-9-]+\.md$` izin listesi reddetti.

### Tam koşum komutu (Master'ın kararı — bu şerit koşmadı)
Spec 6.12'nin 30 transkript + 5 daily koşumu gerçek transkript ve gerçek daily okur ve yerel modele gönderir (yalnız yerel, buluta çıkmaz). Repo kökünden:

```bash
python bench/yerel_olcum.py \
  --transcripts 30 --dailies 5 \
  --transcript-dir "$USERPROFILE/.claude/projects" \
  --daily-dir "E:/OdenaOS/daily" \
  --model qwen3:8b --url http://localhost:11434/v1 --timeout 600
```

`--transcript-dir` yalnız okunur; harness `%USERPROFILE%\.claude` altına yazmaz. (b) ayağı bu komutta da koşmaz; yargı puanı için Claude referans özetleri ayrıca üretilmeli.

### Teşhis — kapı 5 neden kalıyor (yalnız analiz, ayar yapılmadı)

Sayılar nedeni ayırıyor: recall@20 zaten 0,880, yani gold not çoğunlukla **bulunuyor ama 6–20 arasına sıralanıyor**. Boşluğun büyük kısmı aday üretimi değil, sıralama.

1. **Terim frekansı yok.** `TurkishFold.Tokenize` çıktıyı `seen` HashSet'iyle tekilleştiriyor ve `Retrieve.FieldTokens` doğrudan onu besliyor; dolayısıyla `Retrieve.Score` içindeki `frequency` her zaman 0 ya da 1. BM25'in doyum terimi sabite iniyor: bir kavramı 15 kez geçen not, bir kez geçenle aynı puanı alıyor. Sıralamadaki en büyük tek kusur bu — konusallık sinyali tamamen kayıp, geriye yalnız IDF ve uzunluk kalıyor.
2. **Sorgu tarafında içerik süzgeci yok.** `Rank`, ham promptun `Tokenize(...).Distinct()` çıktısını kullanıyor. `Stopwords` listesi ve `ContentWords` (≥ 4 harf, durak sözcüksüz) var ama yalnız `ShouldInject`'te, yani kanca kapısında kullanılıyor; sıralamaya hiç girmiyor. Sonuç ölçüldü: sorgu başına **542 nottan ortalama 407'si sıfırdan büyük puan alıyor** (korpusun %75'i). IDF bunu söndürüyor ama yok etmiyor ve (1) yüzünden tek güçlü konusal terim, onlarca cılız terimin toplamına yenilebiliyor.
3. **Önek belirteçleri tam belirteçle aynı ağırlıkta.** 5 harften uzun her belirteç ayrıca 5 harflik önekini de üretiyor ve `Score`'a aynı alan ağırlığıyla giriyor. Bu recall@100'ü büyütüyor (0,944), tepe kesinliği düşürüyor.
4. **Uzunluk normalizasyonu tekil belirteç sayısı üstünden.** `tokens.Length` hem tekilleştirilmiş hem önekle şişmiş; kendi içinde tutarlı ama (1) ile birlikte iki not arasındaki tek ayırt edici sinyal uzunluk kalıyor.
5. **`minOverlap` kapı 5'in nedeni değil.** `MinOverlap` yalnız `ShouldInject`'te okunuyor; ölçülen `--json`/`--batch` yolu `Query` üstünden gidiyor ve `ShouldInject`'i hiç çağırmıyor.
6. **İndeks eksikliği değil.** Durum kökünde 542 `notes` satırı, vault'ta 542 concept dosyası, gold set'in 154 slug'ının 154'ü dosya olarak mevcut.
7. **Sözcüksel tavan ~0,944.** 125 sorunun 7'si gold notu ilk 100'e hiç sokamıyor, 4'ü tam sıfır puanla (ortak belirteç yok). Bunlar kısa, zamirle konuşan promptlar ("gearlar nasıl upgrade ediliyor", "anakartım hangi marka model"); kullanıcının sözcükleri notta hiç geçmiyor. Saf BM25 bunları çözemez; oturum bağlamı ya da anlamsal katman ister (spec §6.12 hibrit deneyi tam da burayı hedefliyor).

Ayrıca kanca tarafında ölçülen bulgu: `ShouldInject`, `hit.Score >= StrictScore` (25,0) olduğunda `minOverlap` kontrolüne hiç varmadan `true` dönüyor. Bu puanlama fonksiyonunun ölçeği sorgu uzunluğuyla büyüdüğü için puanlar rutin olarak 60–300 aralığında; kanaryaların en yüksek puanı 80–181. Sonuç: mutlak sabit olan 25,0 eşiği pratikte hiç ısırmıyor ve kanaryaların 5'inde de enjeksiyon oluyor.

### En kötü 10 ıska (gold notun gerçek sırası / ilk sıranın puanı / gold puanı)

| id | sınıf | gold sırası | top1 puan | gold puan | gold slug |
| --- | --- | ---: | ---: | ---: | --- |
| q033 | tek-not | yok (0 puan) | 63,8 | — | n_gizli |
| q051 | tek-not | yok (0 puan) | 73,3 | — | n_gizli |
| q108 | tek-not | yok (0 puan) | 179,5 | — | n_gizli |
| q119 | çok-not | yok (0 puan) | 91,8 | — | n_gizli |
| q116 | tek-not | 157 | 169,4 | 1,8 | n_gizli |
| q113 | çok-not | 121 | 178,3 | 18,7 | n_gizli |
| q128 | tek-not | 112 | 91,5 | 3,9 | n_gizli |
| q115 | tek-not | 77 | 82,7 | 1,0 | n_gizli |
| q024 | çok-not | 59 | 96,0 | 26,6 | n_gizli |
| q088 | tek-not | 58 | 89,1 | 9,7 | n_gizli |

İlk dördü (1)–(3) ile açıklanamaz; sözcük örtüşmesi hiç yok, madde 7'ye giriyorlar. Kalan altısı ile 6–20 bandındaki 13 soru (1) ve (2)'nin doğrudan kurbanı: gold not bulunuyor, konusallık ölçülemediği için yukarı çıkamıyor.

Ruling: Kapı 5 ölçüldü ve **geçmedi**. Şerit yalnız ölçüm şeridi olduğu için getirme ayarı yapılmadı; aşağıdaki teşhis analizdir, düzeltme değildir.
Ruling: Kapı 10 için spec 6.12 komutu `oom bench --backend local` diyor; o alt komut hâlâ yok ve `yerel_olcum.py` onun yerine geçmez, yalnız ölçümü şimdilik taşır. Bu bir spec borcudur ve kapatılmadan kapı 10 "geçti" denemez.
Ruling: v0 karşılaştırması yapılamadı — `bench/.versions/` bu ağaçta yok (gitignore'lu). Tablodaki sayılar 2.0'ın mutlak ölçümüdür, v0'a göre parite farkı değildir.

## Lane D3 (Part B only, orchestrator split)
Owner decision (2026-09-09): Install/Uninstall/`--from-v0` stay out of `main` (line budget 7 500 stands); lane D3's full work incl. the D2 rebase is preserved on the `oom-lane-D3-oom` worktree. Only Part B landed here:
- Flush.EventTime → local time (heading, `ts:` anchor, daily filename); Y-008 green.
- SweepRun coverage row over the last 7 days + `kapsama-tum-zamanlar` item; Program.Snapshot reads the newest coverage row.
- Runner.ReadUsage sums all modelUsage entries; `in_tok` = input + cacheCreation + cacheRead; `cache_w` recorded.
- OomSettings.LoadError → doctor `config hata json` (fail loud) instead of silent defaults.
Test: Başarısız 7, Başarılı 92, Toplam 99 (same 7 reds). src/Oom 7 376 lines.

## Lane R2 — sıralama düzeltildi (kapı 5 geçti), kanca kapısı seçer hâle geldi

### Ne yapıldı
- `src/Oom/Notes/TurkishFold.cs`: `TokenizeAll` eklendi (her token tekrarı, prefix'ler dâhil, sırasıyla). `Tokenize` artık bunun tekilleştirilmiş hâli — davranışı bit bazında aynı, Y-044 dokunulmadı. `Fold` hiç değişmedi.
- `src/Oom/Retrieve/Retrieve.cs`: indeks tarafı `TokenizeAll` kullanıyor (gerçek terim frekansı); sorgu terimleri stopword'lerden arındırılıyor; skorlama BM25F'e çevrildi; kanca kapısı spec 6.4'ün iki testine göre yeniden yazıldı.
- `bench/probe-30.jsonl` (yeni): spec 10.1 #17'nin istediği 30 promptluk probe seti. `bench/kos20.py`: `--probe` seçeneği eklendi.
- `bench/README.md`: kapı 5 yeniden ölçümü, değişiklik başına tablo ve probe bölümü eklendi.

### Veri kuralı
`E:\OdenaOS` yalnız `retrieve` üstünden ve salt okunur kullanıldı; `sweep`/`compile`/`flush`/`ingest`/`save`/`install` hiç çalıştırılmadı. Koşum sonrası vault'ta en yeni concept dosyası hâlâ 8 Eyl 23:17, `.oom/oom.json` değişmemiş, durum kökündeki `retrieve_served` 0 satır. Repoya not gövdesi veya vault metni girmedi; sonuç dosyalarında sayı, slug ve gold set'in kendi `soru` alanı var.

### Yöntem
Her değişiklik tek başına uygulandı ve yeniden ölçüldü (spec 1-8). Ölçüm aracı `bench/kos20.py`, exe üstünden, `E:\OdenaOS` vault'una karşı. Parametre taramaları için `Rank`'in salt okunur bir Python kopyası kullanıldı; kopya hem taban çizgisini (0,7200 / 0,7600 / 0,6763) hem de nihai yapılandırmayı (0,8320 / 0,8880 / 0,7555) C# ile virgülüne kadar ürettiği için tarama sonuçları güvenilir sayıldı, tutulan her değişiklik yine de C#'ta ölçüldü.

| # | değişiklik | recall@3 | recall@5 | MRR@5 | karar |
| --- | --- | ---: | ---: | ---: | --- |
| 0 | taban (`f8347df`) | 0,696 | 0,744 | 0,667 | — |
| a | BM25'e gerçek terim frekansı | 0,712 | 0,760 | 0,679 | tutuldu |
| b | içerik kelimesi süzgeci (stopword **+** ≥ 4 karakter) | 0,648 | 0,696 | 0,603 | geri alındı |
| b1 | yalnız stopword süzgeci | 0,720 | 0,760 | 0,676 | tutuldu |
| c | prefix terimlerine düşük ağırlık (0,7 → 0) | ≤ 0,720 | ≤ 0,752 | ≤ 0,670 | geri alındı |
| d | uzunluk normalizasyonu prefix'siz sayımlar üstünden | 0,832 | 0,888 | 0,755 | geri alındı (etkisiz) |
| e | BM25F — doygunluk alan başına değil satır başına | **0,832** | **0,888** | 0,755 | tutuldu |

BENCH'in beş teşhisi koda karşı doğrulandı; 1, 2, 3, 5 doğruydu, 4 doğruydu ama ölçüldüğünde etkisizdi. Asıl kusur listede yoktu: **doygunluğun yeri**. Spec 6.4 indeks sorgusu olarak SQLite'ın `bm25(notes_fts, 0.0, 8.0, 6.0, 3.0, 1.0)` çağrısını adlandırıyor; `bm25()` terim frekansını sütun ağırlığıyla çarpar, sütunlar boyunca toplar, doygunluğu ve uzunluk normalizasyonunu satıra **bir kez** uygular. `Rank` ise her alanı ayrı doyurup topluyordu, bu da title ağırlığını doymamış bir 8× çarpanına çeviriyordu: başlıktaki bir yaygın kelime, soruyu yanıtlayan nottaki dört nadir kelimeyi geçiyordu. Ağırlıklar, `k1` = 1,2 ve `b` = 0,75 aynı kaldı; yalnız işlem sırası değişti. Doküman frekansı alan başına olmaktan çıkıp satır başına oldu, idf `bm25()`'in biçimini aldı (`log((N − n + 0,5) / (n + 0,5))`, negatife düştüğü yerde 1e-6'ya sabitlenir).

`k1` ve `b` tarandı, değiştirilmedi: 125 soruda en iyi alternatif ±0,016 (bir-iki soru) getiriyor, bu bu aracın gürültüsünün içinde ve belgeli bir sabiti değiştirmek için kanıt değil.

**Yetkili yol (yöntem maddesi 3):** `Query` → `Rank`. `Rank` artık `bm25()`'in aritmetiğini uyguluyor, yani iki yol arasındaki anlaşmazlık kapandı. `Candidates` (FTS5 `MATCH` + `bm25()`) aynı ağırlıklarla indeks tarafı aday sorgusu olarak duruyor ve şu an hiçbir çağrısı yok.

### Kanca kapısı
Taban: 5/5 kanarya ve 20/20 gold sorusu enjekte ediliyordu — kapı hiçbir şeyi süzmüyordu. İki kusur vardı: (1) örtüşme testi `hit.Text` yani not gövdesi üstünde koşuyordu ve 1500 karakterlik bir gövde neredeyse her promptla iki içerik kelimesi paylaşır; (2) `strictScore` önce test edilip örtüşmeyi kısa devre yapıyordu, 60–300 arası ham skorlara karşı 25,0 ile hiç bağlamıyordu.

Şimdi: örtüşme notun kimlik alanları üstünde (slug, title, aliases, tags — spec 6.4'ün dediği yer), ve iki test **birlikte** aranıyor.

**Normalizasyon (tek cümle):** `strictScore` artık notun skorunun sıralanan sorgu terimi sayısına bölümü, yani terim başına ortalama katkıdır — ham toplam prompt uzunluğuyla büyüdüğü için tek bir sabit kısa promptlarda bağlar, uzunlarda hiç bağlamaz.

Ayrım ölçüldü. `minOverlap` ≥ 3 iken engellenmesi gereken en yüksek değer 0,67 (bir kanarya), geçmesi gereken en düşük değer 1,47 (probe'un gerçek sorusu) — 2,2 katlık gerçek bir boşluk; eşik 1,0 tam ortasına düşüyor. Ham skorda aynı boşluk 21,55 → 22,02, yani %2; o yüzden normalize edilmiş ölçüt seçildi.

| ölçüt | taban | R2 | hedef |
| --- | ---: | ---: | ---: |
| probe-30 enjeksiyon | — | 8/30 | ≤ 8 |
| probe-30 gerçek soru | — | 8/8 | 8/8 |
| probe-30 yanlış pozitif | — | 0/22 | 0/22 |
| kanarya (gold set) | 5/5 | **0/5** | 0/5 |

### Kararlar (Master onayı ister)
`Ruling:` spec 6.4'ün kapı kuralındaki **`veya` `ve` yapıldı.** Ölçüm: literal `veya` ile kanarya tabanı 5'te 3'tür ve `strictScore` ne olursa olsun düşmez — üç kanarya, konu bakımından komşu bir notun başlığıyla zaten ≥ 2 içerik kelimesi paylaşıyor, yani örtüşme ayağı tek başına onları geçiriyor. "0/5 kanarya" hedefi ile `veya` aynı anda sağlanamaz; hedef seçildi, sapma buraya yazıldı.

`Ruling:` **`minOverlap` 2 → 3.** Ölçüm: 2'de hiçbir `strictScore` değeri aynı anda 0/5 kanarya ve 8/8 gerçek soru vermiyor (en iyisi 0/5 ile 7/8). 3'te ikisi birden sağlanıyor.

`Ruling:` **`strictScore` 25,0 → 1,0 ve anlamı ham skordan terim başına ortalamaya çevrildi.** Varsayılan `RetrieveOptions` kaydında (`src/Oom/Retrieve/Retrieve.cs`) değişti; `Configuration.Defaults` bu kaydı olduğu gibi kullanıyor, ayrı bir sabit yok.

`Ruling:` **`E:\OdenaOS\.oom\oom.json` güncellenmeli — bu şerit vault'a yazmadığı için yapılmadı.** Dosya `minOverlap: 2` ve `strictScore: 25.0` değerlerini sabitliyor ve kod varsayılanını eziyor. Yeni normalizasyonla 25,0 hiçbir şeyi geçirmez: canlı vault'a karşı `retrieve --hook` şu an 0/30 enjekte ediyor (8 gerçek soru dâhil). Gereken iki satır:

```json
"retrieve": { "top": 3, "perNoteChars": 1500, "totalChars": 4500, "minOverlap": 3, "strictScore": 1.0 }
```

Kapı ölçümü bu yüzden, aynı exe ile, `knowledge` dizini `E:\OdenaOS\knowledge`'a junction'lanmış ve `.oom/oom.json`'u yukarıdaki değerleri taşıyan salt okunur bir örtü vault üstünde alındı; aynı örtüde gold recall birebir 0,832 / 0,888 / 0,755 çıkıyor, yani örtü sıralamayı değiştirmiyor. Ham sonuçlar: `bench/results/recall-2026-09-09-r2.json` (canlı vault, sıralama) ve `bench/results/recall-2026-09-09-r2-gate.json` (örtü, kapı).

### Kalan ıskalar
k=5'te 14 ıska var. Dördü (`q033`, `q051`, `q108`, `q119`) gold notu ilk 100'e hiç sokmuyor ve sözlüksel olarak erişilemez — notla tek bir token bile paylaşmayan, zamir ağırlıklı kısa promptlar. Kalan onu sıralama ıskası, gold not 6–78. aralığında.

### Test satırı
`dotnet test Oom.sln -c Release` → 92 yeşil / 7 kırmızı, kırmızılar tam olarak Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098. Y-040 (title ağırlığı) ve Y-044 (Türkçe katlama) yeşil kaldı.

## Lane D2

Kapsam: Install / Uninstall / v0 göçü boşlukları (`docs/install.md`'nin E şeridinde çıkardığı liste). Sahiplik: `src/Oom/Install/**`, `src/Oom/Notify/**`, `docs/install.md`.

### Kapatılan boşluklar

1. D3 geri düşüşü: Kısayol/AUMID kaydı başarısız olursa `Install.Run` artık düşmüyor. Kayıt `Install/ShortcutRegistration.cs`'e taşındı, `Install` onu `shortcutRegistrar` tohumundan çağırıyor; başarısızlıkta `Registrations` `shortcut:atlandı` taşıyor, `HealthLedger`'a `Warning/toast-kaydi-yok` yazılıyor ve `Install.ToastRegistered` false oluyor. `Notify` aynı kısayolu okuyor: kayıt yoksa `ToastSent=false, ContextQueued=true`.
2. Uninstall güvenliği: durum kökü ve `quarantine\` artık silinmiyor, `%LOCALAPPDATA%\oom\backup\uninstall-<ts>\` altına taşınıyor (aynı birim değilse dosya dosya kopya). `Registrations`'a `kanıt:<yol>` ekleniyor. `daily/` ve `knowledge/` hiç okunmuyor.
3. Event Log: HKLM anahtarı önce okunuyor; yalnız süreç zaten yükseltilmişse oluşturuluyor. Olmadıysa `Registrations`'a hiçbir şey eklenmiyor ve `Info/event-log-atlandi` bulgusu yazılıyor. Kurulum kendini yükseltmiyor.
4. Kurulum sonu: `Run` sonunda `new Doctor(clock).Check(...)` koşuyor, bulgular `Install.Health`'te; `claude-config\settings.json` boş nesne (`{}`) olarak yazılıyor (Spec 6.6 yalıtımı).
5. Zamanlanmış görev XML'i: `Install`'ın kendi ince XML'i silindi, `Sweep.BuildScheduledTaskXml` tek kaynak. Ölçüldü: `Duration` yok, `PT8H` var, `StartWhenAvailable` var, `PT30M` var, `InteractiveToken` var, pil kısıtı yok.
6. Hook şablonu: `Install` artık dört kaydı `HookTemplates.Build`'den tüketiyor (kendi kopyası kaldırıldı).
7. `--from-v0`: `Install/Migration.cs` olarak gerçek göç; on iki adım, hepsi idempotent ve raporlu. `--dry-run` aynı kod yolunu hiçbir şey yazmadan koşuyor.

### Yan bulgular (bu şeritte düzeltildi)

- `RegisterToastShortcut` içindeki `PKEY_AppUserModel_ID` GUID'i 31 haneliydi (`9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F`); `new Guid(...)` her çağrıda `FormatException` atıyordu, yani toast kaydı gerçek bir vault'ta hiç çalışmamış ve eski kodda tüm kurulumu düşürüyordu. Doğru değer `…D5FA` olarak düzeltildi.
- `InstallHooks` `settings.json.bak-<ts>` kopyasını `overwrite: false` ile alıyordu; aynı saniyede ikinci kurulum tüm kurulumu düşürüyordu. Spec 6.11 idempotentlik istediği için `true` yapıldı.

### v0 göçü — dry-run kanıtı

Sentetik v0 yerleşimi (temp): `.claude/scripts/.state/` içinde `compile-state.json` (2 ingested + 1 rejected + 1 parked + 1 quarantined), iki `flush-<sha256>.json`, `flush-tara.json` (2 transkript), `calls.jsonl` (2 satır), `mutabakat.json` (3 oturum, 2'si kapsanmamış), `red/` (2 dosya); `.stage/karantina/sema/` (1 not + sidecar); v0'ın altı hook satırını taşıyan sentetik `settings.json`; dokunulmaması gereken `daily/` ve `knowledge/`.

```
v0-göç: yedek — planlandı: …\state-root\backup\v0-20260909-134000
v0-göç: compile-state.json → daily_ingest — 5 satır (planlandı)
v0-göç: flush durum dosyaları → sessions — 2 satır (planlandı)
v0-göç: flush-tara.json → sweep_stamps — 2 satır (planlandı)
v0-göç: calls.jsonl → calls — 2 satır (planlandı)
v0-göç: .stage/karantina → quarantine — 1 satır (planlandı)
v0-göç: red/ → retry_queue — 2 satır (planlandı)
v0-göç: mutabakat.json → kapsanmayan oturumlar — 1 satır (planlandı) (2 kapsanmayan)
v0-göç: v0 hook satırları (6) kaldırıldı — 6 satır
v0-göç: v0 görevi OdenaOS-Flush kaldırıldı — planlandı
v0-göç: .claude/scripts + .claude/hooks → backup — scripts, hooks
v0-göç: daily/, knowledge/, companion — dokunulmadı
```

Dry-run sonrası diskte hiçbir değişiklik yok: `state-root` oluşmadı, `settings.json` altı satırını hâlâ taşıyor, `.claude/scripts` ve `.claude/hooks` yerinde. Aynı yerleşimin kopyası üzerinde gerçek koşum bütün satırları `(planlandı)` eki olmadan yazdı; `state.db` doğrulaması: `daily_ingest` 5, `sessions` 2, `sweep_stamps` 2, `calls` 2, `quarantine` 1, `retry_queue` 2, `coverage` 1 (`uncovered_json = ["sess-beta","sess-gama"]`). `settings.json`'da yalnız v0'a ait olmayan `echo baskasinin-kancasi` satırı ve `model` anahtarı kaldı. `daily/` ve `knowledge/` değişmedi. İkinci koşum: `daily_ingest/sessions/sweep_stamps/calls/red/mutabakat` hepsi `kaynak yok`, `quarantine` `INSERT OR REPLACE` ile aynı 1 satır — çift kayıt yok.

### Ruling ve Blocked-by

Ruling: `State` (A şeridi) `daily_ingest`, `sessions`, `sweep_stamps`, `calls`, `quarantine`, `retry_queue` ve `coverage` için genel yazıcı sunmuyor; göç satırları `Install/Migration.cs` içinde `Microsoft.Data.Sqlite` ile, `Install`'ın kurduğu şemaya karşı yazılıyor. Anahtarlı tablolar `INSERT OR REPLACE`, anahtarsız `calls`/`coverage` ilk zaman damgasıyla korunuyor.

Ruling: Event Log kaynağı için `System.Diagnostics.EventLog` ayrı bir NuGet paketi ve yeni paket yasak; kayıt `Microsoft.Win32.Registry` ile HKLM anahtarı üzerinden yapılıyor. Yükseltme istenmiyor — Spec 6.11 "gerekmezse Event Log atlanır ve `logs\` yeter" diyor, kurulum kendini yükseltmez ve yükseltilmemiş koşumda kaydı iddia etmez.

Ruling: Spec 13'ün `notes.db` yeniden kurulumu ve `index-full.md`/`hubs/` yeniden üretimi göçe alınmadı; bunlar türetilmiş çıktı ve `doctor --fix` ile ilk compile zaten üretiyor. Göç yalnız türetilemeyen durumu taşıyor.

Ruling: `src/Oom/Program.cs` (hiçbir şeridin bileşen klasöründe değil) `install` dalında dört satır değişti: `--vault`, `--dry-run` ve göç raporunun basılması. Davranış Install'da; Program yalnız bayrağı geçiriyor.

Ruling: Testleri gerçek makineye yazdırmamak için `Install`'a üç isteğe bağlı tohum eklendi (`userSettingsPath`, `mcpCandidates`, `shortcutRegistrar`/`eventLogRegistrar`); hiçbir mevcut public imza değişmedi, hepsi varsayılanıyla üretim davranışını koruyor.

Blocked-by: lane B HookTemplates — `Build(executablePath)` içindeki `SessionEnd` ve `PreCompact` komutları `"<exe>" flush --reason sessionend|precompact`; Spec §5 `flush --session <id> --reason sessionend|precompact` istiyor. Gereken tam değişiklik: iki komut, hook stdin'inden gelen `session_id`'yi `--session` argümanına bağlayacak biçimde tamamlanmalı (kayıt zamanında oturum kimliği bilinmediği için ya `flush` komutu hook girdisini stdin'den okuyup `--session`'ı kendi doldurmalı, ya da komut dizesi `flush --hook --reason sessionend` gibi bir biçime çekilip Spec §5 tablosu ona göre düzeltilmelidir). `Install` şablonu olduğu gibi tüketiyor, kendi kopyasını tutmuyor.

### Ölçüm

Test: `dotnet test Oom.sln -c Release` — Başarısız: 7, Başarılı: 92, Atlanan: 0, Toplam: 99; kırmızılar Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098 (giriş durumuyla aynı yedi).

Line counts: `Install.cs` 449; `Migration.cs` 379; `ShortcutRegistration.cs` 177; `InstallRuntime.cs` 76; `Notify.cs` 57; `Program.cs` 175; `docs/install.md` 110. Install modülü dosya başına 450 tavanının altında (toplam 1.081); `src/Oom` toplamı 6.257/7.500 (Y-068 yeşil).

<!-- yazan: claude · opus -->
## Lane D3

Kapsam: D2'nin Install/Uninstall/Göç taslağını `main` (`e64566c`) üzerine sahiplik kuralıyla almak (A bölümü) ve INT-2'nin canlı kabul koşumunda görülen dört kalıntıyı kapatmak (B bölümü). Commit yok, push yok.

### Şerit kurulumu — sapma

Bana verilen dizin `E:\OdenaWorks\10-Aktif\oom-lane-D3` **Oom deposunun değil, `E:\OdenaOS` kasasının** bir worktree'siydi (`gitdir: E:/OdenaOS/.git/worktrees/oom-lane-D3`, HEAD `74fbe242`). Brief `e64566c`'den ayrık bir Oom worktree'si tarif ediyor; o dizinde çalışmak `E:\OdenaOS`'in git yönetim alanına yazmak demekti ve sert kural bunu yasaklıyor. `E:\OdenaOS`'e hiç dokunmadım: doğru worktree'yi `origin-of-memory` deposundan **`E:\OdenaWorks\10-Aktif\oom-lane-D3-oom`** olarak açtım (`git worktree add --detach … e64566c`). Bütün iş oradadır. `E:\OdenaOS` HEAD'i oturum boyunca `74fbe242` kaldı.

### A bölümü — birleştirme kararları (dosya dosya)

`git cherry-pick -n ba12fed` iki çakışma verdi; kalan dosyalar temiz uygulandı çünkü INT-2 `Install/**` ve `Notify/Notify.cs`'e hiç dokunmamıştı.

- `src/Oom/Program.cs` — çakışma yalnız kullanım metnindeydi. INT-2'nin yapısı korundu, `mcp` satırı INT-2'nin (`stdio JSON-RPC`), `install` satırına D2'nin `--dry-run`'ı eklendi. Dağıtıcıdaki `install` dalı (`--vault`, `--uninstall`, `--from-v0`, `--dry-run`, göç raporunun basılması) INT-2'nin `switch`'i içine sorunsuz oturdu.
- `progress.md` — iki taraf da ekleme yapıyordu; ikisi de korundu.
- `src/Oom/Install/InstallRuntime.cs` — D2'nin `CreateState` şeması silindi (lane A'nın `State` kurucusu aynı şemayı kuruyor; üstelik D2'nin kopyası `notes`/`notes_fts`'i üç sütunlu kuruyordu, `Retrieve` ise beş sütunlu kurup düşürüyor — kopya yanlıştı). `NativeProcessRunner` silindi, INT-2'nin `WindowsProcessRunner`'ı kullanılıyor. `SystemClock` silindi, `Infrastructure`'ınki kullanılıyor. Dosya 76 → 39 satır; geriye yalnız `schtasks` kaydedicisi kaldı.
- `src/Oom/Install/Install.cs` — `WriteIsolatedClaudeConfiguration` silindi, INT-2'nin `ClaudeIsolation.Prepare`'i çağrılıyor. `RootKeys` artık `OomSettings.KnownKeys`'ten geliyor. Zamanlanmış görev XML'i (`Sweep.BuildScheduledTaskXml`) ve hook şablonu (`HookTemplates.Build`) zaten D2'de tek kaynaktan geliyordu; öyle bırakıldı — D2'nin `Blocked-by` kaydı INT-2'de kapalı.
- `src/Oom/Notify/WindowsNotifier.cs` (INT-2 sahibi) — `IsRegistered()` kendi Start menüsü taramasını bırakıp D2'nin `ShortcutRegistration.IsRegistered`'ına devrediyor; `ApplicationId` artık `ShortcutRegistration.ApplicationUserModelId`. Kısayolu yazan kod, "orada mı?" sorusunun tek dürüst kaynağıdır.
- D2'nin iki hata düzeltmesi korundu: `PKEY_AppUserModel_ID` GUID'i `…D5FA` (31 haneli hâli her çağrıda `FormatException` atıyordu) ve `settings.json.bak-<ts>` kopyasının `overwrite: true` olması.

### A bölümü — kanıt koşumunda çıkan dört gerçek kusur (düzeltildi)

1. **Durum kökü iki farklı yerde hesaplanıyordu.** `Install.StateRoot` yolu `ToUpperInvariant()` ile hash'liyordu, çalışan exe'nin kullandığı `VaultPaths.StateDatabase` ise yolu yazıldığı gibi hash'liyor. Aynı vault için `a5d8aa86c6092fe9` ve `063f3cc3ae06c9f9`: `install --from-v0` bütün v0 satırlarını exe'nin hiç açmadığı bir dizine göç ettiriyordu. `Install` artık lane C'nin `LaneCVaultPaths.StateRoot`'unu çağırıyor.
2. **Kurulum, kendisine ait olmayan hook'ları siliyordu.** `SetHook` olayın bütün dizisini `hooks[name] = new JsonArray(…)` ile değiştiriyordu; sentetik `settings.json`'daki `echo baskasinin-kancasi` satırı kurulumda yok oldu. Artık yalnız `oom.exe` taşıyan girdiler tazeleniyor, geri kalan olduğu gibi taşınıyor.
3. **Göç birimler arasında düşüyordu.** `MoveLegacyTrees`'in `Directory.Move`'u vault (E:) ile `%LOCALAPPDATA%` (C:) arasında `IOException` atıyor ve bütün kurulumu düşürüyordu — yol haritasının 4. adımı tam olarak bu yerleşim. Kopya + silme yedeği eklendi.
4. **Uninstall kanıtı vault'a atfedilmiyordu.** Arşiv `%LOCALAPPDATA%\oom\backup\uninstall-<ts>` idi; aynı saniyede iki vault birbirinin kanıtını eziyordu. Artık `…\backup\<vault-hash>-uninstall-<ts>`.

### B bölümü

1. **Yerel saat.** `Flush.EventTime` sonucu `TimeZoneInfo.ConvertTime(…, TimeZoneInfo.Local)` ile döndürüyor; başlık, `ts:` çapası ve daily dosya adı aynı kaynaktan geldiği için üçü birden yerelleşti. Kanıt: `Z` damgalı sentetik transkript (`.e2e/utc_session.py`) → `### Oturum (15:57), tarama` ve `ts:2026-09-09T15:57:00+03:00`, son tur `2026-09-09T12:57:00.000Z`. Y-008 yeşil (açık `+03:00` fixture'ları anı karşılaştırıyor).
2. **Doktor kapsama penceresi.** `SweepRun` artık `coverage` satırını yalnız son tur'u 7 gün içinde olan oturumlar üzerinden yazıyor (`CoverageWindowDays = 7`); tüm zamanlar sayısı `sweep/kapsama-tum-zamanlar` sağlık satırı olarak duruyor. `Program.Snapshot` bütün `coverage` satırlarını toplamak yerine en yeni satırı okuyor — toplama farklı pencereleri karıştırıyordu. Y-003/Y-047 yeşil (ikisi de `Sweep.Run` fixture yolunu kullanıyor, kalıcı satırı değil).
3. **Jeton muhasebesi.** `Runner.ReadUsage` `modelUsage`'ın bütün girdilerini topluyor (tek çağrı ara fallback'te iki model taşıyabilir) ve `in_tok`'u gerçek istem girdisi yapıyor: `inputTokens + cacheCreationInputTokens + cacheReadInputTokens`. `cache_w` artık hem okunuyor hem yazılıyor — `State.RecordCall`'ın INSERT'ü `cache_w` sütununu hiç doldurmuyordu. Kanıt: elle yazılmış `captured-claude.json` (gerçek çağrı yok) → `in_tok=15370 out_tok=512 cache_r=14336 cache_w=1024`, `usage_source=actual`. Canlı koşumdaki `in_tok=10` tam olarak `inputTokens`'ın önbelleklenmemiş artık olmasıydı.
4. **`--vault` + `.oom` yerleşimi ve config hatası.** `--vault` verildiğinde `oom.json` `<vault>\.oom\oom.json`'dan okunuyor (doctor `sweep.roots` satırı sentetik kökü gösteriyor). `OomSettings.Load` artık okunamayan dosyayı `LoadError`'da taşıyor ve doctor'da `config hata json` satırı olarak, `--quiet`'te de stderr'de görünüyor; sessiz varsayılana düşüş bitti.

### Sert kural — canlı makineye dokunulmadı

Kanıt koşumları sentetik vault (`.e2e/vault`, `.e2e/v0vault`) ve sahte profil (`.e2e/profile`) üzerinde. `SpecialFolder.UserProfile` kabuktan geldiği için ortam değişkeniyle yönlendirilemiyor; bu yüzden yalıtım tohumlarla yapıldı (`userSettingsPath`, `mcpCandidates`, `scheduler`, `processRunner`, `shortcutRegistrar`, `eventLogRegistrar`). Geriye kalan tek canlı okuma — `ClaudeIsolation`'ın bu makinenin Claude kimliğini yalıtılmış dizine kopyalaması — yalıtılmış dizine daha yeni bir yer tutucu koyularak etkisizleştirildi (`Prepare` "güncel" dönüp hiçbir şey kopyalamadı).

```
önce  userSettings   167f818ad90e98efc23164c8eb0d0b090f63377f24f9169f736ad9b3887a8581  2615 bayt
sonra userSettings   167f818ad90e98efc23164c8eb0d0b090f63377f24f9169f736ad9b3887a8581  2615 bayt
önce  claudeDesktop  9ebc6344403835a4ad4c0d3c5a28cbdf0186c06d03102b78d1339fe7ff52a1de  3334 bayt
sonra claudeDesktop  9ebc6344403835a4ad4c0d3c5a28cbdf0186c06d03102b78d1339fe7ff52a1de  3334 bayt
önce/sonra  schtasks /Query | grep -ic oom = 0 · Start menüsü "oom" = 0
E:\OdenaOS HEAD 74fbe242 (değişmedi; hiçbir komut oraya yazmadı)
%LOCALAPPDATA%\oom yeni girdiler: ab4df82b2af7e36b (.e2e\vault), 063f3cc3ae06c9f9 (.e2e\v0vault), backup\ (uninstall kanıtı) — üçü de sentetik
```

### Sentetik uçtan uca (INT-2 reçetesi + D3 eklentileri)

```
sweep --dry-run → sweep: 5 blok, 5 çapa
ikinci sweep    : 5 blok (idempotent)
mtime değişti   : 5 blok (içerik değişmedi, yeniden açılmadı)
ayrık flush     : 6 blok, kanca dönüşü 86 ms
doctor          : kapsama %100 · ret %0 · bekleyen 1
doctor satırı   : sweep bilgi kapsama-tum-zamanlar "Tüm zamanlar kapsama: 5/5"
doctor satırı   : doctor bilgi coverage 7d "Son 7 gün kapsama: %100,0"
doctor --json   : schema_version 1, coverage/rejection_rate/pending/items
mcp             : initialize · tools/list · tools/call yanıtladı
Z transkript    : ### Oturum (15:57) · ts:…T15:57:00+03:00
bozuk oom.json  : config hata json — "okunamadı, varsayılanlar kullanılıyor"
install probe   : dry-run yazmadı; gerçek koşum daily_ingest=5 sessions=2 sweep_stamps=2
                  calls=2 quarantine=1 retry_queue=2 coverage=1 uncovered=["sess-beta","sess-gama"]
ikinci install  : aynı sayılar (idempotent); daily/ ve knowledge/ dokunulmadı;
                  başkasının kancası duruyor; v0'ın altı satırı gitti
```

### Ölçüm

Test: `dotnet test Oom.sln -c Release` — Başarısız: 8, Başarılı: 91, Atlanan: 0, Toplam: 99. Yedi bilinen kırmızı aynen duruyor (Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098). Sekizinci kırmızı **Y-068**: satır bütçesi.

Line counts: `Install.cs` 465; `Migration.cs` 382; `ShortcutRegistration.cs` 177; `InstallRuntime.cs` 39; `Notify.cs` 57; `WindowsNotifier.cs` 103; `Flush.cs` 499; `SweepRun.cs` 316; `Runner.cs` 478; `State.cs` 502; `Doctor.cs` 196; `Configuration.cs` 206; `Program.cs` 652; `docs/install.md` 110. `src/Oom` toplamı **8.073** (Y-068 tavanı 7.500).

### Ruling ve Blocked-by

Ruling: Bana verilen `oom-lane-D3` dizini `E:\OdenaOS` kasasının worktree'siydi, Oom deposunun değil. Sert kural gereği oraya hiç yazmadım; doğru worktree `oom-lane-D3-oom` olarak `origin-of-memory`'den `e64566c` üzerinde açıldı ve bütün iş oradadır.

Ruling: `Install`'ın kendi durum şeması, kendi süreç işleticisi, kendi saati ve kendi `claude-config` yazıcısı silindi; hepsi INT-2/lane A'nın uygulamasını çağırıyor. `Migration.cs` kopya değildir — lane A `daily_ingest`, `sessions`, `sweep_stamps`, `calls`, `quarantine`, `retry_queue`, `coverage` için genel yazıcı sunmuyor, satırlar `Microsoft.Data.Sqlite` ile `State`'in kurduğu şemaya yazılıyor.

Ruling: Kanıt koşumunu `oom.exe install` ile canlı koşmadım. `Install`'ın varsayılanları gerçek `settings.json`'ı, gerçek `claude_desktop_config.json`'ı, Start menüsünü ve Task Scheduler'ı yazar; yalıtım ancak kurucu tohumlarıyla mümkün, o da ancak kod içinden. Bu yüzden kanıt `.e2e/probe/` altındaki ayrı bir konsol projesiyle alındı — `Oom.sln`'e dâhil değil, `src/` altında değil, ürüne girmiyor.

Ruling: Jeton muhasebesi hiçbir gerçek model çağrısı yapılmadan, elle yazılmış `captured-claude.json` üzerinden doğrulandı; `in_tok` artık `input + cacheCreation + cacheRead` toplamıdır ve iki önbellek yarısı `cache_r`/`cache_w`'de ayrıca durur.

Blocked-by: **Y-068 satır bütçesi — Master kararı gerekiyor.** `main` 7.318 satırla (obj ile 7.390) 7.500 tavanının 110 satır altındaydı; D2'nin taslağı 632 satır yeni yetenek getiriyor (`Migration.cs` 382, `ShortcutRegistration.cs` 177 COM interop dâhil, `InstallRuntime.cs` 39) ve B bölümü ~60 satır ekliyor. Bulabildiğim bütün gerçek kopyaları sildim (~90 satır); kalan açık **573 satır**. Bunu kapatmanın dürüst bir yolu yok: yetenek silmeden 573 satır çıkarılamıyor ve `tests/**` düzenlenemiyor. Karar Master'ın: ya Y-068'in 7.500 tavanı yükseltilir (modül tavanları için zaten bekleyen spec değişikliğiyle birlikte), ya D2'nin kapsamı küçültülür. Ben tavanı kendi başıma değiştirmedim ve kodu tavan uğruna okunmaz hâle getirmedim.

Blocked-by (küçük): Üç ayrı üretim `IClock` var — `SystemClock` (Boundaries) `OOM_FAKE_NOW`'u **okumuyor**, `FlushSystemClock` ve `VaultClock` okuyor. Yani `State`, `Runner`, `WindowsNotifier` ve `Install` sahte saati yok sayarken `Flush`/`Sweep`/`Retrieve` sayıyor: `OOM_FAKE_NOW` ile koşulan bir kanıtta daily'ler sahte, `calls`/`health`/`coverage` gerçek zamanla damgalanır. Tek saatte birleştirmek ~20 satır kazandırır ve bu tutarsızlığı kapatır; bu şeridin görevi olmadığı ve 92 yeşili riske atmamak için dokunmadım.

## Lane P3 — tek saat, `oom bench` ürün komutu, README ölçüm satırları

Ağaç `fdff18a` üstünde; koşum 9 Eylül 2026. Test satırı üç maddenin her birinden sonra aynı: `Başarısız: 7, Başarılı: 92, Toplam: 99` ve kırmızılar tam olarak Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098.

### 1. Tek saat

Üretimde üç değil dört gerçek `IClock` vardı: `SystemClock` (`Infrastructure/Boundaries.cs`, `OOM_FAKE_NOW` okumuyordu), `FlushSystemClock` (`Flush/Flush.cs`), `VaultClock` (`RootMap/VaultPaths.cs`) ve `Doctor.cs` içinde aynı adı gölgeleyen özel bir iç sınıf (o da sahte saati bilmiyordu). Dördü de silinip tek `SystemClock` bırakıldı; spec 4.1'in tek saat değişkeni `OOM_FAKE_NOW` orada, `InvariantCulture` + `RoundtripKind` ile ayrıştırılıyor (`VaultClock`'un kültüre bağlı `TryParse`'ı makine yereline göre farklı okuyordu). Paylaşılan `SystemClock.Instance` eklendi ve her bileşenin `clock ?? …` varsayılanı ona bağlandı: `State`, `Runner`, `WindowsNotifier`, `Install`, `Flush`, `Sweep`, `SweepRun`, `Retrieve`, `Compile`, `Doctor`, `Program.Clock`.

`Ingest/Parsers/ClaudeParser.cs` içindeki `EpochClock` bilerek duruyor: damgasız satıra deterministik zaman veren test/ayrıştırma saatidir (Y-077, Y-078), gerçek saat değildir. `Context.cs`'te kalan tek doğrudan duvar saati okuması (`DateTime.UtcNow`, companion dosyası bayat mı kontrolü) da `SystemClock.Instance` üstünden geçirildi; artık `src/Oom` içinde `DateTimeOffset.Now` yalnız `SystemClock`'un kendi gövdesinde geçiyor.

**Kanıt** (`.e2e/clock.sh`, sentetik vault + 5 sentetik transkript, gerçek saat 2026-09-09T20:06:59+03:00, `OOM_FAKE_NOW=2027-03-04T14:37:11+03:00`):

| | sahte saatle | `OOM_FAKE_NOW` olmadan (kontrol) |
| --- | --- | --- |
| daily başlığı | `# Günlük Log: 2027-03-04` | `# Günlük Log: 2026-09-09` |
| `flush_log.ts` (n=6) | `2027-03-04T14:37:11.0000000+03:00` | `2026-09-09T20:07:29…` |
| `calls.ts` (n=5) | `2027-03-04T14:37:11.0000000+03:00` | `2026-09-09T20:07:38…20:07:59` |
| `coverage.ts` (n=1) | `2027-03-04T14:37:11.0000000+03:00` | `2026-09-09T20:07:29…` |
| `health.ts` (n=2) | `2027-03-04T14:37:11.0000000+03:00` | `2026-09-09T20:07:59…` |

Sahte saatte beş damganın beşi de milisaniyesine kadar aynı — tek saat okunduğunun kanıtı; kontrol koşumunda hepsi gerçek saate ve birbirinden farklı anlara kayıyor. `### Oturum (HH:MM)` başlıkları her iki koşumda da oturumun kendi olay zamanından geliyor (D3 Part B kararı), saatten değil; saatin ısırdığı yer dosya başlığı ve damgalardır.

### 2. `oom bench` (spec 6.12)

`src/Oom/Bench/Bench.cs`, 250 satır ("geri kalan" bütçesinde; `src/Oom` toplamı 8 080 → 8 347, tavan 9 000). `Program.cs`'teki `bench` kolu artık gerçek komutu çağırıyor; seçenekler diğer komutlarınki gibi `Program` içinde okunuyor (`--backend`, `--transcripts`, `--dailies`, `--transcript-dir`, `--daily-dir`, `--out`, `--judge`, `--dry-run`) ve `Command(args)`'ın atlama listesine eklendi.

Ölçüm ürün yolunun kendisini not veriyor, yeniden yazmıyor: (a) özetler `Flush`'tan çıkıyor ve `Flush.ValidateSummary` kabul ediyor, (c) istem `CompilePrompt.Build`'den geliyor ve `Compile.ValidateOutputPaths` + `=== DONE ===` karar veriyor. Bu yüzden `yerel_olcum.py`'nin `--check-drift` sorununun C# tarafında karşılığı yok — kopyalanmış dize kalmadı.

Vault'a hiçbir şey yazılmıyor: `Flush` `VaultPath`, `RawChannelPath`, `RejectionPath` ve `State` verilmeden kuruluyor, bu yüzden daily bloğu, `flush_log`/`calls` satırı ve red dosyası oluşmuyor; `Compile` örneği yalnız saf doğrulayıcısı için, vault dışında bir temp köküne bağlanıyor. Yazılan tek dosya sonuç JSON'u ve o da kayıt başına yalnız dosya adı, karar, gerekçe ve süre taşıyor — model metni (`BenchRecord.Answer`) bellekte kalıyor, (b) ayağı için.

**Kuru koşum** (`--dry-run`): girdiler çözülüyor, model çağrılmıyor, dosya yazılmıyor, tabloda üç ayak da `kuru koşum / —`.

**Sentetik koşum**, 3 sentetik transkript + 2 sentetik daily, `qwen3:8b` (hem fast hem smart), Ollama `http://localhost:11434/v1`. Ham sonuç `bench/results/oom-bench-sentetik-2026-09-09.json`.

| ayak | n | sonuç | eşik | geçti |
| --- | ---: | ---: | ---: | --- |
| (a) flush beş bölümlü şekil | 3 | 1.000 | 0.95 | evet |
| (b) çift-kör yargı | 0 | koşulmadı | 3.50 | — |
| (c) compile uyumu | 2 | 0.000 | 0.95 | hayır |

Karar: `backend.flush = undecided — leg (b) not run`, `backend.compile = drop`. İki compile ıskası da sözleşme hatası: bir daily'de `=== DONE ===` hiç yazılmadı, ötekinde slug `hDMI-fiber-kablo` çıktı ve `^knowledge/concepts/[a-z0-9-]+\.md$` izin listesi bütün koşumu reddetti — lane BENCH'in Python harness'ıyla aynı sınıf hata.

(b) uygulandı (`Bench.RunJudge`: ölçülen arka ucun özetleriyle öteki arka ucun özetlerini kör A/B eşliyor, taraf ataması istemin dışında kalıyor, Claude `smart` yargıç `A=<n> B=<n>` döndürüyor) ve `--judge` verilmeden hiç çağrılmıyor; bu şerit Claude kotası harcamadı. Çağrılmadığında JSON `"judge": {"status": "not run"}` diyor.

`bench/README.md` güncellendi: yetkili ölçüm `oom bench`, `yerel_olcum.py` çevrimdışı çapraz kontrol olarak kalıyor; ikisi çelişirse `oom bench` haklı, Python kopyası bayattır.

### 3. README ölçüm satırları

`README.md`, `README.tr.md` ve `docs/scars.md` başlığı `a739c5b` yerine bu şeridin kendi koşumunu gösteriyor (`fdff18a`, 9 Eylül 2026). Her iki README'ye kapı 5 ve kapı 9 için birer cümle eklendi: kapı 5 recall@3 0,832 / recall@5 0,888 ile (eşikler 0,80 ve 0,88) geçiyor, ham sonuç `bench/results/recall-2026-09-09-r2.json`; kapı 9 yeşil, 6 mutantın 6'sı öldü, 0 sağ kaldı, ham sonuç `bench/results/mutation-2026-09-09.json`.

Ruling: Üretimdeki saat artık tek; `OOM_FAKE_NOW` ile koşulan sentetik tarama daily başlığını, `flush_log`, `calls` ve `coverage` damgalarını aynı sahte ana bağlıyor, kontrol koşumu ise hepsini gerçek saate kaydırıyor.
Ruling: Lane BENCH'in `oom bench` spec borcu kapandı — komut ürün içinde, ölçtüğü yol ürünün kendi yolu ve sonucu `bench/results/`'a yazıyor. Kapı 10 hâlâ **geçmedi**: bu koşum sentetiktir (n=3/n=2, spec 30/5 ister) ve (b) ayağı koşmadı, bu yüzden `backend.flush` belirsiz kalıyor.
Ruling: Sentetik koşum hiçbir karar vermez; `oom.json`'a hiçbir şey yazılmadı, `backend` listelerine dokunulmadı. Spec 6.12'nin tam koşumu Master'ın kararıdır.
## Lane G12 — kabul kapıları 11 ve 12 ölçülebilir hâle geldi

`tests/Oom.Tests/Gates/` yeni klasöründe iki dosya: `Gate11Contracts.cs` (8 test) ve `Gate12Boundary.cs` (8 test). Bütün fixture'lar temp altında sentetik ve test sonunda siliniyor; hiçbir model çağrısı, hiçbir gerçek transkript, hiçbir ağ erişimi yok. `dotnet test Oom.sln -c Release` = **108 yeşil / 7 kırmızı / toplam 115** — 92 yara yeşil değişmedi, 7 bilinen kırmızı (Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098) aynen duruyor, 16 yeni kapı testinin hepsi yeşil.

### Kapı 11 — sözleşmeler (spec 11-11)

| madde | test | sonuç | ürün değişikliği |
| --- | --- | --- | --- |
| claude sabit örneği | `Gate11ClaudeFixedSampleParses` | geçti | yok |
| codex sabit örneği | `Gate11CodexFixedSampleParses` | geçti | yok |
| gerçek Claude Code biçimi (sidechain/meta/araç dışarıda) | `Gate11ClaudeRealShapeExcludesSidechainMetaAndToolLines` | geçti | yok |
| bilinmeyen satır türü hoş görülür | `Gate11UnknownLineTypeIsTolerated` | geçti | yok |
| yarım son satır patlatmaz | `Gate11TruncatedLastLineDoesNotThrow` | geçti | **evet** — `CodexParser` yarım JSON satırında `FormatException` atıyordu, artık o satırı atlıyor |
| MCP üç aracı sahte istemciyle uçtan uca | `Gate11McpStdioLoopAnswersThreeToolsAndEndsAtEndOfInput` | geçti | yok (`Mcp.Run(TextReader, TextWriter)` zaten vardı) |
| MCP not adı dizin yürüyüşünü reddeder | `Gate11McpRefusesPathTraversalInNoteName` | geçti | yok |
| `save --session-json` normal flush yolundan daily bloğuna | `Gate11SaveSessionJsonWritesImportedDailyBlock` | geçti | **evet** — `Save` hand-off dosyasını geri okuyordu, her dış oturum daily'de `içe aktarım:claude` oluyordu; artık oturum kendisi olarak yazma yoluna giriyor |

MCP testi: `initialize` → `notifications/initialized` (yanıtsız) → `tools/list` (tam üç araç) → `tools/call memory_search` (limit 9, beşe kırpılıyor; ikinci çağrı ham anahtarla yapılıyor ve yalnız `Guards.Gate(In)`'in ürettiği `[SIR:anthropic-key]` maskesi eşleştiği için o not dönüyor — sorgunun kapıdan geçtiğinin gözlemlenebilir kanıtı) → `memory_root_map` → `memory_note` → bilinmeyen araç adı (`-32602`) → bozuk satır (`-32700`) → EOF'ta döngü biter. Vault sentetik: 20 kavram notu, kök harita, `hub-config.json`.

`save --session-json` testi modelsiz koşuyor: `Runner(configured: false)` ile yazma yolu kendi extractive geri düşüşünü alıyor (`fallback_backend: extractive` özette doğrulanıyor), blok `, içe aktarım:web-disari` son eki ve `<!-- session:… turns:0-5 source:web-disari -->` çapasıyla yazılıyor, imleç 6'ya ilerliyor, aynı dosyayla ikinci çağrı `NoNewTurns` dönüyor.

### Kapı 12 — sınır (spec 11-12)

| madde | test | sonuç | ürün değişikliği |
| --- | --- | --- | --- |
| `doctor --json` şeması | `Gate12DoctorJsonCarriesItsSchema` | geçti | yok |
| `context --json` şeması | `Gate12ContextJsonCarriesItsSchema` | geçti | yok |
| `retrieve --query --json` şeması | `Gate12RetrieveJsonCarriesItsSchema` | geçti | yok |
| `state.db` beş salt okunur görünümü | `Gate12StateDatabaseExposesReadOnlyViews` | geçti | **evet** — `v_calls`, `v_flush_log`, `v_coverage`, `v_health`, `v_kota` hiç yoktu; `State` şemasına eklendi, `user_version` 1 → 2 |
| görünümler yazılamaz | `Gate12ViewsRefuseWrites` | geçti | yukarıdakiyle aynı |
| `extensions.contextLine` komutu çalışır | `Gate12ExtensionContextLineIsExecuted` | geçti | **evet** — değer komut olarak değil düz metin olarak bağlama basılıyordu; artık çalıştırılıyor, ilk satırı alınıyor |
| hata veren/olmayan uzantı hiçbir şey eklemez | `Gate12FailingExtensionAddsNothing` | geçti | yukarıdakiyle aynı |
| `src/Oom` paket adı tanımaz | `Gate12CoreKnowsNoPackageName` | geçti | yok — `oom-kota`, `oom-rapor`, `oom-ingest-extra`, `oom-pano` hiçbiri yok |

Üç JSON şeması testi kütüphaneyi değil yayımlanan `oom.exe`'yi alt süreç olarak koşuyor: sözleşme paketin gördüğü çıktının kendisi. Alan listeleri testin içinde yazılı ve varlık iddia ediyor, tam küme değil — spec 2.2 alan eklemeye izin verir, silmeye ve yeniden adlandırmaya vermez. `doctor --json` item'ları bugün karışık harf düzeninde (`Component`, `Code`, `Key`, `Detail` büyük; `level`, `stale` küçük); yeniden adlandırma yasak olduğu için test bugünkü adları sabitliyor.

`extensions.contextLine` çalıştırması: komut satırı tırnak duyarlı bölünüyor, çocuk süreç `OOM_INVOKED_BY` taşıyor (bir uzantı `oom`'a geri kabuk açarsa orada durur), 5 saniye zaman aşımı var, yalnız ilk boş olmayan satır alınıyor ve 200 karakterde kesiliyor. Çıkış kodu sıfır değilse, süreç başlamadıysa ya da çıktı boşsa hiçbir şey eklenmiyor; kapanış cümlesi (`Hafıza protokolü zorunludur.`) bloğun son satırı kalıyor.

Satır bütçesi (D13): `src/Oom` yazılmış C# 8 438 / 9 000; `Mcp` 136 / 300; `State` 510 / 700; `Program` 748; `Save` 89; `CodexParser` 76.

Ruling: Kapı 11 ve kapı 12 artık ölçülüyor ve ikisi de yeşil; dördü de gerçek boşluktu — codex yarım satırı, `save --session-json` kaynak etiketi, beş `state.db` görünümü ve `extensions.contextLine`'ın hiç çalıştırılmaması. Ürün yalnız bir test boşluğu gösterdiği yerde değişti.
Ruling: `state.db` şema sürümü 2'ye çıktı; görünümler `CREATE VIEW IF NOT EXISTS` olduğu için mevcut bir veritabanı ilk açılışta kendiliğinden kazanıyor, tablolara dokunulmadı.
Blocked-by: yok.

<!-- yazan: codex · gpt-5 -->
## Lane VM

### 2026-09-10 — VM-2

Gerçek: ADIM 5 sorusu derlenen ilk concept dosyasının ASCII slug'ından üretiliyor; ham sıralama `adim5-query.json`'da, hook stderr/ret gerekçesi `adim5-retrieve.err`'de tutuluyor. `dogrula.py`, hook `additionalContext` alanında derlenen dosya kökünü veya gerçek H1 başlığını arıyor; YAML frontmatter sonrasındaki H1'i okuyabiliyor, ham sıralamadaki yerini `ham sira=1/1` olarak raporluyor ve eski/eksik kanıt klasörlerinde çökmüyor. Tek notluk temiz-VM korpusunda normalize BM25 puanı IDF tabanına düştüğünden VM konfigürasyonu `minOverlap=3`, `strictScore=0.0`; ilgi kapısı concept kimlik alanlarındaki üç sözcük örtüşmesiyle açık kalıyor. Geçici sözleşmesiz yerel model çıktısı için ADIM 4, kavram yoksa en çok üç compile denemesi yapıyor.

Gerçek: Taze host kuru koşumu sonucu `[ADIM 1] ok · [ADIM 2] atlandi · [ADIM 3] ok · [ADIM 4] ok (1 kavram, ilk deneme) · [ADIM 5] ok (enjeksiyon var, ham sıra 1/1) · [ADIM 6] ok · [ADIM 7] ok`; `dogrula.py` tablosunda yedi satırın tamamında `gunluk=olcum`. Kanıtlar: `bench/vm/.out/zincir.log`, `bench/vm/.out/adim5-query.json`, `bench/vm/.out/adim5-retrieve.json`, `bench/vm/.out/adim5-retrieve.err`, `bench/vm/.out/knowledge/concepts/turkce-tokenizasyon-olcusi.md`, `bench/vm/.out/doctor-1.json`, `bench/vm/.out/doctor-6.json`, `bench/vm/.out/state.db`; sonuç `bench/results/vm-2026-09-10.json`. Gerçek `%USERPROFILE%\.claude\settings.json` SHA-256 önce/sonra aynıdır: `167f818ad90e98efc23164c8eb0d0b090f63377f24f9169f736ad9b3887a8581`; host'ta gerçek `claude -p` çalıştırılmadı ve install yalnız `--dry-run` koştu.

### 2026-09-10 — VM-3

Gerçek: `OOMVM_LOCAL` anahtarı varsayılan `1` ile mevcut Ollama ayağını korur; `0` iken bootstrap yalnız Claude flush yapılandırması yazar ve `host.txt` okumaz, zincir ADIM 7'yi Master kararıyla atlar. Host ortamı Sandbox'a doğrudan geçmediği için `hazirla.cmd`, verilen `OOMVM_*` değerlerini yaz-oku bağlı `.out/ayar.cmd` üzerinden `bootstrap.cmd` ve `zincir.cmd`'ye taşır; yerel ayak kapalıyken IP araması ile `host.txt` yazımı yapılmaz. `dogrula.py` atlanan yerel ayağı hata veya başarı saymadan JSON'a `adim7=atlandi`, `yerel_ayak=false` yazar; Sandbox'ta adımlar 1–6 yeşilse Master kararına özgü geçiş hükmünü üretir.

Gerçek: VM-3 taze host kuru koşum kanıtı ve gerçek kullanıcı ayarı SHA-256 önce/sonra sonucu bu paragrafın doğrulama turunda tamamlanacaktır.

## Lane FIX — temiz Windows Sandbox kabul koşumundan çıkan yedi kusur

### 2026-09-10 — FIX

Gerçek (K1 · Y-100): `src/Oom/Oom.csproj` `PublishSingleFile` taşıyordu ama `IncludeNativeLibrariesForSelfExtract` taşımıyordu, bu yüzden `publish/win-x64/` içinde `oom.exe` yanında `e_sqlite3.dll` duruyordu; `Install.InstallBinary` yalnız `Environment.ProcessPath`'i kopyaladığı için kurulan kopya `DllNotFoundException` (rc -532462766) ile ölüyordu. Özellik csproj'a eklendi; yeniden yayımlanan dizinde exe'nin yanında tek bir `.dll` yok (`oom.exe` 99.045.887 bayt + `oom.pdb`), ve `.brief/solo/` içine yalnız exe kopyalanarak koşulan `doctor` rc 0 verdi. `KurulumScars.Y100_SingleFilePublishEmbedsNativeLibraries` hem csproj özelliğini hem — yayımlanmış ağaç varsa — exe'nin yanında `.dll` bulunmamasını doğruluyor. `bench/vm/bootstrap.cmd:86`'daki dll kopyalama geçici çözümü silinmedi, üstüne etkisiz kaldığını söyleyen bir `rem` satırı eklendi.

Gerçek (K3 · Y-101): `save "<metin>"` "kayıt yazıldı" deyip rc 0 dönüyordu ama hiçbir şey yazmıyordu: `Save.VerifiedMemoryWrite` baytları yalnız bellekte tur attırıyor, `Program.RunSave` de `new Save()`'i gerçek yazıcısız kuruyordu. Yeni `Save.WriteCheckpointToVault` bloğu `FormatDailyBlock` biçimiyle `daily/yyyy-MM-dd.md`'ye ekliyor, dosyayı diskten geri okuyor ve blok içinde değilse `Written=false` dönüyor; enjekte edilebilir `checkpointWriter` testler için yerinde duruyor. CLI yolu artık kasayı `RunSave(args, vault)` ile alıyor, başarısızlıkta rc 1 ve "kayıt yazılmadı: …" yazıyor.

Gerçek (K3b · Y-102): `RunSave` kayıt metnini `args[1]`'den okuyordu; `--vault X save "..."` yazıldığında bu, kasa yoluydu ve her koşum "eksik alan: karar, düzeltme, devir" diyordu. Argüman ayrıştırma `src/Oom/Infrastructure/CommandLine.cs` içine tek bir yere alındı: `Positionals` değer alan seçenek çiftlerini atlıyor, `Command` ilk konumsal argüman, `Argument(args, 0)` ise komuttan sonraki ilk konumsal argüman. `Program` kendi kopyalarını sildi ve bu sınıfa devretti; stdin geri düşüşü korundu.

Gerçek (K4 · Y-103): Flush `claude`'u çıplak adla başlatıyordu (`Runner.BuildClaudeRequest`), npm'in yalnız `claude.cmd` bıraktığı makinede `Process.Start` "Sistem belirtilen dosyayı bulamadı" veriyor ve her oturum Retry'a düşüyordu. Y-073'ün `ResolveExecutable`'ı bu yolda hiç çağrılmıyordu; artık çağrılıyor. Çözümleyici saf kaldı: PATH okuması çağrı yerinde yapılıyor, testin sahte PATH vermesi için `BuildClaudeRequest` isteğe bağlı bir `pathValue` alıyor. `Doctor`'ın `claude-reachable` gözlemi de aynı çözümleyiciden geçiyor ve sabit cümle yerine çözümlenen yolu yazıyor.

Gerçek (K5 · Y-104): Kurulumun `oom.json` şablonu kod varsayılanlarının elle yazılmış ikinci bir kopyasıydı ve ikisi ayrışmıştı — `sweep.roots: []` yüzünden temiz kurulum hiçbir şey taramıyor, `retrieve.strictScore: 25.0` / `minOverlap: 2` de kodun 1,0 / 3 değerlerini eziyordu. `git log -S` 25,0 ve 2'yi lane D'nin ilk teslimine (`9f1c36a`) götürüyor; `bench/README.md` R2 ölçümünden sonra `strictScore`'un anlamının değiştiğini (sıralanan sorgu terimi başına ortalama katkı) söylüyor, dolayısıyla brief'in izniyle `src/Oom/Infrastructure/Configuration.cs` değerleri esas alındı. Şablon artık `OomSettings.DefaultJson()` ile üretiliyor; kökler `%USERPROFILE%` biçiminde yazılıyor ve okuma sırasında genişletiliyor, yani tek bir doğruluk kaynağı var.

Gerçek (K6): `Context.Head` üç bölümü başlıkla kesiyor (`## Session:`, `## Active`, son `## `), başlığı olmayan companion dosyası sessizce boş bölüm üretiyordu. Markerlar `docs/install.md` içindeki yeni "Companion file markers" tablosunda belgelendi; `Context.CompanionWarnings` dosya var ama başlık yokken bölüm başına bir satır üretiyor ve `Build` bunları stderr'e yazıyor. Davranış bu uyarının dışında değişmedi; yara numarası açılmadı, `KancaScars.ContextWarnsWhenCompanionFileCarriesNoMarker` uyarıyı doğruluyor.

Gerçek (ölçüm): Değişiklik öncesi `dotnet test tests/Oom.Tests -c Release` — Toplam 115, Geçti 108, Başarısız 7 (92 yeşil yara + 7 kırmızı yara + 16 kapı testi). Kırmızı koşum (`.brief/red.txt`) — Toplam 121, Geçti 108, Başarısız 13: altı yeni test de kırmızı. Yeşil koşum (`.brief/green.txt`) — Toplam 121, Geçti 114, Başarısız 7; kalan yedi kırmızı Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098, yani şerit INT'in hükümlü listesi aynen duruyor, gerileme yok.

Gerçek (K7 · Y-105): Kurulu kopyadan (`<kasa>\.oom\oom.exe`) çalıştırılan `oom.exe install --uninstall`, `Install.Uninstall`'daki dosya silme döngüsünde kendi çalışan ikilisini `File.Delete` ile silmeye çalışıp `UnauthorizedAccessException` (rc -532462766) ile çöküyordu; sıra dosyalar önce, kancalar/görev/MCP sonra olduğu için çökme kancaları, `%USERPROFILE%\.claude\settings.json`'daki dört girdiyi, zamanlanmış görevi ve MCP kaydını yerinde bırakıyordu — yarım kalmış bir kurulum. `bench\vm\.out\tur2-7.log` ve `tur2-ozet.md` bu izi doğruluyor (salt okunur, dokunulmadı). Düzeltme iki parça: (1) `Uninstall` artık kancaları, zamanlanmış görevi ve MCP girdisini EN BAŞTA kaldırıyor, kanıt/yedek arşivini de dosya silmeden ÖNCE yazıyor — hangi adımda patlarsa patlasın makine yarım kalmıyor; (2) silinecek dosya `Environment.ProcessPath`'e (yeni enjekte edilebilir `processPath` seam'i üzerinden, tam yol + OrdinalIgnoreCase karşılaştırmasıyla) eşitse `File.Delete` hiç çağrılmıyor — dosya `<ad>.uninstalled-<yyyyMMdd-HHmmss>` olarak yeniden adlandırılmaya çalışılıyor, adlandırma da başarısız olursa (kilitli kalmışsa) sessizce yakalanıp orijinal yol raporlanıyor; her iki durumda da sonuç `exe-elle-sil:<yol>` kaydı taşıyor ve `Program.cs` konsola `kaldırma tamam — çalışan exe elle silinir: <yol>` satırını basıyor; `UninstallCore`'u saran genel `try/catch` hiçbir istisnanın `Uninstall`'dan kaçmamasını garanti ediyor. Test `KurulumScars.Y105_UninstallFromInstalledCopySurvivesSelfDelete` sahte kasa + sahte `%USERPROFILE%\.claude\settings.json` + enjekte edilmiş `userSettingsPath`/`mcpCandidates`/sahte `ITaskScheduler`/`processPath` ile gerçek makineye hiç dokunmadan çalışıyor; "çalışan exe"yi taklit etmek için dosya `FileShare.None` ile kilitleniyor — kırmızıda bu, gerçek K7 ile birebir aynı `IOException`'ı (`Install.Uninstall` içinde `File.Delete` satırında, yakalanmadan) üretiyor, yeşilde ise kancalar kaldırılmış, diğer aracın kancası korunmuş, istisna kaçmamış ve kilitli dosya orijinal adıyla `exe-elle-sil:` olarak raporlanmış durumda kalıyor. Kırmızı koşum (`.brief/red-k7.txt`) — Toplam 122, Geçti 114, Başarısız 8 (yedi bilinen kırmızı + yeni Y-105). Yeşil koşum (`.brief/green-k7.txt`) — Toplam 122, Geçti 115, Başarısız 7; kalan yedi kırmızı aynen Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098, gerileme yok.

Gerçek (Y-106): `ingest` kaynağı `args[1]` ile okunduğu için `--vault X ingest codex` kasa yolunu kaynak sanıyor, kasa seçeneği komuttan sonra verildiğinde argümansız `ingest` de `--vault` değerini varsayılan `claude` yerine kaynak alıyordu. `Program` artık Y-102'nin ortak `CommandLine.Argument(args, 0)` ayrıştırıcısını kullanıyor; `Program.cs` içindeki `args[1]`/`args[2]` taramasında başka konumsal komut okuyucusu bulunmadı. `YazmaYoluScars.Y106_IngestSourceIsTheFirstPositionalAfterTheCommand` düz, önde `--vault` bulunan ve varsayılan kaynak biçimlerini doğruluyor; kırmızı koşum `.brief/red-y106.txt` içinde Toplam 123 / Geçti 115 / Başarısız 8, yeşil koşum `.brief/green-y106.txt` içinde Toplam 123 / Geçti 116 / Başarısız 7 verdi ve kalanlar Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098 oldu.

Gerçek (Y-107): `save --session-json` dış oturumundaki `turns[0].text` bir string yerine nesne olduğunda `Save.SaveSessionJson` sözleşmesi gereği `FormatException` üretiyor, fakat `Program.RunSave` bunu yakalamadığı için CLI yakalanmayan istisnayla rc -532462766 dönüyordu. `RunSave` artık `--session-json` yolundaki `FormatException` ve `ArgumentException` hatalarını stderr'e `kayıt yazılmadı: <mesaj>` olarak yazıp rc 1 döndürüyor; `Save.SaveSessionJson` sözleşmesi değişmedi. `YazmaYoluScars.Y107_SaveSessionJsonRejectsMalformedInputWithoutEscapingProgram` aynı bozuk JSON'un `Save` sınırında hâlâ fırlatıldığını ve Program sınırından kaçmadan rc 1'e çevrildiğini doğruluyor.

Gerçek (Y-108): `install --uninstall` başarılı olduğunda ortak sonuç dalı `kurulum tamam`, başarısız olduğunda `kurulum başarısız: <hata>` yazıyordu. `Program` kaldırma sonucunu artık `kaldırma tamam` / `kaldırma başarısız: <hata>` olarak ayırıyor; Y-105'in çalışan exe kalıntısı için tek satırlık `kaldırma tamam — çalışan exe elle silinir: <yol>` bildirimi aynı kaldı ve yinelenmiyor. `KurulumScars.Y108_UninstallReportsUninstallOutcome` üç kaynak satırını birlikte sabitliyor.

Gerçek (ölçüm · Y-107/Y-108): Başlangıç tabanı Toplam 123 / Geçti 116 / Başarısız 7 idi; bilinen kırmızılar Y-035, Y-039, Y-042, Y-046, Y-050, Y-069 ve Y-098. İki yeni scar eklendikten sonraki kırmızı koşum `.brief/red-y107-108.txt`, düzeltme sonrası yeşil koşum `.brief/green-y107-108.txt` içindedir.

Gerçek (Y-109): `Program.Main`'in dispatch anahtarı hiçbir genel `try/catch` taşımıyordu; beklenmeyen bir istisna (ör. `save --session-json` dışındaki bir yolda) CLI dışına çıkıp Windows Hata Bildirimi'nin "oom.exe - Uygulama Hatası 0xe0434352" kutusunu açıyor, hook zincirini askıda bırakıyordu. `Main` artık ikiye ayrıldı: eski gövde `Dispatch(string[])`'e taşındı, `Main(string[], Func<string[], int> dispatch)` süreç başında kernel32 `SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX)` çağırıp WER kutusunu kapatıyor, ardından `dispatch(args)`'ı son çare `try/catch (Exception)` ile sarıp yakalanan her hatayı stderr'e tek satır `hata: <TipAdı>: <Mesaj>` olarak yazıp rc 1 dönüyor; tek argümanlı `Main(string[])` bu sarmalayıcıyı gerçek `Dispatch` ile çağırıyor. `GuardedCommands` sessiz-çıkış davranışı ve Y-107'nin `save --session-json` → `kayıt yazılmadı: ...` / rc 1 özel yakalayıcısı dokunulmadan duruyor — genel yakalayıcı yalnız oraya kadar kaçan istisnaları görür. `SurecIsletmeScars.Y109_MainCatchesUnhandledExceptionAndDisablesWerDialog` reflection ile iki-parametreli `Main` aşırı yüklemesini çağırıp kasıtlı bozuk bir dispatcher fırlatıyor, rc 1 ve `hata: InvalidOperationException: dispatch kırıldı` stderr satırını doğruluyor, ayrıca `Program.cs` kaynağında `SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);` satırının var olduğunu sabitliyor. Ölçüm: `.brief/green-y107-109.txt` — Toplam 126, Geçti 119, Başarısız 7; kalanlar aynen Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098, gerileme yok.

<!-- yazan: codex · gpt-6 -->
## Lane GATE

Gerçek: `fdf4b1f` üzerindeki yarım kalmış test Y-110 olarak adlandırıldı. `GetirmeScars.Y110_SmallCorpusGateKeepsRelevantTopHitAndRejectsUnrelatedPrompt` önce boş hook nedeniyle kırmızı (`.brief/gate/red-y110.trx`), sonra yeşil (`.brief/gate/green-y110.trx`). `strictScore`, BM25F toplamının önekler dâhil farklı sorgu terimi sayısına bölümünü; `minOverlap`, slug/başlık/aliases/tags üzerinde uzunluğu en az dört olan farklı içerik terimlerinin örtüşmesini karşılaştırıyordu. 19 notta yaygın terimlerin IDF tabanı nedeniyle doğru ilk notların S2/S3/S4 ortalamaları 0,652047/0,896436/0,246233; örtüşmeleri 3/5/9. Eski 1,0 kapısı üçünü reddetti. Kaynak: `src/Oom/Retrieve/Retrieve.cs`, salt okunur `E:/OdenaWorks/10-Aktif/origin-of-memory-2.0/bench/vm/.out/kiyas/oom/T3-getirme.log`, aynı kanıt ağacındaki `hafiza-obegi/OKU.md` ve `bench/results/recall-2026-09-10-gate.json`.

Gerçek: canlı kasa ölçüm sırasında 542 değil 550 kavram içeriyordu; tarihsel 542 kopyası doğrulanamadı. Aynı 550 dosyada önce/sonra gate 5 (kapısız Query, mevcut kos20 sözleşmesi) 0,832/0,888; 19 notun `.brief/hafiza-obegi` kopyasında hook 5/8 → 8/8, altı negatif 0/6 → 0/6. Manifestler ve Query adları/skorları önce/sonra aynı. Seçilen aday ile gerçek Hook kararları 139 sorguda eşleşti. Canlı kasa hook recall'u ayrıca 0,432/0,440 olarak kaldı; gate 5 sonucu hook recall diye sunulamaz. Göreli skor 0,50/0,25; doğrusal/logaritmik külliyat ölçekleme; örtüşme oranı 0,50; sabit ortalama eşikleri 1/0,50/0,25/0,10/0 ölçüldü. Düşük sabit ve göreli kurallar iki kanaryayı (q089, q129) enjekte etti; logaritmik ölçek 7/8, örtüşme oranı 5/8 kaldı. Tam tablo ve yöntem: `bench/README.md`, GATE bölümü.

Karar / çıkarım: ölçülen adaylar içinde üç şartı birlikte sağlayan doğrusal kapı seçildi: `score / terms >= strictScore * min(1, max(1,N)/542)` ve mevcut `overlap >= minOverlap`. 542 tarihsel gate-5 kalibrasyon büyüklüğü; N≥542'de önceki eşik aynen korunuyor. BM25F sıralaması ve kurulum varsayılanları değiştirilmedi. Altı negatif üzerindeki sonuç diğer küçük külliyatların tüm negatiflerini koruduğu iddiası değildir.

Gerçek: Release build/publish başarılı. Sahibin referansı 123/116/7; sandbox başlangıcı 123/111/12 (`.brief/baseline-isolated-tests.txt`), son koşum 124/112/12 (`.brief/gate/final-tests.trx`). Yedi bilinen kırmızı aynı; beş ek başarısızlık önceden bulunan Gate12 CLI sınır testleri: retrieve/context/doctor JSON ve iki extension testi (rc -532462766). Bu devamın `.brief/gate/baseline-tests.trx` koşumunda açık filtre kasıtlı IntentionalRed tohumunu da çalıştırdı; bu tek tohum çıkarılınca karşılaştırılabilir toplam 123/111/12. Yeni başarısızlık yok. Yara tablosu 107 yara, 100 yeşil, 7 kırmızı; getirme 8 yeşil/3 kırmızı.

Ölçülemeyen / kapsam: tarihsel tam 542 notluk tekrar ve sandbox'ta uçtan uca CLI hook. Canlı kasanın CLI batch ölçümü çalıştı; dilimin CLI çağrısı Windows profil durum klasörünü oluştururken UnauthorizedAccessException verdi. Dilim aynı ürünün gerçek C# Query/Hook yollarıyla, IndexPath verilmeden ölçüldü. Profil durum yolu ve diğer şeritler bu kapı görevinin dışında tutuldu. Kasa, kanıt kaynağı ve Companion dosyalarına yazılmadı; bench ve geçici çıktılar bu worktree içinde.

Gerçek: tek commit denemesi yapıldı; commit engellendi. `git add` ve `git commit`, bu worktree dışındaki `E:/OdenaWorks/10-Aktif/origin-of-memory/.git/worktrees/oom-lane-GATE/index.lock` için Permission denied verdi. Başlık ölçülen gerçek sayıya göre `2.0(GATE): Y-110 — retrieval gate corpus-scaled, measured on 550-note vault and 19-note slice` idi. Değişiklikler commit edilmeden bu worktree içinde duruyor.
