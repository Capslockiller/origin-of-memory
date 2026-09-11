Master Mind, **kararım: önce güvenli teşhis ve ölçüm sözleşmesi tamamlanacak; sonra birleşik aday ölçülecek; canlı onarım ve toplu push en son, ayrı kapılardan geçecek.** Dalga 1’in kabulünü koruyorum. Ürün kabulü henüz kapanmış değil.

**Olgu:** Çalışma ağacı temiz; HEAD `39cb9e7`. `529722e` sonrasındaki fark yalnız kabul belgesi. 222/222 ve exe smoke sonuçlarının kaynağı [INT-1 kabul kaydı](E:/OdenaWorks/10-Aktif/oom-w-INT/denetim/astra-dalga-1/kabul.md); bu oturumda testleri yeniden koşturmadım.

**Çıkarım:** Sentetik v3 deneyi, eksik şemanın yükseltmeyle tamamlanmadığını kanıtlıyor. Sahibin canlı dosyasının aynı nedenle bozuk olduğunu henüz kanıtlamıyor. Onarım yöntemi bu ayrım korunarak seçilecek.

Ana döngüye yürütme emrim aşağıdadır.

1. **INT-2 — tabanı ve girdileri sabitle.**

   Taban `39cb9e7`; ürün davranışı tabanı `529722e`. Ayrı worktree, şerit başına tek commit; mevcut ağaçlar sıfırlanmayacak, taşınmayacak.

   Başlamadan şu kayıtlar oluşturulacak:

   - Tam SHA, çalışma ağacı durumu, şeritlerin kesin OWNS listeleri.
   - Gerçek gold dosyasının kaynağı, korpusun kapsamı ve bunları hazırlayacak sorumlu.
   - Canlı dosyalara dokunmadan kullanılabilecek mevcut yedek/kopyaların envanteri.
   - Her çıktı için `yazan`, gerçek model kimliği, komut ve ham kanıt yolu.

   Gold veya korpus henüz sağlanamıyorsa geliştirme devam eder; nihai recall kapısı **“girdi bekliyor”** kalır. Sentetik veriyle kapatılmaz.

2. **S2 ve R2 bağımsız yürüsün; D2, S2’nin teşhis sözleşmesinden sonra bağlansın.**

   | Şerit | Kesin yazma kapsamı | Yara aralığı | Teslim |
   |---|---|---|---|
   | S2 | `src/Oom/State/State.cs`, State altında yeni şema denetimi dosyası, yeni şema bütünlüğü test dosyası, kendi raporu | Y-230–249 | Sürüm–şekil denetimi ve güvenli açılış |
   | R2 | `bench/verify_recall.py`, yeni kanıt bağlama test dosyası, kendi sentetik kanıt dizini, kendi raporu ve revize Y-042 yama önerisi | Y-250–269 | Adaya bağlanan, tam girdili ölçüm |
   | D2 | `src/Oom/Doctor/Doctor.cs`, yeni doctor teşhis test dosyası, kendi raporu | Y-270–289 | Dürüst teşhis ve kullanıcı yönlendirmesi |
   | INT-2 | `Program.cs`, CI, merkezi scar defteri, kabul kayıtları ve ayrıca kabul edilen mevcut-test yamaları | Y-290–299 | CLI bağlantısı ve birleşik doğrulama |

   Aralık yetmezse merkezden yenisi alınacak; şerit kendi aralığını genişletmeyecek. Ortak dosyaya ihtiyaç, gerekçeli yama önerisiyle merkeze dönecek.

   **S2’nin davranış kararı: göç ile onarım ayrılacak.** Tarihî basamakları her açılışta çalıştırma davranışı geri gelmeyecek.

   - Denetim, dosyanın iddia ettiği sürüme göre gerekli tablo, sütun, görünüm ve indeksleri karşılaştıracak. Şema bilgisi State tarafında kalacak.
   - Geçerli eski sürümler yükseltilebilecek. Eksik veya çelişkili şema, yazan açılışta iş verisi değiştirilmeden açık gerekçeyle reddedilecek.
   - Yeni boş dosya, geçerli tarihî dosya ve yarım/hasarlı dosya birbirine karıştırılmayacak.
   - `doctor`, normal `State` açılışı başarısız olsa da teşhis verebilecek. Bugünkü `doctor --fix` önce yazan açılışa girdiğinden bu bağlantıyı INT değiştirecek. [Kod](E:/OdenaWorks/10-Aktif/oom-w-INT/src/Oom/Program.cs:415)

   **S2 kabul kapısı:** geçerli v2/v3/v4, genişletilmiş v4, sağlam v5, eksik `sweep_stamps`, eksik `calls` sütunu, eksik görünüm/indeks, fiziksel bozulma ve gelecek sürüm ayrı senaryolar olacak. Göçte kayıt içerikleri ve yedek doğrulanacak; reddedilen senaryolarda veri ve sürüm değişmeyecek. Eksik yapıyı yalnız raporlamak için DDL çalışmayacak.

   **Bu şerit canlı onarım yapmayacak.** Boş tablo yaratmak, kaybolmuş kayıtları geri getirmek değildir.

