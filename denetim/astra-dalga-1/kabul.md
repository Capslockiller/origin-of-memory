# INT-1 · Birleşik aday doğrulaması

Koşan: ana döngü · 2026-09-11 · Astra emri INT-1

## Alınan commit'ler, emirdeki sırayla

| Şerit | Şerit commit'i | Entegrasyon commit'i |
|---|---|---|
| U | `b9bcde5` | `decfc77` |
| S | `740d13d` | `d7e2191` |
| D | `4ef4b83` | `5c8413c` |
| R | `6e14e8e` | `0c15476` |

Dördü de çakışmasız uygulandı. Entegrasyon commit'i: **`529722e`**.

## Merkezden atanan scar numaraları

U ve S ikisi de `Y-184`'ten başlamıştı — şerit brief'lerine numara aralığı
konmadığı için. S `Y-191…Y-200`'e kaydırıldı; U `Y-184…Y-190` kaldı.
D `Y-210…Y-214`, R `Y-220…Y-225` zaten ayrıktı.
`DisplayName` düzeyinde çakışma kalmadı — ölçüldü, 178 eşsiz numara.

Defter: 150 → **178 yara**, 28 satır merkezden yazıldı.

## Sahibin onayladığı mevcut-test farkları

`git apply` ikisini de reddetti; sebep içerik çakışması değil, yamalardaki gizli
CR'lerdi. GNU `patch -p1` ikisini de temiz uyguladı.

- `U-test-degisiklik-onerisi.patch` → `UninstallSafetyOrderTests.cs`
- `S-test-degisiklik-onerisi.patch` → yalnız test dosyaları; `docs/scars.md`
  kısmı uygulanmadı, defter merkezden yazıldığı için.

## CI

`ci.yml`'e publish adımı eklendi: tek dosya `win-x64` yayımlanıyor ve
`oom.exe` üretilmezse iş düşüyor. YAML makineyle ayrıştırıldı (7 adım).

## Ayrı doğrulama checkout'u — `oom-dogrulama` @ `529722e`

```
dotnet restore  → iki proje geri yüklendi
dotnet build -c Release --no-restore → 0 Hata
dotnet test  -c Release --no-build   → Başarısız: 0, Başarılı: 222, Toplam: 222
dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true
  → publish/oom.exe · 99.148.287 bayt
  → sha256 479a6b74b2305051bc7be5a6ab0532709b3b39204c3d7c46b1c2ec2931e67b1c
```

## İzole fikstür smoke — yayımlanan exe ile

Makine geneli durum önce damgalandı, sonra yeniden ölçüldü.

| | önce | sonra |
|---|---|---|
| `~/.claude/settings.json` | `586f6fe23f5a340e` | `586f6fe23f5a340e` |
| `claude_desktop_config.json` | `9879555cf8dd56e5` | `9879555cf8dd56e5` |
| Başlat menüsü kısayolu | YOK | YOK |
| Zamanlanmış görev | Ready | **Ready** |
| Durum kökü sayısı | 82 | 82 |

- **install ×2** — ikisi de proje kapsamı, her biri kendi `.claude/settings.json`
  ve `.mcp.json`'unu aldı, makine geneline tek bayt yazılmadı.
- **retrieval** — koştu.
- **doctor** — `OOM_LOCALAPPDATA` ile fikstüre yönlendirildi; yeni `belirsiz`
  sınıfı özet satırında görünüyor.
- **şema yükseltme** — sahte v3 dosya kuruldu (`user_version=3`, 1 `calls`
  satırı). Salt-okunur `doctor` göçü tetiklemedi (Y-161 gereği doğru). Yazan
  komut tetikledi: `user_version 3 → 5`, satır korundu, yedek alındı
  (`state.db.v3-20260911-134327734.bak`).
- **uninstall A** — B bayt bayt aynı kaldı, `.mcp.json`'u yerinde, ve
  **sahibin gerçek zamanlanmış görevi silinmedi.** Eski kodda koşulsuz silinirdi.
- Temizlik: yarattığım iki kök künyeleriyle doğrulanıp silindi; başka hiçbir
  köke dokunulmadı.

## Açık — ölçümde çıktı

Sahte v3 dosyada göç tamamlandıktan sonra `sweep` düştü:
`SQLite Error 1: 'no such table: sweep_stamps'`.

Fikstürüm sentetikti — yalnız `calls` tablosu taşıyordu; gerçek bir v3 dosyada
`BaseTables` koşmuş olacağı için bütün tablolar bulunur. Ama ölçüm, S şeridinin
kendi raporunda adlandırdığı açığı bağımsız olarak üretti: **merdiven artık
tarihî basamakların koştuğunu varsayıyor**, yani eksik tablolu bir dosya göçten
sonra da eksik kalıyor. `021cc83`'ün "her basamak her açılışta" davranışı aynı
zamanda örtük bir onarımdı ve emir gereği kapatıldı.

Bu, sahibin **bozuk canlı veritabanı** için doğrudan anlamlı: hasarlı bir dosya
göç ettirilirse eksik tabloları kendiliğinden geri gelmez.

## CI hakkında — açıkça

Uzaktaki CI **koşmadı**. Yukarıdaki zincir yerel eşdeğerdir ve "CI geçti"
anlamına gelmez. Push kararı sahibinde.

## Son ölçülen durum

`529722e` · 61 dosya · `+16131 / −414` · **222 test, 222 geçti, 0 atlanan** ·
soğuk derlemede 1 uyarı (`CS0028`, `Program.cs:42`, önceden var), 0 hata ·
178 yara.
