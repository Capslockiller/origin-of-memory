---
yazan: codex
model: gpt-5
---

# Origin of Memory 2.0

Origin of Memory, Claude Code için Windows-native bir hafıza hattıdır. 2.0 sürümü; yakalama, derleme, getirme, sağlık denetimi, kurulum ve salt okunur MCP yüzeyini yorumlayıcı ve script zinciri yerine tek, self-contained C#/.NET programında toplamak üzere yeniden kuruluyor.

Burada hafıza bir alışkanlık değil, mekanizmadır. Tasarımı üç karar belirler:

- dağıtılan ürün tek bir self-contained `oom.exe` dosyasıdır;
- asıl yazma yolu zamanlanmış taramadır, hook'lar yalnız hızlandırıcıdır;
- model metin alıp metin döndürür; dosyayı yalnız `oom.exe` yazar.

## Durum

Yeniden kurulum sürüyor. `main`, 2.0 hattıdır. Önceki Python/PowerShell uygulaması [`v0`](https://github.com/Capslockiller/origin-of-memory/tree/v0) dalında okunabilir; son sürümü [`v0.7.0`](https://github.com/Capslockiller/origin-of-memory/releases/tag/v0.7.0)'dır. v0 yalnız referanstır, kodu 2.0'a kopyalanmaz.

9 Eylül 2026 tarihli test ölçümü:

```text
Başarısız! - Başarısız:     7, Başarılı:    92, Atlanan:     0, Toplam:    99, Süre: 2 s - Oom.Tests.dll (net9.0)
```

Bu sonuç bir sürüm iddiası değildir. Ölçüm orkestratör tarafından `main` üzerinde (`a739c5b`, 9 Eylül 2026) `dotnet test Oom.sln -c Release` ile yapıldı. Kalan yedi kırmızı yara (Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098) fixture kurmadan davranış iddia ediyor; hükümleri `progress.md` içinde, sahibin kararıyla şimdilik kırmızı bırakıldı. Bilinen entegrasyon boşlukları [docs/architecture.md](docs/architecture.md) ve [docs/install.md](docs/install.md) içindedir; yara bazındaki tablo [docs/scars.md](docs/scars.md) içindedir.

## Repo yerleşimi

- `src/Oom/<Component>/` — sorumluluklara ayrılmış C# exe kaynağı.
- `tests/Oom.Tests/Scars/` — geçmişteki her yara için bir `Fact` içeren 99 xUnit testi.
- `bench/` — yalnız ölçümde kullanılan, ürünle dağıtılmayan Python araçları.
- `docs/` — mimari, kurulum, yara durumu, atıf ve sürüm geçmişi.

## Derleme ve test

Proje bugün `net9.0-windows10.0.19041.0` hedefinde derleniyor. Bağlayıcı hedef .NET 10 LTS'tir; SDK kurulduğunda hedef framework satırının değiştirilmesi D1 geçişidir.

```powershell
dotnet build Oom.sln -c Release
dotnet test Oom.sln -c Release
```

Exe projesinde `win-x64`, `SelfContained` ve `PublishSingleFile` ayarları açıktır.

## Komutlar

Aşağıdaki tablo kısa 2.0 komut sözleşmesidir. Yeniden kurulum sürdüğü için bazı uçtan uca bağlantılar henüz tamamlanmamıştır.

| Komut | İş |
| --- | --- |
| `context [--json]` | SessionStart bağlam bloğunu üretir. |
| `retrieve --hook` | `UserPromptSubmit` için kapılı BM25 getirmeyi çalıştırır. |
| `retrieve --query "<sorgu>" --json` | Entegrasyonlara ham sıralı hafıza döndürür. |
| `flush --session <id> --reason sessionend\|precompact` | Tek oturumu özetler; hook yolu yalnız hızlandırıcıdır. |
| `sweep [--dry-run]` | Asıl zamanlanmış yakalama yolunu çalıştırır. |
| `compile [--dry-run]` | Daily kayıtlarını kavram notlarına derler. |
| `ingest claude\|codex [--dry-run] [--max N]` | Arşiv transkriptlerini geriye dönük işler. |
| `doctor [--fix] [--json]` | Sağlığı raporlar ve onarım ister. |
| `save "<metin>" [--compile]` | Daily'ye doğrudan kontrol noktası yazar. |
| `save --session-json <dosya>` | Dış `Session` nesnesini normal flush yoluna verir. |
| `mcp [--enable\|--disable]` | Salt okunur MCP yüzeyini sunar veya ayarlar. |
| `install [--uninstall] [--from-v0]` | Mekanizmayı kurar, kaldırır veya göç ettirir. |
| `bench [--backend claude\|local]` | Kabul ölçümlerini çalıştırır. |

## Yara disiplini

2.0 koddan önce yara envanteriyle başladı. `Y-001`–`Y-099` aralığındaki her madde tam bir xUnit `Fact` testine bağlıdır; yara ancak kendi testi yeşil olduğunda kapanır. İlgili kod doğmadan test kırmızıdır; açıklama veya sürüm iddiası testin ve ölçümün yerine geçmez.

## Kabul kapıları

Yayın için yalnız derleme yetmez: bütün yara ve çözüm testleri ile Windows publish; satır bütçesi ve bağımlılık denetimi; gömülü kullanıcı yolu bulunmaması; retrieval recall paritesi; arşiv ve canlı kapsama hedefleri; temiz Windows 11'de kurulumdan getirmeye uçtan uca koşum; uzun kapalılık sonrası geri alım dâhil 30 gün dokunmadan çalışma; güvenlik mutantları; yerel model ölçümü; sabit ingest/MCP/save sözleşmeleri; dört kararlı genişleme arayüzünün belgeli ve testli olması gerekir. Ölçüm sınırı için [bench/README.md](bench/README.md) dosyasına bakın.

## Krediler

Tasarım, Avenox'un MIT lisanslı [avenoxbeyin v2](https://github.com/avenoxai/avenoxbeyin) çalışmasından türemiştir; Andrej Karpathy'nin bilgi tabanı desenini ve Odena Studio'nun MIT lisanslı Origin of Memory v0 çalışmasını kaynak gösterir. Ayrıntı: [docs/attribution.md](docs/attribution.md).
