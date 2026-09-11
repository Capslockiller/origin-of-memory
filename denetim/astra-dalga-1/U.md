# Şerit U — Uninstall yalnız sahipliği doğrulanmış hedeflere müdahale eder

## taban_sha · commit

- Taban: `021cc83` (ayrık HEAD, temiz), worktree `E:\OdenaWorks\10-Aktif\oom-w-U`
- Commit: bu worktree'nin HEAD'i, konu satırı `2.1(FIX): prove ownership before uninstall touches anything` (tek commit, push yok, merge yok). Sha burada yazılmıyor: rapor commit'in içinde, amend edilen sha kendi kaydını yalanlar.
- Delege edilmedi: OWNS listesindeki üç üretim dosyası aynı çağrı zincirini paylaşıyor, ayrık dilim çıkmadı. Üç Sonnet izni kullanılmadı.

## OWNS_farkı

| Dosya | Değişiklik |
| --- | --- |
| `src/Oom/Install/Install.cs` | +394/-36. Yeni `OwnershipVerdict` kaydı ve `InstallOwnership` yardımcı sınıfı; `Uninstall`/`UninstallCore` sahiplik kapılarıyla yeniden yazıldı; `RemoveHooksFrom`, `RemoveMcp`, görev/kısayol/Event Log/durum kökü/paket adımları ayrı ayrı doğrulanır oldu; iki yeni isteğe bağlı yapıcı dikişi (`eventLogTarget`, `ownedShortcutRemover`). |
| `src/Oom/Install/InstallRuntime.cs` | +60/-1. `TaskRemoval` enum'u, `SchtasksScheduler.UnregisterOwned` (önce `/Query /XML`, sonra koşullu `/Delete`), `RegisteredCommand` XML okuyucusu. Eski koşulsuz `Unregister` kaldırıldı. |
| `src/Oom/Install/ShortcutRegistration.cs` | +77/-1. `Target()` (kısayolun gerçek hedefi), `TryRemoveOwned(string)`, `EventLogSourceTarget()`; `TryRemoveEventLogSource` üzerine sahiplik uyarısı. |
| `tests/Oom.Tests/UninstallOwnershipTests.cs` | Yeni, 455 satır, 9 test (Y-184…Y-190). |
| `denetim/astra-dalga-1/U-test-degisiklik-onerisi.patch` | Yeni. `UninstallSafetyOrderTests.cs` için önerilen tam fark. |
| `denetim/astra-dalga-1/U.md` | Bu dosya. |

OWNS dışında hiçbir dosyaya yazılmadı; `git status` yalnız bu altı yolu gösteriyor.

## olgular/kaynaklar

Ölçüm, iddia değil — her satır dosyadan okundu.

- Kanca sahipliği alt dize idi. `Install.cs:600` (taban): `entries[index]?.ToJsonString().Contains("oom.exe", OrdinalIgnoreCase)`. İki kusur birden: (a) komşu vault'un `<B>\.oom\oom.exe` komutu da bu dizeyi taşır, (b) döngü `entries[index]` diye adlandırdığı şeyi grup olarak gezer — settings.json'da `hooks[<olay>]` bir **grup** dizisidir, her grup içinde `hooks` alt dizisi vardır. Yani karma bir grupta oom kaydı bulunduğunda yabancı aracın kaydı da gidiyordu.
- MCP sahipliği anahtar adı idi. `Install.cs:580` (taban): `servers.Remove("oom")` — komut hiç okunmuyordu.
- Görev koşulsuz siliniyordu. `Install.cs:304` (taban): `if (scheduler is InstallRuntime.SchtasksScheduler schtasks) schtasks.Unregister(TaskName);` ve hemen ardından koşulsuz `registrations.Add("task:kaldırıldı")`. `Unregister` çıkış kodunu hiç okumuyordu; `FakeScheduler` kullanan koşumlarda ise makineye hiçbir şey sorulmadan yine "kaldırıldı" bildiriliyordu.
- Kısayol koşulsuz siliniyordu. `ShortcutRegistration.TryRemove()` yalnız `File.Exists` bakıp `File.Delete` ediyor; AUMID ve yol makine sabiti, hiçbir kasayı adlandırmıyor.
- Event Log kaynağı koşulsuz siliniyordu. `UninstallCore` içinde `eventLogRemover()` çağrısı kapısızdı. Kaynağın kendisi münhasır sahipliği kanıtlayamaz: `TryRegisterEventLogSource` anahtara `EventMessageFile = %SystemRoot%\System32\EventCreate.exe` yazar — Windows'un kendi ikilisi, hiçbir kasa adı yok.
- Durum kökü künye sorulmadan taşınıyordu. `Archive(stateRoot, …)` koşulsuzdu; oysa `VaultIdentity.DescriptorName` (`vault.json`) tam da kökü atfedilebilir kılmak için var (`AyrismaTests` Y-181 bunu kurulum tarafında zaten sınıyor).
- Göreli yol dalı yalan söylüyordu. `Uninstall` fully-qualified olmayan yol için hiçbir şey yapmadan `["hooks:kaldırıldı","task:kaldırıldı","aumid:kaldırıldı","mcp:kaldırıldı"]` döndürüyordu.

