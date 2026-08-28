# Origin of Memory

**Windows'ta Claude Code için projeden projeye taşınan kalıcı hafıza —
oturumlar kendiliğinden özetlenir, birbirine bağlı bir Markdown bilgi tabanına
derlenir ve bir sonraki mesajının içine geri enjekte edilir.**

[![tests](https://github.com/Capslockiller/origin-of-memory/actions/workflows/ci.yml/badge.svg)](https://github.com/Capslockiller/origin-of-memory/actions/workflows/ci.yml)
[![Lisans: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> English: [README.md](README.md)

API anahtarı yok, vektör veritabanı yok, harici servis yok. Vault diskinde düz
Markdown (Obsidian ile uyumlu). PowerShell kancaları ve yalnız standart
kütüphaneyi kullanan Python 3.12.

## Nasıl görünüyor

```
  HAFIZASIZ                            ORIGIN OF MEMORY İLE
  ───────────────────────────────      ───────────────────────────────
  > stemmer'ı neden bıraktık?          > stemmer'ı neden bıraktık?

  Önceki oturumların bağlamı           [model turu görmeden, kanca
  bende yok — ilgili kararı             2 notu enjekte etti]
  yapıştırabilir misin?                 · turkce-stemmer-karari.md
                                        · retrieval-tokenizasyon.md

                                       Snowball Türkçede fena
                                       over-stem ediyordu — alakasız
                                       kelimeleri tek köke çöktürüyordu.
                                       Sabit uzunlukta kesmeye geçildi.
```

<!-- Mekanizmanın çizimi; ekran görüntüsü değil. Gerçek bir terminal görüntüsü
     istenirse (README veya sosyal önizleme için) onu orkestratör gerçek bir
     oturumdan üretmeli; sentetik üretilmemeli. -->

## Hızlı başlangıç

```powershell
git clone https://github.com/Capslockiller/origin-of-memory.git
cd origin-of-memory
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1
```

Önerilen plan için Enter'a bas, otomatik algılananları gözden geçir, kurmak için
bir kez daha Enter'a bas. O ikinci Enter'dan önce hiçbir şey yazılmıyor. Ön
ayarlar, etkileşimsiz planlar, alt seviye `install.ps1`, yükseltme ve kaldırma:
[docs/install.tr.md](docs/install.tr.md).

## Bu bana göre mi?

| Sen | Ne alırsın | Ne gerekir | Ön ayar |
| --- | --- | --- | --- |
| Abonelikle Claude Code kullanan biri | Tamamı: otomatik yakalama, gece derlemesi, her oturuma ve her mesaja enjekte edilen hafıza | Windows, Python 3.12+, Claude Code CLI | `cloud` |
| Yerelde çalışsın isteyen biri | Aynı hat, ama oturum özetlerini kendi modelin yazar — Ollama, LM Studio, llama.cpp, vLLM veya Antigravity. Kancalar ve gece derlemesi için `claude` yine gerekli | Yukarıdakiler + yerel bir sunucu ya da `agy` CLI | `local` veya `hybrid` |
| Yalnızca eski konuşmalarında arama yapmak isteyen biri | claude.ai / Codex / Gemini dışa aktarımlarını içeri alır, MCP istemcisinden veya pano köprüsünden sorgularsın. Kanca yok, otomatik yakalama yok, derleme yok | Windows, Python 3.12+, MCP konuşan bir istemci ve içe aktarımları özetleyecek yerel bir backend | `lite` |

## Diğer ajanlar

Her mesaja ilgili üç notu iten mesaj başına enjeksiyon, Claude Code'un sunduğu
bir prompt-submit kancası gerektirir. Diğer ajanlar yine de
[bağlam köprüsü](docs/context-bridge.md) aracılığıyla **statik** kök haritayı
okuyabilir; köprü bunu vault kökündeki mevcut `AGENTS.md`, `GEMINI.md` veya
`CLAUDE.md` dosyalarına yazar. Ayrıca [MCP sunucusuyla](docs/mcp.md) talep üzerine
ya da pano köprüsüyle elle arama yapılabilir. Statik bağlam retrieval değildir;
kancasız otomatik yakalama planlanıyor ama henüz mevcut değil. Kesin sınırlar
için [host yüzeyleri karşılaştırmasına](docs/compatibility.md#host-surfaces)
bakın.

## Nasıl çalışıyor

```
  SessionEnd / PreCompact  ->  flush.py    ->  daily/YYYY-AA-GG.md
                               (haiku)         günde bir dosya

  saat 18'den sonra, log değiştiyse
                           ->  compile.py  ->  knowledge/concepts/*.md
                               (sonnet)        atomik, çapraz bağlı makaleler
                               yalıtılmış sahne ağacında koşar; her yazma
                               canlı vault'a terfi etmeden yeniden doğrulanır

                           ->  rootmap.py  ->  knowledge/index.md + hubs/
                           ->  retrieve.py ->  .state/notes.db (FTS5)

  SessionStart      ->  kök harita + companion hafıza, 16k karakter tavanı
  UserPromptSubmit  ->  mesaj üstünden BM25, ilk 3 tam not enjekte edilir
```

Tam açıklamalı hat ve eksiksiz özellik listesi:
[docs/features.tr.md](docs/features.tr.md). Uygulama:
[docs/architecture.md](docs/architecture.md) (İngilizce).

## Ölçülen sonuçlar

Yazarın kendi korpusunda ölçüldü (yaklaşık 500 kavram notu), 130 gerçek geçmiş
sorudan oluşan gold sete karşı — 125'i puanlandı, 5'i kanarya olarak ayrıldı.
Sayılar dürüst ama tek bir korpus, tek bir dil karışımı ve tek bir kişinin soru
dağılımı: ölçüt değil, "çalışıyor" kanıtı sayın. Yöntem:
[docs/evaluation.md](docs/evaluation.md) (İngilizce).

| Ölçüm | Önce | Sonra |
| --- | --- | --- |
| recall@3 (yargıçsız, gold not ilk 3'te) | %0 | **%83,2** (104/125) |
| recall@5 | %0 | **%91,2** (114/125) |
| Getirme kancası gecikmesi, p95 | — | **347 ms** |
| Derleyici çağrı başı giriş tabanı | 152,8K krkt | **56,1K krkt** (−%63) |

%0 tabanı retorik değil: bu iş yapılmadan önce okuma kancası, hiç oturum
açılmayan bir klasöre proje kapsamında kayıtlıydı — yani hiçbir oturuma hiçbir
hafıza ulaşmıyordu.

Kod tabanına yapılan bağımsız bir inceleme; kod kalitesi, güvenlik, hata
yönetimi ve bağımlılık sağlığında 5/5, mimari, performans ve testlerde 4/5
verdi. Bu, kodun incelenmesiydi — projenin onaylanması değil — ve incelemeyi
yapanın projeyle bir ilişkisi yok.

> **Düzeltme, 2026-08-28:** recall@5 daha önce %84 (105/125) olarak
> yayımlanmıştı. O koşu üç sonuç getirmiş ama sütunu `top5` diye
> etiketlemişti. `limit=5` ile doğru koşu 114/125 = %91,2 veriyor; recall@3
> etkilenmedi. Ayrıntılı açıklama: [docs/evaluation.md](docs/evaluation.md).

## Alternatiflerden farkı

Kategori *ajan hafızası*. Bu projenin dört noktada farklı çalıştığını seçim
yapmadan önce bilmekte fayda var:

- **Hafıza itilir, aranmaz.** Modelin çağırmayı hatırlaması gereken bir araç
  yok. Notları kanca seçiyor ve model turu görmeden enjekte ediyor — çünkü
  ajanlar getirme araçlarını düzenli olarak yeterince çağırmıyor; yalnız model
  bakmayı hatırlarsa çalışan bir şey hafıza değildir.
- **Recall sayısı gerçek bir korpustan geliyor.** Yazarın kendi vault'una karşı
  125 gerçek geçmiş soru; gold set ve sınırları eksiksiz anlatılıyor. Bu bir
  kamuya açık benchmark skoru değil ve senin vault'una taşınacağı iddia
  edilmiyor.
- **Çalışma zamanında sıfır bağımlılık.** Python 3.12 standart kütüphanesi ve
  PowerShell. Gömme modeli yok, vektör deposu yok, ayakta tutulacak servis yok,
  döndürülecek anahtar yok. Tüm indeks tek bir SQLite dosyası.
- **Bilerek Windows-yerel.** "Windows'ta da çalışır" diyen bir POSIX aracı
  değil. Bu aynı zamanda en büyük sınırı — aşağıya bak.

Soyağacı ve neyin nereden geldiği: [docs/attribution.md](docs/attribution.md)
(İngilizce).

## Ne gerekiyor

- **Windows**, **Python 3.12+** (yalnız stdlib) ve `PATH` üzerinde **Claude Code
  CLI**. Model çağrıları varsayılan olarak mevcut aboneliğin üstünden
  `claude -p` ile gidiyor — flush Haiku, derleme Sonnet.
- **FTS5 destekli SQLite.** Windows CPython derlemelerinin çoğunda var; sına:
  `python -c "import sqlite3; sqlite3.connect(':memory:').execute('CREATE VIRTUAL TABLE t USING fts5(a)')"`.
- Claude aboneliğin yok mu? Claude Code kullandıkça öde
  [Anthropic API anahtarıyla](https://platform.claude.com/) da çalışır; arka
  plan çağrılarının maliyeti ayda birkaç dolar mertebesinde.
- Arka plan çağrıları için başka bir model mi? Özetleri Antigravity, Ollama veya
  OpenAI uyumlu herhangi bir yerel sunucu yazabilir. Derleme yine `claude`
  istiyor ve her arka ucun önce okunması gereken dürüst sınırları var.

Tüm gereksinimler, her arka uç, ortam değişkenleri ve sınırları:
[docs/backends.tr.md](docs/backends.tr.md).

## Sınırlar

- **Yalnız Windows.** Kancalar PowerShell, dosya kilidi `msvcrt` bölge kilidine
  düşüyor. Burada test edilmiş bir macOS/Linux yolu yok. O platformlar için bu
  projenin türediği üst projeye bak:
  [avenoxbeyin](https://github.com/avenoxai/avenoxbeyin) (macOS test edildi,
  Linux edilmedi).
- **Tek korpusta ölçüldü.** Buradaki tüm sayılar yazarın kendi vault'undan ve
  kendi soru setinden. Senin recall'ın farklı çıkacak. Kendi gold setini kur —
  [docs/evaluation.md](docs/evaluation.md) nasıl kurulacağını ve istatistiksel
  tabanın neden yalnız büyük değişimi gösterdiğini anlatıyor.
- **Hassas veri filtresi yok.** `secret_guard.py` kimlik bilgisi kalıplarını
  yakalıyor: anahtar, token, bağlantı dizesi, parola ataması. Sağlık bilgisini,
  hukuki statüyü, mali ayrıntıyı veya yazılmaya rıza göstermemiş üçüncü kişileri
  **yakalamıyor.** Oturumlarında bu tür malzeme varsa özetlenir, derlenir ve
  saklanır. Bu bilinen bir açık; dürüst hâliyle [SECURITY.md](SECURITY.md)
  içinde.
- **Derleyici girdisi artık sınırlı, ama bedava değil.** Kök harita katmanı çağrı
  başı tabanı %63 kesti; kavram başına bir satırla sonsuza dek büyüyen mükerrer
  kontrol registry'si ise artık günlüğün eşleştiği hub'lar ile en son güncellenen
  50 kavrama daraltılıyor ve 400 satırda sert tavana vuruyor. Sentetik 1000
  kavramlık korpusta 67.800 → 15.806 karakter. Bedeli gerçek: model kısmi bir
  mükerrer görüşü görüyor, bunu prompt'ta tek satırla öğreniyor ve seçilen
  satırların dışında kalan bir mükerreri kaçırabiliyor.
- **Makineler arası derleme işbirliğiyle önleniyor, garanti edilmiyor.** Derleme
  kilidi kendisini hangi makinenin tuttuğunu kaydediyor; başka bir makinenin
  canlı kilidini gören koşu onun yanında derlemek yerine atlıyor. Bu, senkron
  aracının kilit dosyasını yaymış olmasına bağlı; yeterince hızlı bir yarış hâlâ
  aradan sıyrılabilir.
- **Transkripte giren web metni vault'a özetlenebilir.** Güvenilmeyen veri
  sınırlayıcıları yerinde ama bir dışlama listesi yok.

## Atıf

Bu proje, [Avenox](https://avenox.lol) tarafından yazılan
[avenoxbeyin v2](https://github.com/avenoxai/avenoxbeyin) (MIT) projesinden
**fork edilerek değil, uyarlanarak** türedi. O projenin SPEC-V2 belgesinden
temiz oda yöntemiyle kuruldu, yerel Windows'a taşındı ve ardından üst projede
bulunmayan katmanlarla genişletildi: kullanıcı seviyesinde kanca kaydı, FTS5 BM25
mesaj başına getirme, kök harita katmanı, sır karartma, ingest ailesi ve gold set
değerlendirmesi. Paylaşılan kod geçmişi yok — "fork" değil, "uyarlama" denmesinin
sebebi bu.

Bilgi derleme deseni Andrej Karpathy'nin LLM bilgi tabanı gist'ine dayanıyor:
<https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f>

Tam soyağacı ve gerekçesi: [docs/attribution.md](docs/attribution.md)
(İngilizce).

## Belgeler

| | |
| --- | --- |
| [docs/install.tr.md](docs/install.tr.md) | Ön ayarlar, planlar, doğrudan kurucu, yükseltme, kaldırma |
| [docs/features.tr.md](docs/features.tr.md) | Eksiksiz özellik listesi, Türkçe tasarımı, companion katmanı |
| [docs/backends.tr.md](docs/backends.tr.md) | Gereksinimler ve her model arka ucu |
| [docs/architecture.md](docs/architecture.md) | Hattın nasıl kurulduğu (İngilizce) |
| [docs/retrieval.md](docs/retrieval.md) | Sıralama, oturum çıpaları, bilinen sınırlar (İngilizce) |
| [docs/evaluation.md](docs/evaluation.md) | Gold set, ölçüt, düzeltme (İngilizce) |
| [docs/mcp.md](docs/mcp.md) · [docs/local-models.md](docs/local-models.md) · [docs/compatibility.md](docs/compatibility.md) | MCP kurulumu, yerel model rehberi, ne test edildi (İngilizce) |
| [docs/context-bridge.md](docs/context-bridge.md) · [docs/tool-free-compile.md](docs/tool-free-compile.md) | Diğer ajanlar için statik bağlam, araçsız derleme modu (İngilizce) |

**Ajanlar için:** bir kodlama ajanını [INSTALL-AGENT.md](INSTALL-AGENT.md)
dosyasına yönlendirdiğinde kurulumun tamamını kendisi koşabilir. Bu deponun
*içinde* çalışan ajanlar [AGENTS.md](AGENTS.md) dosyasını okumalı.

## Katkı

Issue ve pull request'lere açık — [CONTRIBUTING.md](CONTRIBUTING.md). Sürüm
geçmişi [CHANGELOG.md](CHANGELOG.md), güvenlik politikası ve tehdit modeli
[SECURITY.md](SECURITY.md) içinde.

## Lisans

MIT. [LICENSE](LICENSE) dosyasına bak.
