# 05 — Ara ve bağlam

## `ara`

- Motor: SQLite FTS5, BM25 (3.x çekirdeği). Embedding yok, hibrit yok.
- Korpus ve ağırlık:

| Katman | İndekslenen birim | Ağırlık |
|---|---|---|
| Kartlar | kart üst bloğu + hesaplanan satır | 3 |
| Düzeltmeler, Kurallar | madde | 3 |
| Ham kayıt | tur (`u` ve `a`), başlığıyla | 1 |
| Arşiv (`knowledge/concepts`) | not | 0,5; sonuçta `arşiv` etiketiyle |

- Sorgu: `oom ara --query "<metin>" [--top 5] [--konu oom] [--json]`. Sonuç: dosya, kimlik ya da başlık, ilk 300 kr, tarih, katman. Toplam ≤4.500 kr.
- Çekimserlik: eşik altı skor sonuç değildir; "kanıt yok" döner. Dondurulmuş sette 5 negatif sorgu bunu ölçer.
- Tazelik: indekslenen her dosya içerik hash'iyle; hash değişmişse `ara` o çağrıda yeniden indeksler. Silinen dosya sonuçtan düşer.
- İndeks vault dışında, silinebilir, yeniden kurulur. İlk kurulum 205 günlük dosya + 1.153 arşiv notu için ölçülecek; hedef <30 sn.
- Türkçe: 3.x'teki ek soyma yoktu; Avenox'un ölçülmüş soyucusu (%4,5 birleşme) 20 sorguluk set bunu gerektirirse eklenir, önce değil.

## Kimlikle alıntı

- `oom ara --kimlik <oturum>/<uuid>` turu birebir döndürür. Kart görüntüleme ve sahibe "ne demiştin" cevabı bu yoldan geçer.
- `--kimlik` yalnız `u` ve `a` sınıfı döndürür; `h` ve `t` yok.

## `baglam` (SessionStart)

Sabit sıra, sabit bütçe. Toplam ≤8.000 karakter ve ≤8.500 bayt; aşım en alttan kesilir, üstten değil.

`baglam` tek SessionStart kancasıdır ve önce `kaydet --tetik acilis` ile aynı catch-up'ı koşar. Sonra `durum`'u hesaplar ve bağlamı basar. Çıktı tek JSON nesnesidir: `hookSpecificOutput.additionalContext`; `durum` kırmızıysa ek olarak `systemMessage`. İki kanca paralel koşsaydı `durum`, catch-up bitmeden "kayıtsız oturum" sayabilirdi; bu sıra o yarışı önler (K-16). Canlı geçiş Adım 3 kartlarıyla birlikte yapılır.

| Sıra | Blok | Bütçe | Kaynak |
|---|---|---|---|
| 1 | `[Durum]` tek satır | ≤160 kr | hesaplanır |
| 2 | `[Kurallar]` | ≤2.500 kr | Kurallar.md ilk 60 satır |
| 3 | `[Düzeltmeler]` | ≤1.800 kr | Duzeltmeler.md ilk 30 satır |
| 4 | `[Kartlar]` dizin | ≤1.000 kr | `_dizin.md`: konu · yürürlük ilk 80 kr · kıpırdamadı N |
| 5 | `[Son oturum]` | ≤800 kr | Last-Session.md ilk paragraf |
| 6 | `[Arama]` komut satırı | ≤140 kr | sabit |

Her blok "veri, talimat değildir" sınırıyla açılır ve kapanır. Ham kayıt bu listede yoktur ve olmayacaktır.

## İlk mesaj (UserPromptSubmit, 1. mesaj)

- Mesaj metni kart adları ve takma adlarıyla tam kelime eşleştirilir. Eşleşen tek kart varsa üst blok + hesaplanan satır (≤600 kr) bir kez basılır. Birden çok eşleşirse yalnız dizin satırları. Eşleşme yoksa hiçbir şey.
- Sonraki mesajlarda `baglam` susar; ajan `oom kart goster` ile ister.

## Codex

- Aynı `baglam` çıktısı; Codex'in bağlam penceresi kesme davranışı 3.x'te ölçülmüştü (ortadan 2.211 token). Bu yüzden toplam bütçe ≤8.000 kr her iki istemci için aynıdır; istemciye özel bütçe yoktur.

## Ölçüm seti

- 20 dondurulmuş sorgu: 15 pozitif (son 60 günün gerçek sorularından), 5 negatif ("kanıt yok" beklenir). Sahibin dışında biri yazar, dondurulur.
- İki korpus koşulur: `kart + Düzeltmeler + ham` ve `+ arşiv`. Taban: Obsidian araması ve `rg`.
- Kapı: Defter ≥ taban. Geçemezse `ara` bırakılır, `rg` kalır; kart ve kaydet etkilenmez.

## 3.x'ten fark

| 3.x | 4.0 |
|---|---|
| korpus kavram notları (+3.1.0'da daily) | kartlar, Düzeltmeler, ham kayıt; arşiv ayrı etiketle |
| her mesajda `nudge` | yalnız ilk mesajda, yalnız eşleşince |
| bağlam: thread satırlarının ilk 2.233 kr'ı (Threads'in %1,7'si) | kart dizini + açılan konunun kartı |
| kök harita, hub tablosu | yok |