3. **R2 — mevcut Y-042 yamasını olduğu gibi uygulama; önce kanıt bağını tamamla.**

   Kod incelemesinde üç boşluk doğruladım:

   - Önerilen test, `identity.*.verified` bayraklarını ve hash’in biçimini okuyor; hash’i kabul edilen exe ile karşılaştırmıyor.
   - Genel eşikler kanıt dosyasının kendi `thresholds` alanından okunuyor.
   - Korpus kopyası yalnız `knowledge/concepts/*.md` içeriyor; ürünün getirme yolu `Duzeltmeler.md` dosyasını da okuyabiliyor. [Koşucu](E:/OdenaWorks/10-Aktif/oom-w-INT/bench/verify_recall.py:133), [yama](E:/OdenaWorks/10-Aktif/oom-w-INT/denetim/astra-dalga-1/R-y042-degisiklik-onerisi.patch), [getirme](E:/OdenaWorks/10-Aktif/oom-w-INT/src/Oom/Retrieve/Retrieve.cs:591)

   R2 bunları kapatacak:

   - Kabul edilen kaynak ağacı → yayımlanmış exe → ölçüm → ham çıktı bağlantısı dışarıdan verilen aday manifestine bağlanacak. Koşucunun bulunduğu checkout’un SHA’sı tek başına exe’nin kaynağı sayılmayacak.
   - Ölçülen getirme girdileri envanterlenecek; düzeltmeler ve sonucu etkileyen ayarlar da kopyalanıp hash’lenecek. Kopya hedefi kaynakla aynı/örtüşen yol olamayacak; dolu hedef sessizce silinmeyecek.
   - `Duzeltmeler.md#…` isabetleri koşulsuz muafiyetle geçmeyecek; gerçekten yüklenen düzeltmelerle doğrulanacak.
   - Ölçüm öncesi ve sonrası kimlikler karşılaştırılacak; değişen girdi geçerli sonuç üretmeyecek.
   - Eşikler kanıt dosyası tarafından düşürülemeyecek: genel @3 ≥ **0,80**, @5 ≥ **0,88**, puanlanan soru ≥ **125**; `tek-not` n≥90/@5≥0,85, `cok-not` n≥20/@5≥0,90.
   - Tam tarama ve indeksli yol ayrı ölçülecek. İndeksli koşuda indeksin gerçekten kullanıldığı gösterilecek; sessiz fallback indeksli kanıt sayılmayacak.

   **R2 kabul kapısı:** eski kanıt + yeni exe, düşürülmüş eşik, değiştirilmiş düzeltme, uydurma düzeltme kimliği ve kullanılmayan indeks senaryoları ilgili kapıyı düşürecek. Bozma kanıtı, amaçlanan assertion’a ulaştığını gösterecek.

4. **D2 — teşhisin anlamını ve yan etkisini düzelt.**

   `belirsiz` için ürün metni kararı:

   > “Bu durum kökünün boş olduğu doğrulanamadı. Dosyaları koruyun; temizleme kararı vermeden önce kökün bağlı olduğu kasayı ve içeriğini inceleyin.”

   “Satır” sayısı hafıza sayısı olarak sunulmayacak. `sahipsiz`, `belirsiz`, `okunamıyor` ve şema uyuşmazlığı ayrı gerekçeler taşıyacak. Çalıştığı kanıtlanmamış `doctor --fix`, genel çözüm olarak önerilmeyecek.

   D raporundaki WAL/SHM istisnası ayrıca ele alınacak: **“salt-okunur tarama hiçbir dosya oluşturmaz” iddiası mevcut kanıtla kapalı değildir.** Canlı dosyada güncelliği bozabilecek bir seçenek sessizce açılmayacak. Güvenli okuma sağlanamayan kök, gerekçesiyle ölçülemedi olarak raporlanacak. [D raporu](E:/OdenaWorks/10-Aktif/oom-w-INT/denetim/astra-dalga-1/D.md)

   **D2 kabul kapısı:** yayımlanan exe ile eksik şema, bozuk dosya ve WAL kullanan izole kökler okunacak; önce/sonra dosya envanteri ve içerik farkları tutulacak. Kaynakta dosya oluşturma istisnası sürüyorsa kapı kapanmayacak.

