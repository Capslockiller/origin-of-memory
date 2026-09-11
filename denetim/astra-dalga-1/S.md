# Şerit S — şema değişikliğini gerçek bir `4 → 5` geçişine çevir

## taban_sha

- `021cc83f6acd4e481d42b6d5327981590e05457c` — ayrık HEAD, temiz başlandı
- Çalışma dizini `E:\OdenaWorks\10-Aktif\oom-w-S`; dal değiştirilmedi, başka worktree'ye geçilmedi
- Sahibin canlı kasasına (`E:\OdenaOS v2`) hiçbir komut koşulmadı, `oom doctor` hiçbir kasaya karşı koşulmadı
- Bütün göç veritabanları oturum scratchpad'i altında üretildi (`ScarFixture.TempDirectory()` → `%TEMP%\oom-scar-*`); hiçbir gerçek durum kökü okunmadı veya yazılmadı

## commit

- Şeridin tek commit'i; konu satırı `2.1(FIX):` ile başlıyor
- Push yok, merge yok, dal değişikliği yok

## OWNS_farkı

- `src/Oom/State/State.cs` — değiştirildi (+135 / −34)
- `tests/Oom.Tests/SchemaV5MigrationTests.cs` — yeni (10 test, 11 koşum; Y-184…Y-193)
- `denetim/astra-dalga-1/S.md` — yeni (bu dosya)
- `denetim/astra-dalga-1/S-test-degisiklik-onerisi.patch` — yeni (onay paketi)
- `git status`: OWNS dışında değişen tek dosya yok; mevcut testlerin ve veritabanlarının hiçbirine dokunulmadı

## olgular/kaynaklar

- `State.cs` (021cc83) `SchemaVersion = 4`, merdiven dört basamak; `Provision` `foreach (… in Ladder)` ile **her basamağı her açılışta** koşuyordu — `applied.Add` koşulluydu, yani filtre yalnız raporlamadaydı, uygulamada değil
- `RetrievalIndex` bir merdiven basamağı değildi: `BaseTables`'ın (basamak 1) son satırıydı. Sonuç: sürüm 4 damgalı bir dosya her açılışta `notes`, `notes_fts`, `oom_index_meta`, `ix_notes_updated`, `ix_retrieve_served_scope` ve yedi `retrieve_served` sütunu kazanabiliyordu — sürüm değişmeden, yedeksiz, `Applied` boş raporlanarak
- `Provision`'ın ilk satırı `PRAGMA journal_mode=WAL` idi; `Guard` ondan **sonra** geliyordu. WAL dosya başlığına yazılır: daha yeni sürümlü bir dosya, okunamayacağı ilan edilmeden önce kalıcı olarak değiştiriliyordu
- Yedek koşulu `found < SchemaVersion && path is not null && TableExists(connection, "calls")` idi — ledger satırı olmayan ama oturum/served/dizin taşıyan dosya yedeksiz göç ediyordu
- `RetrievalIndex` içinde koşulsuz bir `DELETE FROM retrieve_served WHERE rowid NOT IN (SELECT MIN(rowid) …)` vardı; kodun kendi yorumu gerekçe olarak "tablo sahada boş — hiçbir şey ona yazmadı" diyordu
- Göç ve `PRAGMA user_version` damgası transaction dışındaydı: bir basamak çakarsa dosya, damgası ile şekli birbirini tutmayan hâlde kalıyordu
- `docs/scars.md` Y-173 satırı, `retrieve_served`'e hiçbir kodun yazmadığını zaten kayda geçirmiş — yani "boş" varsayımı depo içinde tekrarlanan bir varsayım
- Taban 191 test / 0 düşen ve tek uyarı `CS0028` — emirde verildi, şerit S 021cc83'ü değiştirmeden ayrıca koşmadı. Taban test sayısı dolaylı doğrulandı: koşumda 202 toplam − 11 yeni koşum = 191, ve 196 geçen − 11 yeni = 185 eski geçen + 6 eski düşen = 191

## çıkarımlar

