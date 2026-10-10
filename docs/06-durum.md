# 06 — Durum

## Tek satır

`durum`, 3.x'in 57 satırlık doctor tablosunun yerine geçer. Yeşilse hiç görünmez; kırmızıysa `baglam`ın ilk satırı olur ve nedenini söyler.

```
[Durum] yeşil · son kayıt 4 dk · 6 kart · indeks taze · kaydedilmemiş karar 0
[Durum] KIRMIZI · son kayıt 31 sa (bugünün 2 oturumu kayıtsız) · kart oom geçersiz: kimlik u değil 57250b80
```

## Dört sayaç

| Sayaç | Hesap | Yeşil | Kırmızı |
|---|---|---|---|
| Son kayıt yaşı | en yeni `kayit` bloğunun saati; ayrıca bugün başlayıp hiç bloğu olmayan oturum sayısı | ≤ 1 oturum geriden | bugün kayıtsız oturum ≥1 ya da 24 sa+ |
| Kart geçerliliği | `kart dogrula` sonucu, kart başına | hepsi geçerli | bir kart bile düşerse (adı ve nedeni) |
| İndeks tazeliği | hash'i değişmiş ama indekslenmemiş dosya sayısı | 0 | >0 yalnız bilgi; `ara` kendi tazeler |
| Kaydedilmemiş karar | son 7 günde onay biçimli `u` turu olan oturumlar − o oturumda kart değişikliği olanlar | 0 | ≥1 (ajana kırmızı, sahibe değil) |

Ek bilgi satırı (yalnız `oom durum --uzun`): çatışmalı kart sayısı, maskelenen tur sayısı, 12 aydan eski daily sayısı, ham kayıt toplam boyutu.

## Kıpırdamama

- Her kartın ölçütü ölçülebiliyorsa (sayı, dosya varlığı, komut çıktısı) makine ölçer; ölçülemiyorsa sahip ölçer ve değer kartın Kimlikler bölümünde kimlikle durur.
- "Kıpırdamadı N oturum": bu konuyla eşleşen ardışık oturum sayısı, ölçüt değeri değişmeden. N ≥ 5 olunca kartın hesaplanan satırında `⚠` görünür. Alarm sahibe değil ajana: bir sonraki öneri bunu açıklamak zorundadır.

## Ne değil

- Doctor değildir: düzeltme komutu (`--fix`) yoktur. Kırmızı bir şeyi makine düzeltemez; ya `kaydet` bir sonraki tetikte telafi eder ya ajan kartı düzeltir ya sahip karar verir.
- Günlük özet değildir: ne kadar iş yapıldığını saymaz.
- Çıkış kodu: yeşil 0, kırmızı 1. CI ve kanca bu kodu kullanmaz; yalnız insan ve `baglam`.

## Kanıt kuralı

- Her kırmızı satır bir dosya ya da kimlik gösterir. "Bir şeyler yanlış" satırı yoktur.
- `durum` kendi kendini de ölçer: hesap 500 ms'yi aşarsa satıra `(yavaş: 1,8 sn)` eklenir; bu, indeks bakımı için tek sinyaldir.

## 3.x'ten fark

| 3.x doctor | 4.0 durum |
|---|---|
| 57 hata, 3 uyarı, 19 yeşil satır; `--all`, `--fix`, `--json`, `--quiet` | tek satır; `--uzun`, `--json` |
| 1.024 satır kod | ≤100 satır |
| sağlık satırları state.db'de birikir | hesaplanır, saklanmaz |
| "runner uyumsuz", "transcript unreadable", "hook-failed" | model yok, runner yok; okunamayan transkript "kayıtsız oturum" sayısında görünür |
