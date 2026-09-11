# Şerit R — Y-042 için çalışan ölçüm kapısı

## 0. Bu belgenin kanıt niteliği

`bench/PROVENANCE.md`'nin dört parçalı demeti bu belgeye de uygulanıyor.

- **kim ölçtü:** claude · opus-5 (şerit R, worktree `oom-w-R`)
- **hangi komut:** §6'da birebir listelendi; hepsi bu oturumda, bu ağaçta koştu
- **hangi ham artefakt:** `bench/results/astra-dalga-1/raw/honest.stdout.txt` —
  yayımlanmış exe'nin 130 sorguya verdiği ham stdout; yanındaki
  `corrupt.stdout.txt` ve `misaligned.stdout.txt` iki negatif senaryonun ham
  çıktısı. Özet dosyaları bu üç dosyadan üretildi.
- **hangi commit:** taban `021cc83f6acd4e481d42b6d5327981590e05457c`
- **hangi ikili:** `de8fb8af0cc8cd12cca400591773a18f04d68bb0a6e267deada9603f7bc3a43e`
- **run_status:** ok
- **substitute:** **active: true** — ölçüm bu şeridin kendi sentetik korpusu ve
  kendi gold setiyle yapıldı, sahibinin gold setiyle değil. Buradaki hiçbir
  rakam ürünün recall'ını tarif etmiyor.

Bir dosya kendi commit hash'ini taşıyamaz: bu belgeyi taşıyan commit'in hash'i
ana döngüye ayrıca bildirildi.

## 1. Taban

- taban_sha: `021cc83f6acd4e481d42b6d5327981590e05457c`, ayrık HEAD, temiz
- soğuk derleme tabanı doğrulandı: 191 test, 0 düşen, tam olarak bir uyarı
  (`CS0028`, `src/Oom/Program.cs:42` — OWNS dışı, dokunulmadı)

## 2. OWNS farkı

- `bench/verify_recall.py` — yeni, kimlik sabitlemeli recall kapısı
- `tests/Oom.Tests/RecallEvidenceTests.cs` — yeni, `Y-220…Y-225`
- `bench/results/astra-dalga-1/` — yeni, 5 kanıt dosyası + 3 ham çıktı
- `denetim/astra-dalga-1/R.md` — bu belge
- `denetim/astra-dalga-1/R-y042-degisiklik-onerisi.patch` — Y-042 önerisi
- Üretim kodu, `Y-042`, eski sonuç dosyaları, gold cevapları, eşikler ve
  `docs/scars.md`: **tek bayt değişmedi.** `git status` yalnız yukarıdaki dört
  yolu izlenmeyen olarak gösteriyor.

## 3. Olgular — kaynaklarıyla

### Y-042 gerçekten ne yapıyor

- `tests/Oom.Tests/Scars/GetirmeScars.cs:208` tabanını
  `bench/results/recall-2026-09-09-r2-gate.json`'dan okuyor.
- Bu dosya `bench/PROVENANCE.md` §3'ün **adıyla andığı** ihlal örneği:
  `index.error: "FileNotFoundError: <state-root>\state.db"` ile
  `pass.recall@5: true` aynı dosyada duruyor. Her ikisini de dosyada doğruladım.
- Dosya §2'nin demetinden hiçbirini taşımıyor: `measured_by`, `run_status`,
  `raw_artifact`, `substitute`, `source_commit`/`binary_sha256` — beşi de yok.
- Apex hükmü doğru ve eksik değil: testin 213–219. satırları **geçmiş bir
  koşumun sayılarını sabitlerle karşılaştırıyor**, bugünkü ikilinin recall'ını
  hiç ölçmüyor. Tek canlı iddia (228–229) beş sonucun geldiğini ve hepsinin
  `concept` olduğunu kontrol ediyor — bu bir şekil kontrolü, recall değil.

### `bench/` zaten neye sahipti

- `bench/kos20.py` gold set üzerinde recall ölçen, **dört parçalı demeti zaten
  emen** bir koşucu. `bench/provenance.py`'yi (`assess`, `redact_verdicts`,
  `sha256_file`, `git_commit`) kullanıyor. Bu şerit ikinci bir sözleşme icat
  etmedi; aynı modülü içe aktardı.
- `kos20.py`'nin yapmadığı ve `bench/` içinde hiçbir şeyin yapmadığı: ölçülen
  şeyin **kimliğini ölçmenin önkoşulu** yapmak. `binary_sha256` koşumdan
  *sonra*, eline geçen exe'den yazılıyor; gold seti hiç hash'lenmiyor; korpus
  hiç hash'lenmiyor. Yanlış exe ile koşan bir ölçüm kusursuz biçimli,
  `run_status: "ok"` bir dosya üretiyor.
