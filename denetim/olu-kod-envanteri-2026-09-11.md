# Ölü Kod Envanteri — 2026-09-11

## 0. Bu belgenin kanıt niteliği

`bench/PROVENANCE.md`'nin dört parçalı kanıt demeti bu belgeye de uygulanıyor —
şema `bench/results/*.json` için yazılmış olsa da kural aynı: kim, hangi komut,
hangi ham artefakt, hangi commit.

- **kim ölçtü:** claude (bu şerit — `oom-w-SLIM`, LANE: SLIM)
- **hangi komut:** aşağıdaki §1'de listelenen grep/okuma taramaları; hepsi bu
  oturumda, bu ağaçta çalıştırıldı
- **hangi ham artefakt:** yok, ayrı bir ham günlük tutulmadı — **bu belgenin
  kendisi artefakttır.** Her aday için alıntılanan dosya:satır aralığı, bizzat
  açılıp doğrulanmış hâliyle burada duruyor; ayrı bir kanıt dosyasına işaret
  etmiyorum çünkü öyle bir dosya yok.
- **hangi commit:** `9c8964ad6e5343610da1e7c56a3071be972fee05` (bu worktree'nin
  HEAD'i, "clean")
- **run_status:** ok — hiçbir arama hata vermedi, hiçbir aday kısmi/kesik
  taramayla işaretlenmedi
- **substitute:** yok (`active: false`) — spesifikasyonun istediği taramanın
  yerine başka bir veri seti veya araç kullanılmadı

## 1. Yöntem

İki aşama:

**Aşama 1 — geniş tarama (mekanik).** `src/Oom/**/*.cs` içindeki her
`private`/`internal`/`public` üye (metot, alan, tip) regex ile çıkarıldı; her
sembol adı için:
- `private` üyeler: yalnız **kendi dosyası** içinde `\bad\b` geçiş sayısı
  sayıldı (bir private üye başka dosyadan çağrılamaz).
- `internal`/`public` üyeler: `src/`, `tests/`, `bench/`, `docs/`, `.github/`
  toplamında `\bad\b` geçiş sayısı sayıldı.
- Sayaç ≤ 1 olan (yani yalnız kendi bildirim satırı) her sembol aday listesine
  girdi.

İlk deneme kırık bir regex'le (`[A-Za-z0-9_<>,\[\]\? ]` köşeli parantez
kaçışı POSIX karakter sınıfında geçersiz) "0 aday" üretti — bu, kendi
başına, bu belgenin önlemeye çalıştığı hatanın küçük bir örneğiydi: boş bir
tarama sonucu "temiz" ile karıştırılabilirdi. Regex düzeltilip taramalar
tekrarlandı; sonuçlar aşağıdadır.

**Aşama 2 — düşmanca doğrulama (elle).** Aşama 1'in ürettiği her aday için
tek tek:
1. `src/` içinde gerçek bir çağrı yeri var mı (grep + okuma)
2. `tests/` içinde bir çağrı yeri var mı
3. Sembolün ait olduğu tip bir arayüz (`interface`) uyguluyor mu, sembol o
   arayüzün bir üyesiyle imza eşleşiyor mu
4. Sembol `virtual`/`override` mi
5. `nameof(Ad)` veya `"Ad"` biçiminde bir dize-tabanlı/yansıma (reflection)
   referansı var mı — `.cs`, `.json`, `.yml`, `.yaml`, `.csproj` genelinde
6. `.csproj`, `oom.json` şeması, `.github/workflows/*.yml` içinde ada dair bir
   iz var mı

Bu altı kontrolün hepsini geçemeyen aday PROBABLE'a düşürüldü; hepsini
geçen CERTAIN kaldı. Aşağıda her adayın hangi kontrolleri geçtiği ayrı ayrı
yazılı — "kontrol ettim, hiçbir şey bulamadım" tek cümleyle geçiştirilmedi.

