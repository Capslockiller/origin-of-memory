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

- Blok başlığının hemen altında bir çapa yorumu durur:

```
<!-- session:<oturum> ts:<yazım anı> turns:<a>-<b> source:<claude|codex> kayit:v1 son:<son tur uuid> bayt:<n> -->
```

  `bayt`, `son` turunu üreten transkript satırının dosya başından UTF-8 bayt konumudur (sahip onayı, 18 Eki). Okuyucu doğrudan o satıra gider ve uuid'i doğrular; alan yoksa ya da tutmazsa transkripti baştan okur.
- Kimlik = `<oturum uuid>/<mesaj uuid>`. Kısa biçim (ilk 8 hex) yalnız insan okuması içindir; doğrulama tam uuid ister.
- Saat, turun yerel tarihi bloğun yazıldığı günle aynıysa `HH:mm`, değilse `yyyy-MM-dd HH:mm` olarak yazılır. Sonradan yazılan kuyruk ve geri doldurma böylece tarihini kaybetmez; blok yine yazıldığı günün dosyasına düşer.

## Tur sınıfları

| Sınıf | Kaynak | Kaydedilen |
|---|---|---|
| `u` sahip | `type: user`, içerik metin, harness deseni yok; ya da `tool_result` içinde sahibin cevabı (`The user answered:` ile başlayan soru penceresi cevabı, `To tell you how to proceed, the user said:` içeren yazılı ret notu) | tam metin (maskeli), 20.000 kr üstü kesilir ve `(+N kr)` ile işaretlenir; pencere cevabının başlığına `· cevap` eklenir. Plan onayı (yalnız tıklama) yazılmaz |
| `a` ajan | `type: assistant`, metin blokları | metin (maskeli), tur başına 4.000 kr üstü kesilir; araç çağrıları yalnız ad ve sayı |
| `h` harness | `type: user` ama `<system-reminder>`, `<command-…>`, "Another Claude session sent a message", "[SYSTEM NOTIFICATION", alt ajan geri dönüşü | yalnız başlık satırı: `### [h …] 17:50 · ajan mesajı` ; metin yok |
| `t` araç | `tool_use` / `tool_result` | yazılmaz |

Yalnız `u` sınıfı sahibin sözü sayılır. Kart ve Düzeltmeler'deki her kimlik `u` olmak zorundadır; 10 Eki'de bir ajan raporu `user` tipiyle gelmişti ve bu kural onu dışarıda tuttu.

Alt ajan ve şerit transkriptleri (`beyin-flush-*`, `.claude/worktrees/*`, `Agent` çağrılarının kendi oturumları, `session_meta.payload.source` değeri `exec` olan Codex rollout'ları) kaydedilmez. Kök: sahibin kendi oturumları (`~/.claude/projects/*`, `~/.codex/sessions/*`).

Claude transkriptinde oturum kimliği istenen oturumdur (kancanın `session_id`'si, dosya adı). Desktop'un "btw" yan sorusu oturumu yeni bir dosyaya çatallar; dosya ana oturumun kayıtlarıyla başlar. Bu kopyalar atlanır, onları ana oturumun kendi dosyası yazar.

## Tetikler ve imleç

| Tetik | Ne yazar |
|---|---|
| UserPromptSubmit, her 10. mesaj | imleçten bu yana biriken turlar. İmleç satırına `bayt` ile atlanır, yalnız ondan sonrası okunur (100 MB transkriptte ~30 ms) |
| PreCompact | compaction öncesi tüm bekleyen turlar (compact özeti yazılmaz; ham zaten var) |
| SessionEnd | kalan turlar (Desktop'ta seyrek gelir) |
| SessionStart (`acilis`) | öteki oturumların bekleyen turları, eşiksiz. Desktop oturumu açık bırakır ve SessionEnd çoğu zaman gelmez, kuyruk burada tamamlanır. Son çapası dosyanın son yazımından 60 sn'den fazla yeni olan transkript okunmadan atlanır. Pencere `--gun N` (varsayılan 2). |

- İmleç saklanmaz, türetilir: bugünün ve son 7 günün `daily/` dosyalarında bu oturuma ait son `u`/`a` uuid'si. Dosya silinirse kayıt baştan ve idempotent olarak yeniden yazılır; aynı uuid iki kez girmez.
- İmleç bulunamazsa yalnız pencere içindeki turlar (bugün ve son 7 gün) yazılır. Desktop eski transkriptlere meta veri ekleyip dosyayı tazeler; pencere tabanı eski bir oturumun baştan yazılmasını ve çift kaydı önler.
- 8 saati aşan oturumlar gün değiştirir: yeni gün yeni dosya, aynı oturum kimliği.
- Kanca tetikleri (`mesaj`, `compact`, `kapanis`, `acilis`) hiçbir koşulda kanca akışını engellemez, her zaman 0 döner.
  - Başarıda stdout boştur.
  - Hata stdout'a tek bir `{"systemMessage": …}` olarak yazılır ve sahibe görünür.
  - `acilis` son 24 saatin hata sayısını aynı nesnede `hookSpecificOutput.additionalContext` ile ajana da verir.
- Her çağrı `<durum kökü>/kaydet.jsonl`'a bir satır yazar. Dosya 2 MB'ta `kaydet.1.jsonl`'a döner; metin girmez. Alanlar:
  - tetik, oturum, ms, blok, hata;
  - sonuç: `yazdi`, `bekliyor`, `bos`, `dosya-yok`, `kimlik-yok`, `mesgul` ya da `hata`;
  - `okunan_kb` ve `yol` (`bayt` ya da `tam`);
  - `acilis` ve `tara` için ek olarak `aday`, `atlanan`, `mesgul`, `hata_sayisi`.
- Başka bir sürecin tuttuğu transkript (paylaşım ya da kilit ihlali) son yazımı 24 saat içindeyse `mesgul` sayılır: atlanır, sonra yakalanır, alarm vermez. Daha eskiyse görünür hata kalır.
- `tara` elle koşulur: özet satırı basar, hatada 1 döner.
  - `--gun N` penceresinde imleç ve kayıtlı uuid türetimi max(7, N) gün geriye bakar.
  - Pencere içindeki kayıtsız `u`, `a` ve `h` turlarını imlecin önünde olsalar da yazar (delik doldurma). Compact türü yazılmaz.
  - Hiçbir uuid'i iki kez yazmaz, imleci geri götürmez.
  - Kancalar delik doldurmaz ve 7 gün geriye bakar.

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

1. Oturum açıldıysa en geç 10. mesajda, kapanışta ya da bir sonraki oturum açılışında iz vardır.
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
