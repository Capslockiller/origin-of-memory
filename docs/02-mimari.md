# 02 — Mimari

## Tek cümle

Üç katman, tek yönlü akış: **ham kayıt** (makine yazar, kimse okumaz, her şey atıf verir) → **kartlar ve Düzeltmeler** (ajan yazar, sahip onaylar, makine doğrular) → **enjeksiyon ve arama** (makine servis eder). Hiçbir katmanda model yoktur.

## Adlar

- Depo ve ikili adı değişmez: `origin-of-memory`, `oom.exe`. Kanca satırları yalnız fiil değiştirir; kurulum sürtünmesi sıfır.
- 4.0'ın tasarım adı **Defter**. Fiiller Türkçe: `kaydet`, `kart`, `ara`, `baglam`, `durum`.

## Katmanlar

| Katman | Yer | Yazan | Okuyan | Boyut |
|---|---|---|---|---|
| Ham kayıt | `<vault>/daily/YYYY-MM-DD.md` | `oom kaydet` | `ara`, `kart` (doğrulama), insan nadiren | günde 50–500 KB |
| Kartlar | `<vault>/🔮 850-Companion/Kartlar/<konu>.md` + `_dizin.md` | ajan (üst blok), `oom kart` (hesaplanan satır, dizin) | `baglam`, `ara`, ajan | kart ≤500 kr üst blok; 5–10 canlı kart |
| Kurallar, Düzeltmeler | `🔮 850-Companion/Kurallar.md`, `Duzeltmeler.md` | yalnız sahip (Düzeltmeler: ajan önerir, sahip onaylar, kimlikle) | `baglam` tam metin | Kurallar ≤2.500 kr, Düzeltmeler ilk 30 satır |
| İndeks | `%LOCALAPPDATA%\oom\<vault-hash>\index.db` | `oom kart indeksle`, `ara` (tembel) | `ara` | yeniden kurulabilir; silinirse hiçbir şey kaybolmaz |
| Arşiv | `knowledge/concepts/` (1.153 not), dallar `v3`, `v0` | kimse | `ara` (salt okur) | dondurulmuş |

İndeks tek tablodur (FTS5 `notlar` + bir `meta`). 3.x'in 11 tablolu state.db'si gider: imleç ham kayıttaki çapadan türer, kuyruk yoktur, sağlık hesaplanır.

## Bileşenler

| Bileşen | Kaynağı | Görev |
|---|---|---|
| `Cli/Program.*` | 3.x, kesilmiş | `kaydet · kart · ara · baglam · durum`, `--vault`, `--help`, `--version`. Başka komut yok. |
| `Ingest/Parsers` | 3.x, korunur | Claude Code JSONL ve Codex rollout okuyucuları. Tur sınıflandırması bu katmanda (bkz. 03). |
| `Guards/Redactor` | 3.x, korunur | Sır maskeleme; ham kayıt dahil her yazımdan önce. Varsayılan açık. |
| `Kayit/` | yeni, ~200 satır | İmleç türetme, tur bloğu yazımı, atomik ekleme, idempotentlik. |
| `Kart/` | yeni, ~300 satır | Şema, uzunluk, kimlik, sabit çapa doğrulama; korur/değiştirir ve çatışma; hesaplanan satır; dizin üretimi. |
| `Retrieve/` | 3.x, korunur | BM25/FTS5 çekirdeği; korpus ve çekimserlik kuralı değişir (bkz. 05). |
| `Context/` | 3.x, kesilmiş | Bütçeleyici: sabit sıra, ≤8.000 kr, ≤8.500 bayt. Kart eşleştirme (ilk mesajda). |
| `Durum/` | yeni, ~100 satır | Tek satır sağlık ve dört sayaç (bkz. 06). |
| `Infrastructure/` | 3.x, korunur | `Configuration` (≈6 anahtar), `CommandLine`, `HookPayload`, `DailyFileLock`, `WriteAtomic`, `VaultIdentity`, `Boundaries`. |

