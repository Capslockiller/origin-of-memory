# 08 — Ölçütler ve bütçeler

Projenin kendi kartı gibi: yolda sayılır ancak aşağıdaki sayılar hareket ederse. Hiçbiri kıpırdamıyorsa iş sapmadır, kod yeşil olsa bile.

## Sahibin ölçütleri (10 Eki tabanı)

| Ölçüt | Taban (195 oturum, 30 gün) | Hedef | Nasıl ölçülür |
|---|---|---|---|
| Hafıza + kota konuşması | sahibin mesajlarının **%20**'si (545 + 375 / 4.430) | <%5 | aynı tarama, ham kayıttan, aylık |
| Tekrarlanan düzeltme | ölçülmedi (Düzeltmeler'de 2 kayıt, 346 hayır) | 0 | `durum`: aynı konuda aynı gerekçeli ikinci hayır |
| Kapsama | 7 gün %10, 30 gün %48 | %100 | oturum ↔ `kayit` bloğu çapraz sayımı |
| Kaydedilmemiş karar | ölçülmedi | 0 | `durum` sayacı |
| Devam maliyeti | ölçülmedi | düşer | "odena <konu>" sonrası ilk iş turuna kadar geçen mesaj ve araç çağrısı sayısı |
| "Odena" ile açılış | %70 | aynı kalabilir | bu bir alışkanlık, ölçüt değil; kart o alışkanlığa oturur |

## Makinenin ölçütleri

| Ölçüt | Hedef |
|---|---|
| Mekanizmadaki model çağrısı | 0 (kodda `claude`, `codex`, HTTP istemcisi yok; testle zorlanır) |
| Oturum başı enjeksiyon | ≤8.000 kr, ≤8.500 bayt |
| Mesaj başı maliyet | 0 (ilk mesaj hariç: ≤600 kr, yalnız eşleşince) |
| Kanca süresi | `baglam` <1 sn, `kaydet` p95 <3 sn |
| `ara` | ilk indeks <30 sn, sorgu <1,5 sn, 20 sorguluk sette ≥ `rg` |
| Kod | `src` ≤3.000 satır, test <100 yöntem, kapı <60 sn |
| Sessiz arıza | 0; her kırmızı `durum`da bir dosya ya da kimlikle |
| Sahipten istenen eylem | login 0, elle komut 0, onay kuyruğu 0; yalnız "evet/hayır" |

## Token ve bakım bütçesi

- **Çalışma zamanı:** mekanizma hiçbir token harcamaz. Sahibin oturumuna giren bağlam 3.x ile aynı tavanda (8K kr); ilk mesajda en çok 600 kr daha. Ham kayıt hiçbir zaman bağlama girmez. Net etki: 3.x'e göre artış yok, derleme ve özetleyici tokenları (günde onlarca `claude -p` çağrısı) sıfırlanır.
- **Geliştirme:** tek şerit, ≤300 satırlık adım, kapı yalnız düşerse ikinci göz. 3.1.1'in 35 brief + 13 yargıç + 2,9 kat yama düzeni yoktur. Fable yalnız karar ve kapı okuması; yazım Codex ya da Sonnet.
- **Bakım:** bağımlılık tek (SQLite). Zamanlayıcı yok. Güncelleme = `kur.ps1`. İndeks silinebilir. Hiçbir şey "elle koşulması gereken" değildir; `durum` kırmızıysa nedenini söyler, çoğu zaman sonraki kanca kendi kapatır.
- **Sahibin zamanı:** haftada ≤3 "yürürlükte" cümlesi onayı; ayda bir ölçüt tablosuna bakış. Başka yok.

## Bu belgenin kendi kapısı

- Adım 1'in 7 günlük kapısı geçildiğinde bu tablo gerçek sayılarla güncellenir; güncellenmiyorsa proje durmuştur ve bu da bir ölçümdür.
