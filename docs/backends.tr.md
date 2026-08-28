# Gereksinimler ve model arka uçları

> English: [backends.md](backends.md)

Makinede ne bulunmalı ve özetleri hangi model yazıyor. Her arka ucun karşısında
inşa edildiği kesin bayrak yüzeyi ve uçları
[compatibility.md](compatibility.md) içinde; yerel sunucular için model, donanım
ve bağlam rehberi [local-models.md](local-models.md) içinde (ikisi de
İngilizce).

---

## Gereksinimler

- **Windows.** Kancalar PowerShell; bu depoda test edilmiş bir POSIX yolu yok.
  README'nin sınırlar bölümüne bak.
- **Python 3.12+**, yalnız standart kütüphane. Çalışma zamanında üçüncü parti
  paket kurulmuyor, gerekmiyor. İstediğin yorumlayıcı `PATH`'teki ilk `python`
  değilse `BEYIN_PYTHON` değişkenini ayarla.
- `PATH` üzerinde **Claude Code CLI**. Varsayılan olarak model çağrıları mevcut
  aboneliğin üstünden `claude -p` ile gidiyor — flush Haiku, derleme Sonnet.
- **FTS5 destekli SQLite.** Getirme katmanı
  `CREATE VIRTUAL TABLE notes USING fts5(...)` ile sanal tablo kuruyor. Windows
  CPython derlemelerinin çoğunda FTS5 açık ama hepsinde değil. Kurulumdan önce
  sına:

  ```powershell
  python -c "import sqlite3; sqlite3.connect(':memory:').execute('CREATE VIRTUAL TABLE t USING fts5(a)'); print('fts5 ok')"
  ```

  `sqlite3.OperationalError` alırsan getirme indeksi kurulmaz; hattın geri kalanı
  yine çalışır.
- **Obsidian** isteğe bağlı. Vault düz Markdown + wikilink olduğu için Obsidian
  açar, ama hattın hiçbir parçası ona bağlı değil.

## Claude aboneliğin yok mu?