- `kos20.py` çıktı bütünlüğü için tek şey kontrol ediyor: satır sayısı sorgu
  sayısına eşit mi. Gerisini olduğu gibi ayrıştırıyor.

### Getirme yolu hakkında ölçülmüş olgu

- `src/Oom/Retrieve/Retrieve.cs` → `Search()` her koşulda `LoadCorpus()`
  çağırıyor; FTS indeksi yalnızca **adayları daraltıyor**. Kodun kendi ifadesi:
  eksik indeks "getirmenin hızını düşürmeli, cevaplarını değil".
- **Sonuç:** recall ölçümü için indeks gerekmiyor — tarihî dosyanın
  `index.error` taşırken yine de recall sayıları üretebilmesinin sebebi bu.
  Aynı zamanda şu demek: o koşum **tam tarama yolunu** ölçtü, indeksli yolu
  değil. İndeksli yolun recall'ı bu depoda hiç ölçülmedi.
- Protokol ampirik olarak doğrulandı (fikstür kasasına karşı, canlı kasaya
  değil): `oom.exe --vault <yol> retrieve --batch <jsonl> --top 5` sorgu başına
  bir JSON satırı basıyor; her satır `schema_version`, gönderilen sorgunun
  yankısı ve `hits[].name` / `.score` / `.source` taşıyor.

## 4. Çıkarımlar

- Y-042'nin sorunu provenance değil, **ölçüm yokluğu**. Dosyayı uyumlu bir
  dosyayla değiştirmek tek başına hiçbir şeyi düzeltmezdi: test yine geçmiş
  sayılara bakıyor olurdu.
- Bir recall sayısının kanıt olması için ölçüldüğü şeyin kimliğinin **ölçümden
  önce** sabitlenmesi gerekiyor. Sonradan yazılan hash bir tarif, bir kontrol
  değil.
- Bozuk getirme çıktısı düşük skor değildir. Uydurma not adları üzerinden
  ortalama alınınca `recall@5: 0.000` çıkıyor — bu yayımlanabilir, biçimsel
  olarak kusursuz ve yanlış bir sayı. §7'deki bozma kanıtı bunu ölçtü.
- Kanca yolunun enjeksiyon oranı recall'ın yerine geçemez: `retrieve --hook`
  `Query`'nin uygulamadığı bir niyet kapısı ve skor tabanı uyguluyor.
- Hiç enjekte etmeyen bir kancanın yanlış-pozitif oranı sıfırdır ve hiçbir şeyi
  ayırt etmez. Koşucu bu durumu `controls.vacuous` ile işaretliyor ve verdict
  **vermiyor** — fikstürümüzde tam olarak bu oldu.

## 5. Ne kuruldu

### `bench/verify_recall.py`

- `run` — kapı. Sırayla: exe/korpus/gold hash'lerini bekleneni ile karşılaştır
  → uyuşmazsa **tek sorgu göndermeden** reddet; korpusu sabit bir kopyaya
  al ve kopyayı ölç; batch'i koş; ham stdout'u sakla; çıktının bütünlüğünü
  ölçülen korpusa karşı doğrula; puanla; §2–§4 uyumlu dosya yaz.
- Çıktı bütünlüğü kontrolleri: korpusta olmayan not adı, azalmayan skorlar,
  aynı listede tekrar eden not, gönderilenden farklı yankılanan sorgu, `--top`
  aşımı, satır hizası, tüm puanlanan sorguların boş dönmesi.
- `identity` — kimlik üçlüsünü basar (ölçüm yapmaz).
- `selftest` — bu şeridin kendi fikstürleriyle dört senaryo koşar.
- Puanlama yüklemi (`slug`, `recall_at`) `kos20.py` ile **birebir aynı**;
  `selftest` bunu yorumla değil, modülü içe aktarıp karşılaştırarak doğruluyor.
- Yazılan her dosya `<repo>` / `<home>` / `<temp>` ile temizleniyor.

### `tests/Oom.Tests/RecallEvidenceTests.cs`

| Yara | Neyi çiviliyor |
|---|---|
| Y-220 | Kanıt dosyası dört parçalı demeti taşır; Y-042'nin okuduğu tarihî dosya taşımaz |
| Y-221 | `run_status` 'ok' değilken hiçbir dosya kapı geçti diyemez (§3) |
| Y-222 | Yanlış exe hash'i ölçümden **önce** reddedilir, sayı üretilmez |
| Y-223 | Bozuk getirme çıktısı düşük skora değil, skorsuzluğa düşer |
| Y-224 | Kapı 125 puanlanan soruyu sayar; kanca ve negatif kontroller ayrı raporlanır |
| Y-225 | Sınıf etiketleri yeniden adlandırılmaz; episodik eksen açık kalır |

