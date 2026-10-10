# Sözlük

| Terim | Anlam |
|---|---|
| **Defter** | Bu araç: model çağırmayan hafıza defteri. Üç fiil (kaydet, kart, ara) + `baglam` ve `durum`. |
| **Sahip** | Vault'un ve kararların sahibi olan tek kullanıcı. Ajan değil. |
| **Ajan** | Claude Code, Codex ya da bir şerit; kartı yazar, Defter'i çağırır, kararı veremez. |
| **Ham kayıt** | `daily/YYYY-MM-DD.md` içindeki kimlikli, maskeli, deterministik tur blokları. Tek doğruluk kaynağı. |
| **Kimlik** | Bir turun değişmez adresi: oturum kimliği + mesaj uuid'si. Alıntı yalnız kimlikle yapılır. |
| **Kart** | Konu başına bir dosya (`Kartlar/<konu>.md`): yürürlükte · ölçüt · kapalı yönler · sıradaki + hesaplanan satır. Üst blok ≤500 karakter. |
| **Yürürlükte** | Konuda geçerli karar: ajanın koyduğu cümle + sahibin cevabının kimliği + neyi koruduğu ya da devirdiği. |
| **Korur / değiştirir** | Her önerinin açılış cümlesi: yürürlükteki kararla ilişkisi. "Değiştirir" yalnız sahip onayıyla üst satırı yeniler. |
| **Çatışma** | Yürürlükteki kararı ne koruyan ne deviren öneri. Üzerine yazılmaz, `durum`da sayılır. |
| **Ölçüt** | "Yolda sayılır ancak <gözlenebilir şey>." Konu başına bir tane; değerini makine ya da sahip ölçer. |
| **Kapalı yön** | Sahibin reddettiği yön, kimlikle; neyi kapattığına göre sabitlenir, tarihe göre değil. |
| **Sıradaki** | Yürürlükten türeyen tek eylem satırı. |
| **Hesaplanan satır** | Makinenin ham kayıttan ürettiği satır: sahibin son sözü · ölçüt değeri · kaç oturumdur kıpırdamadı. Elle yazılmaz. |
| **Düzeltmeler** | Konu üstü düzeltme defteri: yanlış bir hafıza iddiasının doğrusu, sahibin kimlikli sözüyle. Her oturumda tam metin. |
| **Kurallar** | Her zaman geçerli kanun; yalnız sahip yazar. |
| **Sabit çapa** | Kart satırındaki tarih, sayı, kimlik ve tırnaklı dizge; atıf verilen ham mesajda geçmek zorunda. |
| **Çekimserlik** | Eşleşme yoksa hiçbir şey enjekte etmemek. |
| **Kapı** | Bir adımın ölçülen bitiş şartı; sonraki adımın giriş şartı. |
| **Arşiv** | `knowledge/concepts` (1.153 not) ve 3.x/0.x kod hatları: salt okur, aramada görünür, yenisi üretilmez. |
