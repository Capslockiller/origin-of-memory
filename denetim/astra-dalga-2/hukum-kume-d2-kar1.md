Master, **hükmüm: KÜME uygulansın; D2 mevcut hâliyle beklesin, aşağıdaki dar eksik kapatılınca birleştirilsin; kararsızlık ayrı bir şeritte kapatılsın ve INT-3’ün önkoşulu olsun.** Yeniden onay istemiyorum; bu üç karar için verdiğin yetkiyi kullanıyorum.

**Tercih dayanağı:** `Kurallar.md` içindeki raporu kanıtla doğrulama, kök neden anlaşılmadan düzeltmeme ve düzeltmeyi bütün kopyalara taşıma ilkeleri. `Core.md` doğrultusunda yürütme sırasını belirliyorum. `Kural-Adaylari.md` açıkça yürürlükte olmadığını söylüyor; adayları bağlayıcı kural saymadım.

**Kanıt sınırı:** İki commit’in kodunu, test değişikliklerini ve raporlarını okudum. Testleri burada yeniden koşturmadım; koşum sayıları aktarılan ölçümlerdir.

1. **KÜME — uygulama onayı.**

   **Olgu:** `748fb41` için üretim farkı boş. Y-199’un iki yarısı, Y-195’in sağlam açılış ve hasar senaryoları, `Applied` listesinin bağımsız ve sıralı beklentisi kodda korunuyor. Aktarılan bozma sonucu, boş `calls` senaryosunun gerekli olduğunu destekliyor.

   **Karar:** Commit bir kez alınsın; içindeki yama ayrıca uygulanmasın. Testlerin davranışında başka değişiklik istemiyorum. Merkez, altı yaranın defter açıklamasını da yeni hükümle eşitlesin; Y-125’in yeşil olması açıklamaların doğruluğunu kanıtlamıyor.

   Bu, **test değişikliğinin kabulüdür**; kararlılık veya birleşik ürün kabulü değildir.

2. **D2 — mevcut commit’e koşulsuz birleştirme onayı vermiyorum.**

   **Olgu:** Dört anlam kararı karşılanmış. Aktarılan çelişkili şema cümlesi `d63c12a` içinde **zaten düzeltilmiş** ve Y-273’e bağlanmış. Bunu yeniden yapılacak iş saymayın.

   Fakat aynı commit’te `StateRootSnapshot.Take`, DB ile WAL’ı ayrı ayrı kopyalıyor; yalnız kaynağın önce/sonra parmak izlerini karşılaştırıyor. Y-277, WAL’da bekleyen kayıtları sınarken **kopyalama sırasında yazma/checkpoint yarışını sınamıyor**. WAL olmayan yolda da başlık kontrolünden sonra özgün dosya açılıyor.

   **Çıkarım:** Dört sabit fikstürde kaynağın değişmemesi kanıtlanmış olabilir; buradan canlı kökten her zaman tutarlı görüntü alındığı sonucu çıkmaz. SQLite da etkin işlem sırasında sıradan dosya kopyasının karışık içerik taşıyabileceğini açıklıyor. [SQLite açıklaması](https://sqlite.org/howtocorrupt.html#backup_or_restore_while_a_transaction_is_active)

   **Kapatılacak dar iş:** D2, kopyalama boyunca kaynak tutarlılığını sağlayan mekanizmayı kurup doğrulasın; sağlayamadığında mevcut `ölçülemedi` yolunu kullansın. Eşzamanlı yazma/checkpoint ve başlık kontrolünden sonra kip değişimi kontrollü olarak sınansın. Kabul: **tutarlı içerik veya gerekçeli ölçülememe; yanlış `artık` hükmü ve taramanın kaynakta oluşturduğu dosya olmayacak.** Düzeltmenin ilgili assertion’a ulaştığı gösterilsin.

   Bu kapı ve merkezdeki Y-270…279 defter bağlantıları kapanınca D2 birleştirilsin. Yeni özellik veya genel refaktör istemiyorum.

3. **Kararsızlık — ayrı `KAR-1` şeridi; INT-3 öncesinde kapanacak.**

   **Olgu:** Aktarılan ölçümler aralıklı başarısızlığı ve seri koşumda 14/14 başarıyı gösteriyor. Ancak **“istisna tipi mekanizmayı birebir kanıtlıyor” hükmünü kabul etmiyorum.** `ObjectDisposedException`, kapatılmış nesnenin kullanıldığını gösterir; kapatan çağrıyı göstermez. Microsoft’un sözleşmesi açık bağlantıların kapandıklarında havuza dönmeyeceğini söylüyor; açık tutamacın anında yok edilmesini normal davranış olarak tanımlamıyor. [Microsoft belgesi](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnection.clearallpools?view=msdata-sqlite-10.0.0)

   **Ayrıca doğruladığım olgu:** Üretimde çağrı var: `Doctor.InspectStateRoots`. D2 buna `StateRootSnapshot.Dispose` içinde kök başına ikinci bir çağrı yeri ekliyor. Dolayısıyla “sebebi ürün kodu olmayacak” sonucu da henüz kurulamaz.

   **Şerit kapsamı:**

   - Y-170’in tam hata yığını, kullanılan paket sürümü ve temizleme/bağlantı yaşam döngüsü üzerinden neden bağını kuracak; önce asgari doğrulayıcı senaryo, sonra düzeltme.
   - **Test sınıflarının varsayılan paralelliğinin kapatılmasını onaylıyorum.** Ayar depoda bulunacak ve yerel koşumla CI tarafından aynı şekilde okunacak. Mevcut test kapsamı ve filtreler korunacak. Bu, test izolasyonu tedbiridir; ürün kusurunun çözüldüğü diye yazılmayacak.
   - Açık bağlantı ile Doctor temizliğini aynı süreçte sınayacak. Ürünün gerçek eşzamanlılığı ayrıca **iki ayrı süreçle**, aynı izole kökte sınanacak. Sınıfların seri koşması bu sınamaların içindeki eşzamanlılığı kaldırmayacak.
   - Birleşik adayda önceden belirlenmiş **20 ardışık tam koşum** yapılacak; bütün sonuçlar saklanacak. Başarısız koşumu tekrar ederek görünmez kılmak yok. 20/20, mutlak kararlılık garantisi diye sunulmayacak.

   **Yürütme sırası:** KÜME → D2’nin dar düzeltmesi → KAR-1 → INT-3’ün birleşik doğrulaması. Toplu push kararı korunuyor; hazır paket bu açıklar kapandıktan sonra sunulacak.

**Kabul kaydına yazılacak ifade:**

> `748fb41`: raporlanan paralel koşumda 244 testin 244’ü geçti. Aynı koşum düzeninde aralıklı başarısızlık ayrıca gözlendi; bu sonuç kararlılık kanıtı değildir. Sonraki teşhis Y-170’i işaret etmektedir. KAR-1 açık; birleşik kabul verilmedi.

Her ölçüme **commit, ölçen, tam komut, paralellik ayarı ve ham çıktı yolu** eklensin. Önceki `222/222` ve `244/244` sonuçları silinmesin; geçtikleri koşumun olgusu olarak korunsun, bütün kabul özetlerinde kararlılık şerhiyle tamamlan­sın. Uzak CI için de yalnız gerçekten koştuktan sonra sonuç yazılsın.

**Odena’ya:** Bu hüküm oturum kaydına alınmalı. Salt-okunur oturum nedeniyle dosya veya kayıt yazmadım.