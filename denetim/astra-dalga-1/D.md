# Şerit D — Doctor'ın boş kök sınıflandırmasını tamamla

## taban_sha

- `021cc83f6acd4e481d42b6d5327981590e05457c` — ayrık HEAD, temiz başlandı
- Çalışma dizini `E:\OdenaWorks\10-Aktif\oom-w-D`; dal değiştirilmedi, başka worktree'ye geçilmedi
- Sahibin canlı kasasına (`E:\OdenaOS v2`) hiçbir komut koşulmadı; `oom doctor` hiçbir kasaya karşı koşulmadı
- Hiçbir gerçek durum kökü okunmadı veya yazılmadı. Bütün fikstür veritabanları `ScarFixture.TempDirectory()` altında (`%TEMP%\oom-scar-*`) üretildi ve `Doctor` yalnız enjekte edilmiş `localAppData` köküyle çağrıldı

## commit

- Şeridin tek commit'i; konu satırı `2.1(FIX):` ile başlıyor
- Push yok, merge yok, dal değişikliği yok
- Hiçbir temizlik işlemi yapılmadı: silme, taşıma, arşivleme yok

## OWNS_farkı

- `src/Oom/Doctor/Doctor.cs` — değiştirildi (+128 / −38)
- `tests/Oom.Tests/DoctorStateContentTests.cs` — yeni (5 test, Y-210…Y-214)
- `denetim/astra-dalga-1/D.md` — yeni (bu dosya)
- `git status`: OWNS dışında değişen tek dosya yok. `docs/scars.md`'ye dokunulmadı; mevcut testlerin hiçbiri değiştirilmedi

## olgular/kaynaklar