- "Hiçbir şey yazmıyordu, dolayısıyla boştur" bir güvenlik kanıtı değil, yalnız kimsenin bakmadığının kanıtı — koşulsuz `DELETE` kaldırıldı; çakışan satır varsa göç açık hatayla durur ve satırlar korunur
- Merdiven basamağı yalnız kendi numarasının **altındaki** dosyaya koşar; getirim dizini artık numaralı basamak 5
- Sürüm kararı `journal_mode` dahil her kalıcı değişiklikten önce verilir; reddedilen dosya bayt bayt geldiği gibi kalır
- İçerik taşıyan her dosya göçten önce `VACUUM INTO` ile kopyalanır ve kopya doğrulanır; koşul artık `calls` tablosuna değil, "bu dosya bir şey tutuyor mu" sorusuna bağlı
- Basamaklar + damga tek `BEGIN IMMEDIATE` … `COMMIT` içinde; hata hâlinde `ROLLBACK`, dosya geldiği sürümde **ve** o sürümün şeklinde kalır
- Yeni bulgu (akıl yürütmeyle değil, çakarak bulundu): basamaklar birbirinin tablosuna yaslanıyordu, çünkü `BaseTables` her açılışta koşuyordu. Basamak 5 artık ihtiyacı olan `retrieve_served`'i (sürüm 1 biçimi, `IF NOT EXISTS`) kendisi kurar
- Başarısız açılış artık bağlantıyı `Dispose` eder: reddedilen dosyanın üzerinde bu süreç bir tutamak tutmaz

## test_komutları/sonuçları

```
cd "E:/OdenaWorks/10-Aktif/oom-w-S" && rm -rf src/Oom/obj/Release tests/Oom.Tests/obj/Release
cd "E:/OdenaWorks/10-Aktif/oom-w-S" && dotnet build Oom.sln -c Release --nologo
cd "E:/OdenaWorks/10-Aktif/oom-w-S" && dotnet test  Oom.sln -c Release --nologo
```

- Soğuk derleme: `0 Hata`, **tam olarak 1 uyarı** — `CS0028`, `src/Oom/Program.cs:42` (OWNS dışı, taban uyarısı). Başka uyarı yok
- Koşum: **202 test · 196 geçti · 6 düştü** (taban 191 + 11 yeni koşum; Y-185 bir `[Theory]`, iki veri satırı)
- Yeni testlerin **11/11'i yeşil**
- Altı kırmızının altısı da beklenen kırmızı, tam listesi aşağıda — hiçbiri gizlenmedi, hiçbiri "geçti" sayılmadı

### Beklenen kırmızılar — tam liste

| test | dosya:satır | assertion | bekliyordu | gördü |
|---|---|---|---|---|
| Y-129 | `tests/Oom.Tests/Scars/DurumDeposuScars.cs:137` | `Assert.Equal(4, report.Version)` | 4 | 5 |
| Y-130 | `tests/Oom.Tests/Scars/DurumDeposuScars.cs:178` | `Assert.Equal(4, provisioned.Scalar("PRAGMA user_version"))` | 4 | 5 |
| Y-170 | `tests/Oom.Tests/Scars/DurumDeposuScars.cs:424` | `Assert.Equal(4, state.Scalar("PRAGMA user_version"))` | 4 | 5 |
| Y-172 | `tests/Oom.Tests/Scars/IndeksScars.cs:324` | `Assert.Equal(4L, Convert.ToInt64(version.ExecuteScalar()))` | 4 | 5 |
| Y-123 | `tests/Oom.Tests/DefterScars.cs:153` | `read.ExecuteReader()` (`SELECT … uncached_in_tok …`) | satır okunabilmesi | `SqliteException: no such table/column: uncached_in_tok` |
| Y-125 | `tests/Oom.Tests/Scars/TestDisipliniScars.cs:142` | `Assert.True(testsWithoutLedgerRow.Length == 0)` | boş liste | `test without a ledger row: Y-184, Y-185, Y-186, Y-187, Y-188, Y-189, Y-190, Y-191, Y-192, Y-193` |

- Y-129 ilk düşen assertion'ında duruyor; arkasında bekleyen üç kırmızı daha var (satır 139 `Applied` listesi, 146 `PRAGMA user_version`, 158 `FoundVersion`). Y-170'in arkasında satır 449 var. Hepsi yamada karşılanıyor
- **Y-123 bir sürüm beklentisi kırmızısı DEĞİL** — fixture'ın kendisi tutarsız (`user_version=3` damgası, sürüm 1 biçimli `calls` tablosu). Gerekçesi ve iki seçenek onay paketinde; ayrıca "onay_bekleyenler"de

### Yamanın ölçülmüş etkisi

- Depo scratchpad'e birebir kopyalandı, yama orada uygulandı, tam paket koşuldu: **202 test · 201 geçti · 1 düştü**
- Düşen tek test `Y-118` (`SurecIsletmeScars.cs:128`) — yamanın etkisi değil: aynı kopyada yama geri alınınca da düşüyor, gerçek çalışma ağacında yamasız geçiyor; kopyalanmış ağaca özgü artefakt
- Yani yama, altı kırmızının altısını da kapatıyor. İddia değil, ölçüm
- Çalışma ağacındaki korumalı dosyalara bu doğrulama sırasında da dokunulmadı

## bozma_kanıtı

