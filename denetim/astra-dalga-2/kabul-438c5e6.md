# Kabul kaydı · `438c5e6` — KÜME

Apex'in yazılmasını emrettiği ifade, birebir:

> `748fb41`: raporlanan paralel koşumda 244 testin 244'ü geçti. Aynı koşum
> düzeninde aralıklı başarısızlık ayrıca gözlendi; bu sonuç kararlılık kanıtı
> değildir. Sonraki teşhis Y-170'i işaret etmektedir. KAR-1 açık; birleşik
> kabul verilmedi.

## Ölçümler

| Alan | Değer |
|---|---|
| commit | `438c5e6` (KÜME `748fb41`'in entegrasyonu), taban `0bf3623` |
| ölçen | ana döngü · `claude-opus-5` |
| tam komut | `dotnet test Oom.sln -c Release --nologo -- xunit.parallelizeTestCollections=false xunit.parallelizeAssembly=false` |
| paralellik ayarı | **kapalı** (komut satırından) |
| sonuç | `Başarısız: 0, Başarılı: 244, Atlanan: 0, Toplam: 244` |
| ham çıktı | bu oturumun kaydı |

| Alan | Değer |
|---|---|
| commit | `748fb41` (KÜME şeridi, kendi worktree'sinde) |
| ölçen | KÜME şeridi, sonra ana döngü bağımsız |
| tam komut | `dotnet test Oom.sln -c Release --nologo` |
| paralellik ayarı | **açık** (varsayılan) |
| sonuç | 244/244 — **ve aynı düzende aralıklı başarısızlık** |

## Kararlılık şerhi

Paralellik açıkken ölçülen aralıklı başarısızlık: KÜME şeridi 9 koşumda 1,
ana döngü 5 koşumda 2, sonra 6 koşumda 0, sonra 14 koşumda 1. Yakalanan ad
**`Y-170`**, istisna `System.ObjectDisposedException: SQLitePCL.sqlite3`.
Paralellik kapalıyken 14/14 ve 244/244.

**Apex'in reddettiği iki çıkarım — ana döngünün aşırı iddiasıydı:**
1. *"İstisna tipi mekanizmayı birebir kanıtlıyor"* — hayır.
   `ObjectDisposedException` kapatılmış nesnenin kullanıldığını gösterir,
   **kapatan çağrıyı göstermez.**
2. *"Sebebi kod olmayacak"* — hayır. `Doctor.InspectStateRoots` üretimde
   `ClearAllPools` çağırıyor, ve D2 `StateRootSnapshot.Dispose` içinde kök
   başına ikinci bir çağrı yeri ekliyor. Ürün kusuru olma ihtimali açık.

Neden bağı `KAR-1` şeridinde kurulacak.

## Önceki sayılar korunuyor

`222/222` ve `244/244` sonuçları silinmedi. Geçtikleri koşumun olgusu olarak
duruyorlar; bütün kabul özetleri bu kararlılık şerhiyle tamamlanır.
Uzak CI için sonuç ancak gerçekten koştuktan sonra yazılır.
