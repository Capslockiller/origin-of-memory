# INT-2 · Dalga 2 tabanı ve girdileri

Yazan: ana döngü (Claude Code, Opus ana döngü) · 2026-09-11 · Astra emri INT-2

## Taban

| | |
|---|---|
| Plan tabanı | `39cb9e7b16b9bf56c0f9dd1afec9d088224e692e` |
| Ürün davranışı tabanı | `529722e` |
| Çalışma ağacı | temiz — `git status --porcelain` sıfır satır |
| .NET SDK | `9.0.312` |
| Test | 222/222 (INT-1 kaydı, `denetim/astra-dalga-1/kabul.md`) |

Şeritler ayrı worktree'lerde, şerit başına tek commit. Mevcut ağaçlar
sıfırlanmayacak, taşınmayacak. `oom-w-INDEKS`'te önceki dalgadan iki commit
edilmemiş dosya duruyor — dokunulmadı.

## Şerit yazma kapsamları ve yara aralıkları — apex tablosundan

| Şerit | Kesin yazma kapsamı | Yara aralığı |
|---|---|---|
| S2 | `src/Oom/State/State.cs` · State altında yeni şema denetimi dosyası · yeni şema bütünlüğü test dosyası · `denetim/astra-dalga-2/S2.md` | Y-230–249 |
| R2 | `bench/verify_recall.py` · yeni kanıt bağlama test dosyası · `bench/results/astra-dalga-2/` · `denetim/astra-dalga-2/R2.md` · revize Y-042 yama önerisi | Y-250–269 |
| D2 | `src/Oom/Doctor/Doctor.cs` · yeni doctor teşhis test dosyası · `denetim/astra-dalga-2/D2.md` | Y-270–289 |
| INT | `src/Oom/Program.cs` · CI · `docs/scars.md` · kabul kayıtları · kabul edilen mevcut-test yamaları | Y-290–299 |

Aralık yetmezse merkezden yenisi alınır; şerit kendi aralığını genişletmez.
Ortak dosya ihtiyacı gerekçeli yama önerisiyle merkeze döner.

## Gold ve korpus — **girdi bekliyor DEĞİL, mevcut**

R şeridi "gold worktree'de yok" demişti; kendi worktree'si için doğruydu.
Makinede ölçüldü, beş kopyası var ve **hepsi aynı hash**.

| | |
|---|---|
| Yetkili kopya | `E:\OdenaOS v2\🏰 300-Projects\Origin-of-Memory\Degerlendirme\gold-sorular.jsonl` |
| Boyut · tarih | 46.120 bayt · 27 Ağustos |
| sha256 | `d140ba3a624d574d…` |
| Soru sayısı | **130** |
| Sınıf dağılımı | `tek-not` 99 · `cok-not` 26 · `kanarya` 5 |
| Alanlar | `soru` `gold` `sinif` `kaynak` `lane` `gerekce` `tarih` |

99 + 26 = **125 puanlanan soru** + 5 kanarya. Apex'in eşik tabanları bununla
birebir karşılanıyor: puanlanan ≥125 ✓ · `tek-not` n≥90 (99) ✓ ·
`cok-not` n≥20 (26) ✓.

**Sorumlu:** gold seti sahibinindir ve kasada durur; ana döngü onu üretmez,
değiştirmez. Ölçüm için **kopyası** alınır ve hash'lenir.

## Ölçülecek getirme girdileri — envanter

| Girdi | Kapsam | Ölçüldü |
|---|---|---|
| `knowledge/concepts/*.md` | **554 not** | evet |
| `Duzeltmeler.md` | `E:\OdenaOS v2\🔮 850-Companion\Duzeltmeler.md` · 1.297 bayt · 1 başlık | evet |

Apex'in tespiti doğrulandı: `Retrieve.cs:637` düzeltmeler dosyasını okuyor ve
`:654` her başlığı `Duzeltmeler.md#<n>` kimliğiyle ayrı bir doküman yapıyor.
Yalnız `knowledge/concepts` kopyalamak korpusu eksik bırakır.

## Canlı dosyaya dokunmadan kullanılabilir kopyalar

| Kopya | Boyut | Zaman | Not |
|---|---|---|---|
| `.oom-kurulum\state-bozuk-2026-09-11\state.db.bozuk` | 5.918.720 | 11:34 | donmuş, sha256 `17f53985…` |
| `.oom-kurulum\state-bozuk-2026-09-11\state.db.bozuk-1142` | 5.918.720 | 12:15 alındı, mtime 11:42 | donmuş, sha256 `0c5049e8…` |
| scratchpad `onarim/nihai.db` | 5.914.624 | — | **oturumluk**, kalıcı değil; bayatladı |

**Canlı dosya:** `state.db` 5.918.720 bayt, mtime **12:30:01** — iki donmuş
kopyadan da yeni. Yanında `state.db-shm` (32.768) ve `state.db-wal` (0 bayt).

⚠ Apex'in şartı: "WAL varsa yalnız ana DB dosyasını kopyalamak yeterli kabul
edilmeyecek." Elimizdeki iki donmuş kopya **yalnız ana dosyadır**; WAL/SHM
dondurulmadı. REC-1 için tutarlılığı doğrulanmış yeni bir kopya gerekir ve
canlı dosya hâlâ yazılıyor.

Ürün yedek dizini `%LOCALAPPDATA%\oom\c0d022686bf934b0\backup\` yalnız
`settings.json.bak-20260910-195802` tutuyor — durum veritabanının ürün yedeği yok.

## Çıktı provenance sözleşmesi

Bu dalgada üretilen her kanıt dosyası şunları taşır:

| Alan | Değer |
|---|---|
| `yazan` | üreten ajanın adı ve **gerçek model kimliği** — ana döngü için `claude-opus-5`, şeritler için kendi model id'si |
| `komut` | koşulan tam komut satırı |
| `raw_artifact` | ham çıktının depo-göreli yolu |
| `source_commit` | ölçülen ağacın tam SHA'sı |
| `exe_sha256` | ölçülen yayımlanmış ikilinin hash'i |

`bench/PROVENANCE.md` sözleşmesi geçerlidir; ikinci bir sözleşme yazılmaz.

## Apex'in Odena'ya notu

> "bu planın kararları ve kaynakları Codex oturum kaydına alınmalı; canlı
> dosyanın kusuru henüz bağımsız teşhis edilmiş sayılmamalı."

Plan salt-okunur oturumdan geldiği için Astra dosyaya yazamadı. Plan metni
`denetim/astra-dalga-2/plan.md` olarak kayda geçirildi.