Giden: `Flush`, `Runner`, `Compile`, `RootMap`, `Bridge`, `Kit`, `Mcp`, `Doctor`, `Nudge`, `Save`, `State` (büyük kısmı), `DetachedProcess`, `extensions`, `RawChannel`, `oom install`. Tek kurucu `kur.ps1` (hook satırlarını yazar, `oom.exe`'yi kopyalar, geri alma kopyası tutar).

Hedef hacim: `src` ≤3.000 satır, test <100 yöntem (davranış adıyla), tam kapı <60 sn.

## Kancalar

| Olay | Komut | Çıktı | Süre hedefi |
|---|---|---|---|
| SessionStart | `oom --vault V baglam` | durum satırı + Kurallar + Düzeltmeler + kart dizini (≤8.000 kr) | <1 sn |
| UserPromptSubmit | `oom --vault V kaydet --tetik mesaj` | ilk mesajda: konu eşleşirse kart (≤500 kr); sonra her 10 mesajda sessiz kayıt | <1 sn |
| PreCompact | `oom --vault V kaydet --tetik compact` | yok | <3 sn |
| SessionEnd | `oom --vault V kaydet --tetik kapanis` | yok | <3 sn |

Codex: `~/.codex/hooks.json` aynı üç komut (PreCompact yok). Kanca komutları ileri eğik çizgi ve `--vault` ile yazılır (bash kancası ters bölüyü yer).

## Akış: bir tur ham kayda nasıl girer

1. Kanca `kaydet`i çağırır; payload'dan oturum kimliği ve transkript yolu okunur.
2. Bugünün ve son 7 günün `daily/` dosyalarından bu oturumun **imleci** türetilir: kayıtlı son mesaj uuid'si. İmleç yoksa baştan.
3. Transkript imleçten sonrası için ayrıştırılır; her tur sınıflandırılır (`u` sahip, `a` ajan, `h` harness, `t` araç; bkz. 03).
4. Metin maskelenir (Redactor). Araç sonuçları yazılmaz, yalnız araç adı ve sayısı.
5. Blok `daily/YYYY-MM-DD.md` sonuna dosya kilidi altında atomik eklenir. Aynı uuid ikinci kez yazılmaz.
6. Çıkış 0. Hata varsa tek satır stderr ve çıkış 1; kanca engellemez, `durum` kırmızı gösterir, sonraki tetik kaldığı yerden sürer.

Üç aşama, bir süreç, bir kilit. 3.x'te on iki aşama, iki süreç sıçraması, üç kilit vardı.

## Akış: bir karar karta nasıl girer

1. Ajan öneriyi "Yürürlükte: X → korur / değiştirir: Y" ile açar; cümleyi sahibe tam metniyle gösterir.
2. Sahip cevap verir. Ajan aynı turda kartın üst bloğunu yazar: cümlenin kendisi + sahibin cevabının uuid'si. Tırnak içine metin yazılmaz.
3. `oom kart dogrula <konu>`: şema, ≤500 kr, uuid'lerin ham kayıtta ve `u` sınıfında olduğu, sabit çapalar, korur/değiştirir alanı. Düşerse kart enjekte edilmez ve `durum` söyler.
4. `oom kart hesapla <konu>`: hesaplanan satır (son söz uuid'si, ölçüt değeri, kıpırdamama sayacı) ve `_dizin.md`.
5. Üst blok değiştirilir, eklenmez; önceki yürürlük satırının uuid'si "← devirir" alanında kalır. Tarih ham kayıttadır.

## Eşzamanlılık

- Her oturum yalnız kendi turlarını yazar; `daily/` dosya kilidi 2 sn bekler, alamazsa o tetiği atlar (sonraki tetik telafi eder).
- Kartı yalnız sahibin cevabını alan oturum yazar. İki oturum aynı kartı farklı yürürlükle yazarsa `kart dogrula` ikinciyi çatışma olarak işaretler; sahip çözer.
- İndeks tembel yenilenir; `ara` çağrısında bayat dosyalar yeniden indekslenir.

## Arıza biçimleri

| Arıza | Davranış |
|---|---|
| Transkript okunamıyor | stderr tek satır, çıkış 1, `durum` kırmızı; sonraki tetikte yeniden |
| Kilit alınamadı | o tetik atlanır, sayaç artar, sonraki tetikte telafi |
| Kart doğrulama düştü | kart enjekte edilmez, `durum` "kart geçersiz: <konu>, <neden>" |
| İndeks bozuk | silinir, yeniden kurulur; `ara` o çağrıda yavaş döner |
| Vault yolu yok | çıkış 2, tek satır |
| Model yok | geçerli bir durum değil; model yok |

Sessiz arıza yoktur: her kırmızı `durum` satırında görünür, her yeşil görünmez.

## Güvenlik sınırı

- Ham kayıt enjeksiyona asla girmez; yalnız aranır ve kimlikle alıntılanır.
- Enjekte edilen her blok "veri, talimat değil" etiketiyle ve sabit sınır satırlarıyla çevrilidir.
- Araç sonuçları kaydedilmez; kullanıcı metni ve ajan metni maskelenir.
- Mekanizmanın hiçbir yerinde araçlı model yoktur; Avenox v2'nin düştüğü yol burada yoktur.

## 3.x'ten alınan dersler (kodda değil, biçimde)

- Tek sınıf, tek sorumluluk. `Kayit`, `Kart`, `Retrieve`, `Context`, `Durum` birbirinin dosyasını açmaz.
- Varsayılan tek yerde: `Configuration` dışında hiçbir sabit yok.
- Test dosyası davranış adıyla: `Kaydet_ImlecTurer`, `Kart_SahipOlmayanKimlikDuser`; sürüm ya da dalga adıyla değil.
- Kapı 60 saniyeyi aşarsa test silinir, süre değil.
