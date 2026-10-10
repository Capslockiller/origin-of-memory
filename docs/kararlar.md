# Kararlar

Her karar: bağlam · karar · gerekçe · neyi değiştirdiği · geri dönüş şartı. Bu dosya kartın kendi kuralına uyar: yeni bir karar eskisini sessizce ezmez, "değiştirir" diye yazar. Sahibin kararları kimlikleriyle (oturum `3b8a48ad…`, 10 Eki 2026) anılır.

## K-01 — Ürün: Defter, model çağırmayan hafıza defteri
- **Bağlam:** 3.x üç mekanizmayı (özetleyici, derleyici, enjeksiyon) tek iş için taşıyordu; ölçüm ikisini çürüttü; model döngüdeyken aynı eklem üç kez kırıldı.
- **Karar:** OoM 4.0 = ham kayıt + konu kartı + arama + tek satır durum. Mekanizmada model yok. Sahip: "Defter." (#829c5c21, 15:13); "oom bölünmeli" (#1f0fea69, 16:53).
- **Değiştirir:** 28 Eyl yol haritasının 3.1.x → 3.2 → 3.3 sırası; 3 Eki "hepsi öneriye göre" ile kurulan 3.1.2/H1 sırası.
- **Geri dönüş:** 3 ay içinde sessiz arıza → özel altyapı kapanır (07-yol › Durdurma).

## K-02 — Dil ve çekirdek: C# (.NET 9), 3.x'ten kesilerek; üçüncü yeniden yazım yok
- **Bağlam:** Seçenekler: (a) mevcut C# çekirdeğini kesmek, (b) Python stdlib ile sıfırdan (Avenox, memkeel yolu), (c) Go/Rust tek ikili.
- **Karar:** (a). Kanıtlanmış parçalar korunur: Claude ve Codex ayrıştırıcıları, Redactor, Guards, atomik yazım, FTS çekirdeği, bağlam bütçeleyici. Tek exe, 1 sn kanca, tek bağımlılık.
- **Gerekçe:** Proje bir tam yeniden yazımı zaten ödedi (Python → C#, tek haftada 251 commit). (b) test hızı kazandırır ama kanıtlanmış çekirdeği ve Windows'ta ölçülmüş kanca davranışını atar. (c) sıfır birikim. Dil son kullanıcıya görünmez; sahip çalışıp çalışmadığına bakar.
- **Değiştirir:** hiçbir şey; 3.0'ın dil kararını korur.
- **Geri dönüş:** Adım 5 kapısı (`src` ≤3K, kapı <60 sn) geçilemezse (b) yeniden açılır.

## K-03 — Yakalama LLM'siz, deterministik, kimlikli
- **Karar:** `kaydet` tam turu yazar; özet yok, doğrulayıcı yok, ret yok. Her tur uuid taşır. Sınıflandırma `u/a/h/t`; yalnız `u` sahibin sözüdür.
- **Gerekçe:** LoCoMo ham 70,7 vs derlenmiş 36,2; kapsama %10 → hedef %100; 3.x `ExtractiveSummary` tohumu zaten vardı. Önceki sanat: agent-memory-ledger kimliği kancada basar.
- **Değiştirir:** 3.x `flush` (dilim/tam kipleri, `cli`/`host` backend'leri), 3.1.2 H1.
- **Geri dönüş:** yok; bu ilke değişirse ürün değişir.

## K-04 — Kart = yürürlükteki karar + korur/değiştirir
- **Bağlam:** İlk taslak "niyet + son söz + son hayır"dı. İki bağımsız Opus (biri kör, biri eleştiren) merkezi devirdi: sahibin evetleri bizim menülere işaret eder, tek başına okunmaz; sapma adım adım onaylandı çünkü hiçbir öneri neyi devirdiğini söylemedi.
- **Karar:** Üst blok: yürürlükte (ajanın cümlesi + sahibin cevabının kimliği + korur/değiştirir) · ölçüt · kapalı yönler · sıradaki. Hesaplanan satır makineden. Ekleme değil değiştirme. Sahip: "evet biçim bu" (#6e7d894e, 17:59).
- **Değiştirir:** 28 Eyl haritasının "konu kartı" alan listesini (Durum · Son karar · Sende bekleyen · Sıradaki · Yerler · Erişim) ve 10 Eki ilk taslağı.
- **Geri dönüş:** Adım 3 kapısı (20 devam anı) düşerse kart enjeksiyonu açılmaz, kartlar elle kalır.

## K-05 — Alıntı yalnız kimlikle; sabit çapa doğrulaması
- **Bağlam:** Kartın tasarlandığı öğleden sonra ajan üç kez çevirmenlik yaptı: "niyet" diye konan cümle ajanın kendi Journal cümlesiydi; aynı sahip cümlesi üç belgede üç biçimde yazıldı.
- **Karar:** Tırnak içine elle metin yazılmaz. Kimlik `u` sınıfında ve ham kayıtta olmalı; satırdaki tarih/sayı/tırnaklı dizge o turda geçmeli. Düşen satır kartı geçersiz yapar.
- **Değiştirir:** 3.x'in ve ilk taslağın serbest alıntı pratiğini.
- **Geri dönüş:** yok.

## K-06 — Derleme (compile) kaldırılır; kavram notları salt okur arşiv
- **Karar:** `compile`, hub, kök harita, `Bridge` gider. 1.153 not `knowledge/concepts`te kalır, `ara`da `arşiv` etiketiyle görünür, yenisi üretilmez.
- **Gerekçe:** derleme 14 gün durunca kopma artmadı; iki yanlış hatırlama derlenmiş nottan; bugünkü aramada tek bayat cevap yine oradan. Yol haritası karar 6 "ölçüm gösterirse sor" diyordu; ölçüm üç kez gösterdi.
- **Değiştirir:** 25 Ağu'dan beri süren "gece derleyici" tasarımı.
- **Geri dönüş:** Adım 4'ün iki korpus ölçümünde `+arşiv` korpusu anlamlı isabet eklerse arşiv ağırlığı yükseltilir; derleyici geri gelmez.

## K-07 — Dışarı çıkanlar: kit, mcp, doctor, nudge, install, save
- **Karar:** `kit` → skill kütüphanesi `kur.ps1`; `mcp` kaldırılır (hiçbir istemciye kayıtlı değildi); `doctor` → `durum`; `nudge` kaldırılır; `oom install` → `kur.ps1` tek kurucu; `save` → `kaydet` kapsar. İkili adı `oom.exe`, depo adı `origin-of-memory` kalır.
- **Gerekçe:** 790+1.707 satır kit hafızayla ilgisizdi; üç kurulum yolu birbirini tutmuyordu; doctor 1.024 satırdı ve 57 satır kırmızı basıyordu.
- **Değiştirir:** 24 Eyl OoM Kit kararı; 3.x komut yüzeyi.
- **Geri dönüş:** MCP: Adım 4'te `ara`yı iki istemciye MCP ile açmak istenirse yeniden değerlendirilir; sahibin kalıcı ayar kararıdır.

## K-08 — Süreç: tek şerit, ≤300 satır, ölçülen kapı; sürü yok
- **Bağlam:** 3.1.1: 737 kaynak satırına 2.883 satır belge, 35 brief, 30 yama, 13 yargıç raporu, 488 MB ops. Sahip 12 Eyl: "sahibin okuyamayacağı hacimde iş üretmek, o işi onun elinden almaktır." 10 Eki: "detaylı uzun planlar istemiyorum" (#dcb02300).
- **Karar:** Adım başına tek şerit, tek brief, tek kapı; yargıç yalnız kapı düşerse; toplam net kod ≤900 satır (Adım 1–4). Sahibe diff + ölçüm + bir cümle.
- **Değiştirir:** Sürü Doktrini'nin OoM'daki uygulamasını (dalga, lider, yargıç turları). Doktrin başka projelerde geçerliliğini korur.
- **Geri dönüş:** bir adım üç kez kapıdan dönerse Kural 8: dur, tasarımı sorgula, sahiple.

## K-09 — Repo public kalır; tören biter
- **Karar:** Depo public; sürüm = etiket + tek CHANGELOG girdisi; özel veri taraması tek otomatik komut (CI'da), insan töreni yok. `v3` ve `v0` dalları arşiv; yerel `surum/3.1.x` push edilmez.
- **Gerekçe:** Sahip "git sayfamızı temizle" dedi (#ac81f578, 18:19), kapat demedi. Public olmanın maliyeti tören kaldırılınca küçüktür; faydası dış göz (29 Eyl'de bir yabancı inceleyici gerçek hata yakalamıştı).
- **Değiştirir:** 28 Eyl "tek temiz commit" geleneğini; 3.x sürüm kapısındaki çapraz aile incelemesini.
- **Geri dönüş:** sahip bir cümleyle private'a çevirebilir; tasarım etkilenmez.

## K-10 — Threads.md emekliye; Düzeltmeler konu üstü ve kimlikli
- **Karar:** Canlı konular karta taşınır (sahip ilk 5'i seçer); kalan `arsiv/`. Last-Session tek paragraf + kart bağlantıları. Düzeltmeler her oturumda tam metin, her madde sahibin kimlikli sözüyle; konuya bağlı değil.
- **Gerekçe:** Threads 132K kr, bağlama %1,7'si giriyordu; OoM satırı 6,3K kr beş "Önceki" paragrafla ve 10 Eki öğleden sonra 8,3K'ya çıktı; kural yazarına yenildi. Düzeltmeler 2 kayıt / 346 hayır. Sahip ihtiyaç sırası: 1 düzeltmeler, 2 altyapı sessizliği (#65da628e, 17:03).
- **Değiştirir:** CLAUDE.md'nin "Threads carry state" uygulamasını (ilke kalır, dosya değişir).
- **Geri dönüş:** Adım 3 kapısı düşerse Threads kalır, kartlar ek olur.

## K-11 — Bütçeler: ≤8.000 kr kanca, ≤500 kr kart, eşleşme yoksa sessizlik
- **Karar:** Sayılar 01-ilkeler'de. Kart üst bloğu 400 hedef, 500 tavan (ilk kart 503 ölçüldü; iki hafta yaşayıp ölçülecek).
- **Değiştirir:** 3.x `capChars` 16.000 → 8.000 kararını korur, kart tavanını ekler.
- **Geri dönüş:** Claude Code kanca sınırı değişse bile 8.000 yeniden açılmaz.

## K-12 — Codex eşitliği
- **Karar:** Aynı ikili, aynı fiiller, aynı kartlar; `hooks.json` aynı komutlar. İstemciye özel bütçe yok.
- **Gerekçe:** Sahip iki ajanı aynı konularda kullanıyor; kart iki ajanın da okuduğu tek dosya olmalı. 3.x Codex ayrıştırıcısı ölçülmüş ve çalışıyor.
- **Değiştirir:** hiçbir şey; 13 Eyl Codex kanca kararını korur.

## K-13 — Önce tasarım, kod sonra; ilk adım ham kayıt
- **Karar:** Bu depo kodsuz yayınlanır; sahip okur ve "evet" der; sonra Adım 1. Sahip: "kodları yazmadan tüm mimari ve yönleri repoda yayınla" (#ac81f578).
- **Değiştirir:** 10 Eki 15:13 "Defter planı"nın ilk hamlesi olan `surum/4.0` dalını açma sırasını; dal Adım 1'de açılır.
- **Geri dönüş:** yok; sıradaki iş 07-yol'da.

## Açık kararlar (sahibin)
- Yok. 10 Eki 20:20: sahip "ilk 5 kartı neye göre seçmemi bekliyorsun?" dedi; kriter veriden çıkarıldı (son 30 günde ilk mesajda açılan konu × Threads'te canlı × son 7 günde dokunulmuş). Öneri: OoM, SCREENSHOT, 3D disiplin, STBC26, Sürü; ikinci dalga Pipeline ve kanal. Adım 3'te Odena açar, sahip yalnız yürürlük cümlelerine evet/hayır der.
- Kapatılan eski maddeler: temizlik Adım 5'te arşiv anlık görüntüsünden sonra Odena yapar (veto saklı) · kavram arşivi vault'ta salt okur kalır · istemciden cevap basamağı istemci desteği gelene kadar yok.