## çıkarımlar

- **Tek kanıt, tek yol.** Bir kaydın bu kasaya ait olduğunu kanıtlayan tek şey, çalıştırdığı ikilinin `<vault>\.oom\oom.exe` olmasıdır. `InstallOwnership.PathsEqual` her iki yanın da **tam nitelikli** olmasını şart koşar: göreli bir yazım, kaldırmanın hangi dizinde koştuğuna göre çözülür — bu bir tesadüftür, kimlik değil. Bozuk/eksik/uyuşmayan kimlik bu yüzden "bizim değil" diye okunur ve hedef korunur.
- **Komut satırında yalnız ilk belirteç çalıştırılır.** `CommandRuns` yalnız baştaki (gerekirse tırnaklı) belirtece bakar. `logger.exe --watch "<bizim exe>"` bir argümandır, hedef değil; eski alt dize testi bunu da siliyordu.
- **Grup bir kap, kayıt bir hedef.** Karma grupta yalnız sahip olunan alt kayıt kalkar; grup, başkasına ait bir kayıt kaldığı sürece ayakta kalır. Bu koşumda kendimizin boşalttığı grup silinir, zaten boş gelen grup silinmez.
- **Münhasır sahipliği kanıtlanamayan kayıt korunur.** Event Log kaynağı için doğru cevap neredeyse her zaman "koru"dur: artık bir anahtar zararsızdır, silinen anahtar hâlâ kullanan kurulumu susturur. Kod ölü değil — `EventLogSourceTarget()` gerçekten okunur ve bizim exe'mizi adlandırdığı tek durum sınanır (Y-189).
- **Yapılmayan iş bildirilmez.** `task:kaldırıldı` yalnız `/Delete` 0 döndüğünde yazılır; `task:yok`, `task:korundu:yabancı-hedef`, `task:korundu:hedef-okunamadı`, `task:kaldırılamadı`, `task:atlandı:kaydedici-yok` ayrı satırlardır. Aynısı `hooks:kaldırılmadı`, `mcp:kaldırılmadı`, `shortcut:korundu`, `kimlik:korundu`, `kanıt:yok`, `paket:korundu:*`, `durum-kökü:korundu:*` için geçerli.
- **Sahiplik kararı kayıttan ayrı raporlanır.** Yeni `Install.Ownership`, `IReadOnlyList<OwnershipVerdict>`: hedef, sahiplik doğrulandı mı, hangi kanıtla. Kayıt satırı ne yapıldığını söyler; bu liste hangi delille söylendiğini söyler — kanıt yokluğundan dokunulmayan hedef artık sessizlik değil, sonuç.
- **Sıra korundu.** Kimlik dizininden bağımsız üç temizlik (kancalar, görev, MCP) hâlâ `ClaudeIsolation.ConfigurationDirectory` çözülmeden önce koşar. Bir güvenlik reddi canlı giriş noktası bırakamaz — `UninstallSafetyOrderTests`'in gerçekten koruduğu sözleşme budur.
- **Dikişler geriye uyumlu.** `shortcutRemover` (`Func<bool>`) imzası korundu; üretim varsayılanı `ShortcutRegistration.TryRemoveOwned`'a bağlandı, mevcut fixture'lar tam olarak eskiden ne demekse onu demeye devam ediyor. İki yeni isteğe bağlı parametre (`eventLogTarget`, `ownedShortcutRemover`) listenin sonunda; her çağrı yeri adlandırılmış argüman kullandığı için hiçbiri kırılmadı.
- **Yan fayda.** `KurulumScars` Y-105 hiçbir kısayol/Event Log dikişi enjekte etmez, yani taban kodda o test sahibin gerçek `Origin of Memory.lnk` dosyasını ve gerçek HKLM `oom` anahtarını **silmeye çalışıyordu**. Artık ikisi de sahiplik kapısından geçiyor ve geçici kasanın exe'siyle eşleşmedikleri için korunuyorlar.

