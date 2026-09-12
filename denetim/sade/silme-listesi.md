---
title: OoM 2.x sadeleştirme — silme listesi
created: 2026-09-12
type: karar
status: onay-bekliyor
---
# OoM 2.x sadeleştirme · silme listesi (12 Eyl 2026, Fable)

Taban: `oom-w-INT` `1f717dd`, 12.770 satır kaynak, 9.194 satır test. Ölçek: A/B kalır, D gider.
**Hiçbir şey bu listeye onay gelmeden silinmez.** Silme yeni dalda tek commit; `wave/` dalındaki 42 commit dokunulmaz; disk üstünde repo dışı hiçbir dosya silinmez.

## 1. Kaynak — bütünüyle silinecek dosyalar

| Dosya | Satır | Neden |
|---|---|---|
| `src/Oom/Bench/Bench.cs` | 383 | bench (D) |
| `src/Oom/Install/Install.cs` | 1.005 | user-scope installer, uninstall, kimlik yalıtımı (D) |
| `src/Oom/Install/InstallRuntime.cs` | 98 | schtasks (D) |
| `src/Oom/Install/ShortcutRegistration.cs` | 253 | Başlat kısayolu, AUMID, Event Log (D) |
| `src/Oom/Install/Migration.cs` | 651 | v0 göçü, tek seferlik (D) |
| `src/Oom/Infrastructure/ClaudeIsolation.cs` | 212 | kimlik kopyası, installer'a bağlı (D) |
| `src/Oom/State/StateSchemaAudit.cs` | 564 | şema merdiveni denetimi; merdiven gidince gereksiz (D) |
| `src/Oom/Notify/Notify.cs` | 57 | ölü kod |
| `src/Oom/Notify/WindowsNotifier.cs` | 103 | toast (D) |
| `src/Oom/Ingest/Ingest.cs` | 122 | arşiv geri doldurma; `gecmis-import` skill'i ve `save --session-json` yerine geçer (C) |
| `src/Oom/Contracts/LaneA..D.cs` | 6 | boş şerit işaretçileri |
| **Toplam** | **3.454** | |

Kalan `Install/` klasörü boş kalır; proje kapsamı kurulum (`.oom\oom.exe` + `vault.json` + `oom.json` + proje `settings.json` kancaları) Program.cs içinde ~60 satırlık tek fonksiyona iner.

## 2. Kaynak — kısmen budanacak dosyalar

| Dosya | Şimdi | Giden parça | Kalan |
|---|---|---|---|
| `Retrieve/Retrieve.cs` | 1.139 | `Hook()` :339, `ShouldInject` :406, `GateReason` :455, `Filter`/served :485–560, `ServedLedger` sınıfı :895–1035 → ~400 satır | `Build`, `VerifyIndex`, `Query`, `Search`, BM25, `Candidates`, `IndexVerifier` |
| `State/State.cs` | 1.016 | merdiven + audit + yedek-doğrula-göç :552–980 → ~430 satır; tablolar `calls`, `kota`, `notified`, `locks`, `retrieve_served`, `coverage`, `ingest_done`, `vault_meta`, `compile_runs` + 6 görünüm | tek `CREATE IF NOT EXISTS`, `user_version` yok, dosya silinip yeniden kurulabilir. Kalan tablolar: `sessions`, `flush_log`, `retry_queue`, `sweep_stamps`, `daily_ingest`, `quarantine`, `health`, `notes`, `notes_fts`, `oom_index_meta` |
| `State/SessionStore.cs` | 158 | kota okuma/yazma, `notified` | imleç, retry kuyruğu, damgalar |
| `Doctor/Doctor.cs` | 832 | `InspectStateRoots` :226, `StateRootSnapshot` :336, `SourceGuard`, kök sınıflandırması :608–756, `ValidateInstalledBinary` :137, iki `ClearAllPools` → ~500 satır | kanca doğrulama, indeks sağlığı, kapsama, `Check` tablosu |
| `Program.cs` | 916 | `install` :136–161, `bench` :163–181, `ingest` :105–115, `Snapshot`'taki `calls`/kota satırları, `--user-scope` → ~300 satır | context, retrieve, flush, sweep, compile, doctor, save, mcp + mini kurulum |
| `Runner/Runner.cs` | 494 | Ollama arka ucu :29–35, `CallLocal`, `_localUrl`, `calls` defteri yazımı (`RecordCall`) → ~230 satır | `claude -p` çağrısı, zarf temizleme, zaman aşımı |
| `Guards/Guards.cs` | 331 | sır maskeleme :30–44/191–206, KVK maskeleme :46–51/208–315, egress kuralı, `NormalizePath` → ~250 satır | unicode katlama + talimat tespiti (avenox eşdeğeri) |
| `Infrastructure/Configuration.cs` | 284 | `Local` ayarları, loopback doğrulaması, `Backend` zinciri → ~60 satır | `oom.json` okuma |
| `Sweep/Sweep.cs` | 174 | `BuildScheduledTaskXml` :133–160 | bellek içi tazelik motoru (SweepRun kullanıyor) |
| `Compile/Compile.cs` | 620 | `locks` tablosu kullanımı, `calls` → ~40 satır | mutex, geri alma, karantina, yayın |
| `Context/Context.cs` | 271 | `[Durum]` kota satırı :66, :262–270 | 8 bölüm |
| **Toplam giden** | | **~2.400** | |