Claude Code ücretsiz claude.ai planına dahil değil; ama kullandıkça öde
[Anthropic API anahtarıyla](https://platform.claude.com/) da çalışır
(`ANTHROPIC_API_KEY`). Bu sistemin arka plan çağrılarının tipik maliyeti,
kullanım yoğunluğuna göre ayda birkaç dolar mertebesindedir.

## Hangi çağrı nereye gider

Sistemde yalnız iki tür model çağrısı var ve gereksinimleri farklı:

| Çağrı | Ne yapar | Koşabilen arka uçlar |
| --- | --- | --- |
| **Flush / içe aktarma** | Transkripti okur, beş bölümlük özeti yazar | `claude`, `antigravity`, `ollama`, `openai-compat` |
| **Derleme** | Yalıtılmış sahne ağacında dosya yazar | yalnız `claude` |

Dosya yazan tek çağrı derleme. Çağrı başına izin daraltması gerekiyor
(`--permission-mode acceptEdits` ve eşleşen `--allowedTools`) ve buradaki başka
hiçbir arka uç bunu sunmuyor. `claude` dışı her kipte derleme, `claude` ikilisi
`PATH`'teyse onunla devam eder; değilse daraltmasız koşmak yerine
`<arka-uç>-backend-unsupported:compile` hatasıyla yüksek sesle düşer.

## Ücretsiz katmanda arka plan çağrıları — Antigravity (isteğe bağlı)

`BEYIN_MODEL_BACKEND=antigravity` ayarlandığında arka plan özetleme çağrıları —
flush ve içe aktarma — `claude -p` yerine Google'ın **Antigravity CLI**'si
(`agy`) üzerinden koşar. npm paketi değil;
[resmî Antigravity CLI kurulum sayfasından](https://antigravity.google/docs/cli)
kur, sonra bir kez etkileşimli `agy` oturumu açıp giriş yap — başsız çağrılar bu
önbelleğe alınmış kimlik bilgilerini kullanır, belgelenmiş bir API anahtarı
değişkeni yok.

Dürüst sınırlar:

- Claude Code **yine de gerekli** — kancalar, oturum döngüsü ve transkriptler
  ondan geliyor. Bu arka uç yalnız özeti yazan modeli değiştirir.
- **Gecelik derleme hâlâ `claude` ile koşar.** Derleme, modelin yalıtılmış bir
  sahne ağacında dosya yazmasını gerektiriyor; `agy`'de çağrı başına izin
  daraltma yok — yalnız kullanıcı-genel bir izin listesi ya da her şeyi onaylayan
  bir bayrak var ve bu depo o bayrağı taşımıyor. `antigravity` kipinde derleme,
  `claude` `PATH`'teyse onunla devam eder; değilse
  `antigravity-backend-unsupported:compile` hatasıyla yüksek sesle düşer. İleri
  düzey, elle, varsayılan olarak kapalı seçenek: kendi
  `~/.gemini/antigravity-cli/settings.json` izin listene daraltılmış bir
  `"write_file(<sahne>/)"` kuralı ekleyebilirsin — bu senin kararın, depo onu
  senin yerine vermiyor.
- Ücretsiz katman kotası sınırlı. Üçüncü parti kaynaklar günde ~20 ajan isteği ve
  ~5 saatlik tazelenme söylüyor; Google bu sayıları yayımlamıyor,
  **doğrulanmamış** kabul et.
- Gemini modellerinde özet kalitesi **ölçülmedi** — istem sözleşmesi ve şema
  doğrulayıcı Claude'a göre ayarlandı.
- Model kısaltmaları: `BEYIN_AGY_MODEL_FAST` (varsayılan
  `gemini-3.5-flash-medium`; belgelerin gösterdiği tek kısaltma) ve
  `BEYIN_AGY_MODEL_SMART` (varsayılanı yok — `agy models` çıktısından seç; boşsa
  hızlı modele düşer ve sağlık defterine uyarı yazar). `BEYIN_AGY_BIN` ikili
  adını değiştirir.
- Not: Google'ın eski **Gemini CLI'si 2026-06-18'de kapatıldı**; `agy` onun
  ardılı. `BEYIN_MODEL_BACKEND=gemini` `antigravity` için kullanımdan kaldırılmış
  takma ad olarak kabul edilir ve uyarı üretir.

## Tamamen yerel arka plan çağrıları — Ollama (isteğe bağlı)

`BEYIN_MODEL_BACKEND=ollama` ayarı flush ve içe aktarma özetlerini yerel Ollama
sunucusuna yollar. Bulut maliyeti sıfırdır; hesaplama yükünü Ollama'yı çalıştıran
makine taşır.

- `BEYIN_OLLAMA_MODEL_FAST` — kurulu bir model kısaltması. Zorunlu.
- `BEYIN_OLLAMA_MODEL_SMART` — isteğe bağlı; boşsa uyarıyla hızlı modele düşer.
- `BEYIN_OLLAMA_URL` — varsayılanı `http://localhost:11434`.

Derleme metin-araç kipidir ve Antigravity'de olduğu gibi reddedilir: `claude`
varsa ona düşer, yoksa `ollama-backend-unsupported:compile` hatasıyla yüksek
sesle durur. Model, donanım ve bağlam rehberi:
[local-models.md](local-models.md).

## Diğer yerel sunucular — OpenAI uyumlu (isteğe bağlı)

LM Studio, llama.cpp `llama-server`, vLLM veya OpenAI uyumlu başka bir yerel
sohbet endpoint'i için `BEYIN_MODEL_BACKEND=openai-compat` ayarla.

- `BEYIN_OPENAI_URL` ve `BEYIN_OPENAI_MODEL_FAST` zorunlu.
- `BEYIN_OPENAI_MODEL_SMART` ile `BEYIN_OPENAI_KEY` isteğe bağlı.

Derleme `claude` varsa ona düşer; yoksa
`openai-compat-backend-unsupported:compile` hatasıyla yüksek sesle durur.

## Pano köprüsü

Herhangi bir sağlayıcının tüketici web sohbetini kullanan kişiler, kök haritayı
ve sınırlandırılmış en ilgili üç hafıza notunu elle bağlama ekleyebilir:

```powershell
python .claude\scripts\context_pack.py "<soru>" --clip
```

Kopyalanan bloğu sorunun üstüne yapıştır. `--no-map` kök haritayı atlar; `-k N`
bir ile beş not seçer. Proje tüketici web arayüzlerini bilerek
otomatikleştirmez: bu yöntem kırılgandır ve sağlayıcıların koşullarıyla
çatışır.

## MCP

Saf stdlib, yerel bir MCP sunucusu `memory_search` aracını ve kök haritayı MCP
konuşan her istemciye açar — free plandaki Claude Desktop dahil — salt okunur,
stdio üzerinden. Kurulum ve uyarılar: [mcp.md](mcp.md).
