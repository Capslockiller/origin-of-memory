# KÜME · Apex hükmüne göre altı yaranın beklentilerinin yeniden yazılması

| | |
|---|---|
| `taban_sha` | `0bf3623` (ayrık HEAD, temiz) |
| `commit` | `2.1(TEST): rewrite six scar expectations onto the apex ruling` |
| yazan | KÜME şeridi · `claude-opus-5` |
| yara kümesi | `Y-129`, `Y-192` (iki koşum), `Y-195`, `Y-199`, `Y-200` |
| `git diff -- src/` | **boş** — ölçüldü, aşağıda |

---

## Raporun başı — kızaran mevcut testler

Nihai durumda **244 test, 244 geçti, 0 düştü.** Yeşile çevirdiğim bir mevcut test yok.

Bildirmem gereken **tek** olay: mutasyon turlarından sonraki ilk soğuk koşumda
`Y-174 · Onaylanmayan teslim teslim sayılmaz; hafıza geri verilir` bir kez kızardı
(`Retrieve.Query`, `src/Oom/Retrieve/Retrieve.cs:255`). Ölçüm:

| Koşum | Sonuç |
|---|---|
| Değişikliklerle, soğuk (`rm -rf obj/Release` + build) × 4 | 1 kez Y-174 kırmızı, 3 kez 244/244 |
| Değişikliklerle, sıcak × 6 | 6 kez 244/244 |
| Tabanda (değişikliklerim stash'te), soğuk × 3 | 3 kez 238/244 — hep aynı 6 kırmızı, Y-174 yok |

Toplam 9 ardışık yeşil koşumdan sonra tekrarlamadı. Nedeni bana göre şeride ait değil:
`SqliteConnection.ClearAllPools()` süreç genelinde çalışıyor ve `tests/` içinde **106**
çağrı yeri var; xUnit test sınıflarını paralel koşturduğu için bir sınıfın havuz
temizliği başka bir sınıfın açık bağlantısını kapatabiliyor. Benim eklediğim net fazla
çağrı **bir** tane (Y-195'in sağlam tekrar açılış yarısından sonra). Kararı merkeze
bırakıyorum; yeşile çevirmek için hiçbir şey yapmadım.

---

## OWNS farkı

| Dosya | Değişiklik |
|---|---|
| `tests/Oom.Tests/SchemaV5MigrationTests.cs` | Y-192, Y-195, Y-199, Y-200 yeniden yazıldı; `Seed`'e `callRow` düğmesi ve salt-okunur `ReadOnlyLong` yardımcısı eklendi |
| `tests/Oom.Tests/Scars/DurumDeposuScars.cs` | Y-129'un `Applied` beklentisi ve DisplayName'i yeniden yazıldı, "ikinci yedek üretilmez" ölçümü eklendi |
| `denetim/astra-dalga-2/KUME-yama-onerisi.patch` | **asıl teslim** — `git diff -- tests/`, 361 satır, 2 dosya, +176/−64 |
| `denetim/astra-dalga-2/KUME.md` | bu rapor |

`src/**` — özellikle `src/Oom/State/` — `docs/scars.md`, `bench/**` ve diğer bütün testlere
dokunulmadı. Mutasyonlar `src/Oom/State/State.cs` üzerinde yapıldı ve her biri
`git checkout --` ile geri alındı.

Yama iki yönde de doğrulandı:

```
git apply --check denetim/astra-dalga-2/KUME-yama-onerisi.patch   # taban tests/ üzerine temiz uygulanır
git apply --check --reverse ...                                   # bu ağaçtan temiz geri alınır
```

---

## Olgular / kaynaklar

### Basamak seçimi damganın tarihsel sözleşmesine bakar, dosyanın anlık sütunlarına değil

`src/Oom/State/StateSchemaAudit.cs:263` — `StepApplies(step, found)`, `Required(found)`
ile karşılaştırır. `Required` `Introduced` sözlüğünden gelir ve o sözlük deponun
kendi geçmişinden ölçülmüştür (`100e6ce`, `v2.0.0`, `v2.1.0`, `ea3879e`). Dosyanın
gerçekten hangi sütunları taşıdığına bakılmaz.

Sonuç — ana döngünün "yalnız ad değişti" tespiti eksikti, **basamak seçimi de değişti**:

| Kaynak damga | Yürütülen basamaklar | Neden |
|---|---|---|
| 2 | **1, 2, 3, 4, 5** | `ingest_done`/`vault_meta` damga 3'te, çağrı kimliği sütunları damga 4'te geldi; damga 2'ye hâlâ borçlular |
| 3 | **2, 3, 4, 5** | çağrı kimliği ve `uncached_in_tok` sütunları damga 4'te geldi |

`State.cs:738`'deki raporlama ternary'si numarası damganın **üstünde** olan basamağı
`{found}→{version}: {ad}`, **altında ya da eşit** olanı
`{found}→5: {n}. basamağın eksik kalan kısmı tamamlandı: {ad}` diye yazar. Beklenen
metinleri buradan **türetmedim** — testte birebir yazdım (aşağıda).

### Neden altı kırmızıydı

Hepsi aynı kök: denetim artık damgaya inanmıyor, ölçüyor.

| Yara | Tabandaki kırmızının sebebi |
|---|---|
| Y-129, Y-192 (×2) | `Applied` beklentisi eski (yalnız damganın üstündeki) basamakları sayıyordu |
| Y-195 | Fikstür `DROP INDEX ix_notes_updated` sonrası **sessiz kabul** bekliyordu; üretim `StateSchemaException` atıyor |
| Y-199 | `calls` tablosu hiç olmayan sürüm 4 dosyasının **göç etmesini** bekliyordu; üretim reddediyor |
| Y-200 | `retrieve_served` eksikken basamağın **tabloyu kurmasını** bekliyordu; üretim göç başlamadan reddediyor |

---

## Çıkarımlar — her yaranın yeni beklentisi

### KÜME A · üç ret senaryosu

Üçünde de şu altı çapa var: `StateSchemaException` · eksik nesnenin **adı** · değişmeyen
sürüm damgası · değişmeyen **kayıt içeriği** (adet değil, alan alan) · eksik yapının
**hâlâ eksik** oluşu · **yeni göç yedeği oluşmaması**. Ret sonrası her inceleme
`Mode=ReadOnly` bağlantıyla yapılıyor (`ReadOnlyLong`): reddedilmiş dosya delildir,
yazan bir tutamaçla incelenen delil incelenirken değişmiş olabilir. Hata paragrafının
tamamına eşitlik aranmıyor; eksik nesnenin adı çapa olarak yeterli.

**Y-195** — mevcut hasar senaryosu korundu, önüne sağlam tekrar açılış eklendi.
İlk yarı: dosya zaten 5, `Applied` boş, `BackupPath` null, yedek dizininde hâlâ tek
dosya, `ix_notes_updated` yerinde. İkinci yarı: indeks düşürülür → ret,
`ix_notes_updated` adı hatada, indeks **hâlâ yok**, damga hâlâ 5, `calls`/`sessions`
satırları alan alan aynı, ikinci yedek yok.

**Y-199** — yaranın iki yarısı da duruyor.
Birinci yarı (ret): `calls` tablosu olmayan sürüm 4 → ret, hatada `tablo calls`,
tablo hâlâ yok, damga hâlâ 4, `sessions` ve `retrieve_served` satırları içerikleriyle
duruyor, `backup/` dizini hiç açılmamış.
İkinci yarı (eski yedek güvencesi): **tam** sürüm 4 şeması, `calls` tablosu var ama
**boş**, diğer tablolar dolu → göç eder ve doğrulanmış yedek alınır. `Seed`'e bunun
için `callRow` düğmesi eklendi; `calls: false` (tablo yok) ile `callRow: false`
(tablo var, satır yok) artık iki ayrı olgu. *"Yalnız ret testine çevirmek yaranın
yarısını kaybettirirdi"* — kaybettirmedi, ölçüsü aşağıdaki **D bozması**.

**Y-200** — başarılı göç ve "boş tablo kurulur" beklentileri kaldırıldı; yerine ret ve
değişmezlik kondu. Hatada `tablo retrieve_served`, tablo **hâlâ yok** (boş tablo
yaratılmadı), damga hâlâ 4, `calls`/`sessions`/`flush_log` satırları alan alan aynı,
yedek yok. Testin XML yorumuna Dalga 1 beklentisinin Dalga 2 kararıyla geçersizleştiği
yazıldı.

### KÜME B · iki rapor beklentisi

`Applied` için sıralı, açık, birebir metin beklentisi korundu — `NotEmpty`'ye de,
adet kontrolüne de düşürülmedi. Beklenen diziler testte **elle yazıldı**; üretimin
`Ladder`/`StepApplies` yordamından türetilmedi, çünkü üretimden türeyen beklenti
kendi kendini doğrular.

**Y-192 (damga 2)** ve **Y-129**, beşi de sırayla:

```
2→5: 1. basamağın eksik kalan kısmı tamamlandı: temel tablolar
2→5: 2. basamağın eksik kalan kısmı tamamlandı: çağrı kimlikleri (operation_id, attempt_id, attempt_no)
2→3: önbelleksiz girdi sayacı (uncached_in_tok)
2→4: bölünmüş sayaç anlambilimi (usage_rank, usage_semantics)
2→5: getirim dizini ve served defteri kapsamı (notes, notes_fts, oom_index_meta, retrieve_served kapsam sütunları)
```

**Y-192 (damga 3)**, dördü de sırayla:

```
3→5: 2. basamağın eksik kalan kısmı tamamlandı: çağrı kimlikleri (operation_id, attempt_id, attempt_no)
3→5: 3. basamağın eksik kalan kısmı tamamlandı: önbelleksiz girdi sayacı (uncached_in_tok)
3→4: bölünmüş sayaç anlambilimi (usage_rank, usage_semantics)
3→5: getirim dizini ve served defteri kapsamı (notes, notes_fts, oom_index_meta, retrieve_served kapsam sütunları)
```

Y-129'a ayrıca "sağlam ikinci açılışta **yeni yedek üretilmez**" ölçümü eklendi:
`backup/` dizininin dosya listesi göç sonrası ve tekrar açılış sonrası birebir aynı.

---

## Testler

```
rm -rf src/Oom/obj/Release tests/Oom.Tests/obj/Release
dotnet build Oom.sln -c Release --nologo   → 0 Hata, 1 Uyarı: CS0028, Program.cs(42,24)
dotnet test  Oom.sln -c Release --nologo   → Başarılı! 244 / 244 / 0
```

Taban: 244 test, 238 geçti, 6 düştü. Hedef ve sonuç: **244 / 244 / 0**, tek bir `src/`
baytı değişmeden. Soğuk derlemede tam olarak bir uyarı, beklenen yerde.

---

## Bozma kanıtı

Beş bozma, hepsi `src/Oom/State/State.cs` üzerinde, hepsi derlendi, hepsi geri alındı.
Her satır, bozmanın **amaçlanan assertion'a ulaştığını** gösteriyor — dalın hiç
çalışmadığı bir kırmızı değil.

### A — denetim reddi devre dışı: `if (missing.Count > 0)` → `> 1000` (`State.cs:681`)

| Test | Düşen assertion | Kanıt |
|---|---|---|
| Y-195 | `SchemaV5MigrationTests.cs:268` `Assert.Throws<StateSchemaException>` | `Assert.Throws() Failure: No exception was thrown` — indeks düşmüş dosya sessizce kabul edildi |
| Y-200 | `:501` `Assert.Throws<StateSchemaException>` | aynı — ve göç tamamlanıp `retrieve_served`'i boş kurdu, yani Dalga 1 davranışı geri geldi |
| Y-199 | `:445` `Assert.Empty(Backups(refused))` | `Collection was not empty` — ret yerine göç denendiği için yedek alındı |

Yan hasar (beklenen, aynı denetime yaslanan mevcut testler): Y-235, Y-236, Y-237, Y-238.
Geri alınca hepsi yeşil. 7 kırmızı → 0.

### B — rapor metni: `applied.Add(version > found …)` → `version >= found` (`State.cs:738`)

| Test | Düşen assertion | Kanıt |
|---|---|---|
| Y-192 (damga 2) | `SchemaV5MigrationTests.cs:110` `Assert.Equal(expected, report.Applied)` | 2. eleman `2→5: 2. basamağın…` yerine `2→2: çağrı kimlikleri…` geldi |
| Y-192 (damga 3) | `:110` aynı | 2. eleman `3→5: 3. basamağın…` yerine `3→3: önbelleksiz…` geldi |
| Y-129 | `DurumDeposuScars.cs:145` `Assert.Equal(…, report.Applied)` | damga 2 ile aynı sapma |

Göç başarıyla tamamlandı, yani assertion'a gerçekten ulaşıldı. 3 kırmızı, başka hiçbir
test etkilenmedi — bu üç beklenti bu metnin **tek** taşıyıcısı.

### C — basamak seçimi numaraya döndürüldü: `StepApplies(step.Version, found)` → `step.Version > found` (`State.cs:735`)

| Test | Düşen assertion | Kanıt |
|---|---|---|
| Y-192 (damga 3) | `SchemaV5MigrationTests.cs:110` `Assert.Equal(expected, report.Applied)` | beklenen 4 satır, gelen 2 satır (`3→4`, `3→5`) — 2. ve 3. basamaklar hiç koşmadı |

Y-192 (damga 2), Y-129, Y-195, Y-170, Y-230, Y-231 bu bozmayla **daha erken** düştü
(dosya `AuditMigrated`/`AuditUpgradePath` tarafından reddedildi), yani onlar için bu
bozma "amaçlanan assertion'a ulaştı" sayılmaz; damga 3 koşumu için ulaştı ve kanıt odur.

### D — yedek koşulu çağrı satırına bağlandı: `HasContent` → `TableExists(…, "calls") && Count(…, "calls") > 0` (`State.cs:787`)

| Test | Düşen assertion | Kanıt |
|---|---|---|
| Y-199 | `SchemaV5MigrationTests.cs:460` `Assert.NotNull(report.BackupPath)` | `Assert.NotNull() Failure: Value is null` — boş `calls` taşıyan tam sürüm 4 dosyası yedeksiz göç etti |

**Tek** kırmızı: 243 geçti, 1 düştü. Y-199'un ikinci yarısı olmasa bu gerileme
suitede hiçbir yerde yakalanmıyordu — yaranın yarısını kaybetmediğimizin ölçüsü bu.

### E — "deliği kazarak raporla": ret atılmadan önce eksik tablo/indeks kuruluyor (`State.cs:681` öncesine eklendi)

| Test | Düşen assertion | Kanıt |
|---|---|---|
| Y-195 | `SchemaV5MigrationTests.cs:275` `Assert.Equal(0, ReadOnlyLong(… 'ix_notes_updated'))` | indeks sessizce geri kuruldu |
| Y-199 | `:441` `Assert.Equal(0, ReadOnlyLong(missing, … 'calls'))` | eksik `calls` tablosu yaratıldı |
| Y-200 | `:508` `Assert.Equal(0, ReadOnlyLong(… 'retrieve_served'))` | eksik tablo yaratılarak eksiklik örtüldü |

Üçünde de `Assert.Throws` ve `Assert.Contains(ad)` geçti, sonra değişmezlik çapası
düştü — yani bozma tam olarak apex'in yasakladığı yere ulaştı. Yan hasar: Y-238, Y-235,
Y-241. Geri alınca hepsi yeşil.

### Geri alma ölçümü

```
$ git diff -- src/
$ (boş)
```

Commit öncesi `git status --porcelain` yalnız iki test dosyasını ve bu dizindeki iki
yeni dosyayı gösteriyor.

---

## Açıklar

1. **`docs/scars.md` güncellenmedi.** DO-NOT-TOUCH'tı. Apex'in verdiği altı defter
   metni hâlâ merkezin yazmasını bekliyor. `Y-125` yalnız Y-numarasının varlığını
   eşliyor (`TestDisipliniScars.cs:141`), metni değil — bu yüzden DisplayName'leri
   yeni hükme göre yazmak `Y-125`'i kırmadı. Defter metniyle DisplayName arasındaki
   tutarlılığı kimse ölçmüyor; bu zaten var olan bir açık.
2. **Y-174 bir kez kızardı** (raporun başı). Kök neden sanısı: `ClearAllPools()`'un
   süreç geneli olması + xUnit sınıf paralelliği. Ölçmedim, iddia etmiyorum;
   9 ardışık yeşil koşumda tekrarlamadı.
3. **Y-192'nin damga 2 koşumu C bozmasında erken düşüyor.** Yani "damga 2 için beş
   basamak koşar" iddiasının doğrudan, tek-assertion'lık bozma kanıtı yok; dolaylı
   kanıt B bozmasından geliyor (beş elemanın ikincisi metniyle beklendi ve saptı).
   Doğrudan kanıt için `Introduced[3]`/`Introduced[4]`'ü oynatan bir bozma gerekirdi,
   o da denetimin kendisini oynatmak olurdu.
4. **Hata paragrafının tamamı çapalanmadı** — apex'in izniyle. Eksik nesnenin adı
   çapa. Hata metninin geri kalanı (yol, "İş verisine dokunulmadı", "Teşhis: oom doctor")
   hiçbir testte birebir tutulmuyor; metin değişirse kimse duymaz.
5. **Yama uygulanmadı, önerildi.** Mevcut test değişikliği sahibinin onayını ister.
   Commit hem düzenlenmiş test dosyalarını hem yamayı taşıyor; hangisinin kullanılacağına
   merkez karar verir.
