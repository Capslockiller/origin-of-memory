# Kurulumun tamamı

> English: [install.md](install.md) · Hızlı başlangıç:
> [../README.tr.md](../README.tr.md#hızlı-başlangıç)

README'deki üç komutluk hızlı başlangıç çoğu kişi için hikâyenin tamamı. Bu
sayfa onun altında kalan her şey: ön ayarlar, etkileşimsiz yollar, alt seviye
kurucu, kurucunun gerçekte ne yazdığı ve nasıl geri alınacağı.

Plan dosyasının doğrulama kuralları ve otomatik karar mantığı
[setup-wizard.md](setup-wizard.md) içinde (İngilizce); bu sayfa işin operasyonel
tarafı.

---

## Sihirbaz

```powershell
git clone https://github.com/Capslockiller/origin-of-memory.git
cd origin-of-memory
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1
```

İki ekran. Birincisi Claude Code, Ollama, LM Studio, llama.cpp, vLLM, donanımını,
Documents yönlendirmesini ve Claude Desktop'ın MCP yapılandırmasını algılayıp bir
ön ayar önerir. İkincisi, hiçbir şey yazılmadan önce ortaya çıkan planı gösterir.
Önerilen yol için iki kez Enter; yalnızca algılanan bir varsayılanı değiştirmek
istediğinde `Custom` seç.

Sihirbaz ayrıca doğrulanmış Ollama etiketlerini donanım uyumuna göre sıralar,
açık onayla Ollama kurabilir ve LM Studio için — bir masaüstü uygulamasını
otomatikleştiriyormuş gibi yapmak yerine — elle GUI adımlarını yazar.

## Ön ayarlar

| Ön ayar | Yakalama ve derleme | Flush / içe aktarma | Okuma erişimi |
| --- | --- | --- | --- |
| `cloud` | Claude Code kancaları + Claude derlemesi | Claude | Kancalar; isteğe bağlı MCP / pano |
| `hybrid` | Claude Code kancaları + Claude derlemesi | Antigravity, Ollama veya OpenAI-uyumlu | Kancalar; isteğe bağlı MCP / pano |
| `local` | Claude Code kancaları + Claude derlemesi | Antigravity veya yerel uç | Varsayılan MCP + pano; ayrıca kancalar |
| `lite` | Yok — otomatik yakalama ve derleme yok | Algılanan yerel backend veya yalnızca içe aktarma | MCP + pano; hafıza dışa aktarım ZIP dosyalarından gelir |

`local`, kancalar ve derleme için yine `claude` CLI ister. `lite` Claude Code'u
hiç kullanmaz: otomatik yakalama da gece derlemesi de yoktur.

## Etkileşimsiz yollar

Ajanın otomatik algılamalı koşusu için. Gerçek çalıştırmadan önce kuru koşu onay
ekranını kullanıcıya mutlaka raporla:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Recommended -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Recommended
```

Açıkça hazırlanmış, tekrarlanabilir bir plan için:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Answers C:\plan\yolu.json -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Answers C:\plan\yolu.json
```

Plan sözleşmesi:

```json
{"preset":"cloud|hybrid|local|lite","vault":"<yol>","backend":"claude|antigravity|ollama|openai-compat|none","backend_env":{"BEYIN_*":"<değer>"},"mcp":true,"skills":["beyin-doktor"],"force":false,"install_runtime":false,"pull_models":[]}
```

Doğrulama kuralları ve doldurulmuş örnekler:
[setup-wizard.md](setup-wizard.md). `-DryRun`, kurulum, ortam değişkeni ve MCP
eylemlerinin tamamını basar; hiçbir şey yazmaz.

## Doğrudan kurucu

`install.ps1` alt seviye bağımsız yol olarak kalır; varsayılan davranışı tüm
skill'leri ve altı kancayı kurmaktır.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -VaultPath C:\vault\yolu -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -VaultPath C:\vault\yolu
```

Yükseltmede betik ve kancaların üzerine yazmak için `-Force`; yalnız seçili
skill'leri kurmak için `-SkillFilter beyin-doktor,beyin-ice-aktar`.

Sırasıyla ne yapar:

1. Vault dizini yoksa (sorarak) oluşturur.
2. `scripts/` ve `hooks/` klasörlerini `<vault>\.claude\` altına, vault
   iskeletini `<vault>\` içine, `skills/` klasörünü `<kullanıcı>\.claude\skills\`
   altına kopyalar.
3. `template/hub-config.example.json` dosyasını
   `<vault>\.claude\scripts\hub-config.json` olarak kopyalar — kendi konu
   merkezlerini burada tanımlarsın; dosya bir kez oluştuktan sonra üzerine
   yazılmaz.
4. `<kullanıcı>\.claude\settings.json` içine altı kanca kaydeder; dosyayı önce
   yedekler ve zaten kayıtlı olanları atlar.
5. Python 3.12+ veya `claude` CLI `PATH`'te değilse uyarır.

`.state`, `.stage`, `.import` ve hiçbir `.db` / `.lock` / `.pyc` dosyasını asla
kopyalamaz; `-Force`'u mevcut bir vault üzerinde güvenli kılan da bu: notların,
indeksin ve sağlık durumun program dosyası değil, onlara dokunulmuyor.

## Sonra ne olur

- `cloud`, `hybrid` ve `local` ayarlarında bir sonraki Claude Code oturumun
  hafıza bloğuyla açılır, her mesajda üç ilgili nota kadar not enjekte edilir.
- İlk `daily/YYYY-AA-GG.md` o oturum kapanınca düşer.
- Saat 18'den sonra, değişen günlük içerik bulan ilk oturum kapanışı ayrık bir
  derleme koşusu başlatır; `knowledge/` o koşu bitince belirir.
- `lite` ayarında dışa aktarım ZIP dosyalarını içe aktarır, MCP veya pano
  köprüsünü kullanırsın; otomatik yakalama ve derleme bilerek yoktur.
- Önce geçmişi doldurmak istersen: `python scripts/ingest.py status`, ardından
  `claude`, `codex`, `web` veya `gemini` alt komutları.

Hattın durumunu istediğin an görmek için:

```powershell
python scripts/durum.py
```

…ya da bir Claude Code oturumunda `beyin doktor` de; aynı tabloyu 🟢/🟡/🔴
olarak verir.

## Yükseltme

`git pull`, sonra `install.ps1`'i `-Force` ile (veya sihirbazı aynı ön ayarla)
yeniden koş. Göç adımı yok. FTS indeksine ne olduğu dahil ayrıntılar:
[release-notes-0.2.0.md](release-notes-0.2.0.md#upgrading-from-010) (İngilizce).

## Kaldırma

Önce güvenli kaldırıcının kuru koşusunu yap. Düzenlediği her dosyayı yedekler;
`daily/`, `knowledge/`, companion dosyaları ve diğer vault içeriğine asla
dokunmaz:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1 -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1
```

Proje kayıtlarını geri alır; kopyalanan çalışma dosyalarını ise yalnızca ayrıca
onay verirsen siler.

## Ajanlar için

Bir kodlama ajanını [../INSTALL-AGENT.md](../INSTALL-AGENT.md) dosyasına
yönlendirdiğinde kurulumun tamamını — ön koşul kontrolü ve doğrulama dahil —
kendisi koşabilir. Bu deponun *içinde* çalışan ajanlar bunun yerine
[../AGENTS.md](../AGENTS.md) dosyasını okumalı.