5. **INT-3 — birleştir, yayımla, ardından nihai recall’ı ölç.**

   Birleştirme sırası **S2 → D2 → R2 → merkezi bağlantılar**. Sonra temiz doğrulama checkout’unda restore/build/test/publish ve izole CLI smoke koşacak.

   Ürün ve koşucu değişiklikleri bittikten sonra aday sabitlenecek. Aynı exe ile:

   - İndekssiz ve indeksli recall, aynı sabit korpus/gold üzerinde ölçülecek.
   - İki yol mevcut recall zeminlerini geçecek; indeksli yolun farklı sonuçları sorgu bazında açıklanacak.
   - Kanca pozitifleri, kanaryalar ve mevcut 30 promptluk probe ayrı raporlanacak. `vacuous=true` kabul olmayacak; enjeksiyon oranı recall diye yazılmayacak.
   - Episodik eksen ölçülmediyse açık kalacak; `tek-not/cok-not` yeniden adlandırılmayacak.

   **Geçerli `recall-final.json` üretildikten sonra** revize Y-042 yaması uygulanacak. Düşük recall çıkarsa sonuç saklanacak ve kapı kırmızı kalacak; eşik veya gold değiştirilerek yeşile çevrilmeyecek.

   Yama sonrasında testler yeniden koşacak. Ürün ya da build girdisi değişirse exe yeniden yayımlanıp ölçülecek. Yalnız test/kanıt commit’i geldiyse ölçülen ürün ağacıyla bağı manifestte gösterilecek.

   Özel gold’un bulunmadığı uzak CI, ürün recall’ını ölçmüş gibi raporlanmayacak. Yerel ürün kabulü ve uzak CI sonucu ayrı durumlar olacak.

6. **REC-1 — canlı dosya için somut onarım paketi; ardından yayın kapısı.**

   S2/D2 hazır olduğunda sahibin dosyasının **tutarlılığı doğrulanmış kopyası** üzerinde teşhis yapılacak. WAL varsa yalnız ana DB dosyasını kopyalamak yeterli kabul edilmeyecek.

   Teslim paketi şunları gösterecek:

   - Gerçek kusur: eksik şema mı, fiziksel bozulma mı, başka neden mi?
   - Korunabilen tablolar ve kayıt içerikleri.
   - Yeniden türetilebilen veriler ve kaynakları.
   - Geri getirilemeyen/bilinmeyen veriler.
   - Kopyada denenmiş onarım, doğrulanmış yedek ve geri dönüş.
   - Canlıda değişecek kesin yollar ve işlemler.

   Mevcut `Repair()` retention ve başka kasa yazıları da yapıyor; bu nedenle doğrudan kurtarma reçetesi olarak kullanılmayacak. Önce kopyada sınırlı işlem kanıtlanacak.

   **Canlı değişiklik onayı bu hazır paket üzerinden alınacak.** Toplu push kararı da hazır commit listesi ve kabul kaydı üzerinden ayrı tutulacak; uzak CI ancak koştuktan sonra “geçti” denecek.

**Kapsam sınırı:** Bu dalgada ölü kod temizliği, genel refaktör, SDK geçişi, recall ayarı ve kök temizliği yok. U’nun junction/sahiplik ve gerçek Windows hedef okuma açıkları unutulmayacak; canlı uninstall veya dağıtım kabulünden önce ayrı doğrulama kapısı olacak.

**İlk yürütülecek iş INT-2; ardından S2 ve R2.** Ana döngü, her teslimi `taban_sha · commit · OWNS_farkı · olgular/kaynaklar · çıkarımlar · testler · bozma_kanıtı · açıklar` biçiminde getirecek.

Bu oturum salt-okunur olduğu için planı ve oturum kaydını dosyaya yazamadım. **Odena’ya:** bu planın kararları ve kaynakları Codex oturum kaydına alınmalı; canlı dosyanın kusuru henüz bağımsız teşhis edilmiş sayılmamalı.