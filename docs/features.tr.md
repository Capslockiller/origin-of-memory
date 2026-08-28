# Özellikler, ayrıntısıyla

> English: [features.md](features.md)

Eksiksiz özellik listesi, açıklamalı hat, Türkçe tasarım kararları ve kendi
yazdığın companion katmanı. README kısa sürümü tutuyor; bu sayfa onun kısalttığı
şey. Özelliğin değil uygulamanın peşindeysen:
[architecture.md](architecture.md) (İngilizce).

---

## Hat, açıklamalı

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

## Özellik listesi

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
  `<vault>/.stage/compile-stage-*` ağacında çalışıyor. Sonuçtaki dosya
  manifestosu karşılaştırılıyor, silme ve kapsam dışı yazma PolicyError'a
  düşüyor, yalnız izinli yollar atomik olarak terfi ediyor.
- **Frontmatter şema kapısı.** Frontmatter'ı doğrulanmayan sahnelenmiş not terfi
  etmiyor; sorunları sayan bir yan dosyayla `.stage/karantina/sema/` altına
  yönleniyor. Hiçbir şey kendiliğinden onarılmıyor — eksik bir tarihi uydurmak,
  uydurma bir olguyu kalıcı hafızaya kaydetmek olurdu.
- **Sır karartma.** `secret_guard.py`, kimlik bilgisi kalıplarını özetçiye
  girerken ve çıkarken `[SIR:<kalıp>]` ile değiştiriyor; derleyici çıktısını da
  terfi kapısında tarıyor.
- **Geçmiş içe aktarma.** `ingest.py`, Claude Code arşivlerini
  (`~/.claude/projects`), Codex rollout'larını (`~/.codex/sessions`), claude.ai
  dışa aktarım ZIP'lerini ve Google Takeout Gemini arşivini içeri alıyor.
- **Oturum çıpaları.** Flush edilen her günlük blok
  `<!-- session:<id> ts:<ISO8601> -->` taşıyor; derleyici bunu kavram notunun
  kaynaklarına aktarıyor, getirme katmanı enjeksiyondan önce söküyor — yani
  derlenmiş bir iddia, onu üreten oturuma kadar izlenebiliyor. Bkz.
  [retrieval.md](retrieval.md#4-session-anchors-and-what-a-notes-date-means)
  (İngilizce).
- **Tercihe bağlı hibrit sıralama.** `BEYIN_RETRIEVAL=rrf`, BM25'i etiket/alias
  örtüşmesiyle ve tazelikle kaynaştırıyor. Uygulandı ve testleri var ama gold
  sete karşı **ölçülmedi**; varsayılan olmamasının sebebi bu.
- **MCP hafıza sunucusu.** Saf stdlib, yerel bir MCP sunucusu `memory_search`
  aracını ve kök haritayı MCP konuşan her istemciye açar — free plandaki Claude
  Desktop dahil — salt okunur, stdio üzerinden. Kurulum:
  [mcp.md](mcp.md) (İngilizce).
- **Çağrı başına muhasebe.** `.state/calls.jsonl` her model çağrısı için tek
  satır tutuyor — arka uç, model katmanı, bileşen, karakter sayıları, süre,
  sonuç — ve **hiçbir prompt/yanıt içeriği** tutmuyor.
  `python scripts/durum.py` son 7 günü özetliyor.
- **Sağlık kontrolü skill'i.** `beyin doktor`, kanca kaydını, betikleri, günlük
  log tazeliğini, karantina durumunu, indeks tutarlılığını ve son derleme
  durumunu tek tabloda veriyor.
- **Türkçe birinci sınıf.** Aşağıya bak.

## Türkçe desteği

Türkçe sonradan eklenmiş bir uyum katmanı değil, tasarım hedefi:

- **Çift biçimli indeksleme.** En az üç karakterlik her kelime hem ham katlanmış
  biçimiyle, hem de beş karakterden uzunsa beş karakterlik önekiyle
  indeksleniyor. Sorgu tam olarak aynı fonksiyondan geçiyor, yani indeks ile
  sorgu hiç ayrışmıyor.
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

## Skill'ler

Sihirbaz her skill'i ayrı sorar; `beyin-doktor` ile `beyin-ice-aktar` varsayılan
evet, diğerleri varsayılan hayırdır. Doğrudan `install.ps1`, `-SkillFilter`
verilmedikçe tüm skill'leri kopyalar.

İkisi mekanizmanın parçası: `beyin-doktor` (hattın sağlık kontrolü) ve
`beyin-ice-aktar` (claude.ai dışa aktarım ZIP'ini vault'a işler).

Diğer dördü — `companion`, `orchestration`, `codex-fleet`, `gece-vardiyasi` —
**yazarın çalışma setinden genelleştirilmiş kopyalar**: delegasyon politikası,
gece taslak-modu vardiya protokolü ve Codex CLI kullanım kılavuzu. Desenler işe
yaradığı için yayımlandılar, sana uygun oldukları için değil; birkaçı sende
kurulu olmayan araçları varsayıyor.

Her birinin ne yaptığı ve "genelleştirilmiş"in burada ne demek olduğu:
[../skills/README.md](../skills/README.md).

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

`../skills/companion/SKILL.md` bu yapıyı yer tutucu içerikle belgeliyor;
[../template/rules.example.md](../template/rules.example.md) ise kancanın
beklediği biçimde örnek bir kurallar dosyası.
