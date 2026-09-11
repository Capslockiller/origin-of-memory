# INT-0 · Dalga 1 tabanı

Kaydeden: ana döngü · 2026-09-11 · Astra emri INT-0

## Taban kimliği

| | |
|---|---|
| HEAD | `021cc83f6acd4e481d42b6d5327981590e05457c` |
| Dal | `wave/security-ledger-index-boundary` (yerel, push yok) |
| Baz | `b110495` · `main` dokunulmamış |
| Çalışma ağacı | temiz — `git status --porcelain` sıfır satır |
| .NET SDK | `9.0.312` |
| Fark | 38 dosya · `+5558 / −399` |

## Ham test çıktısı

```
dotnet test Oom.sln -c Release --nologo
Başarılı!  - Başarısız: 0, Başarılı: 191, Atlanan: 0, Toplam: 191, Süre: 3 s - Oom.Tests.dll (net9.0)
```

Soğuk Release derlemesi: `1 Uyarı` (`CS0028`, `Program.cs:42`, önceden var), `0 Hata`.

**Devir bildirimiyle fark yok.** Devirde bildirilen 25 commit / 191 test / 150 yara
ve `+5558 / −399` burada birebir doğrulandı.

## Canlı yüzeye erişen test sınırları — koşturmadan önce belirlendi

Gerçek profile, kullanıcı ayarlarına, makine kayıtlarına veya yayımlanan exe'ye
ulaşabilen dosyalar:

| Dosya | Ne yüzeye değiyor |
|---|---|
| `tests/Oom.Tests/AyrismaTests.cs` | kurulum/kaldırma; tüm yollar enjekte, gerçek profil kullanılmıyor |
| `tests/Oom.Tests/Gates/Gate11Contracts.cs` | yayımlanan exe'yi koşan fikstür (`GateFixture`) |
| `tests/Oom.Tests/Gates/Gate12Boundary.cs` | yayımlanan exe; `doctor` taraması fikstüre yönlendirildi |
| `tests/Oom.Tests/KimlikScars.cs` | kimlik yalıtımı yolları |
| `tests/Oom.Tests/Scars/DurumDeposuScars.cs` | yayımlanan exe; `Y-162` taraması fikstüre yönlendirildi |
| `tests/Oom.Tests/Scars/KurulumScars.cs` | kurulum yolları |
| `tests/Oom.Tests/UninstallSafetyOrderTests.cs` | kaldırma sırası; `schtasks` argüman vektörünü çiviliyor |

**Ölçülmüş durum:** `doctor` taramasını koşan iki test (`Gate12Boundary`,
`DurumDeposuScars.Y-162`) `OOM_LOCALAPPDATA` ile fikstüre yönlendirildi. Tam paket
koşumundan önce ve sonra canlı kasanın veritabanı dosyalarının mtime'ları
değişmiyor — ölçüldü.

**Açık kalan:** `%LOCALAPPDATA%` altındaki durum kökleri birim testlerce yazılıyor;
bu beklenen davranış, ama gerçek profilin altında oluyor.

## Şeritler

U ve S `021cc83` tabanından açıldı. Mevcut kirli worktree'ler
(`oom-w-INDEKS`, 2 commit edilmemiş dosya) sıfırlanmadı, taşınmadı, dokunulmadı.
