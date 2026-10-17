# CHANGELOG

Biçim: sürüm başına tek girdi, insan okur. Eski girdiler `v3` ve `v0` dallarındaki CHANGELOG dosyalarındadır.

## [4.0.0-adim2] — 2026-10-18

### Eklendi
- **`oom kaydet --tetik acilis`.** SessionStart'ta öteki oturumların bekleyen turlarını eşiksiz yazar. Desktop oturumu açık bıraktığı için SessionEnd çoğu zaman gelmez. Son çapası dosyanın son yazımından yeni olan transkript okunmadan atlanır.
- **`oom durum [--uzun] [--json]`.** Tek satır sağlık. Gösterdiği üç şey:
  - son kayıt yaşı;
  - bugünkü kayıtsız oturum;
  - son 24 saatin `kaydet` hatası.

  Yeşilde 0, kırmızıda 1 döner.
- **`oom baglam [--metin]`.** Yeni SessionStart bağlamı.
  - Önce `acilis` catch-up'ını koşar, sonra şu blokları basar: Durum (yalnız kırmızıysa) · Kurallar · Düzeltmeler · Kartlar dizini · Son oturum · Arama.
  - Sınır: ≤8.000 kr ve ≤8.500 bayt. Çıktı tek JSON nesnesi.
  - Bu sürümde canlı kancaya bağlanmadı; geçiş Adım 3 kartlarıyla birlikte.
- **`kaydet.jsonl`.** Durum kökünde, her çağrı için bir satır:
  - süre;
  - sonuç: `yazdi`, `bekliyor`, `bos`, `dosya-yok`, `kimlik-yok`, `mesgul` ya da `hata`;
  - okunan kB;
  - yol: `bayt` ya da `tam`.
- **Çapada `bayt:<n>`.** İmleç satırının bayt konumu. Okuyucu o konuma gidip uuid'i doğrular, eşleşmezse tam okumaya döner.
- **`tara --gun N`.** İmleç penceresi max(7, N) gün. Penceredeki kayıtsız turları, imlecin önünde olsalar da yazar (delik doldurma). Hiçbir uuid'i iki kez yazmaz.
- **Sahip cevapları.** `AskUserQuestion` cevabı ve yazılı ret notu `u` olarak, `· cevap` başlığıyla kaydedilir. Plan onayı yazılmaz.

### Değişti
- Claude transkriptinde kimlik istenen oturumdur (dosya adı). Desktop'ta "btw" çatalının ana oturumdan kopyaladığı kayıtlar atlanır.
- Kanca tetikleri hata verince tek `{"systemMessage": …}` basar ve 0 döner. Önceden stderr'e yazıp 1 dönüyordu. `acilis`, son 24 saatin hata sayısını `additionalContext` ile ajana da iletir.
- İmleci bulunamayan oturumda yalnız pencere içindeki turlar yazılır. Desktop'un meta veriyle tazelediği eski oturumlar baştan yazılmaz.
- Başka bir günün turu `yyyy-MM-dd HH:mm` biçimiyle gösterilir.
- Codex `exec` (şerit) rollout'ları kaydedilmez.
- Başka bir sürecin tuttuğu transkript, son yazımı 24 saat içindeyse hata değil `mesgul` sayılır ve sonra yakalanır.
- Transkriptler `FileShare.ReadWrite | Delete` ile okunur.
- Kanca şablonu: SessionStart → `baglam`. Canlı kurulum bu sürümde `context` + `kaydet --tetik acilis` ile kalır.

### Kaldırıldı
- Kök dizindeki artık `ꩠ` dosyası.

### Ölçüm (gerçek makine, 17–18 Eki)
- **7 günlük kapı (onarım öncesi):** oturum kapsaması %84,8, sahip mesajı %85,5 ile düştü. Kök nedenler:
  - kapanmayan oturumun kuyruğu yazılmıyordu;
  - çatal oturumda kimlik uyuşmuyordu;
  - ilk istemde dosya henüz yoktu;
  - hatalar sessizdi.
- **Kopya kasada geri doldurma (`tara --gun 9`):**
  - oturum %100 (34/34), sahip %100 (409/409), ajan %100 (1926/1926);
  - uuid tekrarı 0;
  - ikinci koşu 0 blok yazdı.