## test_komutları/sonuçları

```
rm -rf src/Oom/obj/Release tests/Oom.Tests/obj/Release
dotnet build Oom.sln -c Release --nologo    →  Oluşturma başarılı, 1 Uyarı, 0 Hata
dotnet test  Oom.sln -c Release --nologo    →  Başarısız: 2, Başarılı: 198, Toplam: 200
```

- Tek uyarı: `CS0028`, `src/Oom/Program.cs:42` — taban uyarısı, OWNS dışı, dokunulmadı. Yeni uyarı yok.
- Taban 191 test / 0 düşen; şimdi 200 test (9 yeni) / 2 düşen.

### Beklenen kırmızılar (ikisi de emirde önceden bildirildi)

1. `Y-125 · Test gövdesindeki her Y-numarası scars.md'de bir satıra karşılık gelir…`
   `test without a ledger row: Y-184, Y-185, Y-186, Y-187, Y-188, Y-189, Y-190`
   `docs/scars.md` OWNS dışı; defter satırlarını merkez atar.
2. `UninstallSafetyOrderTests.RejectedIdentityPath_StillCleansIndependentRegistrations_AndReturnsPartial`
   `Assert.Contains() Failure … Not found: "hooks:kaldırıldı"`
   `Collection: ["hooks:kaldırılmadı", "task:korundu:hedef-okunamadı", "mcp:kaldırılmadı", "kaldırma-hata:…"]`
   Fixture `fixture\oom.exe` (göreli, kasa dışı) kaydediyor; sahiplik doğrulaması altında bu bir yabancının komutudur ve **doğru olarak korunur**. Dosyaya dokunulmadı; gereken tam fark `U-test-degisiklik-onerisi.patch`'te.

Bunların dışında hiçbir mevcut test kızarmadı — `KurulumScars` Y-105, `AyrismaTests` Y-180…Y-183, `KimlikScars` tümü yeşil.

### Yeni testler (hepsi yeşil)

| Test | Ne sınar |
| --- | --- |
| Y-184 | A, B ve yabancı bir `oom.exe` aynı fixture'da; A kaldırılırken B'nin ve yabancının dosya hash'leri ve kayıtları bozulmuyor; yabancı hedefli görev, paylaşılan Event Log kaynağı ve kısayol korunuyor |
| Y-185 | Karma hook grubunda yalnız sahip olunan alt kayıt kalkıyor; yabancı araç ve argüman olarak exe yolu taşıyan komut kalıyor |
| Y-186 | Eksik / bozuk / uyuşmayan künye — durum kökü ve paket yerinde kalıyor, sonuçta açıkça bildiriliyor (Theory, 3 vaka) |
| Y-187 | Reddedilen config yolunda doğrulanmış bağımsız kayıtlar yine de temizleniyor; `/Query` `/Delete`'ten önce geliyor |
| Y-188 | `schtasks /Delete` hatası "kaldırıldı" diye bildirilmiyor |
| Y-189 | Sahiplik kararı hedef hedef, kayıt satırından ayrı bildiriliyor; kanıtlanabilir tek Event Log silme dalı da koşuluyor; göreli yol hiçbir şey iddia etmiyor |
| Y-190 | Sahiplik kanıtı yol/komut düzeyinde okunuyor; görev XML'i tırnak ve varlık çözümüyle |

## bozma_kanıtı

