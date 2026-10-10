# 03 — Ham kayıt

## Amaç

Her oturum, hiçbir şeye bağlı olmadan iz bırakır. Kayıt sıkıcıdır: özet yok, yorum yok, model yok. Her tur bir kimlik taşır ve bu kimlik sistemin geri kalanının tek atıf birimidir.

## Dosya ve blok

- Yer: `<vault>/daily/YYYY-MM-DD.md` (yerel saat). Dosya yoksa frontmatter ile açılır (`type: daily`, `kayit: v1`).
- Bir oturum bir günde birden çok blok bırakabilir (her tetikte yeni turlar). Blok başlığı:

```
## kayit · oturum 3b8a48ad-2bf8-4818-9ffc-ad915720fdef · claude-code · <proje> · 14:47–15:13 · tur 1–9
```

- Tur:

```
### [u e185c1d8-3990-4547-a539-f6a15b7d02d5] 14:47
Oom projesini devral. Sence proje nasıl ilerlemeli? …

### [a 7c0d…] 14:49 · araç: Skill ×1, Bash ×2
Oom projesini devralıyorum. Önce Odena protokolünü yükleyip …
```

- Kimlik = `<oturum uuid>/<mesaj uuid>`. Kısa biçim (ilk 8 hex) yalnız insan okuması içindir; doğrulama tam uuid ister.

## Tur sınıfları

| Sınıf | Kaynak | Kaydedilen |
|---|---|---|
| `u` sahip | `type: user`, içerik metin, harness deseni yok | tam metin (maskeli), 20.000 kr üstü kesilir ve `(+N kr)` ile işaretlenir |
| `a` ajan | `type: assistant`, metin blokları | metin (maskeli), tur başına 4.000 kr üstü kesilir; araç çağrıları yalnız ad ve sayı |
| `h` harness | `type: user` ama `<system-reminder>`, `<command-…>`, "Another Claude session sent a message", "[SYSTEM NOTIFICATION", alt ajan geri dönüşü | yalnız başlık satırı: `### [h …] 17:50 · ajan mesajı` ; metin yok |
| `t` araç | `tool_use` / `tool_result` | yazılmaz |

Yalnız `u` sınıfı sahibin sözü sayılır. Kart ve Düzeltmeler'deki her kimlik `u` olmak zorundadır; 10 Eki'de bir ajan raporu `user` tipiyle gelmişti ve bu kural onu dışarıda tuttu.

Alt ajan ve şerit transkriptleri (`beyin-flush-*`, `.claude/worktrees/*`, `Agent` çağrılarının kendi oturumları) kaydedilmez. Kök: sahibin kendi oturumları (`~/.claude/projects/*`, `~/.codex/sessions/*`).

## Tetikler ve imleç

| Tetik | Ne yazar |
|---|---|
| UserPromptSubmit, her 10. mesaj | imleçten bu yana biriken turlar |
| PreCompact | compaction öncesi tüm bekleyen turlar (compact özeti yazılmaz; ham zaten var) |
| SessionEnd | kalan turlar, blok kapanır |

- İmleç saklanmaz, türetilir: bugünün ve son 7 günün `daily/` dosyalarında bu oturuma ait son `u`/`a` uuid'si. Dosya silinirse kayıt baştan ve idempotent olarak yeniden yazılır; aynı uuid iki kez girmez.
- 8 saati aşan oturumlar gün değiştirir: yeni gün yeni dosya, aynı oturum kimliği.
- Çıkış hiçbir zaman kanca akışını engellemez; `kaydet` yalnız stderr'e tek satır yazar.

## Maskeleme

- 3.x `Redactor` kuralları olduğu gibi: API anahtarı, token, parola, bağlantı dizgesi desenleri ve vault'un sır dosya listesi.
- Maskeleme yazımdan önce, bellekte; maskesiz metin hiçbir yere düşmez (3.x'in `RawChannel`ı yoktur).
- Maske işareti `[maske:tur]`; maskelenen tur sayısı `durum`da görünür.
- Maskeleme zaman aşımına uğrarsa tur atlanmaz; metin yerine `[maske: zaman aşımı, N kr]` yazılır ve kırmızı sayılır.

## Codex

- `~/.codex/sessions/*.jsonl` rollout biçimi 3.x ayrıştırıcısıyla okunur; sınıflandırma aynı tabloyla.
- `hooks.json` içinde aynı `kaydet` komutu (PreCompact olayı yoksa yalnız mesaj ve kapanış).

## Boyut ve ömür

- Ölçülen taban: 195 oturum / 30 gün, ortanca 13 sahip mesajı, ortanca 100 kr. Beklenen günlük ham kayıt 50–500 KB; yıl ≈ 50–100 MB markdown. BM25 için sorun değildir; enjeksiyona asla girmediği için bağlam maliyeti sıfırdır.
- Silme ve arşivleme sahibin kararıdır; Defter silmez. 12 aydan eski `daily/` dosyalarının `arsiv/daily/` altına taşınması `durum`da öneri olarak görünür, otomatik yapılmaz.

## Garantiler

1. Oturum açıldıysa en geç 10. mesajda ya da kapanışta iz vardır.
2. Aynı tur iki kez yazılmaz.
3. Yazılan hiçbir satır sonradan değiştirilmez (yalnız sahip, elle).
4. Hiçbir kimlik yanlış sınıfta değildir; `h` asla `u` olmaz.
5. `kaydet` hiçbir koşulda ağ, model ya da ikinci süreç kullanmaz.

## 3.x'ten fark

| 3.x `flush` | 4.0 `kaydet` |
|---|---|
| `claude -p` ile özet, 240 sn zaman aşımı | model yok, <3 sn |
| ayrık süreç, duman testi, retry kuyruğu, karantina | tek süreç, tek dosya kilidi |
| beş başlıklı özet; doğrulayıcı reddederse `red/` | tam tur; ret kavramı yok |
| imleç state.db'de | imleç ham kayıttan türer |
| kapsama 7 günde %10 | hedef %100 (kapı) |