## 6. Test komutları ve sonuçları

```
dotnet build Oom.sln -c Release --nologo          → 1 uyarı (CS0028), 0 hata
dotnet test  Oom.sln -c Release --nologo          → 197 test, 1 düşen
python bench/verify_recall.py selftest --exe publish/win-x64/oom.exe --measured-by claude → exit 0
```

- Düşen tek test **`Y-125`**: `test without a ledger row: Y-220, Y-221, Y-222,
  Y-223, Y-224, Y-225`. Şerit emri bunu tek beklenen kırmızı olarak adlandırdı;
  `docs/scars.md`'ye dokunulmadı, defter satırlarını merkez atar.
- 191 taban testinin tamamı yeşil; yeni altı testin tamamı yeşil.

### Fikstür ölçümünün rakamları (ürünün değil)

- korpus: 160 not, `10b030783ecf6d55…`; gold: 130 satır (125 puanlanan +
  5 `kanarya`), `ec7b12c1e507c5fe…`; tohum 20260911
- `overall`: n=125 · recall@3 **0,856** · recall@5 **0,920** · MRR@5 0,8611
- `tek-not`: n=105 · recall@3 0,8286 · recall@5 0,9048
- `cok-not`: n=20 · recall@3 1,000 · recall@5 1,000
- ilk gold isabetin rank dağılımı: 1→105, 2→1, 3→1, 4→4, 5→4, isabetsiz→10
- kontroller: `kanarya` n=5, enjeksiyon 0, yanlış-pozitif 0,00 · pozitif örnek
  n=20, enjeksiyon 0,00 → **vacuous: true**, verdict verilmedi

Fikstür bilerek iki eşiği ayırt edecek biçimde kuruldu: recall@5 (0,920)
recall@3'ten (0,856) yüksek. İlk denemede her soru ya 1. sırada isabet ediyor ya
da hiç etmiyordu; iki sayı birbirinin aynısı çıkıyordu ve kapı tek eşik
ölçüyordu. Aile belirteçleri bu yüzden eklendi.

## 7. Bozma kanıtı

Her yeni test için: **derlenen kasıtlı davranış bozması → beklenen assertion
başarısızlığı → düzeltme sonrası yeşil.** Bozmalar `bench/verify_recall.py`
üzerinde yapıldı (kanıt dosyası elle düzenlenmedi), kanıt demeti yeniden
üretildi, test filtreyle koşuldu, sonra koşucu bayt-aynı geri yüklendi.

| Test | Bozma | Gözlenen kırmızı |
|---|---|---|
| Y-220 | Özet yükünden `measured_by` düşürüldü | `Assert.Empty() Failure … Collection: ["measured_by"]` |
| Y-221 | Ret yolundan `redact_verdicts` kaldırıldı, `decision` doğru yazıldı | `selftest-2-wrong-exe-hash.json: run_status 'ok' değil ama decision doğru diyor` |
| Y-222 | `matched = True` — kimlik karşılaştırması hiçbir şeyi doğrulamıyor | `Assert.True() Failure — Expected: True, Actual: False` |
| Y-223 | Korpusta-var-mı kontrolü devre dışı | `Assert.False() Failure — Expected: False, Actual: True` |
| Y-224 | `recall_gate` kontrollerden hesaplandı | `Assert.True() Failure — Expected: True, Actual: False` |
| Y-225 | `tek-not→episodik`, `cok-not→kavram` yeniden adlandırma | `Assert.Contains() Failure … Collection: ["episodik","kavram"], Not found: "tek-not"` |

İki bozma özellikle konuşuyor:

- **Y-223'ün bozması** tek satırlık kontrolü kapatınca, uydurma not adlarıyla
  cevap veren backend `n=125` üzerinde `recall@3: 0.000 · recall@5: 0.000`
  üretti. Sayı çıktı; hiçbir şey ölçülmemişti. Kontrol açıkken aynı koşum 650
  bütünlük hatası sayıyor ve hiç sayı üretmiyor.
- **Y-224'ün bozması** recall 0,856/0,920 ile iki eşiğin de üstündeyken
  `recall_gate: false` verdi — çünkü verdict kontrollerden okunmuştu. Kapısız
  recall'ın kontrollerin yerine geçmesi kadar, kontrollerin recall'ın yerine
  geçmesi de yanlış cevap veriyor.