Her kanıt: derlenen kasıtlı davranış bozması → beklenen assertion başarısızlığı → düzeltme sonrası yeşil. Bozmalar tek tek uygulandı ve tek tek geri alındı; `State.cs` sonunda bozma öncesiyle **bayt bayt aynı** (md5 `35bb6495…`, `diff` boş).

| # | bozulan davranış | kodda ne yapıldı | düşen test | assertion | bekliyordu → gördü |
|---|---|---|---|---|---|
| 1 | tarihî basamaklar tekrar koşmasın | `found == SchemaVersion` erken dönüşü kaldırıldı, merdiven filtresi `Ladder.Where(…)` → `Ladder` | Y-188 | `SchemaV5MigrationTests.cs:239` — `ix_notes_updated` var mı | 0 → **1** (iki açılış arasında elle düşürülen indeks geri geldi) |
| 2 | sürüm kararı `journal_mode`'dan önce | `PRAGMA journal_mode=WAL` yeniden `ReadVersion`/`Guard`'dan öne alındı | Y-189 | `:278` — reddedilen dosyanın `PRAGMA journal_mode`'u | `"delete"` → **`"wal"`** |
| 3 | sessiz dedupe silmesi olmasın | koşulsuz `DELETE FROM retrieve_served …` geri kondu | Y-190 | `:307` — `Assert.Throws<StateSchemaException>` | istisna → **istisna atılmadı** (göç sessizce başarılı oldu) |
| 3 | " | " | Y-191 | `:343` — `Assert.Throws<StateSchemaException>` | istisna → **istisna atılmadı** |
| 4 | göç + damga tek transaction | `BEGIN IMMEDIATE;` ve `COMMIT;` kaldırıldı | Y-191 | `:348` — çakan göçten sonra `calls.usage_rank` var mı | 0 → **1** (damga 3'te kaldı, şekil 4'e geçti: sürüm ile şekil ayrıştı) |
| 5 | yedek koşulu `calls`'a bağlı olmasın | `HasContent(connection)` → `TableExists(connection, "calls")` | Y-192 | `:400` — `Assert.NotNull(report.BackupPath)` | yol → **null** (ledger satırı olmayan dosya yedeksiz göç etti) |
| 6 | basamak kendi tablosunu kursun | basamak 5'teki `CREATE TABLE IF NOT EXISTS retrieve_served(…)` kaldırıldı | Y-193 | `:431` — göç istisnasız tamamlandı mı | istisnasız → **`SQLite Error 1: 'no such table: retrieve_served'`** |
| 7 | getirim dizini numaralı basamak olsun | merdiven girdisi 5 silindi, `RetrievalIndex(connection)` yeniden `BaseTables`'ın kuyruğuna kondu | Y-184 | `:37` — `Applied.Count` | 5 → **4** |
| 7 | " | " | Y-185 (2) | `:92` — `Applied` tam listesi | `["2→3…","2→4…","2→5…"]` → **`["2→3…","2→4…"]`** |
| 7 | " | " | Y-185 (3) | `:92` — `Applied` tam listesi | `["3→4…","3→5…"]` → **`["3→4…"]`** |
| 7 | " | " | Y-186 | `:142` — `Assert.Single(report.Applied)` | tek eleman → **koleksiyon boş** |
| 7 | " | " | Y-187 | `:186` — `Assert.Single(report.Applied)` | tek eleman → **koleksiyon boş** |
| 7 | " | " | Y-193 | `:436` — `retrieve_served` tablosu var mı | 1 → **0** |

- Bozma 3'te göç **sessizce başarılı** oldu: tohumlanan iki çakışan satır üzerinde `CREATE UNIQUE INDEX` ancak biri silindiyse kurulabilir. Silmenin kendisi ayrıca ölçülmedi — istisnanın kaybolması ölçüldü, silme bu ifadeden çıkarımdır
- Derleme hatası hiçbir yerde kanıt yerine geçmedi; yedi bozmanın yedisi de derlendi ve koştu

### Kabul senaryolarının karşılığı

| senaryo | test |
|---|---|
| yeni DB | Y-184 |
| eski sürüm 2 ve 3 | Y-185 (`[Theory]`, iki koşum) |
| indeks eklenmemiş v4 | Y-186 |
| `021cc83` biçimindeki genişletilmiş v4 | Y-187 |
| gelecekteki sürüm | Y-189 |
| yarıda kesilen göç | Y-191 |
| tekrar açılış | Y-188 |
| çakışan served satırları | Y-190 |
| eski kayıtların **içeriği** | Y-185 (değer bazlı; sayı değil), Y-186, Y-187, Y-191 |
| **yedek** doğrulaması | Y-185, Y-186, Y-187, Y-192 |
| başarılı tekrar açılış yeni göç/yedek üretmesin | Y-188 (`Applied` boş, `BackupPath` null, yedek dizini hâlâ tek dosya) |

