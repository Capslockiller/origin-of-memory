# Origin of Memory

<!-- <hesap> yerine bu deponun barındığı GitHub hesabını/organizasyonunu yaz. -->
[![tests](https://github.com/Capslockiller/origin-of-memory/actions/workflows/ci.yml/badge.svg)](https://github.com/Capslockiller/origin-of-memory/actions/workflows/ci.yml)
[![Lisans: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> İngilizce sürüm: [README.md](README.md)

**Origin of Memory, [Claude Code](https://claude.com/claude-code) için kalıcı bir
hafıza sistemidir — bir "ikinci beyin".** Sıradan Claude Code oturumlarına
projeden projeye taşınan kalıcı hafıza verir: her oturum kendiliğinden bir günlük
loga özetlenir, gece koşan bir derleyici o logları birbirine bağlı Markdown bilgi
tabanına damıtır, kancalar da ilgili notları her yeni oturuma ve her mesaja geri
enjekte eder. Hiçbir adım modelin "hafıza dosyalarını güncellemeyi hatırlamasına"
bağlı değildir. Vault diskinde düz Markdown'dır (Obsidian ile uyumlu); API
anahtarı, vektör veritabanı veya harici servis yoktur.

Sistem Windows üzerinde yerel çalışır: PowerShell kancaları ve yalnız standart
kütüphaneyi kullanan Python 3.12.

---

## Nasıl çalışıyor

```
  oturum biter                 konuşma sıkışmak üzere
  (SessionEnd)                       (PreCompact)
       |                                    |
       +----------------+-------------------+
                        v
                 flush-launch.ps1        bir saniyenin altında ayrılır
                        v
                     flush.py            claude -p --model haiku
              transkripti okur, beş bölümlük özeti yazar
              secret_guard.py girişte ve çıkışta sırları karartır
                        v
                daily/YYYY-AA-GG.md      makine yazar, sen değil
                        |
        (saat 18'den sonra, günde bir kez, değişen log varsa)
                        v
                    compile.py           claude -p --model sonnet
        knowledge/ + tek günlük log'un yalıtılmış kopyasında koşar
        yalnız knowledge/concepts/**, index-full.md, log.md yazabilir
        her değişiklik canlı vault'a terfi etmeden önce yeniden doğrulanır
                        v
        +---------------+---------------+
        v                               v
   rootmap.py                      retrieve.py build
   knowledge/index.md              .state/notes.db
   (kompakt kök harita)            (SQLite FTS5 indeksi)
   knowledge/hubs/*.md
        |                               |
        v                               v
  session-start.ps1              memory-retrieve.ps1
  SessionStart:                  UserPromptSubmit:
  companion hafıza +             mesaj üstünden BM25 ->
  kök harita, 16k krkt tavan     ilk 3 tam not enjekte edilir
```

İki tasarım kararı taşıyıcı:

- **Getirmeyi kanca yapar, model değil.** Claude'a arama aracı verilip "git bak"
  denmiyor; ajanlar böyle araçları yeterince çağırmıyor. Seçim, model turu
  görmeden önce kancada bitiyor.
- **Kancalar kullanıcı seviyesinde kayıtlı.** Beyin her projeden yazıyor ve her
  projeden okuyor; yalnız vault klasörünün içinden değil.

## Özellikler

- **Otomatik oturum yakalama.** Hem `SessionEnd` hem `PreCompact` flush ediyor;
  ortada sıkıştırılan bir konuşma kaybolmuyor.
- **Gece bilgi derlemesi.** Günlük loglar `knowledge/concepts/` altında atomik
  kavram makalelerine dönüşüyor; her bağın yazılı bir gerekçesi oluyor, tam tablo
  `knowledge/index-full.md` içinde makale başına tek satır tutuluyor.
- **Mesaj başına getirme.** SQLite FTS5, `bm25(notes, 8, 6, 3, 1)` ağırlıklarıyla
  (başlık, aliases, etiket, gövde) ilk 3 tam notu döndürüyor; not başına 1.500,
  toplamda 4.500 karakter tavanı var. 12 karakterin altındaki mesajlar ve eğik
  çizgi komutları atlanıyor, oturum içi defter aynı notu tekrar enjekte etmiyor.
- **Kök harita katmanı.** `rootmap.py`, `knowledge/index.md`'yi 4.000 karakter
  bütçesinin altında, `knowledge/hubs/*.md` merkezlerine yönlendiren bir konu
  haritası olarak tutuyor. Yayından önce her kavramın bir merkezde göründüğü
  doğrulanıyor.
- **Derleme yalıtımı.** Derleyici canlı vault'u hiç düzenlemiyor; `0700` izinli
  `<vault>/.stage/compile-stage-*` ağacında çalışıyor. Sonuçtaki dosya manifestosu
  karşılaştırılıyor, silme ve kapsam dışı yazma PolicyError'a düşüyor, yalnız
  izinli yollar atomik olarak terfi ediyor.
- **Sır karartma.** `secret_guard.py`, kimlik bilgisi kalıplarını özetçiye
  girerken ve çıkarken `[SIR:<kalıp>]` ile değiştiriyor; derleyici çıktısını da
  terfi kapısında tarıyor.
- **Geçmiş içe aktarma.** `ingest.py`, Claude Code arşivlerini
  (`~/.claude/projects`), Codex rollout'larını (`~/.codex/sessions`), claude.ai
  dışa aktarım ZIP'lerini ve Google Takeout Gemini arşivini içeri alıyor.
- **Türkçe birinci sınıf.** Aşağıdaki [Türkçe desteği](#türkçe-desteği) bölümü.
- **MCP hafıza sunucusu.** Saf stdlib, yerel bir MCP sunucusu `memory_search`
  aracını ve kök haritayı MCP konuşan her istemciye açar — free plandaki
  Claude Desktop dahil — salt okunur, stdio üzerinden. Kurulum:
  [docs/mcp.md](docs/mcp.md).
- **Sağlık kontrolü skill'i.** `beyin doktor`, kanca kaydını, betikleri, günlük
  log tazeliğini ve son derleme durumunu tek tabloda veriyor.

## Ölçülen sonuçlar

Yazarın kendi korpusunda ölçüldü (yaklaşık 500 kavram notu), 130 gerçek geçmiş
sorudan oluşan gold sete karşı — 125'i puanlandı, 5'i kanarya olarak ayrıldı.
Sayılar dürüst ama tek bir korpus, tek bir dil karışımı ve tek bir kişinin soru
dağılımı: ölçüt değil, "çalışıyor" kanıtı sayın. Yöntem:
[docs/evaluation.md](docs/evaluation.md).

| Ölçüm | Önce | Sonra |
| --- | --- | --- |
| recall@3 (yargıçsız, gold not ilk 3'te) | %0 | **%83** (104/125) |
| recall@5 | %0 | **%84** (105/125) |
| Getirme kancası gecikmesi, p95 | — | **347 ms** |
| Derleyici çağrı başı giriş tabanı | 152,8K krkt | **56,1K krkt** (−%63) |

%0 tabanı retorik değil: bu iş yapılmadan önce okuma kancası, hiç oturum
açılmayan bir klasöre proje kapsamında kayıtlıydı — yani hiçbir oturuma hiçbir
hafıza ulaşmıyordu.

## Hızlı başlangıç

<!-- yazan: codex · gpt-5.6-sol -->
Birincil yol kurulum sihirbazıdır. Önerilen kurulum için Enter'a basın, otomatik
algılanan planı inceleyin ve kurmak için bir kez daha Enter'a basın:

```powershell
git clone https://github.com/Capslockiller/origin-of-memory.git
cd origin-of-memory
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1
```

İki ekranlı yol Claude Code, Ollama, LM Studio, llama.cpp, vLLM, donanım,
Documents yönlendirmesi ve Claude Desktop MCP yapılandırmasını algılar. Yalnızca
algılanan bir varsayılanı değiştirmek istediğinizde `Custom` seçin.

| Ön ayar | Yakalama ve derleme | Flush / içe aktarma | Okuma erişimi |
| --- | --- | --- | --- |
| `cloud` | Claude Code kancaları + Claude derlemesi | Claude | Kancalar; isteğe bağlı MCP / pano |
| `hybrid` | Claude Code kancaları + Claude derlemesi | Antigravity, Ollama veya OpenAI-uyumlu | Kancalar; isteğe bağlı MCP / pano |
| `local` | Claude Code kancaları + Claude derlemesi | Antigravity veya yerel uç | Varsayılan MCP + pano; ayrıca kancalar |
| `lite` | Yok — otomatik yakalama ve derleme yok | Algılanan yerel backend veya yalnızca içe aktarma | MCP + pano; hafıza dışa aktarım ZIP dosyalarından gelir |

`local`, kancalar ve derleme için yine `claude` CLI ister. `lite` Claude Code
kullanmaz; otomatik yakalama ve gece derlemesi yoktur.

<!-- yazan: codex · gpt-5.6-sol -->
- **Rehberli yerel modeller.** Sihirbaz Ollama, LM Studio, llama.cpp ve vLLM'i
  algılar. Doğrulanmış Ollama etiketlerini donanım uyumuna göre sıralar, onayla
  Ollama kurabilir ve LM Studio için elle GUI kurulumunu açıklar.

Ajanın otomatik algılamalı kurulumu için `-Recommended` kullanın; gerçek
çalıştırmadan önce kuru koşu onay ekranını kullanıcıya mutlaka raporlayın:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Recommended -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Recommended
```

Açıkça hazırlanmış tekrarlanabilir planlar için `-Answers` kullanılmaya devam eder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Answers C:\plan\yolu.json -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Answers C:\plan\yolu.json
```

Plan sözleşmesi:

```json
{"preset":"cloud|hybrid|local|lite","vault":"<yol>","backend":"claude|antigravity|ollama|openai-compat|none","backend_env":{"BEYIN_*":"<değer>"},"mcp":true,"skills":["beyin-doktor"],"force":false,"install_runtime":false,"pull_models":[]}
```

Doğrulama kuralları ve doldurulmuş örnekler:
[kurulum sihirbazı sözleşmesi](docs/setup-wizard.md). `-DryRun`, kurulum,
ortam değişkeni ve MCP eylemlerinin tamamını basar; hiçbir şey yazmaz.

Proje kayıtlarını geri almak ve istenirse kopyalanan çalışma dosyalarını
kaldırmak için önce güvenli kaldırıcının kuru koşusunu yapın. Düzenlediği her
dosyayı yedekler; `daily/`, `knowledge/`, companion dosyaları ve diğer vault
içeriğine asla dokunmaz:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1 -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1
```

### Doğrudan kurucu

`install.ps1` alt seviye bağımsız yol olarak kalır; varsayılan davranışı tüm
skill'leri ve altı kancayı kurmaktır.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -VaultPath C:\vault\yolu -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -VaultPath C:\vault\yolu
```

Yükseltmede betik ve kancaların üzerine yazmak için `-Force`; yalnız seçili
skill'leri kurmak için `-SkillFilter beyin-doktor,beyin-ice-aktar` kullanılır.

Kurulum betiği:

1. Vault dizini yoksa (sorarak) oluşturur.
2. `scripts/` ve `hooks/` klasörlerini `<vault>\.claude\` altına, vault iskeletini
   `<vault>\` içine, `skills/` klasörünü `<kullanıcı>\.claude\skills\` altına
   kopyalar.
3. `template/hub-config.example.json` dosyasını
   `<vault>\.claude\scripts\hub-config.json` olarak kopyalar — kendi konu
   merkezlerini burada tanımlarsın; dosya bir kez oluştuktan sonra üzerine
   yazılmaz.
4. `<kullanıcı>\.claude\settings.json` içine altı kanca kaydeder; dosyayı önce
   yedekler ve zaten kayıtlı olanları atlar.
5. Python 3.12+ veya `claude` CLI `PATH`'te değilse uyarır.

Sonra ne olur:

- `cloud`, `hybrid` ve `local` ayarlarında bir sonraki Claude Code oturumun hafıza bloğuyla
  açılır, her mesajda üç ilgili nota kadar not enjekte edilir.
- İlk `daily/YYYY-AA-GG.md` o oturum kapanınca düşer.
- Saat 18'den sonra, değişen günlük içerik bulan ilk oturum kapanışı ayrık bir
  derleme koşusu başlatır; `knowledge/` o koşu bitince belirir.
- `lite` ayarında dışa aktarım ZIP dosyalarını içe aktarır, MCP veya pano
  köprüsünü kullanırsın; otomatik yakalama ve derleme bilerek yoktur.
- Önce geçmişi doldurmak istersen: `python scripts/ingest.py status`, ardından
  `claude`, `codex`, `web` veya `gemini` alt komutları.

Ayrıntılı hat: [docs/architecture.md](docs/architecture.md).

**Ajanlar için:** bir kodlama ajanını [INSTALL-AGENT.md](INSTALL-AGENT.md)
dosyasına yönlendirdiğinde kurulumun tamamını — ön koşul kontrolü ve doğrulama
dahil — kendisi koşabilir. Bu deponun *içinde* çalışan ajanlar
[AGENTS.md](AGENTS.md) dosyasını okumalı.

## Skill'ler

<!-- yazan: codex · gpt-5.6-sol -->
Sihirbaz her skill'i ayrı sorar; `beyin-doktor` ile `beyin-ice-aktar`
varsayılan evet, diğerleri varsayılan hayırdır. Doğrudan `install.ps1`,
`-SkillFilter` verilmedikçe tüm skill'leri kopyalar. İkisi mekanizmanın parçası:
`beyin-doktor` (hattın sağlık kontrolü) ve
`beyin-ice-aktar` (claude.ai dışa aktarım ZIP'ini vault'a işler).

Diğer dördü — `companion`, `orchestration`, `codex-fleet`,
`gece-vardiyasi` — **yazarın çalışma setinden genelleştirilmiş
kopyalar**: delegasyon politikası, gece taslak-modu vardiya protokolü ve Codex CLI
kullanım kılavuzu. Desenler işe yaradığı için
yayımlandılar, sana uygun oldukları için değil; birkaçı sende kurulu olmayan
araçları varsayıyor.

Her birinin ne yaptığı ve "genelleştirilmiş"in burada ne demek olduğu:
[skills/README.md](skills/README.md).

`skills/companion` bir kimlik değil, **yapı örneğidir** — aşağıya bak.
[template/rules.example.md](template/rules.example.md) ise oturum kancasının
enjekte ettiği kalıcı kurallar dosyasının eşlik eden örneği.

## Gereksinimler

- **Windows.** Kancalar PowerShell; bu depoda test edilmiş bir POSIX yolu yok.
  [Sınırlar](#sınırlar) bölümüne bak.
- **Python 3.12+**, yalnız standart kütüphane. Çalışma zamanında üçüncü parti
  paket kurulmuyor, gerekmiyor. İstediğin yorumlayıcı `PATH`'teki ilk `python`
  değilse `BEYIN_PYTHON` değişkenini ayarla.
- **Claude Code CLI** `PATH` üzerinde. Varsayılan olarak model çağrıları mevcut
  aboneliğin üstünden `claude -p` ile gidiyor — flush Haiku, derleme Sonnet.
  - **Aboneliğin yok mu?** Claude Code ücretsiz claude.ai planına dahil değil;
    ama kullandıkça öde [Anthropic API anahtarıyla](https://platform.claude.com/)
    da çalışır (`ANTHROPIC_API_KEY`). Bu sistemin arka plan çağrılarının tipik
    maliyeti, kullanım yoğunluğuna göre ayda birkaç dolar mertebesindedir.
  - **Ücretsiz katmanda arka plan çağrıları (isteğe bağlı).**
    `BEYIN_MODEL_BACKEND=antigravity` ayarlandığında arka plan özetleme
    çağrıları — flush ve içe aktarma — `claude -p` yerine Google'ın
    **Antigravity CLI**'si (`agy`) üzerinden koşar. npm paketi değil;
    [resmî Antigravity CLI kurulum sayfasından](https://antigravity.google/docs/cli)
    kur, sonra bir kez etkileşimli `agy` oturumu açıp giriş yap — başsız
    çağrılar bu önbelleğe alınmış kimlik bilgilerini kullanır, belgelenmiş bir
    API anahtarı değişkeni yok. Dürüst sınırlar:
    - Claude Code **yine de gerekli** — kancalar, oturum döngüsü ve
      transkriptler ondan geliyor. Bu arka uç yalnız özeti yazan modeli
      değiştirir.
    - **Gecelik derleme hâlâ `claude` ile koşar.** Derleme, modelin yalıtılmış
      bir sahne ağacında dosya yazmasını gerektiriyor; `agy`'de çağrı başına
      izin daraltma yok — yalnız kullanıcı-genel bir izin listesi ya da her şeyi
      onaylayan bir bayrak var ve bu depo o bayrağı taşımıyor. `antigravity`
      kipinde derleme, `claude` `PATH`'teyse onunla devam eder; değilse
      `antigravity-backend-unsupported:compile` hatasıyla yüksek sesle düşer.
      İleri düzey, elle, varsayılan olarak kapalı seçenek: kendi
      `~/.gemini/antigravity-cli/settings.json` izin listene daraltılmış bir
      `"write_file(<sahne>/)"` kuralı ekleyebilirsin — bu senin kararın, depo
      onu senin yerine vermiyor.
    - Ücretsiz katman kotası sınırlı. Üçüncü parti kaynaklar günde ~20 ajan
      isteği ve ~5 saatlik tazelenme söylüyor; Google bu sayıları
      yayımlamıyor, **doğrulanmamış** kabul et.
    - Gemini modellerinde özet kalitesi **ölçülmedi** — istem sözleşmesi ve şema
      doğrulayıcı Claude'a göre ayarlandı.
    - Model kısaltmaları: `BEYIN_AGY_MODEL_FAST` (varsayılan
      `gemini-3.5-flash-medium`; belgelerin gösterdiği tek kısaltma) ve
      `BEYIN_AGY_MODEL_SMART` (varsayılanı yok — `agy models` çıktısından seç;
      boşsa hızlı modele düşer ve sağlık defterine uyarı yazar). `BEYIN_AGY_BIN`
      ikili adını değiştirir.
    - Not: Google'ın eski **Gemini CLI'si 2026-06-18'de kapatıldı**; `agy` onun
      ardılı. `BEYIN_MODEL_BACKEND=gemini` `antigravity` için kullanımdan
      kaldırılmış takma ad olarak kabul edilir ve uyarı üretir.
  <!-- yazan: codex · gpt-5.6-sol -->
  - **Tamamen yerel arka plan çağrıları (isteğe bağlı).**
    `BEYIN_MODEL_BACKEND=ollama` ayarı flush ve içe aktarma özetlerini yerel
    Ollama sunucusuna yollar. Bulut maliyeti sıfırdır; hesaplama yükünü Ollama'yı
    çalıştıran makine taşır. Kurulu bir model kısaltmasını
    `BEYIN_OLLAMA_MODEL_FAST` ile ver. `BEYIN_OLLAMA_MODEL_SMART` isteğe
    bağlıdır; boşsa uyarıyla hızlı modele düşer. `BEYIN_OLLAMA_URL`
    varsayılanı `http://localhost:11434` adresidir. Derleme Antigravity'de
    olduğu gibi reddedilir: `claude` varsa ona düşer, yoksa
    `ollama-backend-unsupported:compile` hatasıyla yüksek sesle durur. Model,
    donanım ve bağlam rehberi için [Yerel model arka uçları](docs/local-models.md)
    belgesine bak.
  <!-- yazan: codex · gpt-5.6-sol -->
  - **Diğer yerel sunucular (isteğe bağlı).** LM Studio, llama.cpp
    `llama-server`, vLLM veya OpenAI uyumlu başka bir yerel sohbet endpoint'i
    için `BEYIN_MODEL_BACKEND=openai-compat` ayarla. `BEYIN_OPENAI_URL` ve
    `BEYIN_OPENAI_MODEL_FAST` zorunlu; `BEYIN_OPENAI_MODEL_SMART` ile
    `BEYIN_OPENAI_KEY` isteğe bağlıdır. Derleme `claude` varsa ona düşer; yoksa
    `openai-compat-backend-unsupported:compile` hatasıyla yüksek sesle durur.
  - **Pano köprüsü.** Herhangi bir sağlayıcının tüketici web sohbetini kullanan
    kişiler, kök haritayı ve sınırlandırılmış en ilgili üç hafıza notunu elle
    bağlama ekleyebilir:

    ```powershell
    python .claude\scripts\context_pack.py "<soru>" --clip
    ```

    Kopyalanan bloğu sorunun üstüne yapıştır. `--no-map` kök haritayı atlar;
    `-k N` bir ile beş not seçer. Proje tüketici web arayüzlerini bilerek
    otomatikleştirmez: bu yöntem kırılgandır ve sağlayıcıların koşullarıyla
    çatışır.
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

## Türkçe desteği

Türkçe sonradan eklenmiş bir uyum katmanı değil, tasarım hedefi:

- **Çift biçimli indeksleme.** En az üç karakterlik her kelime hem ham katlanmış
  biçimiyle, hem de beş karakterden uzunsa beş karakterlik önekiyle indeksleniyor.
  Sorgu tam olarak aynı fonksiyondan geçiyor, yani indeks ile sorgu hiç ayrışmıyor.
- **Açık i-katlama.** Noktalı/noktasız I (`I` `ı` `İ` `i`) `casefold()` öncesi
  elle yazılmış bir çeviri tablosundan geçiyor; yerel ayara bağlı
  `lower()`/`upper()` hiç kullanılmıyor. `turkish_fold()` hem `retrieve.py` hem
  `rootmap.py` içinde aynı tanım.
- **Bilerek kök bulucu yok.** Snowball'ın Türkçe stemmer'ı fena over-stem ediyor,
  alakasız kelimeleri tek köke çöktürüyor. Sabit uzunlukta kesme + ham biçim,
  ayrı kavramları birleştiren bir stemmer'dan ölçülebilir biçimde güvenli.
- Gelen özetçi ve derleyici prompt'ları Türkçe makale yazıyor. Başka bir dil
  istersen `scripts/flush.py` içindeki `build_flush_prompt()` ile
  `scripts/compile.py` içindeki `COMPILE_PROMPT`'u düzenle; getirme katmanı
  Türkçe katlaması dışında dilden bağımsız, o katlama da diğer Latin alfabesi
  dilleri için zararsız.

## Kendi companion protokolünü yazmak

`session-start.ps1`, üretilmiş kök haritanın yanında bir *companion hafıza*
katmanı da enjekte ediyor: vault içinde `*850-Companion` kalıbına uyan dizini
arıyor, `Last-Session.md`, `Threads.md`, `Kurallar.md` (kalıcı kurallar) ve
`Journal.md` dosyalarını okuyup modeli `Core.md`'ye yönlendiriyor.

**Bu dosyalar depoda gelmiyor.** Kimlik katmanı — asistanının sana kim olduğu,
sana nasıl hitap ettiği, hangi kuralı asla çiğnemeyeceği — sana ait; jenerik bir
şablon hiç yoktan kötü olurdu. Mekanizmanın tek beklentisi:

- dizin adı `850-Companion` ile bitsin;
- `Last-Session.md` içinde `## Session:` başlıkları ve bir `## Previous` sınırı
  olsun;
- `Threads.md` içinde `## Active` bölümü (`### ` maddeleri ve `**Status:**`
  satırlarıyla) ve bir `## Closed` sınırı olsun;
- `Kurallar.md` ile `Journal.md` düz Markdown olsun — enjekte edilen sırasıyla
  ilk 60 satır ve son `##` girdisi.

Olmayan dosyalar sessizce atlanıyor. Makine katmanı (`daily/`, `knowledge/`,
getirme) companion katmanı yazsan da yazmasan da çalışıyor.

`skills/companion/SKILL.md` bu yapıyı yer tutucu içerikle belgeliyor;
[template/rules.example.md](template/rules.example.md) ise kancanın beklediği
biçimde örnek bir kurallar dosyası.

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
- **Derleyici maliyeti korpusla büyüyor.** Kök harita katmanı çağrı başı tabanı
  %63 kesti, ama mükerrer kontrol registry'si hâlâ kavram sayısıyla ölçekleniyor.
- **Gece derlemesi tek makinelik.** Tetik talebi yerel bir dosya; tek bir senkron
  vault'u paylaşan iki makine aynı anda derleyebilir.

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

Tam soyağacı ve gerekçesi: [docs/attribution.md](docs/attribution.md).

## Katkı

Issue ve pull request'lere açık — [CONTRIBUTING.md](CONTRIBUTING.md). Sürüm
geçmişi [CHANGELOG.md](CHANGELOG.md), güvenlik politikası ve tehdit modeli
[SECURITY.md](SECURITY.md) içinde.

## Lisans

MIT. [LICENSE](LICENSE) dosyasına bak.
