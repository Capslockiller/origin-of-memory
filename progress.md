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