- **Mesaj tetiği:** 46–332 MB transkriptlerde önce 1,1–4,9 sn sürüyordu; `bayt` yolunda ~30 ms. Yalnız eski çapalı oturumlar hâlâ tam okunuyor.
- **Kapı:** 439/439. Her tur bir Claude yargıcından ve negatif kontrolden geçti. Adım 2 kodunu Gemini 4 Argon yazdı.

### Kapı (açık)
- Canlı kurulum ve geri doldurmadan sonra: oturum ve sahip mesajı %100, kanca hatası 0, p95 <3 sn (`kaydet.jsonl`). 7 gün yeniden beklenmeyecek (sahip kararı).

## [4.0.0-adim1] — 2026-10-10

### Eklendi
- `oom kaydet [--tetik mesaj|compact|kapanis|tara]`: model çağırmayan ham kayıt. Her tur kimlikli (`u` sahip, `a` ajan, `h` harness, `t` araç), maskeli, idempotent; imleç daily çapasından türer; UserPromptSubmit'te 10 turda bir sessiz yazım; `tara` kökleri 48 saat tarar. Claude Code ve Codex aynı yol.
- `tools/gate.sh`: ortam içi restore + özet satırı şartı (özetsiz koşu geçmiş sayılmaz).

### Kaldırıldı
- `flush`, `sweep`, `compile`, `save`, `mcp`, `install`, `nudge`, `kit` komutları ve modülleri; `Runner`, `Bridge`, `RootMap`, `DetachedProcess`; eski ayar anahtarları (`backend`, `flush`, `compile`, `mcp`, `nudgeEvery`, `reflectionMinPrompts`, `extensions`) artık sessizce yok sayılır.
- 27 test dosyası (kaldırılan modüllerin kahinleri) ve 3.x belge kahini `DocsKabul`.

### Değişti
- Kancalar: UserPromptSubmit → `kaydet --tetik mesaj`, SessionEnd → `kaydet --tetik kapanis`, PreCompact → `kaydet --tetik compact`. SessionStart → `context` değişmedi (Adım 2'de `baglam`).
- `context`, `retrieve`, `doctor` yalnız ölü atıf temizliği; davranış aynı (Adım 2 ve 4'te değişir).

### Ölçüm (gerçek makine, 10 Eki)
- Kapı 286/286, 50 s (taban: 521 test, 99 s). `src` net +360 / −5.356 satır.
- Gerçek tekrar: 151 transkript, 0 hata, ortalama 254 ms, en uzun 847 ms; ikinci koşu 0 yeni blok; sır sondası 0.
- Canlı kurulum 19:55: `4.0.0-adim1+899169b`, önceki nesil `bin.onceki` (3.1.1). 48 saatlik geri doldurma: 237 oturum, 235 blok, 0 hata, 8,5 s.

### Kapı (açık)
- 7 gün gerçek kullanımda kapsama %100, 0 kayıtsız oturum, kanca p95 <3 sn. Ölçüm 17 Eki'de.

## [4.0.0-tasarim] — 2026-10-10

### Değişti
- `main` artık yalnız tasarım taşır: README, `docs/00…08`, `docs/kararlar.md`, `docs/sozluk.md`. Kod yok.
- Depo açıklaması ve konuları Defter'e göre güncellendi; `mcp` ve `knowledge-base` konuları düştü, `decision-records` eklendi.

### Arşivlendi
- 3.x C# hattı → dal `v3` (son commit `4aedd77`, etiket `v3.1.0`). 0.x Python hattı → dal `v0`.
- 14 eski dal (şerit, deneme ve dependabot dalları) uzak depodan kaldırıldı; hepsi yerelde `arsiv/*` olarak duruyor. 5 dependabot PR'ı kapatıldı.

### Karar
- Mekanizmada model çağrısı yok; ham kayıt + konu kartı + arama + tek satır durum. Gerekçeler `docs/kararlar.md` K-01…K-13.

### Sıradaki
- Adım 1 `kaydet` (`docs/07-yol.md`). Kapı: 7 gün %100 kapsama.