**Satır sayımı üç eksenli:** total (aralıktaki her satır) / non-blank (boş
satır hariç) / non-blank-non-comment (boş ve yalnızca `//`, `/*`, `*` ile
başlayan satırlar hariç). `bash`+`sed`+`grep -c` ile üretildi, elle
çarpraz kontrol edildi.

## 2. CERTAIN — hiçbir referans bulunamadı

### C-1 — `HookTemplates.Duplicates`

- **Dosya:satır:** `src/Oom/Flush/HookTemplates.cs:42-57` (42-45 XML doc, 46-57 gövde)
- **Sembol:** `public static IReadOnlyList<string> Duplicates(string, string)`
- **Satır:** total 16 / non-blank 15 / non-blank-non-comment 11
- **Kanıt:** `grep -rn "Duplicates" .` (uzantı filtresiyle `.cs .md .json .yml
  .yaml`, `.git` hariç) yalnız bildirim satırını (46) döndürdü. `src`, `tests`,
  `bench`, `docs`, `.github` — hepsi tarandı, sıfır çağrı yeri.
  `HookTemplates` statik bir sınıf, arayüz uygulamıyor; `Duplicates`
  `virtual`/`override` değil. `nameof(Duplicates)` ve `"Duplicates"` dize
  taraması boş.