- `Doctor.InspectOneStateRoot` (021cc83, `Doctor.cs:241`) satır toplamını sabit bir listeden alıyordu: `calls`, `flush_log`, `health`, `sessions`, `coverage`, `compile_runs` — altı tablo
- `StateStore.BaseTables` + `RetrievalIndex` (`src/Oom/State/State.cs:741-825`) **yirmi** tablo yaratıyor: yukarıdaki altısı, artı `retry_queue`, `sweep_stamps`, `ingest_done`, `daily_ingest`, `vault_meta`, `quarantine`, `notified`, `retrieve_served`, `locks`, `kota`, `notes`, `oom_index_meta`, `notes_fts`
- Yani sayımın dışında kalan **on dört** tablo vardı. Bir kök yalnız `daily_ingest`/`retrieve_served`/`quarantine`/`retry_queue`/`ingest_done` içinde kayıt taşıyorsa toplam 0 çıkıyor, künyesi de yoksa `artık` sınıfına düşüyor ve rapor sahibine harfiyen "hiç satır yok" diyordu — silme daveti
- `notes_fts`, varsayılan seçeneklerle bir FTS5 sanal tablosu (`State.cs:743`); SQLite ona `notes_fts_data`, `_idx`, `_content`, `_docsize`, `_config` gölge tablolarını kendisi açıyor ve bunlar `sqlite_master`'da `type='table'` olarak görünüyor. **Ölçüldü:** hiç yazılmamış bir `state.db`'de bu gölgeler 3 satır taşıyor (BOZMA-2 kanıtı). Yani "bütün tabloları say" saf düzeltmesi her sağlanmış kökü dolu gösterirdi ve `artık` sınıfı düzelmek yerine yok olurdu
- DDL hiçbir satır tohumlamıyor (`State.cs` içindeki her `INSERT` bir çalışma zamanı API'sinin gövdesinde); tek istisna kurulumun `WriteVaultStamp` çağrısı, o da DDL'in parçası değil
- Ana döngünün ölçümü: bu makinedeki 65 okunabilir kökün **sıfırı** bugünkü listede yanlış sınıflanıyor. Kusur gerçek, bugünkü zararı yok — bu şerit gelecekteki bir yanlış silmeyi kapatıyor, bugünkü bir raporu değil

## çıkarımlar

- Sayım artık `sqlite_master`'dan okunuyor, elde tutulan bir listeden değil: **gelecekte eklenen bir tablo bu taramanın düzenlenmesini beklemeden içerik sayılır**. Y-210'un altıncı fikstürü tam da bu build'in hiç duymadığı bir tabloyu (`oom_gelecek_defter`) kullanıyor
- Üç şey sayımdan çıkarıldı: `sqlite_`-önekli SQLite iç tabloları, FTS5 gölge tabloları (`_data`/`_idx`/`_content`/`_docsize`/`_config`/`_row`), ve sanal tabloların kendileri
- FTS5 sanal tablosuna **yalnız bütün sıradan tablolar sıfır dediyse** soruluyor. Gerekçe: indeks normalde indekslediği tablonun kopyasıdır, sıfır olmayan bir toplama eklemek çift sayım olurdu; ama indekslediği tablolar boşken indeks hâlâ satır bildiriyorsa o satırların yaşadığı tek yer odur
- Yeni bir hüküm eklendi: **`belirsiz`**. `artık`, bir kökün hiçbir şey taşımadığı iddiasıdır ve tek silme davetidir; bu yüzden ancak boşluk gerçekten kanıtlandığında veriliyor. Kanıt düşerse (tanınmayan bir sanal tablo modülü, ya da `COUNT(*)`'ı hata veren bir tablo) hüküm `belirsiz` olur ve `state-root-indeterminate` kodu "silme adayı değildir" diye yazar
- Sınıflandırma sırası: `okunamıyor` → `kullanımda` → `eşleşmiyor` → `sahipsiz` (satır var) → `artık` (boşluk kanıtlandı) → `belirsiz`. Künyesi eşleşen bir kök için içerik belirsizliği önemsiz, çünkü zaten kullanımda
- `okunamıyor` yolu korundu ve genişletildi: `integrity_check` önce koşuyor, sonuç `ok` değilse hiçbir `COUNT(*)`'a gidilmiyor — bozuk dosyada `COUNT(*)` bir sayı döndürür ve o sayı yalan söyler
- Özet satırına `belirsiz` kovası eklendi; `artık` iletisi artık "veritabanındaki hiçbir kullanıcı tablosunda satır yok" diyor — eskiden söylediği "hiç satır yok" doğru değildi

## test_komutları/sonuçları

```
cd "E:/OdenaWorks/10-Aktif/oom-w-D" && rm -rf src/Oom/obj/Release tests/Oom.Tests/obj/Release
cd "E:/OdenaWorks/10-Aktif/oom-w-D" && dotnet build Oom.sln -c Release --nologo 2>&1 | tail -8
cd "E:/OdenaWorks/10-Aktif/oom-w-D" && dotnet test  Oom.sln -c Release --nologo 2>&1 | tail -6
```

- **Taban (değişiklikten önce, soğuk):** `Başarısız: 0, Başarılı: 191, Toplam: 191` · `1 Uyarı, 0 Hata`
- **Sonuç (soğuk):** `Başarısız: 1, Başarılı: 195, Toplam: 196` · `1 Uyarı, 0 Hata`
- Soğuk derlemedeki tek uyarı taban ile aynı: `CS0028`, `src/Oom/Program.cs(42,24)` — OWNS dışı, dokunulmadı. Yeni bir uyarı üretilmedi

### Beklenen kırmızılar — tam liste

- `Y-125 · Test gövdesindeki her Y-numarası scars.md'de bir satıra karşılık gelir…` → `test without a ledger row: Y-210, Y-211, Y-212, Y-213, Y-214`
- Emirde önceden bildirilen tek kırmızı budur. `docs/scars.md` şeridin OWNS'unda değil; defter satırlarını merkez atınca kendiliğinden yeşile döner
- Bunun dışında **hiçbir mevcut test kızarmadı**; hiçbir mevcut test yeşile çevirmek için değiştirilmedi

### Yeni testler (hepsi yeşil)

| # | Ne ölçüyor |
|---|---|
| Y-210 | Kısayol listesinin göremediği her tablo — `daily_ingest`, `retry_queue`, `ingest_done`, `quarantine`, `retrieve_served` ve bu build'in hiç duymadığı bir tablo — kök başına bir tane; hiçbiri `artık` sayılmıyor, hiçbiri `stray-state-root` üretmiyor |
| Y-211 | FTS5 gölge tabloları ve SQLite iç tabloları sayacı şişirmiyor: hiç yazılmamış bir `state.db` 0 satır bildiriyor ve dürüstçe `artık` kalıyor |
| Y-212 | Tanınmayan bir yapı (FTS4 indeksi) taşıyan kök `belirsiz`; `artık` hükmü verilmiyor |
| Y-213 | Bozuk veritabanı iki ayrı kapıdan da `okunamıyor`: `integrity_check`'in `ok` **döndürmediği** hâl ve okumanın **fırlattığı** hâl |
| Y-214 | Tarama hiçbir kök, veritabanı veya şema oluşturmuyor; mevcut hiçbir dosya bir bayt oynamıyor |

## bozma_kanıtı

Her yeni test için: derlenen kasıtlı bir davranış bozması → beklenen assertion başarısızlığı → geri alma sonrası yeşil. Beşi de derlendi; hiçbiri derleme hatası değil.

**BOZMA-1 — Y-210.** Sayım döngüsüne özgün kusur geri kondu (`.Where(name => name is "calls" or "flush_log" or "health" or "sessions" or "coverage" or "compile_runs")`).

> `'daily_ingest' tablosunda kayıt var ama sayım 0 satır buldu: …\oom\0000000000000001`

**BOZMA-2 — Y-211.** Gölge tablo elemesi kapatıldı (`user.Where(name => false && IsShadowOf(name, fts))`).

> `hiç yazılmamış bir state.db 3 satır bildirdi; gölge tablolar sayaca sızıyor`

Bu aynı zamanda olgular bölümündeki "gölgeler 3 satır taşıyor" ölçümünün kaynağıdır.

**BOZMA-3 — Y-212.** Kanıt bayrağı sabitlendi (`var proven = true;`).

> `tanınmayan bir yapı taşıyan kök 'artık' diye sınıflandı, 'belirsiz' olmalıydı`

**BOZMA-4 — Y-213.** `integrity_check` başarısızlığı yutuldu (`return new StateRootContent(0, true, true);`).

> `bozuk veritabanı taşıyan kök 'artık' diye sınıflandı, 'okunamıyor' olmalıydı: …\oom\0000000000000001`

Bu bozmanın **ilk turu testi düşürmedi.** Y-213'ün ilk fikstürü dosyayı 2048. bayttan itibaren eziyordu; o şema sayfasını yok ettiği için okuma `integrity_check`'e varmadan *fırlatıyor* ve hüküm dıştaki `catch`'ten geliyordu — yani test, iddia ettiği dalı hiç ölçmüyordu. Fikstür ölçülerek düzeltildi (500 satır `flush_log`, sonra **son veri sayfası** eziliyor; `integrity_check` o zaman fırlatmadan `*** in database main *** Tree 4 page 48: btreeInitPage() returns error code 11` döndürüyor) ve ikinci turda bozma yukarıdaki iletiyle düştü. Şimdi test iki kapıyı da tutuyor.

**BOZMA-5 — Y-214.** Tarama yazar hâle getirildi (`File.Exists` kapısı kaldırıldı + `Mode=ReadOnly` düşürüldü).

> `tarama dosya oluşturdu: …\oom\0000000000000002\state.db, …\oom\0000000000000003\state.db`

### Kabul senaryolarının karşılığı

| Kabul | Nerede |
|---|---|
| `retry_queue`/`ingest_done`/`daily_ingest`/`quarantine`/`retrieve_served` veya bilinmeyen tabloda kayıt → `artık` değil | Y-210 (altı kök, altı tablo) |
| SQLite iç tabloları ve FTS gölgeleri sayacı şişirmesin | Y-211 + BOZMA-2 |
| Boşluk kanıtlanamıyorsa kesin boş hükmü yok | Y-212 + BOZMA-3 (`belirsiz`) |
| Bozuk DB `okunamıyor` kalsın | Y-213 (iki yol) + BOZMA-4 |
| Tarama dosya veya şema oluşturmasın | Y-214 + BOZMA-5 — bir istisnayla, açıklar bölümüne bak |

## onay_bekleyenler

- **`docs/scars.md` defter satırları.** Y-210…Y-214 için beş satır gerekiyor; şerit deftere dokunmadı, merkez atacak. Satırlar girilene kadar Y-125 kırmızı kalır. Önerilen sınıf: `durum-deposu`. Test sütunu: `DoctorStateContentTests.Y210_*` … `Y214_*`
- **`belirsiz` hükmünün CLI'da nasıl görüneceği.** `state-root-indeterminate` bir `Warning`; `oom doctor` metninde `artık`'tan ayrı bir satır olarak çıkıyor ama sahibine hangi eylemi önerdiğine dair bir metin yazılmadı ("elle bak" mı, "dokunma" mı). Bu bir ürün kararı
- **Sahipsiz köklerin sayısı artacak.** Düzeltmeden sonra daha çok kök `sahipsiz`/`belirsiz` tarafına geçecek (eskiden `artık` görünüyorlardı). Bu doğru davranış ama rapor uzayacak; sahibin listeyi yeniden okuması gerekir

## açıklar

- **WAL yan dosyaları (önceden var olan, bu şeritte kapatılmadı).** WAL kipindeki bir veritabanına salt-okunur bağlanmak `state.db-wal` ve `state.db-shm` dosyalarını yaratıyor; bağlantının yazma izni olmadığı için kapanırken bunları **kaldıramıyor**. 60+ köklük bir sayım geride 120 dosya bırakabilir. Ölçüldü: Y-214'ün ilk hâli tam olarak bunun üzerine düştü (`tarama dosya oluşturdu: …\state.db-shm, …\state.db-wal`). **Bu davranış 021cc83'te de vardı** — bağlantı dizesi (`Data Source={databasePath};Mode=ReadOnly`) bu şeritte bayt bayt aynı kaldı, yalnız başka bir metoda taşındı. Y-214 bu iki adı tek belgelenmiş istisna olarak sabitliyor, böylece istisna sessizce büyüyemez. Kapatmanın bilinen yolu URI `immutable=1`; canlı bir kökte bayat okuma riski taşıdığı için bu şeritte **denenmedi** — ayrı bir karar
- **Sanal tablo gölgeleri yalnız FTS5 için tanınıyor.** Bir FTS4 veya R-Tree indeksinin gölgeleri (`x_segdir`, `konum_node`, …) sıradan kullanıcı tablosu olarak sayılır. Ölçüldü: boş bir R-Tree'nin `konum_node` gölgesi daha yaratılırken 1 satır taşıyor, dolayısıyla böyle bir kök `belirsiz` değil `sahipsiz` çıkar. Yanılma yönü güvenli (boş olmayan diye bildirmek), bu yüzden düzeltilmedi; ayrıca bu modüller bu üründe hiç kullanılmıyor
- **`Rows` bir muhasebe rakamı değil.** Kullanıcı tablolarının toplamı; `vault_meta`, `locks`, `notified` gibi defter tutma tabloları da içinde. Kurulumun `installed_at` damgası tek başına bir kökü "1 satır" yapar. Amaç boş/dolu ayrımı olduğu için bu kabul edildi, ama rapordaki sayı "şu kadar hatıra" diye okunmamalı
- **`Doctor` hâlâ `State` şemasını bilmiyor ve bilmemeli.** Bu düzeltme şemayı `sqlite_master`'dan okuduğu için iki taraf birbirinden bağımsız kaldı; bunun bedeli, `notes` ile `notes_fts` arasındaki çift sayımın ancak "sıradan tablolar sıfırsa sor" kuralıyla önlenmesi. Şema sahibi tarafta bir gün "hangi tablolar kullanıcı verisidir" diye bir liste yayımlanırsa bu kural oraya taşınmalı
