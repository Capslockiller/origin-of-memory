# 01 — İlkeler

## Değişmezler

1. **Mekanizmada model çağrısı yok.** `kaydet`, `kart`, `ara`, `baglam`, `durum` hiçbir LLM'e gitmez. Dolayısıyla login, OAuth, duman testi, özet doğrulayıcı, karantina ve retry kuyruğu da yoktur.
2. **Ham kayıt eksiksiz ve sıkıcıdır.** Asla "boş", asla ret, asla zaman aşımı. Kaydedemediği an tek satırla söyler ve bir sonraki kancada kaldığı yerden sürer.
3. **Alıntı yalnız kimlikle.** Kart, Düzeltmeler ve her türetilmiş satır tırnak içine metin yazmaz; mesaj kimliği yazar. Alıntı okuma anında ham kayıttan çekilir. Kimlik sahibin mesajı değilse satır geçersizdir.
4. **Makine doğrular, anlam yazmaz.** Kartı ajan yazar, sahip onaylar; makine şema, uzunluk, kimlik ve sabit çapa denetler, hesaplanan satırı üretir. Her kart ham kayıttan yeniden kurulabilir olmalıdır.
5. **Yürürlükteki karar neyi devirdiğini söyler.** Her yeni öneri "Yürürlükte: X → korur / değiştirir: Y" ile açılır. Ne koruyan ne deviren öneri üzerine yazmaz; çatışma olarak görünür.
6. **Kanca bütçesi ≤8.000 karakter.** anthropics/claude-code#94358 kalksa bile yeniden açılmaz. Kart üst bloğu ≤500 karakter. Eşleşme yoksa hiçbir şey enjekte edilmez.
7. **Sır değeri hiçbir katmana yazılmaz.** Maskeleme varsayılan açık, ham kayıt dahil.
8. **Silinen hiçbir şey yok olmaz.** Arşiv dalı, arşiv klasörü, salt okur indeks.
9. **Tek şerit, küçük adım, ölçülen kapı.** Adım ≤300 satır (test ve fikstür hariç); her adımın kapısı sonrakinin girişi. Sürü, dalga, yargıç turu yok. Sahibe karar tablosu değil, diff + bir ölçüm + bir cümle gider.
10. **Sahipten hiçbir şey istenmez.** Login, onay kuyruğu, okunacak paragraf, elle koşulacak komut yok. Tek istisna: yürürlük cümlesini okuyup "evet" demek.

## Asla yapılmayacaklar

- Vektör, hibrit ya da embedding arama.
- Onay kuyruğu arayüzü, TUI, zaman ekseni.
- NLI ya da Jaccard tabanlı çelişki dedektörü.
- Makine metnini Kurallar, Düzeltmeler, CLAUDE.md ya da AGENTS.md'ye yazan herhangi bir yol.
- Özetleyicinin, derleyicinin ya da bir "konsolidasyon ajanı"nın mekanizmanın içinde koşması.
- Filo hafıza servisi, çok kullanıcı, bulut senkronu.
- Kanca içinden ağ çağrısı (güncelleme kontrolü dahil).
- İkinci backend, ikinci kurucu, ikinci sağlık sistemi.

## Yerel özelliklere bırakılanlar

- Kod kalıbı ve tercih dersleri → Claude Code auto-memory, Codex memories.
- Statik kurallar → CLAUDE.md / AGENTS.md, yalnız insan eliyle.
- Bilgi ("nasıl yapılır") → skill kütüphanesi ve elle yazılmış notlar. Defter bilgi üretmez.
- Tam metin tabanı → Obsidian araması + `rg`. `ara` 20 sorguluk sette bunu geçemezse bırakılır.