## onay_bekleyenler

Hepsi `denetim/astra-dalga-1/S-test-degisiklik-onerisi.patch` içinde; hiçbiri uygulanmadı.

- **Sürüm beklentileri (rutin)** — `4` → `5`, dokuz yerde: `DurumDeposuScars.cs` 137/139/146/158/178/424/449, `IndeksScars.cs` 324, `DefterScars.cs` 164
- **`docs/scars.md`** — Y-172 satırındaki `user_version=4` metni `=5`; Y-184…Y-193 için on defter satırı. Y-125 çift yönlü eşleşme istediği için satırlar olmadan yeşil olamaz. `docs/scars.md` OWNS'ta değil
- **Y-123 fixture'ı (`DefterScars.cs:142`) — karar gerektirir.** Fixture `user_version=3` damgalıyor ama `calls` tablosu sürüm 1 biçiminde. 021cc83'te her basamak her açılışta koştuğu için yalan damga örtülüyordu; damga artık inanıldığı için test çakıyor
  - Seçenek A (yamada olan): tabloyu gerçekten sürüm 3 biçimine getir. Damga 3 kalır, testin adı doğru kalır, dokuz assertion'ın dokuzu geçer (ölçüldü)
  - Seçenek B (reddedildi): damgayı `1` yap. Tek karakter, assertion'lar yine geçer, ama senaryo "sürüm 3" olmaktan çıkar
- **Korunanlar** — `Seed(…, version: 2)`, `FoundVersion` 2 beklentileri, yedeğin `user_version=2` beklentisi, `user_version=99` senaryosu ve Y-123'ün damgası (3) yamada aynen bırakıldı

## açıklar

- **Örtük onarım kapatıldı, bedeli gerçek.** 021cc83'ün "her basamak her açılışta" davranışı aynı zamanda sessiz bir tamir mekanizmasıydı: damgası şeklinden ileri olan bozuk bir dosya kendiliğinden tamamlanıyordu. Emir bunu kapatmayı söylüyor ve kapatıldı; bedeli Y-123'ün fixture'ında birebir görüldü. Onarımın doğru yeri her açılış değil `oom doctor`'dır — doctor'da "damga şekli tutmuyor" denetimi var mı, ölçülmedi (DO-NOT-TOUCH). Ayrı iş olarak açık
- **Sağlıklı açılışta `Views()` de koşmuyor.** Sürümünde olan dosyada `v_calls`/`v_call_usage` yeniden kurulmuyor. Emrin gereği ("tarihî basamaklar her açılışta tekrar çalıştırılmayacak") ama sonucu şu: görünümü elle düşürülmüş bir v5 dosyası kendiliğinden onarılmaz. Test edilmedi, bilinerek bırakıldı
- **Basamak 2–4 hâlâ `calls` tablosunun varlığını varsayar.** Basamak 5 kendi tablosunu kurar hâle getirildi (Y-193), basamak 2/3/4 getirilmedi — `calls`'ı olmayan bir sürüm 1–3 dosyası `no such table: calls` ile çakar. Minimal fark tutmak için dokunulmadı; 021cc83'te de aynı davranış vardı (orada `BaseTables` her açılışta koştuğu için maskeliydi). Açık
- **Çakışma tespiti NULL'ları dışarıda bırakır.** `ServedScopeConflicts` yalnız mesajdaki sayı içindir ve `session_id`/`query_sig`/`note` NULL olan satırları saymaz, çünkü benzersiz indeks iki NULL'ı farklı sayar. Kararı veren sorgu değil, `CREATE UNIQUE INDEX`'in kendisidir
- **Y-118 kopya ağaçta düşüyor.** Yamayı doğrulamak için alınan scratchpad kopyasında `SurecIsletmeScars.Y118` yamalı ve yamasız hâlde düşüyor, gerçek çalışma ağacında geçiyor. Kopyalanmış ağaca özgü (dizin oluşturma zamanına bakan bir yol); bu şeridin değişikliğiyle ilgisi ölçülerek elendi, ama kendi başına bir kırılganlık işareti. Açık
- **Bozma 3'te satırın silindiği doğrudan ölçülmedi.** İstisnanın kaybolduğu ölçüldü; silme, çakışan satırlar üzerinde benzersiz indeksin kurulabilmiş olmasından çıkarılan sonuçtur
- **İkinci yedek dosya adı milisaniye damgalıdır.** `state.db.v{n}-yyyyMMdd-HHmmssfff.bak`; aynı milisaniyede iki göç denemesi `VACUUM INTO` hedefinin var olması yüzünden çakardı. Y-191 iki yedek üretiyor ve geçiyor, ama bu bir zamanlama varsayımıdır