- **İlginç olan:** metodun kendi XML belgesi "the report belongs to doctor"
  diyor — yani Doctor'ın onu çağırması *tasarlanmış*. Ama
  `Doctor.ValidateHooks` (`src/Oom/Doctor/Doctor.cs:55-79`) aynı
  yinelenen-hook denetimini kendi elinde, bağımsız olarak yeniden yazmış
  (satır 66-68, `GroupBy` ile). İki ayrı uygulama var; biri (Doctor'ınki)
  yaşıyor, öteki (`Duplicates`) hiç çağrılmıyor.
- **Silinirse ne kırılır:** hiçbir şey — canlı davranış zaten
  `Doctor.ValidateHooks` içinde, bu metoda hiç uğramıyor.

### C-2 — `Ingest.DailySuffix`

- **Dosya:satır:** `src/Oom/Ingest/Ingest.cs:102-106`
- **Sembol:** `public string DailySuffix(string source)`
- **Satır:** total 5 / non-blank 5 / non-blank-non-comment 5
- **Kanıt:** `grep -rn "DailySuffix" src tests bench docs .github` yalnız
  bildirim satırını döndürdü. `Ingest` arayüz uygulamıyor (yalnız
  `IIngestStateStore`'u iç sınıfı `MemoryIngestStateStore` uyguluyor,
  `DailySuffix` onun üyesi değil). `virtual`/`override` yok. `nameof`/dize
  taraması boş.
- **İlginç olan:** üretmeye çalıştığı dize (`", içe aktarım:{source}"`)
  `src/Oom/Flush/Flush.cs:333`'te birebir aynı biçimde, doğrudan satır içinde
  yeniden yazılmış: `FlushReason.Ingest => $", içe aktarım:{session.Source}"`.
  `DailySuffix` üretilip sonra terk edilmiş; canlı yol onu hiç görmüyor.
- **Silinirse ne kırılır:** hiçbir şey — `Flush.cs:333` bağımsız kopyasını
  zaten kullanıyor.

### C-3 — `Notes.IndexTokens`

- **Dosya:satır:** `src/Oom/Notes/Notes.cs:144-145`
- **Sembol:** `public IReadOnlyList<string> IndexTokens(Note note)`
- **Satır:** total 2 / non-blank 2 / non-blank-non-comment 1
- **Kanıt:** `grep -rn "IndexTokens" src tests bench docs .github` yalnız
  bildirim satırını döndürdü. `Notes` sınıfı arayüz uygulamıyor;
  `virtual`/`override` yok; dize/`nameof` taraması boş.
- **İlginç olan:** `Retrieve.cs` indeksi alan bazlı ağırlıklandırıyor
  (Y-040'ın düzeltmesi — bkz. `docs/scars.md`), `title`/`aliases`/`tags`/`body`
  alanlarını ayrı ayrı `_fold.TokenizeAll(...)` ile tokenluyor
  (`src/Oom/Retrieve/Retrieve.cs:411-415`), `Notes.IndexTokens`'ın tek-blok
  `IndexableText` birleştirmesini hiç çağırmıyor. `IndexTokens` Y-040'tan
  *önceki* tasarımın kalıntısı gibi duruyor.
- **Silinirse ne kırılır:** hiçbir şey — arama indeksi zaten
  `Retrieve.cs`'in kendi alan-bazlı tokenluyucusunu kullanıyor.

### C-4 — `SessionStore.RecordQuota` (bkz. State.cs — partial class)

- **Dosya:satır:** `src/Oom/State/SessionStore.cs:153-157`
- **Sembol:** `public void RecordQuota(DateTimeOffset now, QuotaWindow window)`
  (`public sealed partial class State`'in bir parçası)
- **Satır:** total 5 / non-blank 5 / non-blank-non-comment 4
- **Kanıt:** `grep -rn "RecordQuota" src tests bench docs .github` yalnız
  bildirim satırını döndürdü. `State` `IDisposable` ve `IIngestStateStore`
  uyguluyor; `RecordQuota`'nın imzası (`DateTimeOffset, QuotaWindow → void`)
  ikisinin de üyesiyle eşleşmiyor — arayüz dispatch'i değil. `virtual`/
  `override` yok, dize/`nameof` taraması boş.
- **İlginç olan (bkz. §3'te ayrıca `Notify` bulgusu):** `kota` tablosuna tek
  yazan yer burası (`INSERT INTO kota(...)`, `State.cs:506`'daki şemaya göre)
  ama hiçbir yerden çağrılmıyor. Buna karşılık `SessionStore.ReadStatusLine`
  (`SessionStore.cs:106-116`) aynı `kota` tablosundan `SELECT ... ORDER BY ts
  DESC LIMIT 1` ile okuyor ve bulamazsa "kota bilinmiyor" basıyor —
  yani context satırındaki kota göstergesi bu kod bu hâldeyken **hep**
  "kota bilinmiyor" diyecek, çünkü tabloyu dolduracak tek yazıcı hiç
  tetiklenmiyor. `State.cs`'te `ReadQuota`/`BuildQuotaRequest` de var (kota
  okuma tarafı, `KotaScars.cs` testleriyle iyi kapsanmış — Y-052..Y-057) ama
  onlar da yalnız testlerden çağrılıyor, üretim koduna hiç bağlanmamış.
  `ReadQuota`/`BuildQuotaRequest` bu yüzden CERTAIN listesine girmiyor —
  testte çağrıları var, brief'in ölçütüyle "referans yok" değiller — ama
  `RecordQuota`'nın tam tersine hiçbir çağrısı yok, o yüzden yalnız o burada.
- **Silinirse ne kırılır:** bugün hiçbir şey — zaten hiç çalışmıyor. Ama
  `kota` tablosunu doldurabilecek **tek** kod yolu budur; silinirse, kota
  gösterimini canlıya bağlamak isteyen biri bu mantığı sıfırdan yazmak
  zorunda kalır.

### C-5 — `State.IsStamped`

- **Dosya:satır:** `src/Oom/State/State.cs:319-321`
- **Sembol:** `public bool IsStamped(string path, string mtime, long size)`
- **Satır:** total 3 / non-blank 3 / non-blank-non-comment 2
- **Kanıt:** `grep -rn "IsStamped" src tests bench docs .github` yalnız
  bildirim satırını döndürdü. Arayüz üyesi değil, `virtual`/`override`
  değil, dize/`nameof` taraması boş.
- **İlginç olan:** `SweepRun.cs:96-97` "değişmemiş dosyayı yeniden açma"
  kuralını *farklı* bir üye ile uyguluyor:
  `_state?.ReadStamp(candidate.Path)` (`SessionStore.cs:86-93`, `(string
  Mtime, long Size, string Outcome)?` döndüren tuple sürümü), `State.
  IsStamped`'ı değil. Aynı `sweep_stamps` tablosunu okuyan, aynı işi yapan,
  farklı imzalı iki metot var; yalnız biri gerçekten çağrılıyor.
- **Silinirse ne kırılır:** hiçbir şey — `SweepRun.cs` zaten
  `SessionStore.ReadStamp`'ı kullanıyor.

### C-6 — `Compile.ResolveLock` + `CompileLock` (eşleşen çift)

- **Dosya:satır:**
  - `src/Oom/Compile/Compile.cs:8-9` — `CompileLock` kaydı
  - `src/Oom/Compile/Compile.cs:332-340` — `ResolveLock` metodu
- **Sembol:**
  - `public sealed record CompileLock(string Machine, int Pid, DateTimeOffset Timestamp)`
  - `internal static string ResolveLock(CompileLock? existing, DateTimeOffset now, Func<int, bool> processAlive)`
- **Satır:** `CompileLock` total 2 / non-blank 2 / non-blank-non-comment 1;
  `ResolveLock` total 9 / non-blank 9 / non-blank-non-comment 8;
  **birlikte** total 11 / non-blank 11 / non-blank-non-comment 9
- **Kanıt:** `grep -rn "ResolveLock" src tests bench docs .github` yalnız
  bildirim satırını (333) döndürdü. `grep -rn "CompileLock" ...` yalnız iki
  yer döndürdü: kaydın kendi bildirimi (9) ve `ResolveLock`'ın parametre tipi
  (333) — yani `CompileLock`'ın **hiçbir** `new CompileLock(...)` çağrısı
  yok (`grep -rn "new CompileLock" src tests` boş). `Compile` sınıfı arayüz
  uygulamıyor; ikisi de `virtual`/`override` değil; dize/`nameof` taraması
  boş.
- **İlginç olan:** `ResolveLock`'ın kendi XML belgesi tam olarak Y-033'ün
  konusunu anlatıyor ("A lock row older than two hours whose owning process
  is gone is taken over"), ama Y-033'ün gerçek düzeltmesi başka bir yerde:
  `State.AcquireLock` (`State.cs:201-241`), `locks` tablosunu doğrudan okuyup
  (satır 210) kendi bayatlık/devralma mantığını uyguluyor, `LockResult`
  döndürüyor — ve `DerleyiciScars.Y033_CrossMachineCompileLockAllowsSinglePublisher`
  testiyle kapsanmış. `Compile.cs` içinde `AcquireLock`'a hiç referans yok
  (`grep -n "AcquireLock" src/Oom/Compile/Compile.cs` boş) — yani derleyici
  şu an *hiçbir* çapraz-makine kilit çağrısı yapmıyor; `State.AcquireLock`
  nerede çağrılıyorsa oradan (bu belge onu araştırmadı, kapsam dışı) ya da
  hiç çağrılmıyor. `ResolveLock`/`CompileLock` bu ikinci, yaşamayan
  tasarımın kalıntısı.
- **Silinirse ne kırılır:** hiçbir şey — hiçbir canlı yol bu ikisine
  uğramıyor.

## 3. PROBABLE — bir referans var, ama davranışa bağlanmıyor

### P-1 — `OomSettings.Toast` (config key)

- **Dosya:satır (üç ayrı nokta, tek anahtar):**
  - `src/Oom/Infrastructure/Configuration.cs:41` — `bool Toast,` (kayıt alanı)
  - `src/Oom/Infrastructure/Configuration.cs:122` — `notify = new { toast = defaults.Toast },` (yazma/serileştirme)
  - `src/Oom/Infrastructure/Configuration.cs:147` — `Flag(Section(root, "notify"), "toast", defaults.Toast),` (okuma/ayrıştırma)
- **Sembol:** `oom.json`'daki `notify.toast` anahtarı → `OomSettings.Toast`
- **Satır:** üç nokta da tek satır, hepsi non-blank, hepsi non-comment →
  total 3 / non-blank 3 / non-blank-non-comment 3
- **Kanıt:** `grep -rn "\bToast\b" src --include=*.cs` üç sonuç verdi —
  yukarıdaki üçü. Bunların dışında **hiçbir** üretim kodu `settings.Toast`'ı
  okumuyor; `WindowsNotifier.Toast(string line)` adı çakışan ama tamamen
  ilgisiz bir private metot (toast'ın *gösterilip gösterilemeyeceğine*
  `ShortcutRegistration.IsRegistered()` ve Windows sürüm kontrolüyle karar
  veriyor, `settings.Toast`'a hiç bakmıyor —
  `src/Oom/Notify/WindowsNotifier.cs:47-66`). Neden CERTAIN değil: bir test
  (`tests/Oom.Tests/ConfigurationUpgradeTests.cs:28`,
  `Assert.False(settings.Toast)`) değeri okuyor — ama yalnız JSON
  round-trip'in doğru ayrıştığını doğruluyor, anahtarın herhangi bir
  davranışı tetiklediğini değil. Yani "hiçbir referans yok" değil, ama
  "davranışa erişen bir referans yok" — PROBABLE'ın tam tanımı: erişebilecek
  tek yer, anahtarın kendi round-trip testi.
- **Silinirse ne kırılır:** `ConfigurationUpgradeTests.cs:28`'deki iddia
  (derlenmez, güncellenmesi gerekir). Davranışsal hiçbir şey kırılmaz —
  zaten hiçbir kod dalı bu değere bakarak farklı davranmıyor.

## 4. Değerlendirilip reddedilen — yanlış pozitifler

Bunlar aday listesine **girmedi**; neden dışarıda bırakıldıkları da kanıt:

- **`DetachedProcess.StartupInfo`/`ProcessInformation` alanları**
  (`lpTitle`, `dwFlags`, `cbReserved2`, `hStdError`, `dwThreadId` —
  `src/Oom/Infrastructure/DetachedProcess.cs:20-33`): mekanik tarama bunları
  "tek referans" diye işaretledi, ama bunlar `[StructLayout(LayoutKind.
  Sequential)]` ile işaretli, `CreateProcessW`'a `ref` geçirilen P/Invoke
  struct alanları. Yönetilen kod onları adla okumasa da native tarafın
  struct'ı doğru boyutta/sırada görmesi için gerekiyorlar
  (`Marshal.SizeOf<StartupInfo>()`); silinmesi marshaling'i bozar. Ölü kod
  değil, yapısal zorunluluk.
- **`src/Oom/Contracts/LaneA.cs` (0 bayt), `LaneB.cs`, `LaneC.cs`,
  `LaneD.cs`** (1-3 satır, yalnız `namespace` bildirimi ve şerit-taşıma
  yorumu): kod içermiyorlar — silinecek bir "sembol" yok, silinecek bir
  şey de yok. Üç metriğin hepsinde zaten sıfıra yakın katkıları var (`LaneA.
  cs` tam sıfır). Envanterin "ölü kod" tanımına girmiyorlar; iskelet
  belgeleme dosyaları.

## 5. En ilginç bulgu — aday olamayan `Notify` sınıfı

`src/Oom/Notify/Notify.cs` (57 satır) tam, test edilmiş, `docs/
architecture.md:66-68`'de belgelenmiş bir bildirim sınıfı: sınıf bazında
7 günlük tekrar-engelleme (`ConcurrentDictionary`), izinli bildirim sınıfı
listesi (`ToastClasses`), toast kaydı kontrolü. Tek çağrıcısı
`tests/Oom.Tests/Scars/KotaScars.cs:49` (`new Notify().Send(...)`) — **hiçbir
üretim kodu onu hiç kurmuyor** (`grep -rn "new Notify(" src` boş).

Üretim yolu (`Compile.cs:437/466/476`, `Flush.cs:271/398`) doğrudan
`INotifier.Notify(string)` arayüzünü çağırıyor; bu her zaman
`WindowsNotifier` (`Program.cs:489,607`) — ve `WindowsNotifier` kendi
**bağımsız** 7 günlük tekrar-engellemesini `state.Scalar(...notified...)`
üzerinden zaten uyguluyor (`WindowsNotifier.cs:68-77`). `Notify` sınıfının
tüm mantığı bir ikinci, paralel, hiç çalışmayan uygulama.

Bu, brief'in uyardığı tam tuzak: mekanik tarama "bir çağrısı var" diyip
temize çıkarıyor (test onu çağırıyor), ama üretimde çalışan program bu
sınıfa hiç uğramıyor. Brief'in "no callers" tanımı testleri de sayıyor,
o yüzden `Notify` CERTAIN/PROBABLE listesine giremiyor — teknik olarak ölü
değil. Ama sahibinin bilmesi gereken şey şu: `docs/architecture.md`'nin
"`Notify`... persistent `notified` storage... planned" notu artık yanlış —
kalıcı depolama planlanmadı, *başka bir sınıfta* (`WindowsNotifier`) zaten
yazıldı, `Notify`'ın kendisi hiç geliştirilmeden askıda kaldı. `OomSettings.
Toast` (P-1) da aynı resmin parçası: `Notify.ToastClasses` sabit listesi de,
`settings.Toast` bayrağı da, gerçek toast kararını hiç etkilemiyor — o karar
tamamen `WindowsNotifier.Toast()`'ta, `ShortcutRegistration.IsRegistered()`
ve Windows sürümüne bakarak veriliyor.

## 6. Özet tablo

| Grup | Aday sayısı | total | non-blank | non-blank-non-comment |
|---|---|---|---|---|
| CERTAIN (C-1..C-6) | 6 sembol (7 bildirim: `CompileLock`+`ResolveLock` çift) | 42 | 41 | 32 |
| PROBABLE (P-1) | 1 (3 dokunma noktalı tek config anahtarı) | 3 | 3 | 3 |
| **Toplam** | **7** | **45** | **44** | **35** |

Erken şeridin iddiası "5 ölü kod adayı, 56 satır" idi ve **hiçbir dosya
üretmemişti** — denetimin "ARTIFACT YOK" tespiti buradan geliyordu. Bu
ölçüm o iddiayı yeniden üretmeye çalışmadı; bu ağaç üzerinde sıfırdan
ölçtü. Sonuç 56 ile **uyuşmuyor**: en gevşek eksende (total) bile 42
(yalnız CERTAIN) / 45 (CERTAIN+PROBABLE) çıkıyor, 56 değil. Fark
mutabakat edilemez — önceki iddianın ne saydığına (hangi 5 aday, hangi
satır tanımı, hangi commit) dair hiçbir kayıt yok. Bu belgenin var oluş
nedeni tam olarak bu: bir sayı, açılabilir bir kanıt olmadan bir iddiadır.

## 7. Sahibin önündeki karar

Onaylanırsa sahip şunu onaylamış olur: yukarıdaki 6 CERTAIN sembolü (42/41/32
satır) — hepsi kanıtlı biçimde başka, yaşayan bir uygulama tarafından
gölgelenmiş veya hiç bağlanmamış kod parçaları — depodan silmek, ve
`OomSettings.Toast` anahtarını (P-1, 3 satır) `oom.json` şemasından ve tek
testinden çıkarmak; hiçbiri bugünkü davranışı değiştirmez, ikisi de
(`RecordQuota`'nın açtığı `kota` yazma yolu ve `Notify`/`Toast` çiftinin
işaret ettiği bildirim mimarisi) gelecekte birinin baştan yazmak zorunda
kalacağı bir boşluk bırakır.