Her biri **derlenen** bir davranış bozmasıdır; derleme hatası kanıt sayılmadı. Bozma sonrası tam hata iletisi, ardından geri alınıp yeşil doğrulandı.

**1 — Kanca/MCP sahipliği yeniden ada bağlandı.** `InstallOwnership.CommandRuns` → `commandLine.Contains("oom.exe", OrdinalIgnoreCase)`; `RemoveMcp`'te `PathsEqual` kapısı `if (false)` ile atlandı.
- Y-190 (satır 439): `Assert.False() Failure / Expected: False / Actual: True`
- Y-185 (satır 247): `Assert.Contains() Failure: Filter not matched in collection / Collection: ["other-tool.exe hook"]` — `logger.exe` kaydı, yalnız argümanında exe yolu geçtiği için silinmiş
- Y-184 (satır 193): `Assert.Contains() Failure: Filter not matched in collection / Collection: []` — B'nin kancası ortak dosyadan silinmiş

**2 — Görev hedefi sorulmadan silindi.** `UnregisterOwned` gövdesi `/Delete` çağırıp koşulsuz `TaskRemoval.Removed` döndürdü.
- Y-187 (satır 349): `Assert.Equal() Failure: Collections differ / Expected: ["/Query","/TN","OdenaOS Memory Sweep","/XML"] / Actual: ["/Delete","/TN","OdenaOS Memory Sweep","/F"]`
- Y-185 (satır 250): `Not found: "task:yok"` — `Collection: ["hooks:kaldırıldı","task:kaldırıldı",…]`
- Y-188 (satır 377): `Not found: "task:kaldırılamadı"`
- Y-184 (satır 209): `Not found: "task:korundu:yabancı-hedef"`

**3 — Karma grup yine kap olarak silindi.** Grupta bizden bir kayıt varsa `entries.Clear()`.
- Y-185 (satır 246): `Assert.Contains() Failure … Collection: [] / Not found: "other-tool.exe hook"`
- Y-184 (satır 193): `Assert.Contains() Failure: Filter not matched in collection`

**4 — Künye sorulmadı.** `PackageOwnership` ve `StateRootOwnership` koşulsuz `Owned: true` döndürdü.
- Y-186, üç vakada da (satır 296): `künyesi doğrulanmayan durum kökü taşındı`

**5 — Paylaşılan Event Log kaynağı koşulsuz silindi.** `PathsEqual` kapısı `if (false)`.
- Y-184 (satır 213): `Not found: "event-log:korundu:paylaşılan-kaynak"` — `Collection: [… "shortcut:kaldırıldı","aumid:kaldırıldı", …]`

**5b — Kısayol sahipliğe bakılmadan silindi.** `if (shortcutRemover(owned) || true)`.
- İlk koşumda **hiçbir test kızarmadı** — testim eksikti. Y-184'e `shortcut:korundu` / `DoesNotContain("shortcut:kaldırıldı")` / `DoesNotContain("aumid:kaldırıldı")` eklendi, bozma yerindeyken yeniden koşuldu:
- Y-184 (satır 219): `Assert.Contains() Failure … Not found: "shortcut:korundu"` — `Collection: [… "shortcut:kaldırıldı","aumid:kaldırıldı", …]`

**6 — Kimlik yolu bağımsız temizlikten önce çözüldü.** `Uninstall` başına `ClaudeIsolation.ConfigurationDirectory(...)` çağrısı eklendi.
- Y-187 (satır 350): `Assert.Contains() Failure … Collection: ["kaldırma-hata:Claude kimlik dizini LOCALAPPDATA dı"…] / Not found: "hooks:kaldırıldı"`

**7 — Sahiplik kararı ayrıca bildirilmedi.** `finally { Ownership = []; }`.
- Y-189 (satır 416), Y-186 ×3 (satır 304), Y-188 (satır 384): `Assert.Contains()/Assert.Single() Failure … Collection: []`

**8 — Görev XML'inde tırnak/varlık çözümü atlandı.**
- Y-190 (satır 448): `Assert.Equal() Failure: Strings differ / Expected: "C:\a b\oom.exe" / Actual: "\"C:\a b\oom.exe\""`

