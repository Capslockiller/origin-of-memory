# 04 — Kart

## Amaç

Bir konuyu açan ajanın okuyacağı tek şey. Durum değil, **yürürlükteki karar**: sahibe ne soruldu, sahip ne dedi, bu hangi önceki kararı devirdi, yolda olup olmadığımız neyle anlaşılır, neye hayır denmiş, sıradaki ne.

Kartı ajan yazar, sahip onaylar, makine doğrular. Makine kartı ham kayıttan her an yeniden kurabilmelidir; kurulamayan satır karta giremez.

## Dosya

`<vault>/🔮 850-Companion/Kartlar/<konu>.md`. Konu adı küçük harf, ASCII, kısa (`oom`, `sahne`, `web`). Takma adlar frontmatter'da.

```markdown
---
type: kart
konu: oom
takma: [origin of memory, defter]
updated: 2026-10-10 18:05
---
# OoM

**Yürürlükte:** <ajanın cümlesi> → değiştirir: <neyi> · #<sahip-uuid>
**Ölçüt:** yolda sayılır ancak <gözlenebilir şey>; bunu kıpırdatmayan iş sapmadır.
**Kapalı yönler:** <neyi kapattığı> #<uuid> · <neyi kapattığı> #<uuid>
**Sıradaki:** <tek eylem>

— hesaplanan: son söz #<uuid> <tarih> · ölçüt <değer> · kıpırdamadı <N> oturum · doğrulama <yeşil|kırmızı: neden>

## Kimlikler
- <uuid tam> · <tarih saat> · <sınıf: söz | kapalı yön | ölçüt onayı>
## Kimliksiz (enjekte edilmez)
- <eski karar, kaynağı, neden kimliksiz>
```

Üst blok = dört kalın satır. Sınır **500 karakter**; 400 hedef. Hesaplanan satırı yalnız `oom kart hesapla` yazar.

## Alanlar

| Alan | Kim yazar | Kural |
|---|---|---|
| Yürürlükte | ajan | Ajanın sahibe koyduğu cümle, sahibe **önce** tam metniyle gösterilmiş olmalı. Sonuna sahibin cevabının uuid'si. `→ korur:` ya da `→ değiştirir:` zorunlu; ilk kartta `→ ilk`. |
| Ölçüt | ajan önerir, sahip onaylar | Gözlenebilir olmalı: sayı, dosya, davranış. "Daha iyi" ya da "temiz" ölçüt değildir. Onay uuid'si Kimlikler'de. |
| Kapalı yönler | ajan | Her madde: neyi kapattığı (ajan sözü, ≤8 kelime) + sahibin hayırının uuid'si. Tarihe göre değil, kapattığı şeye göre durur; sahip yeniden açınca düşer. |
| Sıradaki | ajan | Tek eylem, yürürlükten türemiş. Saat yok, sıra var. |
| Hesaplanan | makine | Son `u` turu (bu konuyla eşleşen oturumlarda), ölçüt değeri (ölçülebiliyorsa), ardışık "ölçüt kıpırdamadı" oturum sayısı, son doğrulama sonucu. |

## Kimlik kuralı

- Tırnak içine metin yazılmaz. Her alıntı `#<uuid>` biçiminde kimliktir; görüntülenirken ham kayıttan çekilir.
- Kimlik şu dört şartı geçmek zorundadır: ham kayıtta var · sınıfı `u` · bu vault'un bir oturumundan · kart satırındaki sabit çapalar (tarih, sayı, tırnaklı dizge) o turun metninde geçiyor.
- Geçmeyen satır kartı **geçersiz** yapar: kart enjekte edilmez, `durum` nedenini yazar. Yarım kart yoktur.
- Kimliksiz eski kararlar "Kimliksiz" bölümünde durur; enjekte edilmez; ham transkripti bulununca taşınır.

## Korur / değiştirir ve çatışma

- Konuda yapılan **her öneri** sahibe şu cümleyle açılır: `Yürürlükte: <mevcut> → korur` ya da `→ değiştirir: <ne, neden>`. Bu cümle yoksa öneri yoktur.
- Yalnız sahip onaylı bir "değiştirir" üst satırı yeniler. Yeni satır eskisinin uuid'sini `← devirir: #…` olarak taşır; eski satır ham kayıttadır, kartta değil.
- İki oturum aynı kartı farklı yürürlükle yazarsa ya da yeni yürürlük `korur/değiştirir` taşımıyorsa `kart dogrula` ikinciyi **çatışma** olarak işaretler: kart enjekte edilir ama üstüne `⚠ çatışma: #a ile #b` satırı düşer ve `durum` sayar. Çatışmayı yalnız sahip çözer; makine son yazanı kazandırmaz.

## Ne zaman yazılır

- **Canlı.** Sahibin cevabı geldiği turda, aynı ajan tarafından. Devir teslime bırakılmaz (oturumların %25'i 8 saati aşıyor; compaction araya giriyor).
- Söz ile fikir ayrımı: onay sözcüğü ("evet", "yap", "başla", "devam", "bu") ya da açık yön cümlesi gelmeden hiçbir şey yürürlüğe yazılmaz. "sence/bence" fikirdir; kartta yeri yoktur.
- Soru biçimli kararlar ("haksız mıyım?") ve aynı nefeste dönüşler ("go → dur → önce şu") için kural: son cümle kazanır, önceki cümleler kapalı yön değildir.
- Hayır: gerekçeli her ret kapalı yöndür. Gerekçesiz "hayır" yalnız ham kayıtta kalır.

## Yazılmayan kararlar

En olası arıza kartın yanlış olması değil, **hiç yazılmamasıdır** (3.x döneminde 346 hayıra karşı Düzeltmeler'de 2 kayıt). İlaç makinede: `durum`, ham kayıtta onay biçimli `u` turu olup o oturumda kart değişikliği olmayan sayıyı gösterir ("kaydedilmemiş karar: 3"). Sayı sahibe değil ajana kırmızıdır; ajan bir sonraki açılışta önce onu kapatır.

## Enjeksiyon

- SessionStart: yalnız `_dizin.md` (konu adı + yürürlük satırının ilk 80 kr, ≤1.000 kr toplam).
- İlk mesaj: konu adı ya da takma adı geçiyorsa o kartın üst bloğu + hesaplanan satır (≤600 kr), bir kez. Eşleşme yoksa hiçbir şey.
- Ajan isterse `oom kart goster <konu>` ile her an.
- Geçersiz kart enjekte edilmez; yerine tek satır: `kart geçersiz: oom (kimlik u değil: 57250b80)`.

## Şerit brief'leri

- Her şerit brief'inin ilk satırı kartın yürürlük satırıdır; ikinci satırı kapalı yönler. Şerit kartı okur, yazmaz. Şerit transkripti ham kayda girmez.

## Gelecek basamak (4.0'da yok)

- İstemci MCP elicitation destekliyorsa sahibin evet/hayır cevabı istemci arayüzünden alınır ve uuid yerine elicitation kaydı kimlik olur. Ajanın aktardığı kimlikten daha güçlüdür; istemci desteği gelince açılır.

## Geçiş

- `Threads.md` emekliye ayrılır: canlı konular karta taşınır (sahip hangi 5 konunun önce taşınacağını söyler), gerisi `arsiv/Thread-Geçmişi-*.md`'de kalır. Taşıma budamadır; 11 thread 11 kart olmaz.
- `Last-Session.md` kısalır: tek paragraf ve açık konuların kart bağlantıları.
- Düzeltmeler biçimi kart kuralına uyar: her düzeltme sahibin kimlikli sözüyle.
