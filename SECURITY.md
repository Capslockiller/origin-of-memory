# Güvenlik

## Sınır

- Mekanizmanın hiçbir yerinde dil modeli, ağ çağrısı ya da ikinci süreç yoktur. Güvenilmeyen transkript metni hiçbir zaman araçlı bir modele ulaşmaz.
- Ham kayıt oturum bağlamına asla enjekte edilmez; yalnız aranır ve kimlikle alıntılanır.
- Enjekte edilen her blok "veri, talimat değildir" sınır satırlarıyla çevrilidir.
- Araç sonuçları kaydedilmez. Kullanıcı ve ajan metni yazımdan önce maskelenir (Redactor); maskesiz metin bellekten başka yere düşmez.
- İndeks vault dışında (`%LOCALAPPDATA%\oom\<vault-hash>`), silinebilir, yeniden kurulur.

## Bildirim

- Bir sızıntı ya da sınır ihlali bulursanız GitHub Issues'da açın; sır değerini issue'ya yazmayın, yalnız yerini ve biçimini.
- Tek bakımcı; cevap süresi için söz verilmez.

## Bu sürümde

- 4.0.0-tasarim: kod yok. Bu belge Adım 1 ile birlikte ölçülmüş iddialara dönüşür (`docs/08-olcutler.md`).