Bozmalardan sonra `grep -n "KASITLI BOZMA"` boş; soğuk derleme + tam koşum yukarıdaki sonuçları verdi.

## onay_bekleyenler

1. **`tests/Oom.Tests/UninstallSafetyOrderTests.cs` değişikliği.** Dosyaya dokunulmadı; tam fark `denetim/astra-dalga-1/U-test-degisiklik-onerisi.patch` (92 satır, `git apply --check` temiz). Öneri fixture'ın kaydettiği exe'yi `fixture\oom.exe` yerine `<vault>\.oom\oom.exe` yapar ve `RecordingProcessRunner`'a `/Query` cevabı ekler; görev iddiası `Assert.Single` yerine `/Query` + `/Delete` çiftini çiviler. Koruduğu sözleşme değişmiyor: güvenli bağımsız temizlik kimlik yolu hatasından önce. **Doğrulandı** — yama geçici olarak uygulanıp koşuldu, test yeşil geçti (`Başarılı: 1`), sonra dosya `git checkout` ile aslına döndürüldü.
2. **`docs/scars.md` defter satırları.** Y-184…Y-190 için satır gerekiyor; `docs/` OWNS dışı, merkez atacak.

## açıklar

- **Tırnaksız, boşluklu kanca komutu belirsizdir.** `CommandRuns` böyle bir komutu ayıramaz ve "bizim değil" okur, yani kanca korunur. Güvenli yön bu, ama `HookTemplates.Quote` zaten boşluklu yolu tırnaklar; yalnız elle yazılmış bir kayıt bu duruma düşer. Kod yorumunda belirtildi.
- **Kısayol hedefi okuma canlı makinede sınanmadı.** `ShortcutRegistration.Target()` COM `IShellLinkW.GetPath` kullanır; birim testi yok, çünkü sınamak sahibin gerçek Start menüsünü okumayı gerektirirdi (emir izolasyon şart koşuyor). Kararın kendisi (`PathsEqual`) ve dikişin doğru exe'yi aldığı (Y-184/f) sınandı. **Başarısızlık yönü güvenli:** `Target()` null dönerse kısayol korunur.
- **`schtasks /Query /XML` çıktısı gerçek makinede okunmadı.** `RegisteredCommand` senaryolu XML üzerinde sınandı; gerçek `schtasks` UTF-16 + BOM üretir ve bunu `ProcessResult.StandardOutput`'un nasıl çözdüğü ölçülmedi. Çözemezse sonuç `task:korundu:hedef-okunamadı` — yani görev silinmez, güvenli yön; ama sahibin makinesinde gerçekten kaldırma yapılacaksa bu tek satır önce ölçülmeli.
- **Künyesiz eski durum kökleri artık arşivlenmez.** Künye `8436f94` ile geldi; ondan önce kurulmuş bir kök `durum-kökü:korundu:künye-yok` ile yerinde kalır. Emrin "belirsiz hedef korunur" hükmünün doğrudan sonucu, ama bir göç adımı gerektirebilir.
- **`.oom` paketi konumla sahiplenilir.** Yol kasa argümanından türediği için tek kaçış yolu bağlantıdır; `InstallOwnership.IsInside` yalnız uç düğümün bağlantısını çözer, `ClaudeIsolation.ResolvePath` gibi her segmenti değil. Ara segmentte junction taşıyan bir kasa için kanıt zayıf. Sınanmadı; `ClaudeIsolation`'ın daha güçlü çözücüsü OWNS dışında ve `internal private`.
- **`Uninstall`'ın göreli-yol dalı artık `["kaldırma:atlandı:göreli-yol"]` döndürüyor.** Eskiden dört sahte "kaldırıldı" satırı dönüyordu. Hiçbir mevcut test veya `Program.cs` tüketicisi bu satırları okumuyor (arandı), ama dışarıdan bir sözleşme varsa kırılır.
- Satır sonları: `Install.cs` ve `InstallRuntime.cs` üzerinde bir `perl -i` geçişi CRLF'i LF'e çevirdi. `core.autocrlf` normalize ettiği için `git diff` yalnız gerçek değişiklikleri gösteriyor (394/60/77 satır), fark şişmedi.