Geri yükleme doğrulandı: `diff -q` bayt-aynı, `grep -c "BREAK-Y"` → 0.

## 8. Y-042 değişiklik önerisi

- Tam fark: `denetim/astra-dalga-1/R-y042-degisiklik-onerisi.patch`
- **Dosyaya dokunulmadı.** `git status tests/Oom.Tests/Scars/GetirmeScars.cs`
  boş; dosya HEAD ile bayt-aynı.
- Yama doğrulandı (uygulandı → ölçüldü → geri alındı):
  - `git apply --check` → 0
  - uygulandığında `dotnet build` → **0 uyarı, 0 hata** (yani derleniyor)
  - kanıt dosyası yokken Y-042 **kırmızı**, doğru gerekçeyle: `ölçülmüş recall
    kanıtı yok: bench/results/astra-dalga-1/recall-final.json`
  - uyumlu bir kanıt dosyası konduğunda Y-042 **yeşil**
  - sonra `git checkout --` ile geri alındı, `git status` temiz
- Yamadaki yeni Y-042 sırayla şunları arıyor: `run_status == "ok"` ·
  `output_integrity.ok` · exe/korpus/gold için `identity.*.verified` ·
  64-hex `binary_sha256` · n ≥ 125 · dosyanın kendi eşiklerine karşı recall@3 ve
  recall@5 · `tek-not`/`cok-not` sınıf zeminleri (90/20, 0,85/0,90 — **eşikler
  değiştirilmedi**) · `kanarya` etiketiyle kontrol bloğu · ve mevcut canlı
  sıralayıcı kontrolü **olduğu gibi korundu**.
- Yama **şimdi uygulanmamalı**: `recall-final.json` son birleşik adaydan sonra
  üretilecek. Uygulanır da dosya yoksa Y-042 haklı olarak kırmızı kalır.

## 9. NE ÖLÇTÜM

- Yayımlanmış `oom.exe`'nin (`de8fb8af…`) 160 notluk sentetik bir korpusun
  **sabit kopyası** üzerinde 130 soruya verdiği sıralamaları; kimlik üçlüsü
  ölçümden önce doğrulandı, ham stdout saklandı ve commit'lendi.
- Kapı aritmetiğinin kendisi: 125 puanlanan soru, recall@3 ≥ 0,80,
  recall@5 ≥ 0,88 — ikisi de fikstürde ayrı ayrı bağlayıcı.
- Yanlış exe hash'inin ölçümden önce reddedildiğini.
- Uydurma not adları ve yanlış yankılanan sorgu ile gelen çıktının
  reddedildiğini; kontrol kapatıldığında bunun `0.000` diye bir sayıya
  dönüştüğünü.
- Kanca yolunu (`retrieve --hook`) fikstür üzerinde: 5 `kanarya` + 20 pozitif.
- Getirme sıralamasının indekse ihtiyaç duymadığını (kod okuması + fikstür
  koşumu; hiç `state.db` yokken sıralama geldi).
- Yamalı Y-042'nin derlendiğini ve iki yönde de doğru davrandığını.

## 10. NE ÖLÇMEDİM — hepsi "ölçülmedi"

- **Ürünün recall'ı: ölçülmedi.** Şerit emri nihai ölçümü son birleşik adaya
  bağladı; bu şerit kapıyı ve koşucuyu kurdu, rakamı üretmedi.
- **Sahibinin gold seti (`.brief/gold-sorular.jsonl`): ölçülmedi.** Dosya
  `.gitignore`'da, worktree'de yok. `kos20.py` onu varsayılan olarak gösteriyor
  ama depoda bulunmuyor. 125/130 ayrımını `kos20.py`'nin kodundan ve
  `bench/README.md`'den okudum, dosyayı açmadım.
- **Sahibinin gerçek korpusu: ölçülmedi.** `bench/kos20.py` ve
  `bench/gate_measure.py` kasayı `<vault>` yer tutucusuyla taşıyor. Sahibinin
  canlı kasasının durum veritabanı bozuk ve onarım bekliyor; ortam emri gereği
  ona karşı hiçbir komut koşturulmadı, korpus kopyası alınmadı. Kasanın yolu
  bu belgede de yazılmıyor — depo zaten her yerde `<vault>` kullanıyor.
