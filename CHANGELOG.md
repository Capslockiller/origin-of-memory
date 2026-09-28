# Changelog

## Unreleased

Henüz 3.1.0'dan sonra yayınlanmamış bir değişiklik yok.

## 3.1.0 — 2026-09-28

Tez: yeni kullanıcı özelliği yok. Günlük kullanılan çekirdek (kanca→bağlam→tarama→derleme→arama→doctor) küçültüldü ve her parçası ölçülebilir bir kontrolle kanıtlandı; kanıtlanamayan kesildi.

### Eklendi
- `oom --version` artık `3.1.0+<commit>` basar (git etiketi yoksa `+unknown`); MCP `initialize` yanıtı aynı dizgiyi taşır.
- Bağlam enjeksiyonu tek bütçeye alındı: dokuz bölüm, bu sırayla — `[Bildirim]`, `[Zaman]`, `[Hafıza — Son Oturum]`, `[Hafıza — Aktif Threadler]`, `[Hafıza — Kurallar]`, `[Hafıza — Düzeltmeler]`, `[Hafıza — Son Journal]`, `[Bilgi Tabanı — Arama]` (eski İndeks bölümünün yerine, çalışan `oom.exe`'nin tam yoluyla hazır bir `retrieve --query` satırı) ve `[Bugünün Logu]` —, toplam ≤ 8.000 karakter / ≤ 8.500 bayt; Kurallar ve Düzeltmeler hiç kesilmez, `context.capChars` bu tavanın üstüne çıkamaz.
- Arama korpusu genişledi: `knowledge/concepts` ve `Duzeltmeler.md`'nin yanına `daily/` blokları ve el katmanı (`Last-Session.md`, `Threads.md`, `Journal.md`) eklendi; düzeltme isabeti ×4 çarpan yerine düz BM25 + sabit 0,25 ek puan alır. `Kurallar.md` indekslenmez.
- Guards, `flush` ve `compile`'ın modelden aldığı her çıktıyı talimat enjeksiyonuna karşı denetler; yakalanan metin `daily/`'ye ya da bir kavram notuna hiç yazılmaz, sırları maskelenmiş olarak karantinaya alınır: `flush` çıktısı `%LOCALAPPDATA%\oom\<vault-hash>\red\` altına (yanında `.reason` dosyasıyla), `compile` çıktısı `<vault>\.oom\quarantine\` altına. `oom save` bu kapıdan geçmez, metni olduğu gibi yazar. Redactor bilinen sır biçimlerini (`ghp_`/`sk-ant-`/`AKIA`, URL içi `user:pass@`, anahtar/şifre yakını jetonlar) flush, compile, retrieve ve MCP çıktısında maskeler.
- `doctor` artık son 7 günün gerçek penceresini, sayısal kapsamayı yaşıyla, 'son tarama'/'son derleme' satırlarını basar; kırmızı bir satır varsa exit 1 (`--json`'daki `exit_code` alanı kabuk çıkış koduyla aynıdır). Kapsama %95'in altındaysa (pencerede en az 5 oturum varken) satır sarı değil kırmızıdır — elle koşulan taramada bu, dürüst bir sinyaldir.
- Kabul (acceptance) süiti: enjekte edilebilir durum kökü (`OOM_LOCALAPPDATA`), sabit saat (`OOM_FAKE_NOW`), profil kökü (`OOM_USERPROFILE`: `doctor`'ın kanca denetimi ve `kit`), tarama dışlama kökü (`OOM_TEST_ROOT`, varsayılan `%TEMP%`), süreç sınırında gerçek `oom.exe`'yi süren `KabulHarness`, ve eski koda karşı en az 8 testin kırmızı olduğunu kanıtlayan `tests/REGRESYON-KANITI.md`.

### Değişti
- `oom sweep` artık hiçbir saatte derleme başlatmaz; derleme yalnız `oom compile` ile, bekleyeni her koşumda katlar. `compile --dry-run` gerçek son-derleme zamanını `daily_ingest`'ten basar; eski "karar" (evening/interval) satırı kalktı.
- `doctor`'ın varsayılan görünümü uyarı/hata satırlarına ek olarak altı sabit başlık satırını (`coverage`/`rejection-rate`/`refused`/`last-sweep`/`last-compile`/`pending` — yeşil olsalar bile) ve "`N` yeşil" özetini gösterir; diğer her yeşil satır yalnız `--all` ile görünür (`kit` satırları dahil).
- Vault yolu artık diskteki gerçek harf büyüklüğüne çözülür (`E:\OdenaOS`, `e:\odenaos`, `E:/OdenaOS/` aynı durum dizinini paylaşır); bilinmeyen bir `oom.json` anahtarı sessizce kabul edilmez, stderr'e `bilinmeyen ayar: <anahtar>` yazar.
- `save` serbest metni kabul eder; zorunlu üç alan kuralı kalktı.
- `sweep.roots`'taki bir kök, kaynağı Codex olarak sınıflandırmak için yolunda tam olarak `.codex` adlı bir segment taşımalı (büyük/küçük harf duyarsız); yoksa döküm `claude` kaynaklı sayılır.

### Kaldırıldı
- `doctor`'ın bu sürüm içinde eklenip sonra tamamen kaldırılan `arac` bileşeni (codebase-memory-mcp/Agent Reach sağlık satırları) — OoM artık yalnız kendi üzerine raporlar, başka hiçbir programı çalıştırmaz.
- `retrieve --session` (no-op'tu); `oom.json`'daki `extensions[].contextLine` artık hiç çalıştırılmaz, yalnız yok sayıldığını bildirir.
- Ölü kod ve onu "kontrol var" gösteren testler (bkz. SPEC-3.1.0.md Cut bölümü); `sweep.everyHours` ve kök düzeyi `retrieveMode` anahtarları artık ölü, ayarlanırsa uyarır.

### Eklendi (kit)
- `oom kit status` and `oom kit install` read a kit directory's `manifest.json` (given with `--kit <dizin>`) and compare a skill, agent or rule against its installed copy by SHA-256 (`kuru`/`güncel`/`farklı`/`bozuk`); a malformed or unreadable manifest is reported as a clear error on the command line. This repository ships the kit engine only; kit content is not distributed here.

### Düzeltildi (kit)
- A link (junction/symlink) anywhere in a component's source subtree, in the destination subtree, or in any ancestor between the home root and the destination — including `.claude`, `.claude/skills`, `.agents/skills`, and the backup root `.claude/.oom-kit-yedek` — is walked without being followed and makes the row `bozuk`, naming the link; install refuses such rows. Two manifest entries resolving to the same destination are both `bozuk: hedef çakışması` and nothing is written for either; a file destination is copied with no-overwrite semantics and a skill (directory) destination is staged in a temp sibling and moved into place only if the destination still doesn't exist, with every row re-checked after install. A `null` entry in the manifest's `components` array, or a component `path` that makes `Path.GetFullPath` throw (an embedded NUL, for example), is reported as a manifest-level error or a single `bozuk` row instead of an unhandled `NullReferenceException`/`ArgumentException` — every other component's row is unaffected.

### Belgeler
- README/SECURITY/CONTRIBUTING ölçülen davranışla senkronlandı; komut tablosu artık `oom --help` çıktısının birebir kopyası. Her davranış cümlesi `docs/iddialar.md`'de bir teste ya da komuta bağlanır (bkz. `tests/Oom.Tests/Kabul/DocsKabul.cs`).

### Tarihsel not — geri alınan iş
- `oom doctor` (ve `doctor --json`) bu sürüm içinde kısa süreliğine bir `arac` bileşeni taşıdı: codebase-memory-mcp ve Agent Reach sağlığını raporluyordu (araç yoksa `kurulu değil`, codebase-memory-mcp için proje başına satır, Agent Reach için sürüm — her biri 15 saniyelik zaman aşımı arkasında, `doctor`'ı hiç düşürmeden), ve bir düzeltme turu `nodes`/`edges` gibi bozuk bir değerin diğer aracın satırlarını da götürmesini önlemişti. Yukarıdaki "Kaldırıldı" bölümünün söylediği gibi, `arac` aynı 3.1.0 döngüsü içinde tamamen kaldırıldı — bu not yalnız geçmiş kaydı içindir, `arac` kod tabanında artık yoktur ve `--all` dahil hiçbir `doctor` görünümünde beklenmemelidir.

## 3.0.4 — 2026-09-13

### Fixed
- Doctor validates user and vault hooks, checks the search index against the corpus, and counts quarantine Markdown files. Install prints hook errors to stderr.
- Flush and save share a cross-process daily file lock, retrying every 100 ms for up to ten seconds. Install and uninstall preserve timestamped settings backups and replace the file through a temporary write.
- Flush records measured turns and summary length; unknown retry measurements remain NULL.
- The README states that summaries are plain text and session secrets can reach daily logs. The 3.0.0 source-size claim now names its measured scope.

### Removed
- The unused quarantine table declaration, unreachable masking notifications and the unimplemented notifier interface.
- The constant retrieval metric claim and its two word-matching tests.
- Uncalled audit helpers and their tests. WriteDailyIngest remains in the compile path; IndexHealth now runs in doctor.

## 3.0.3 — 2026-09-13

### Added
- A clock on every prompt: `nudge` prints the time, when the session started, the gap since the last message and the prompt count; `context` prints when the last session ended.
- Daily notes start with frontmatter (`type: daily`, `date`, `source`). Concept notes carry `type: concept` and `hub`; `doctor --fix` migrates existing concepts and appends the hub link. Hub files carry `type: hub`.
- The compile prompt lists the hub ids and a tag vocabulary (hub-config tags plus the forty most frequent); more than one tag outside it is a health warning.
- `doctor` reports orphan concepts, concepts without a hub, and notes outside the schema.
- Compile keeps the reason a daily was rejected; a failed `claude` call records its stdout and stderr.
- Flush reads the compact summary Claude Code writes when a conversation is compacted and puts it before the raw-turn slice, under its own character budget. A session that holds only a compact summary still flushes.

### Fixed
- Codex rollouts in the current `response_item` format parse; developer and injected blocks are skipped. Until now every Codex session was unreadable to sweep.
- The hub configuration lives at `.oom/hub-config.json`; without it every concept fell into the catch-all hub.
- The nudge reminder is English. `sessions` gained `last_prompt_ts`; an older state database is set aside and rebuilt.
- Sweep and flush read Codex rollouts through the Codex parser; stamps marked unreadable by the old parser are retried.
- Hub membership matches whole tags and the note's name and title. A short tag such as `vr` no longer matches inside a word such as `kavram`, which had put every concept into one hub.

## 3.0.2 — 2026-09-12

### Changed
- The runner calls `claude` with the user's own login. The separate `.oom/claude-config` directory is gone; it needed its own `/login` after every fresh install and, without one, every summary failed with exit code 1 (Y-011).

## 3.0.1 — 2026-09-12

### Fixed
- Hook commands are written with forward slashes and carry `--vault <path>`. Claude Code runs hooks through bash on machines where bash is the shell; a backslash path was swallowed there and none of the four hooks ever ran (Y-309). Re-run `oom --vault <vault> install` to rewrite them.
- `--help` lists what each command does.

## 3.0.0 — 2026-09-12

A smaller program with no history attached. Everything the owner had not asked for was removed, then every line of prose in the repository was deleted and rewritten from zero.

### Removed
- Commands `bench` and `ingest`; the machine-scope installer, the state migration ladder, quota tracking, the Ollama backend, secret masking, notifications and the `calls` ledger.
- Single-file, self-contained and trimmed publish. `dotnet build` output is the deliverable.
- All previous documentation, release notes, audit reports, scar ledger and code comments. Old GitHub releases and tags remain as they were.
- Test Y-125, which checked the deleted scar ledger.

### Changed
- src+tests 7,669 non-blank lines; tests 261 → 105; state database 16 → 9 tables in one `CREATE` script, no `user_version`.
- A state database with an older schema is set aside as `state.db.eski-<timestamp>` and rebuilt; it is never migrated.
- `doctor` reads coverage from the sweep's own health row and says it is unmeasured before the first sweep.
- `install` writes only the project: `.oom/vault.json`, `.oom/oom.json` and four hooks in `.claude/settings.json`.
- Every model call leaves through one egress gate; `retrieve` no longer runs at session start, only on request or over MCP.
- Target framework net9.0.
- License: PolyForm Noncommercial 1.0.0.

### Added
- `nudge` — the UserPromptSubmit hook counts prompts and, every `nudgeEvery` prompts, asks the session to record what it learned. `reflectionMinPrompts` sets the minimum before a reflection is expected.
- `flush.mode` — `dilim` summarises only the last `flush.sliceTurns` turns of a session and skips sub-agent transcripts (`subagents/`, `agent-*.jsonl`); `tam` summarises every turn. Default `dilim`, 30 turns.
- Tests Y-300 to Y-308 over the behaviour above.
