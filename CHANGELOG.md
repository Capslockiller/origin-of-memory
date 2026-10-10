# CHANGELOG

Biçim: sürüm başına tek girdi, insan okur. Eski girdiler `v3` ve `v0` dallarındaki CHANGELOG dosyalarındadır.

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
