# KAR-1 · Aralıklı test kırmızısının kök nedeni

**taban_sha** `3a05200bf338f7901db8a2ea3c418d597c4174df` · **ağaç** `E:\OdenaWorks\10-Aktif\oom-w-KAR1`
**ölçen** claude · opus-5 (üç Sonnet alt ajanı yalnız okuma yaptı; `dotnet` koşmadı, dosya yazmadı)
**paket** Microsoft.Data.Sqlite **10.0.12** · xunit **2.9.2** · xunit.runner.visualstudio **2.8.2** · Microsoft.NET.Test.Sdk **17.12.0**
**ham çıktı kökü** `E:\OdenaWorks\10-Aktif\kar1-kanit\` (ağacın dışında; hiçbir koşum silinmedi, hiçbir koşum tekrarlanmadı)

> Tek cümlede: **kök neden kuruldu ama sonuna kadar değil** — aralıklı kırmızının sınıf paralelliğini
> gerektirdiği, kurbanın her seferinde Microsoft.Data.Sqlite havuzundan yeni kiralanmış bir bağlantı
> olduğu ve yalnızca `ClearAllPools()` koşturan bir iş parçacığının **üretim kodundaki** bir bağlantıyı
> öldürdüğü ölçüldü; havuzun bir bağlantıyı hangi anda "sızmış" sayıp attığı, yani kusurun son halkası,
> bağımsız ve deterministik bir senaryoyla **üretilemedi**.

---

## 1. Ölçüm defteri

Her satır apex'in istediği beşliyi taşır. "Paralellik" sütunu xunit sınıf/koleksiyon paralelliğidir.

| # | Ölçüm | commit | Tam komut | Paralellik | Ham çıktı |
|---|---|---|---|---|---|
| M1 | Soğuk derleme: **1 uyarı** `CS0028 Program.cs(42,24)`, 0 hata | `3a05200` | `rm -rf src/Oom/obj/Release tests/Oom.Tests/obj/Release && dotnet build Oom.sln -c Release --nologo` | — | `kar1-kanit/build-baseline.txt` |
| M2 | **259 test, 259 geçti** (tek koşum) | `3a05200` | `dotnet test Oom.sln -c Release --no-build --nologo` | AÇIK (xunit varsayılanı) | `kar1-kanit/par-on-probe-01.txt` |
| M3 | **30 koşumda 4 kırmızı**, hepsi `ObjectDisposedException: SQLitePCL.sqlite3`, **dört farklı kurban** | `3a05200` | M2'deki komut ×30 | AÇIK | `kar1-kanit/repro-paron/` (30 dosya + `summary.txt`) |
| M4 | Bağımsız havuz sondaları: **her kolda 0 başarısızlık** | `3a05200` | tek testlik filtre koşumu | test içi iş parçacıkları | `kar1-kanit/probe-01.txt`, `probe-02.txt` |
| M5 | `oom doctor` `%TEMP%\oom-doctor-*` altında **kopya bırakıyor**: derece 1 → 1–3, derece 2 → 2–4, derece 4 → 1–2 | `3a05200` | tek testlik filtre koşumu | süreç dışı | `kar1-kanit/probe-copies.txt`, `probe-copies-2.txt` |
| M6 | Örtüşme tanığı: ayar yok → **tepe 2** (805 ms); RunSettings `<xUnit>` düğümü → **tepe 2**; `xunit.runner.json` + `Content` kalemi → **tepe 1** (1 s) | `3a05200`+aday | `dotnet test Oom.sln -c Release --no-build --nologo --filter "FullyQualifiedName~OverlapProbe"` | ölçülen büyüklük | bu belgede §3 |
| M7 | Saldırgan kolları, **40'ar koşum**: `clear` **14/40**, `spin` **8/40** `ObjectDisposedException`'lı koşum | `3a05200`+aday | `dotnet test Oom.sln -c Release --no-build --nologo --filter "Category!=IntentionalRed"` | AÇIK (`xunit.runner.json` geçici olarak çıkarıldı) | `kar1-kanit/arms2/` |
| M8 | **Doğrudan yakalama**: yalnız `ClearAllPools()` koşturan iş parçacığı, `Doctor.ReadContent`'in bağlantısını öldürdü | `3a05200`+aday | M7'deki komut | AÇIK | `kar1-kanit/arms2/clear/run-010.txt` |
| M9 | Kasıtlı bozma: 3 bozmanın 3'ü derlendi, **amaçlanan** assertion düştü, geri alınınca yeşil | aday | `kar1-kanit/bozma.sh` | KAPALI | `kar1-kanit/bozma/` |
| M10 | **20 ardışık tam koşum** | aday | `dotnet test Oom.sln -c Release --nologo` | KAPALI | `kar1-kanit/yirmi/` |

Ayrıca ölçülen, sayı içermeyen olgular:

- `SqliteConnection.ClearAllPools()` testlerde **12 dosyada 114 çağrı yeri** (`grep -rn` 116 satır, 2'si yorum). Apex'in
  brifingindeki "34 dosyada 106" rakamı **yanlış**; muhtemelen `bin/`+`obj/` ikilileri sayılmış. Üretimde **2** çağrı yeri:
  `src/Oom/Doctor/Doctor.cs:250` ve `:504`.
- `src/` içinde `Pooling` hiçbir yerde geçmiyor → havuzlama Microsoft.Data.Sqlite varsayılanıyla **açık**.
- Test kaynaklarında `[Collection]`, `CollectionDefinition`, `DisableParallelization`, `CollectionBehavior` **yok**;
  depoda `xunit.runner.json` **yoktu**. Yani paralellik tamamen varsayılan haliyle işliyordu.

---

## 2. Neden bağı

### 2.1 Yakalanan olgu — kurban `Y-170` değil, sıradaki kim varsa o

30 koşumun 4'ü kırmızı verdi ve **her birinde başka bir test** düştü:

| Koşum | Kurban | Düştüğü üretim çerçevesi |
|---|---|---|
| `run-01` | `Y-192` | `StateStore.Verify` → `Backup` → `Provision` → `Open` (`State.cs:837`) |
| `run-07` | `İndeks · içerik değişince manifest değişir` | `StateStore.Execute` → `Provision` → `Open` → `Retrieve.OpenIndexForWriteAt` (`State.cs:986`) |
| `run-10` | `Y-242` | `StateDiagnostics.Integrity` → `Diagnose` (`StateSchemaAudit.cs:559`) |
| `run-20` | `Y-235` | aynı aile |

İstisna her seferinde aynı: `System.ObjectDisposedException : Cannot access a disposed object.
Object name: 'SQLitePCL.sqlite3'` ve yığının tepesi her seferinde
`SafeHandle.DangerousAddRef` → `sqlite3_prepare_v2`.

Buradan çıkan **ilk sağlam sonuç**: kırmızı `Y-170`'e ait bir özellik değil. Kurban, o anda
**havuzdan yeni kiralanmış bir bağlantıyla ilk ifadesini hazırlayan** hangi test varsa odur.
`Y-170`, `Y-210` ve yukarıdaki dördü aynı kusurun farklı örnekleridir. Apex'in brifingi `Y-170`'i
tek ad olarak veriyordu; ölçüm bunu **genişletiyor**.

### 2.2 Ne kuruldu

1. **Paralellik şart.** Sınıf paralelliği açıkken 30 koşumda 4 kırmızı; kapalıyken (M10) 20 koşumda
   `ObjectDisposedException` **hiç yok**. Kusur, xunit'in sınıfları paralel koşturmasına bağlı.
2. **Ortak kaynak tek.** Testlerin kendi aralarında paylaştığı tek süreç-geneli nesne
   Microsoft.Data.Sqlite'ın bağlantı havuzudur. `ClearAllPools()` bu havuzu **bütün süreç için**
   siler ve testlerde 114, üretimde 2 yerden çağrılıyor.
3. **Havuzun kodu bu sonucu üretebiliyor.** `SqliteConnectionPool.Clear()` önce `_connections`'taki
   her bağlantıya `DoNotPool()` diyor, sonra sıcak/soğuk yığınları atıyor, sonra
   `ReclaimLeakedConnections()` çağırıyor. Orada "sızmış" ölçütü
   `Leaked => _active && !_outerConnection.TryGetTarget(out _)` — yani **dış `SqliteConnection`
   nesnesine zayıf başvuru**. Sızmış sayılan bağlantı `Return`'e gidiyor, `DoNotPool()` yüzünden
   havuza dönemiyor ve `DisposeConnection` ile `sqlite3` tutamacı **atılıyor**. Atılan tutamacı
   kullanan bir sonraki `sqlite3_prepare_v2`, gözlenen istisnanın ta kendisidir.
   Kaynak: `dotnet/efcore` · `src/Microsoft.Data.Sqlite.Core/SqliteConnectionPool.cs` ve
   `SqliteConnectionInternal.cs`.
4. **Doğrudan yakalama (M8).** Hiçbir şey yapmayıp yalnız `ClearAllPools()` döngüsü koşturan fazladan
   bir iş parçacığı eklendiğinde, `%36` koşumda kırmızı alındı ve bir koşumda ölen şey **test kodu
   değil, üretim kodu** oldu:

   ```
   at Microsoft.Data.Sqlite.SqliteCommand.ExecuteReader()
   at Oom.Contracts.Doctor.ReadContent(String databasePath) in src\Oom\Doctor\Doctor.cs:line 628
   at Oom.Contracts.Doctor.InspectOneStateRoot(...) in src\Oom\Doctor\Doctor.cs:line 274
   at Oom.Contracts.Doctor.InspectStateRoots(String localAppData) in src\Oom\Doctor\Doctor.cs:line 240
   ```

   `ReadContent` bağlantısını `Doctor.cs:610`'da açıyor ve `:628`'de kullanıyor; arada başka bir iş
   parçacığının süreç-geneli havuz temizliği tutamacı aldı. **Bu, apex'in reddettiği ikinci çıkarımı
   ölçümle karşılıyor: sebep ürün kodunda da yaşıyor.**

### 2.3 Ne kurulamadı — bunu yeşile boyamıyorum

**Bağımsız, deterministik bir asgari senaryo üretilemedi.** Denenen ve *hepsi temiz çıkan* koşullar
(`kar1-kanit/probe-01.txt`, `probe-02.txt`):

| Sonda | Kurgu | Sonuç |
|---|---|---|
| P1 | aynı iş parçacığı: aç → kullan → `ClearAllPools()` → kullan | istisna yok |
| P2 | bir iş parçacığı bağlantıyı açık tutup 400 sorgu; ikincisi `ClearAllPools()` döngüsü | istisna yok |
| P3 | aç/kapa döngüsü × `ClearAllPools()` döngüsü | istisna yok |
| P4 | aynısı `Pooling=False` ile | istisna yok |
| P5 | kurban başka bir veritabanında | istisna yok |
| P6 | 6 iş parçacığı gerçek `State` açıp kapatırken `clear`×`GC` matrisi (4 kol) | **dört kolda da** istisna yok |

Yani "eşzamanlı `ClearAllPools()`, kullanımdaki bir bağlantıyı öldürür" cümlesini **istediğim anda**
tetikleyemedim. Kol karşılaştırması (M7) da tek başına kesmiyor: `clear` 14/40, `spin` 8/40 — fark
yönü doğru ama bu örneklem büyüklüğünde **istatistiksel olarak ayırt edici değil** (Fisher kesin testi,
çift yönlü **p = 0,21**),
ve `spin` kolunun tabandan (4/30) yüksek çıkması, saldırganın etkisinin bir kısmının **yalnızca zamanlama
ve CPU baskısı** olduğunu söylüyor. `ClearAllPools()` gerekli bir bileşen **değil**: paketin kendi 114
çağrısı zaten yetiyor.

Kurulamayan son halka şudur: `Leaked` ölçütü dış `SqliteConnection`'a **zayıf** başvuru tuttuğu için,
hâlâ kullanılmakta olan bir bağlantının dış nesnesinin hangi koşulda toplanabilir hâle geldiği. Bunu
dosyalardan okuyarak değil, ancak havuzun içine bakan bir ölçümle kurmak gerekir; bu şeridin kapsamı
dışında ve `src/**` dokunulmaz olduğu için burada yapılmadı.

---

## 3. Paralellik ayarı — ölçüldü, varsayılmadı

Apex "hangisi hem yerel hem CI tarafından okunuyorsa; **ölç, varsayma**" dedi. Ölçüm, iki xunit
koleksiyonu (iki ayrı test sınıfı) ve paylaşılan bir tepe-eşzamanlılık tanığıyla yapıldı: her test
girişte bir sayacı artırıp 800 ms bekliyor, çıkışta azaltıyor; ikisi de gördükleri tepeyi bildiriyor.

| Aday | Sonuç | Hüküm |
|---|---|---|
| Ayar yok (bugünkü hâl) | **tepe 2**, duvar saati 805 ms | paralellik açık |
| `default.runsettings` içine `<xUnit><ParallelizeTestCollections>false</...>` | **tepe 2**, 818 ms | **okunmuyor** |
| `tests/Oom.Tests/xunit.runner.json` + csproj'a `<Content Include=... CopyToOutputDirectory="PreserveNewest" />` | **tepe 1**, 1 s | **çalışıyor** |

Yani xunit belgelerinin tarif ettiği RunSettings `<xUnit>` düğümü, buradaki
runner.visualstudio **2.8.2** ile **işlemiyor**. Alt ajanın belgelerden çıkardığı öneri bu noktada
ölçümle çürüdü; şeride giren çözüm `xunit.runner.json`'dır.

İki yan ölçüm, ileride yanlış sayı yazılmasın diye:

- **`xunit.runner.json` kendiliğinden kopyalanmıyor.** `xunit.core/2.9.2` ve
  `xunit.runner.visualstudio/2.8.2` paketlerinin `build/*.props|targets` dosyalarında `runner.json`
  geçmiyor. `Content` kalemi olmadan dosya depoda durur ve **kimse okumaz**.
- **Komut satırındaki `-- xunit.parallelizeTestCollections=...` de işlemiyor.** `xunit.runner.json`
  yerindeyken `-- xunit.parallelizeTestCollections=true` geçildiğinde tanık yine **tepe 1** ölçtü.
  Bu, apex'in brifingindeki "paralellik kapalı: `-- xunit.parallelize...=false`" ölçümlerinin
  bayrağın *etkisiyle* değil, muhtemelen rastlantıyla yeşil olduğunu düşündürür.
- **`--filter`, runsettings'teki `TestCaseFilter`'ı daraltmaz, değiştirir.** `--filter` verilen bir
  koşumda `Category!=IntentionalRed` **uygulanmaz** ve `IntentionalRed · CI çıkış kodu tohumu` kırmızı
  olur. Bu, bu şeritte bir ölçümü bir kez bozdu (`kar1-kanit/amp/`, geçersiz sayıldı ve saklandı).

`tests/Oom.Tests/default.runsettings` **değiştirilmedi**: gerekmedi, filtre olduğu gibi duruyor.

> ⚠ Apex'in şerhi, birebir: **bu bir test izolasyonu tedbiridir; ürün kusurunun çözüldüğü anlamına
> gelmez.** `oom doctor`, `Program.Health` açık bir `State` tutamacı tutarken aynı süreçte aynı
> süreç-geneli temizliği koşturmaya devam ediyor ve hiçbir koşucu ayarı oraya erişmez.

---

## 4. Eşzamanlılık testleri — `Y-290`, `Y-291`

`tests/Oom.Tests/EszamanliTemizlikTests.cs`. İkisi de **kendi eşzamanlılığını kurar**; sınıfların seri
koşması bu testlerin içindeki eşzamanlılığı kaldırmaz.

**`Y-290` — aynı süreç, açık bağlantı × `Doctor` temizliği.** Ana iş parçacığı, taranan profilin
**dışındaki** bir veritabanında bir `State` tutamacını açık tutup 3 saniye boyunca sorgular; ikinci bir
iş parçacığı aynı anda `Doctor.InspectStateRoots(localAppData)` koşturur — yani `Doctor.cs:250`'deki
süreç-geneli `ClearAllPools()`'u. Test, pencerenin gerçekten açıldığını da ölçer (`scans > 0`,
`queries > 0`); iki iş parçacığından **hangisi ölürse** kırmızı verir.

**`Y-291` — iki ayrı süreç, aynı izole kök.** İki `oom doctor --json --vault …` süreci bir bariyerle
aynı anda salınır ve aynı iki köklü profili okur. Pinlediği sessiz kusur şudur: ikizinin tuttuğu
koruma yüzünden kökü açamayıp onu `ölçülemedi`/`okunamıyor` diye yazan bir tarama. Ölçüm: her iki süreç
de `2 durum kökü · … · okunamıyor 0 · ölçülemedi 0` veriyor ve **iki hüküm birebir aynı** —
derece 1, 2 ve 4'te de aynı (M5). Ayrıca profilin baytları değişmiyor ve `--fix` olmadan durum kökü
yaratılmıyor.

Hiçbiri gerçek profile dokunmaz: taranan her kök testin kendi geçici dizininde kurulur, tarama oraya
açıkça yönlendirilir (`InspectStateRoots(localAppData)`, süreç için `OOM_LOCALAPPDATA`) ve **`--fix`
hiç geçilmez**.

### `Y-291`'in bilerek iddia *etmediği* şey

`oom doctor`, taradığı her kökün baytı baytına kopyasını `%TEMP%\oom-doctor-*` altında **bırakıyor**.
İlk yazdığım hâlde bunu assertion yapmıştım ve test kırmızı verdi; sonra ölçtüm: **tek, eşzamanlı
olmayan** bir `oom doctor` de aynı iki köklü fikstürde 1–3 kopya bırakıyor (M5). Yani bu bir
eşzamanlılık özelliği değil, düz bir ürün kusurudur — `StateRootSnapshot.Dispose`'daki `Delete`'in
sessiz `catch`'i. `src/**` dokunulmaz olduğu için **adlandırıldı ve yamaya konuldu**, assertion'la
örtülmedi. Test yine de kendi yarattığı kopyaları sayıp siler.

D3'ün "çağrı kaldırılınca 66 dizin kalıyor" ölçümüyle çelişmez: çağrı **varken de** sıfır değil,
sadece daha az.

---

## 5. Kasıtlı bozma kanıtı

`kar1-kanit/bozma.sh` üç bozmayı sırayla uygular, derler, koşar ve geri alır. Üçü de **derlendi** ve
**amaçlanan** assertion'a ulaştı:

| Bozma | Ne bozuldu | Düşen assertion | Ham çıktı |
|---|---|---|---|
| 1 | `Y-290`'da tutamaç 5. sorgudan sonra `Dispose` edilir | `açık durum tutamacı, 1 tarama süresince 5 sorgudan sonra öldü: …` (satır 116) | `bozma/bozma-1-y290.txt` |
| 2 | `Y-291`'de parmak izi alındıktan sonra bir köke 1 bayt yazılır | `Assert.Equal() Failure: Dictionaries differ` — `155648` → `155649` (satır 214) | `bozma/bozma-2-y291-bayt.txt` |
| 3 | `Y-291`'de beklenen hüküm `2 durum kökü` → `3 durum kökü` | `Assert.Contains() Failure` (satır 205) | `bozma/bozma-3-y291-hukum.txt` |
| — | üçü de geri alındı | `Başarılı! … Başarılı: 2, Toplam: 2` | `bozma/duzeltme-sonrasi.txt` |

**Aralıklı kusur için, apex'in sorduğu zor kısım:** `Y-290`'ın *gerçek* kırmızıyı yakaladığı sentetik
bir bozmayla değil, **canlı bir yakalamayla** gösterildi — M8'deki koşumda `Y-290`'ın `scanError`
assertion'ı, gerçek `ObjectDisposedException` ile ve üretim çerçevesinde düştü
(`kar1-kanit/arms2/clear/run-010.txt`). Ama bunu **istediğim anda üretemiyorum**: `Y-290` ancak
saldırgan bir iş parçacığı ve paralellik açıkken, 20 koşumda 1 kez kırmızı verdi. Deterministik değil,
ve öyleymiş gibi yazmıyorum.

**Bunun bedeli açıkça söylenmeli:** `Y-290` ürün bugünkü hâliyle kaldıkça, düşük olasılıkla CI'da
kırmızı verebilir. Verdiğinde yanlış alarm olmayacak — gerçek bir ürün tehlikesini bildirecek. Bunu
gizlemek için assertion'ı zayıflatmadım.

---

## 6. 20 ardışık tam koşum

Aday ağaçta, birleşik hâlde, `dotnet test Oom.sln -c Release --nologo` ile. Paralellik: **KAPALI**
(`xunit.runner.json`). Ham çıktı: `kar1-kanit/yirmi/run-01.txt` … `run-20.txt`, özet
`kar1-kanit/yirmi/summary.txt`. **Hiçbir koşum tekrarlanmadı, hiçbir sonuç atılmadı.**

| Koşum | Toplam | Geçti | Kırmızı | `ObjectDisposedException` | Kırmızı olan |
|---|---|---|---|---|---|
| `run-01` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-02` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-03` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-04` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-05` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-06` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-07` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-08` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-09` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-10` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-11` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-12` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-13` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-14` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-15` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-16` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-17` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-18` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-19` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |
| `run-20` | 261 | 260 | 1 | **0** | `Y-125` (beklenen) |

**Toplam: 20 koşumun 20'sinde aynı sonuç** — 261 test, 260 geçti, tek kırmızı `Y-125`, 
`ObjectDisposedException` **hiç yok**. Paralellik açıkken ölçülen aralıklı kırmızı (M3: 30'da 4) 
bu 20 koşumun hiçbirinde görülmedi.

> **Bu 20/20 mutlak kararlılık garantisi değildir.** Taban kusur oranı paralellik açıkken ölçülen
> `4/30`'du; 20 temiz koşum, aynı kusurun kapalı ayarla *bu makinede, bu yükte* görülmediğini söyler,
> daha fazlasını değil.

---

## 7. Adlandırılan, düzeltilmeyen ürün kusurları

`src/**` dokunulmaz. Yama önerisi: `denetim/astra-dalga-2/KAR1-yama-onerisi.patch`
(`git apply --check -p1` ile temiz uyguluyor; **uygulanmadı ve ölçülmedi**).

1. **Süreç-geneli temizlik yarıçapı.** `Doctor.cs:250` ve `Doctor.cs:504`, tek bir taramanın/dizinin
   tutamaçlarını bırakmak için sürecin **bütün** havuzlarını siliyor. `Program.cs:415-446` (`Health`)
   bu taramayı, açık bir `State` tutamacını elinde tutarken aynı iş parçacığında çağırıyor.
   Öneri: taramanın teşhis bağlantıları `Pooling=False` ile açılsın, iki süreç-geneli çağrı kalksın.
2. **Sahibin veritabanının kopyaları geçici dizinde kalıyor** (M5). `StateRootSnapshot.Dispose` →
   `Delete` başarısızlığı sessizce yutuyor.
3. **`State.Scalar` kilitsiz.** `State.cs:492-497` `_connection`'ı `_gate` almadan kullanıyor; aynı
   sınıftaki `Text` (`:503-507`) alıyor. `State`, `WriteHealthConcurrently`'de (`State.cs:168-173`)
   `Parallel.ForEach` içinden kullanılıyor ve `SqliteConnection` iş parçacığı güvenli değil.
   Bu şeritte bir kırmızıya **bağlanamadı**; yine de aynı ailenin kusuru olduğu için adlandırılıyor.

---

## 8. Ortam hijyeni

- Sahibin kasası `E:\OdenaOS v2`'ye **hiçbir komut koşulmadı**.
- `oom doctor` **gerçek profile karşı hiç koşturulmadı**; `--fix` hiç geçilmedi. Her koşum
  `OOM_LOCALAPPDATA` ile fikstüre yönlendirildi.
- Gerçek `%LOCALAPPDATA%\oom` kök sayımı: şerit başı **92** (`kar1-kanit/roots-baseline.txt`),
  şerit sonu ham **128**. Aradaki **36** kökün tamamı bugünün tarihli, 155 648 baytlık fikstür
  şeklindeydi; **yalnız o 36'sı** silindi ve kapanış listesi tabanla **birebir aynı** çıktı
  (`diff roots-baseline.txt roots-final.txt` boş). Tabandaki 92 kökten hiçbiri kaybolmadı,
  başkasının köküne dokunulmadı. Bu 36 kökü yaratan bu şerit değil, **paketin kendisi**: her tam
  koşum gerçek profile kök bırakıyor.
- `%TEMP%\oom-doctor-*`: kapanışta kalan 1 (boş) dizin silindi.
- Adlandırılıp **dokunulmayan** ayrı bir hijyen gözlemi: `%TEMP%` altında `oom-scar-*` fikstür
  dizinleri birikiyor — 10 Eyl 73, 11 Eyl 1835, 12 Eyl 1920. Çoğu bu şeride ait değil ve başka
  şeritler koşuyor olabileceği için silinmedi.

---

## 9. OWNS farkı

| Dosya | Durum |
|---|---|
| `tests/Oom.Tests/xunit.runner.json` | **yeni** — paralellik ayarı (ölçülerek seçildi) |
| `tests/Oom.Tests/Oom.Tests.csproj` | `+15` — `Content` kalemi ve neden-gerekçesi |
| `tests/Oom.Tests/EszamanliTemizlikTests.cs` | **yeni** — `Y-290`, `Y-291` |
| `denetim/astra-dalga-2/KAR1.md` | **yeni** — bu belge |
| `denetim/astra-dalga-2/KAR1-yama-onerisi.patch` | **yeni** — uygulanmadı |
| `tests/Oom.Tests/default.runsettings` | **değiştirilmedi** (gerekmedi) |

`src/**`, `docs/scars.md`, diğer testler, `bench/**`, `.github/**`: **dokunulmadı**.
`Y-125` kırmızısı beklenen: yeni `Y-290`/`Y-291` `docs/scars.md`'de satır bulamıyor ve o dosya
bu şeridin kapsamı dışında.