- **`oom doctor`: hiç koşturulmadı**, hiçbir kasaya karşı.
- **Episodik recall ekseni: ölçülmedi.** Bu depoda böyle bir eksen üreten
  hiçbir araç yok. `tek-not`/`cok-not` cevap-kardinalitesi etiketleri;
  yeniden adlandırılmadı ve bu eksenin yerine sayılmadı. Kanıt dosyası bunu
  `episodic_axis.measured: false` ile yapısal olarak kaydediyor. **Açık kalıyor.**
- **İndeksli aday yolunun recall'ı: ölçülmedi.** Hem tarihî koşum hem benim
  fikstür koşumum `state.db` yokken, tam tarama yolunda ölçtü. İndeksli yolun
  aynı sayıyı verdiği **gösterilmedi**.
- **`bench/probe-30.jsonl`'in 30 promptluk kapısı: koşulmadı.** Dosya depoda
  var (`kos20.py`'nin `hook_probe` docstring'i "bench'te yok" diyor; bu ifade
  artık yanlış), ama fikstür korpusuna karşı anlamlı olmadığı için koşmadım.
- **Kanca pozitif tarafı: ölçüldü ama hiçbir şey ayırt etmedi.** Fikstür
  notları kancanın skor tabanını aşmıyor, 20 pozitifin hiçbiri enjekte etmedi.
  Bu yüzden `controls.pass` null; yanlış-pozitif 0,00 tek başına kanıt değil.
  Kancanın ayırt etme gücü **ölçülmedi**.
- **Gerçek gold seti üzerinde `kos20.py` ile `verify_recall.py` parite**:
  puanlama yüklemlerinin aynı olduğu doğrulandı, **uçtan uca aynı sayıyı
  verdikleri ölçülmedi** (ikisini birlikte koşacak gold set yok).
- **Tarihî `recall-2026-09-09-r2-gate.json`'un sayılarının doğru olup
  olmadığı: ölçülmedi.** Yalnız o dosyanın kanıt sözleşmesini karşılamadığı ve
  `index.error` ile `pass: true`'yu birlikte taşıdığı ölçüldü. Sayıların kendisi
  doğru da olabilir — bilinmiyor, ve bilinmemesi tam olarak sorunun kendisi.
- **Performans/gecikme:** `backend.elapsed_ms` kaydedildi ama hiçbir eşiğe
  bağlanmadı; **ölçüm hedefi değildi.**

## 11. Onay bekleyenler

- **Nihai ölçüm.** Son birleşik aday yayımlandığında:
  `python bench/verify_recall.py run --exe <yayımlanmış exe> --vault <kasa>
  --gold <gold> --snapshot <sabit kopya> --expect-exe-sha256 <hash>
  --expect-corpus-sha256 <hash> --expect-gold-sha256 <hash>
  --raw bench/results/astra-dalga-1/raw/final.stdout.txt
  --out bench/results/astra-dalga-1/recall-final.json`
  Bu komut sahibinin korpusuna erişim gerektiriyor; **bu şerit onu koşmadı ve
  koşmaya yetkili değil.**
- **`docs/scars.md` defter satırları** `Y-220…Y-225` için — merkez atar. O
  satırlar girene kadar `Y-125` kırmızı kalır.
- **Y-042 yaması** yalnız `recall-final.json` ürediğinde uygulanmalı.

## 12. Açıklar

- `verify_recall.py` sahibinin gerçek korpusuna karşı **hiç koşmadı**. 160
  dosyalık sentetik korpusta çalışan bir kod, 542 dosyalık gerçek korpusta
  ölçeklenme ya da kodlama sürprizi yaşayabilir; bu bilinmiyor.
- Fikstür korpusu depoda **durmuyor**, tohumdan (20260911) yeniden üretiliyor.
  Korpus hash'i bu yüzden yeniden üretilebilir, ama Python'ın `random`
  uygulaması sürümler arasında değişirse hash kayar. Not `bytes` manifesti
  kanıt dosyasında duruyor, yani kayma fark edilir.
- `parse_and_check` düzeltme isabetlerini (`Duzeltmeler.md#3`) korpusta-var-mı
  kontrolünden muaf tutuyor — meşru, ama uydurma bir `#`'li ad bu muafiyetten
  geçer. Fikstürde düzeltme dosyası yok, dolayısıyla bu yol **hiç koşmadı**.
- Kanca kontrollerinin fikstürde vacuous çıkması, kontrol bloğunun gerçek
  ayırt etme gücünü kanıtsız bırakıyor.
- `selftest-2` ham artefakt tutmuyor (`none-kept`) — doğru davranış, çünkü
  ölçümden önce reddediyor; ama §2'yi harfiyen okuyan bir denetçi için bu
  dosyada okunacak ham çıktı yok.
