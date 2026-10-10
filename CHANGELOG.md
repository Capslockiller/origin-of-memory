# CHANGELOG

Biçim: sürüm başına tek girdi, insan okur. Eski girdiler `v3` ve `v0` dallarındaki CHANGELOG dosyalarındadır.

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
