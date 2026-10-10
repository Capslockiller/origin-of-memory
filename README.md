# Defter

**Model çağırmayan hafıza defteri.** Claude Code ve Codex oturumlarını ham ve kimlikli kaydeder, sahibin onayladığı konu kartlarını doğrular ve oturum başında iter, ikisinin üstünde arar. Mekanizmanın içinde hiçbir dil modeli çağrısı yoktur; anlam yazmak ajanın işidir, oturumun içinde.

> **Durum: 4.0 tasarım aşaması, kod yok.** Bu depo şu an yalnız mimariyi ve kararları taşır. 3.x C# hattı [`v3`](../../tree/v3) dalında, 0.x Python hattı [`v0`](../../tree/v0) dalında durur; ikisi de arşivdir.

## Neden

- 43 günde 446 commit, 11,7K satır C#, 23,6K satır test ve bir yol haritası vardı; proje yine kendi haritasından saptı. Kaybolan **durum** değildi (6.352 karakter doğru durum kaydı vardı), **yürürlükteki karar ve neyi devirdiği**ydi.
- Projenin kendi ölçümleri sürekliliği makine derlemesinin değil **el katmanının** taşıdığını gösterdi: ham tur araması derlenmiş notu LoCoMo'da 70,7'ye 36,2 geçti; derleme 14 gün durunca kopma artmadı; kayıtlı iki yanlış hatırlamanın ikisi derlenmiş nottan geldi.
- Model döngünün içindeyken üç kez aynı eklem kırıldı: login (12 Eyl), ortam mirası (5 Eki), OAuth (10 Eki). Modeli döngüden çıkarınca bu arızalar sınıf olarak kalkar.

Ayrıntı: [docs/00-neden.md](docs/00-neden.md).

## Üç fiil, üç kanca

| Fiil | Kanca | Ne yapar |
|---|---|---|
| `kaydet` | UserPromptSubmit (10 mesajda bir), PreCompact, SessionEnd | Oturumun yeni turlarını kimlikli, maskeli, deterministik olarak `daily/`ye yazar. Hedef ~1 sn, sıfır model. |
| `kart` | ajan çağırır | Konu kartını doğrular ve indeksler: alıntılar yalnız mesaj kimliğiyle; yürürlükteki karar neyi devirdiğini söyler. |
| `ara` | ajan çağırır | BM25 arama: ham kayıt + kartlar + Kurallar + Düzeltmeler (+ salt okur arşiv). |
| `baglam` | SessionStart | Tek satır durum + Kurallar + Düzeltmeler + kart dizini + açılan konunun kartı. ≤8.000 karakter. |

`durum`, `baglam`ın ilk satırıdır: son kayıt yaşı · kart sayısı · indeks tazeliği · kaydedilmemiş karar sayısı. Yeşilse görünmez.

## Kart: yürürlükteki karar

Bir konu kartı dört satır (≤500 karakter) ve bir hesaplanan satırdır:

- **Yürürlükte:** ajanın sahibe koyduğu cümle + sahibin cevabı (mesaj kimliğiyle). Hangi önceki kararı koruduğu ya da devirdiği başta yazar.
- **Ölçüt:** "yolda sayılır ancak <gözlenebilir şey>; bunu kıpırdatmayan iş sapmadır."
- **Kapalı yönler:** sahibin hayırları, kimlikle; neyi kapattığına göre sabit.
- **Sıradaki:** tek satır, yürürlükten türer.
- *Hesaplanan (makine):* sahibin son sözü birebir · ölçüt değeri · kaç oturumdur kıpırdamadı.

Kart tırnak içinde elle yazılmış metin taşımaz. Alıntı kimlikten çekilir; kimlik sahibin mesajı değilse satır geçersizdir. Her yeni öneri **"Yürürlükte: X → korur / değiştirir: Y"** ile açılır; yürürlükteki kararı ne koruyan ne deviren öneri üzerine yazmaz, **çatışma** olarak görünür.

## İlkeler (kısa)

1. Mekanizmada model çağrısı yok. Login, duman testi, doğrulayıcı, karantina, retry: yok.
2. Ham kayıt eksiksiz ve sıkıcı; asla ret, asla zaman aşımı.
3. Alıntı kimlikle. Çeviri ve yanlış atıf yapısal olarak imkânsız.
4. Makine doğrular, anlam yazmaz. Kart ham kayıttan her an yeniden kurulabilir.
5. Kanca bütçesi ≤8.000 karakter; sır değeri hiçbir katmana yazılmaz.
6. Tek şerit, adım ≤300 satır, her adımın ölçülen kapısı. Sürü yok.

Tam liste ve "asla yapılmayacaklar": [docs/01-ilkeler.md](docs/01-ilkeler.md).

## Belgeler

| | |
|---|---|
| [00-neden](docs/00-neden.md) | Ölçümler ve sapma hikâyesi; önceki sanat |
| [01-ilkeler](docs/01-ilkeler.md) | Değişmezler ve asla listesi |
| [02-mimari](docs/02-mimari.md) | Katmanlar, bileşenler, akış, arıza biçimleri, 3.x'ten korunanlar |
| [03-ham-kayit](docs/03-ham-kayit.md) | Ham kayıt biçimi: kimlik, blok, imleç, maske |
| [04-kart](docs/04-kart.md) | Kart biçimi, doğrulama, devirme, çatışma, hesaplanan satır |
| [05-ara-ve-baglam](docs/05-ara-ve-baglam.md) | Arama korpusu, çekimserlik, oturum başı bütçe |
| [06-durum](docs/06-durum.md) | Tek satır sağlık ve sayaçlar |
| [07-yol](docs/07-yol.md) | Adımlar, kapılar, tavanlar, geçiş |
| [08-olcutler](docs/08-olcutler.md) | Projenin kendi başarı ölçütleri; token ve bakım bütçesi |
| [kararlar](docs/kararlar.md) | K-01…K-13: her karar gerekçesi ve neyi devirdiğiyle |
| [sozluk](docs/sozluk.md) | Terimler |

## Kimin için

Tek sahip, kendi makinesi, Obsidian vault'u, Claude Code ve Codex. Takım, bulut ve çok kullanıcı hedef değildir. Lisans: PolyForm Noncommercial 1.0.0.

---

### English abstract

Defter ("ledger") is a memory layer for Claude Code and Codex that calls no language model. A hook writes every session's turns to markdown, deterministically, with message ids and secret masking. Per topic, a ≤500-character card holds the decision in force (the agent's proposal plus the owner's reply, by message id), one observable on-course criterion, the owner's closed directions, and the next action. The machine validates cards, computes staleness, serves BM25 search over the raw record and the cards, and injects at most 8,000 characters at session start. Quotes are never typed into a card; they are resolved from the immutable record by id, and an id that is not the owner's message fails validation. Every new proposal must state which standing decision it keeps or overturns. Status: design only, no code. Evidence and prior art: [docs/00-neden.md](docs/00-neden.md).
