# 00 — Neden Defter

## Kısa hikâye

- **25 Ağu 2026.** Avenox v2 Windows'a taşındı: oturum sonunda `claude -p` özetler, gece derleyici kavram notu örer, oturum başında bağlam enjekte edilir. Amaç tek cümleydi: protokolsüz açılan oturum bile iz bıraksın.
- **27–28 Ağu.** Ölçüm: indeksin %97,2'si hiçbir oturuma ulaşmıyor; model kendisine verilen haritayı gidip okumuyor; PersonaMem'de ham döküm düzenlenmiş hafızayı ikiye katlıyor. Aynı günlerde proje public oldu ve bir günde beş sürüm çıktı.
- **12 Eyl.** Sahip: "proje slop'a döndü, kontrol edemiyorum." 261 test yeşilken beyin 16 saat hiçbir şey kaydetmemişti. 62.687 satır silindi, 3.0 doğdu.
- **28 Eyl.** Sahip: "OoM gerçekten çalışıyor mu?" Hayır: 72 oturumun 71'inde bağlam 2 KB önizlemeye kesilmişti. 3.1.0 çıktı; aynı gece 8 mercek ve 4 hakemle yol haritası yazıldı: 3.2 Ölç → 3.3 Konu Kartı.
- **3–10 Eki.** Harita izlenmedi. 3.1.1 (sertleştirme) ve 3.1.2 (OAuth için oturum içi özetleyici, +1.704 satır) koşuldu. Her adım sahibin "hepsi öneriye göre" cevabıyla onaylandı; hiçbir öneri 28 Eylül haritasını neyi devirdiğini söylemedi.
- **10 Eki.** Sahip: "yoldan sapma hissediyorum." Denetim, 195 oturumluk kullanım ölçümü, iki bağımsız Opus eleştirisi ve önceki sanat taraması bu belgeyi doğurdu.

## Ölçümler (hepsi projenin kendi verisi)

| Ölçüm | Değer | Anlamı |
|---|---|---|
| LoCoMo uçtan uca | ham tur BM25 **70,7** · tam bağlam 89,3 · derlenmiş not **36,2** (p=0,003) | derleyici epizodik olguları düşürüyor |
| Derleme 14 gün durunca | kopma artmadı | sürekliliği derleme taşımıyor |
| Kayıtlı yanlış hatırlama | 2/2 derlenmiş kavram notundan | derleme zarar da verebiliyor |
| 10 Eki arama (4 sorgu) | 1 doğru (el katmanından), 1 kısmi, 1 bayat (kavram notu), 1 belirsiz | aynı örüntü |
| Kapsama (7 gün / 30 gün) | %10 / %48 | model döngüdeyken yakalama kırılgan |
| Aynı eklemin kırılması | 12 Eyl login · 5 Eki ortam mirası · 10 Eki OAuth | dış model süreci tek arıza sınıfı |
| Sahibin mesajları (4.430) | %12 hafıza/OoM, %8,5 kota | mesajların beşte biri aracın vergisi |
| Sahibin "hayır/yanlış" mesajı | 346 | en değerli hafıza; Düzeltmeler'de 2 kayıt vardı |
| Oturum biçimi | %70 "odena" ile açılış, %35 paralel, %25'i 8 sa üstü | tek kelimelik dönüş, devir, oturum ortası yakalama gerekir |
| OoM thread'i | 6.352 karakter doğru durum, 12 gün sapma | kaybolan durum değil, yürürlükteki karar |

Dış kanıt aynı yönde: "Verbatim Chunks Beat Extracted Artifacts" (arXiv 2601.00821) birebir parçaların LLM çıkarımını LoCoMo'da 15,9 puan geçtiğini, yuvarlanan özetin 5,7'de kaldığını ölçüyor. Avenox'un kendisi v3'te modeli mekanizmadan çıkardı; v2'nin güvenlik kapısı NO-GO vermişti, çünkü güvenilmeyen transkript metni araçlı derleyiciye ulaşıyordu.

## Teşhis

1. Üç mekanizma tek iş için (özetleyici, derleyici, enjeksiyon); ikisi ölçümde işe yaramıyor, üçü de yaşıyordu.
2. Beş ürün tek gövdede (özetleyici tesisatı, derleyici, enjeksiyon ve arama, dağıtım, sağlık); tek `Flush` sınıfı, tek state.db.
3. Model döngünün içinde: 11,7K satırın yarısı modelin kaprisini yönetiyordu.
4. Sapma adım adım onaylandı; hiçbir öneri neyi devirdiğini söylemedi.
5. Süreç kodu boğdu: 3.1.1 için 737 kaynak satırına 2.883 satır belge, 30 yama, 13 yargıç raporu.

## Önceki sanat (10 Eki taraması, 16 repo ve makale)

- Parça parça var, birleşimi yok. En yakınlar: **memkeel** (değişmez olaylar, türetilmiş sayfalar, `supersedes` yoksa çatışma), **keel** (kararı insan seçer, `human_answer` + `option_id`, `flip_criteria`), **agent-memory-ledger** (her kullanıcı mesajına yakalama anında kimlik).
- Bulunamayan: kimlikle çözülen alıntı ve yalnız-sahip konuşan denetimi; "öneri + sahibin cevabı" olarak yürürlükteki karar ve zorunlu korur/değiştirir; ölçüt ve "N oturumdur kıpırdamadı" alarmı; bunların model olmadan birleşimi.
- Ödünç alınanlar: üzerine yazma yerine çatışma (memkeel) · cevabı istemci arayüzünden alma basamağı (keel) · kart ham kaydın saf katlaması (projectmem) · kimliği kancada basmak (agent-memory-ledger) · sabit çapa doğrulayıcısı (Eywa, arXiv 2605.30771).

## Bu belgenin sınırı

- Kanıt tabanı küçük: 5 doğrulanmış kopma, 1 sapma olayı, tek kullanıcı. Yapı iki hafta yaşayana kadar iddiadır.