## 3. Kaynak — dokunulmayacak

`Flush/*` (728) · `Compile/CompilePrompt.cs` · `Mcp/Mcp.cs` (136) · `Save/Save.cs` (121) · `Notes/*` (358) · `RootMap/*` (320) · `Bridge/Bridge.cs` (67) · `Sweep/SweepRun.cs` (310) · `Sweep/SourceClassifier.cs` · `Ingest/Parsers/*` (232, flush ve sweep transkript okuyor) · `Infrastructure/{Boundaries,CommandLine,DetachedProcess,HookPayload,VaultIdentity}.cs` · `Contracts/Models.cs`.

Not: Flush'ın imleç tasarımının avenox'un "son N tur" dilim tasarımına dönmesi **silme değil değişiklik**; ayrı iş, bu listede yok.

## 4. Testler

**Silinecek (17 dosya, 5.971 satır):** `KotaScars` 61 · `KurulumScars` 520 · `UninstallOwnershipTests` 455 · `UninstallSafetyOrderTests` 121 · `SchemaV5MigrationTests` 671 · `SchemaIntegrityTests` 597 · `DoctorStateContentTests` 331 · `DoctorHonestDiagnosisTests` 904 · `EszamanliTemizlikTests` 251 · `RecallCandidateBindingTests` 590 · `RecallEvidenceTests` 287 · `LoopbackScars` 125 · `KimlikScars` 177 · `AyrismaTests` 264 · `DefterScars` 238 · `GonderimSiniriScars` 294 · `ConfigurationUpgradeTests` 85.

**Budanacak:** `DurumDeposuScars` 528 (merdiven testleri) · `Gate11Contracts` 359 (install/bench sözleşmeleri) · `Gate12Boundary` 200 (kök sınırları) · `GetirmeScars` 436 (hook kapısı, served) · `YazmaYoluScars` 341 (`calls`).

**Kalan:** `IndeksScars` · `KancaScars` · `OzetleyiciScars` · `DerleyiciScars` · `SurecIsletmeScars` · `TestDisipliniScars` · `ScarFixture` · `IntentionalRed` · `xunit.runner.json` · `default.runsettings`.

`docs/scars.md`: silinen testlerin Y satırları defterden düşer; eski defter git tarihinde kalır. Y-125 çift yönlü bağ korunur.

## 5. Repo — kaynak dışı

| Yol | Ne | Karar |
|---|---|---|
| `bench/` (tümü) | 16 Python betiği ~9.500 satır, `results/` ~90.000 satır JSON, `vm/`, `gate/` | **sil** (bench D). Recall kanıtları git tarihinde kalır; gold set zaten repo dışında |
| `.github/workflows/ci.yml` | build+test+publish | kalır, publish adımı çıkar |
| `.github/{FUNDING,ISSUE_TEMPLATE,PULL_REQUEST_TEMPLATE,dependabot}` | public repo metinleri | **metin kararı (Faz 7)**, bu listede değil |
| `docs/release-notes-*`, `docs/releases/*`, `CHANGELOG.md` 1.693, `progress.md` 855, `README*`, `CONTRIBUTING`, `CODE_OF_CONDUCT`, `SECURITY`, `docs/architecture.md`, `docs/install.md`, `docs/attribution.md` | metin | **metin kararı (Faz 7)**, bu listede değil |
| `denetim/` | Astra dalga kayıtları | kalır (tarih) |

## 6. Sonuç tahmini

| | Şimdi | Sonra |
|---|---|---|
| Kaynak | 12.770 | ~6.900 (bütün 3.454 + kısmi ~2.400 gider) |
| Test | 9.194 | ~2.700 |
| Tablo | 19 + 6 görünüm | 10, görünüm yok, göç yok |
| Makine dışı yan etki | 7 | 1: `%LOCALAPPDATA%\oom\<hash>\` (yalnız indeks önbelleği ve durum) |
| Komut | 11 | 8 |

4–5 bin hedefine bu adımla inilmez; flush dilim tasarımı ve Doctor/Program tekrarları ikinci adımda ~1.500 daha düşürür.

## 7. Uygulama protokolü

1. `1f717dd` üzerinden yeni dal `sade/2026-09-12`, ayrı worktree.
2. §1 ve §4-silinecek `git rm`; §2 budamaları dosya dosya, her biri derlenip test edilerek.
3. `docs/scars.md` DisplayName'lerden yeniden üretilir.
4. Yeşil: build 0 hata, kalan testler geçer, proje kapsamı kurulum izole fikstürde çalışır.
5. Tek commit, push yok. Kabul: Master.
