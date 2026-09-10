# Kabul kapısı 7 — temiz Windows zinciri (Windows Sandbox)

Spec 11-7: *"Temiz Windows 11 VM: `oom install` → oturum → 8 saat beklemeden `oom sweep` → daily bloğu →
`OOM_FAKE_NOW` ile compile → concept, root map, index → yeni oturumda enjeksiyon. Aynı VM'de `claude`
yokken yerel model tek başına flush."*

Temiz Windows hedefi **Windows Sandbox**'tır: her açılış tertemiz bir Windows 11 verir, kapanınca
hiçbir şey kalmaz, host klasörleri salt-okunur ya da yaz-oku bağlanabilir. Sanal makine kurmaya,
lisansa, disk imajına gerek yoktur.

Bu klasördeki araçlar:

| Dosya | Nerede koşar | Ne yapar |
| --- | --- | --- |
| `hazirla.cmd` | host | `.out\` açar, `ayar.cmd` ile koşum ayarlarını taşır, gerekirse `host.txt`'i ve çözülmüş `.wsb`'yi üretir |
| `oom-sandbox.wsb` | host | Sandbox yapılandırması (bağlanan klasörler, 8 GB, ağ, `LogonCommand`) |
| `bootstrap.cmd` | Sandbox | boş vault, companion, `vault.json`/`oom.json`, Node LTS, `claude` CLI |
| `zincir.cmd` | Sandbox | kapı 7 zincirinin yedi adımı, her adım `[ADIM n] ok\|hata` |
| `dogrula.py` | host | `.out\` kanıtını okur, hüküm tablosunu basar, `bench/results/vm-<tarih>.json` yazar |

Depoda hiçbir `.ps1` yoktur (Y-094) ve hiçbir mutlak kullanıcı yolu yoktur (kapı 11-4);
Sandbox içinde Python da yoktur, o yüzden Sandbox tarafı yalnız `.cmd` ve `oom.exe`'dir.

---

## 1. Bir kerelik host hazırlığı

### 1.1 Windows Sandbox'ı aç

Yönetici bir terminalde:

```
Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
```

Sonra **makineyi yeniden başlat**. (Bu tek satır PowerShell'dir ve bilerek depoya değil bu
belgeye yazılmıştır; Windows özelliğini açmanın `.cmd` karşılığı `dism /online
/enable-feature /featurename:Containers-DisposableClientVM /all` satırıdır.)
Gereksinim: Windows 11 Pro/Enterprise + BIOS'ta sanallaştırma açık.

### 1.2 Ollama'yı Sandbox'a aç — yalnız `OOMVM_LOCAL=1` için

Ollama varsayılan olarak yalnız `127.0.0.1`'i dinler; Sandbox ayrı bir ağ ucudur, oraya
erişemez. Host'ta bir kez:

```
setx OLLAMA_HOST 0.0.0.0
```

ve Ollama'yı yeniden başlat (tepsi ikonundan çık, tekrar aç). Güvenlik duvarı ilk bağlantıda
sorarsa **özel ağ** için izin ver; sormazsa yönetici terminalde:

```
netsh advfirewall firewall add rule name="Ollama 11434" dir=in action=allow protocol=TCP localport=11434
```

Kosum bittikten sonra `setx OLLAMA_HOST 127.0.0.1` ile geri al: `0.0.0.0` modeli yerel ağa açar.

### 1.3 `oom.exe` yayımla

```
dotnet publish src\Oom\Oom.csproj -c Release -o publish\win-x64
```

`publish\win-x64\` içinde tek dosya `oom.exe` (self-contained) ve `e_sqlite3.dll` olur;
Sandbox'ta .NET kurulu değildir, o yüzden self-contained yayım şarttır.

---

## 2. Koşum

### 2.1 Hazırlık ve başlatma

```
bench\vm\hazirla.cmd
start "" bench\vm\.out\oom-sandbox.wsb
```

`hazirla.cmd`:
* `bench\vm\.out\` klasörünü açar (Sandbox'a `C:\oom-out` olarak **yaz-oku** bağlanır, `.gitignore`'dadır),
* host ortamındaki `OOMVM_*` değişkenlerini `.out\ayar.cmd` içine `set "..."` satırlarıyla yazar,
* `OOMVM_LOCAL=1` ise host'un LAN adresini bulup `bench\vm\host.txt`'e `<ip>:11434` olarak yazar
  (Sandbox oradan Ollama'ya bağlanır); `OOMVM_LOCAL=0` ise `host.txt` yazmayı ve IP aramayı atlar,
* `oom-sandbox.wsb` şablonundaki `%OOM_REPO%` yerine gerçek depo kökünü koyup `.out\oom-sandbox.wsb` üretir.

Ortam değişkenleri Windows Sandbox'a doğrudan geçmez. `C:\oom-out` yaz-oku eşlemesi taşıma
kanalıdır: `bootstrap.cmd` ve `zincir.cmd`, varsayılanları uygulamadan önce varsa buradaki
`ayar.cmd` dosyasını çağırır.

Depodaki `bench\vm\oom-sandbox.wsb` şablonu doğrudan da açılabilir; o zaman host'ta
`OOM_REPO` ortam değişkeninin depo köküne ayarlı olması ve Sandbox sürümünüzün `.wsb`
içinde ortam değişkeni genişletmesi gerekir. Üretilen dosya bu ikisine de bağlı değildir.

Bağlanan klasörler:

| Host | Sandbox | Erişim |
| --- | --- | --- |
| `publish\win-x64` | `C:\oom-bin` | salt-okunur |
| `bench\vm` | `C:\oom-vm` | salt-okunur |
| `bench\vm\.out` | `C:\oom-out` | yaz-oku |

### Ollama'sız koşum (`OOMVM_LOCAL=0`)

Bu turda yerel model ayağını Master kararıyla atlamak için host'ta:

```
set OOMVM_LOCAL=0
bench\vm\hazirla.cmd
start "" bench\vm\.out\oom-sandbox.wsb
```

Bu kipte Ollama ayarı, güvenlik duvarı kuralı ve `host.txt` gerekmez. Açılıştan sonra aşağıdaki
elle `/login` adımı ve `zincir.cmd` komutu değişmez; yalnız ADIM 7 atlanır.

### 2.2 Açılış (otomatik)

Sandbox açılınca `LogonCommand` `C:\oom-vm\bootstrap.cmd`'yi koşturur. Bu betik:

1. `C:\vault\` iskeletini kurar: boş `daily\`, `knowledge\concepts\`, `knowledge\hubs\`,
   ve kısa uydurma `Last-Session.md` / `Threads.md` / `Kurallar.md` / `Duzeltmeler.md` /
   `Journal.md` dosyalarıyla bir companion klasörü.
   **Companion klasörünün adı `Companion`'dır, `🔮 850-Companion` değil**: batch emoji
   içeren klasör adını güvenilir yazamaz, bu yüzden ad `oom.json`'daki
   `context.companionDir` alanından gelir ve Sandbox'ta `Companion` olur. Companion
   dosyalarının içeriği bilerek ASCII'dir: `cmd` cp437 altında Türkçe harfleri bozar.
2. `C:\oom-bin\oom.exe` (ve `e_sqlite3.dll`) dosyasını `C:\vault\.oom\`'a kopyalar.
3. `vault.json` ve kapı koşumu ayarlarıyla `oom.json` yazar:
   `sweep.roots = ["%USERPROFILE%\.claude\projects"]`. `OOMVM_LOCAL=1` iken
   `backend.local.url = http://<host>:11434/v1` (adres `C:\oom-vm\host.txt`'ten gelir) ve
   `backend.flush = ["claude","local"]`; `OOMVM_LOCAL=0` iken `backend.flush = ["claude"]`
   olur ve yerel blok zararsız `localhost` yer tutucusuyla kalır. Temiz VM'deki tek notluk korpusta BM25 IDF tabana
   düştüğü için kanca puan eşiği `strictScore = 0.0`'dır; konu kapısı gevşetilmez,
   `minOverlap = 3` ile slug/başlık/takma ad/etiket kimliğinde üç sözcük örtüşmesi aranır.
4. Node LTS'i sessizce kurar: `curl.exe` ile `node-v24.21.0-x64.msi` indirilir,
   sha256'sı **kurulumdan önce** betikte sabitli
   `bb0eaee134f9357f22aea915ee793343e627aefc1e66488164bac6915bce2cac` değeriyle
   `certutil -hashfile` üzerinden karşılaştırılır, tutmazsa kurulum yapılmaz.
   Ardından `npm i -g @anthropic-ai/claude-code`.
5. Ekrana ve `C:\oom-out\bootstrap.log`'a sıradaki elle adımı yazar.

### 2.3 Tek elle adım: `/login`

Otomatikleştirilemez. Sandbox içinde bir terminal aç:

```
claude
```

açılan ekranda bir kez `/login` çalıştır ve tarayıcı akışını tamamla.

### 2.4 Zinciri koş

```
C:\oom-vm\zincir.cmd
```

Yedi adım, her biri `C:\oom-out\zincir.log`'a `[ADIM n] ok|hata` satırı yazar:

| Adım | Ne | Beklenen |
| --- | --- | --- |
| 1 | `oom install --vault C:\vault` + `oom doctor --json` | `settings.json`'da dört hook, zamanlanmış görev, `state.db` |
| 2 | `claude -p "..." --max-turns 1` | gerçek oturum; SessionStart/UserPromptSubmit/SessionEnd hook'ları kurulumdan tetiklenir (`OOM_INVOKED_BY` **kurulmaz**) |
| 3 | hemen `oom sweep` | `daily\<bugün>.md` içinde **tam bir** `### Oturum` bloğu; hook zaten yazdıysa `no-new-turns` doğru sonuçtur, ölçü "tek blok, kopya yok" |
| 4 | `OOM_FAKE_NOW=<bugün>T19:00:00+03:00` + `oom compile`; model geçici olarak sözleşmesiz çıktı verirse kavram oluşana dek en çok 3 deneme | ≥ 1 `knowledge\concepts\*.md`, `index.md`, `index-full.md`, `log.md` |
| 5 | İlk derlenen kavramın dosya slug'ından soru üret; ham `oom retrieve --query ... --json`, sonra `claude -p` ve `oom retrieve --hook` çalıştır | enjeksiyon bloğu derlenen kavramın dosya kökünü veya H1 başlığını anar; ham sırası ve olası kanca ret gerekçesi kanıtta kalır |
| 6 | `oom doctor --json` | kapsama %100, ret %0 |
| 7 | `OOMVM_LOCAL=1` ise `backend.flush=["local"]` ile ikinci `oom.json` + ikinci sentetik transkript + `oom sweep`; `0` ise Master kararıyla atla | yerel backend'in yazdığı ikinci blok veya `[ADIM 7] atlandi ...` |

Sonunda `C:\vault\daily`, `C:\vault\knowledge`, `state.db` ve
`%USERPROFILE%\.claude\settings.json` `C:\oom-out\`'a kopyalanır — yani host'taki
`bench\vm\.out\`'a. Sandbox kapatılabilir.

### 2.5 Hükmü oku

Host'ta:

```
python bench\vm\dogrula.py
```

`bench\vm\.out\` içindeki kanıtı okur, adım adım tabloyu basar ve
`bench/results/vm-<tarih>.json` yazar. Tablo iki sütun taşır: `gunluk` (zincir.cmd'nin kendi
kaydı) ve `olcum` (dogrula.py'nin aynı kanıtı dosyalardan bağımsız yeniden ölçmesi). İkisi
ayrışırsa satırda görünür.

Adım 7'nin backend kanıtı `state.db`'nin `calls` tablosundan okunur (`backend='local'`
satırı), `flush_log.backend`'den değil: `flush_log.backend` sütunu yazan **yolu**
(`runner`/`extractive`/`sweep`) tutar, model backend'ini `calls.backend` tutar.

Hüküm `gecti` ise kapı 7 kapanmıştır. `kuru-kosum` çıkarsa tablo yalnızca araçların
çalıştığını gösterir, kapıyı kapatmaz.
`OOMVM_LOCAL=0` Sandbox koşumunda adımlar 1–6 yeşilse hüküm
`KAPI 7 GECTI (yerel model ayagi atlandi, Master karari)` olur; sonuç JSON'u
`"adim7": "atlandi"` ve `"yerel_ayak": false` taşır.

---

## 3. Host üzerinde kuru koşum (Sandbox olmadan)

Aynı betikler `OOMVM_*` değişkenleriyle atılabilir bir vault'a yönlendirilebilir. Bu bir
kapı koşumu **değildir**; yalnız betiklerin sözdizimini ve akışını sınar.

| Değişken | Varsayılan | Ne için |
| --- | --- | --- |
| `OOMVM_BIN` | `C:\oom-bin` | `oom.exe`'nin bulunduğu klasör |
| `OOMVM_VM` | `C:\oom-vm` | betiklerin bulunduğu klasör (`host.txt` de burada) |
| `OOMVM_OUT` | `C:\oom-out` | kanıt klasörü |
| `OOMVM_VAULT` | `C:\vault` | vault kökü |
| `OOMVM_HOME` | `%USERPROFILE%` | sahte ev dizini (`.claude\projects`, `.claude\settings.json`) |
| `OOMVM_STATEDIR` | `%LOCALAPPDATA%\oom` | `state.db` aranan kök |
| `OOMVM_COMPANION` | `Companion` | companion klasör adı |
| `OOMVM_PROFILE` | `sandbox` | `local` ise iki backend zinciri de yalnız yerel model |
| `OOMVM_LOCAL_FAST` / `OOMVM_LOCAL_SMART` | `qwen3:8b` / `qwen3:14b` | yerel model adları |
| `OOMVM_NODE` | `1` | `0` ise Node/claude kurulumu atlanır |
| `OOMVM_DRY` | `0` | `1` ise kuru koşum |
| `OOMVM_LOCAL` | `1` | `0` ise Ollama/host adresi ve ADIM 7 atlanır; flush yalnız `claude` olur |
| `OOMVM_TZ` | `+03:00` | `OOM_FAKE_NOW` saat dilimi |

`OOMVM_DRY=1` iken:

* `oom install` **yalnız `--dry-run`** koşar — gerçek `%USERPROFILE%\.claude\settings.json`,
  `%APPDATA%\Claude`, Başlat menüsü ve Görev Zamanlayıcı'ya dokunulmaz;
* `sweep.roots`, sentetik transkriptin bulunduğu sahte `OOMVM_HOME` altına somutlaştırılır;
* adım 2 ve adım 5'in gerçek `claude -p` ayağı koşulmaz (adım 5'in `retrieve --hook` ayağı koşar);
* adım 2'nin yerine, gerçek Claude Code JSONL biçiminde sentetik bir transkript bırakılır;
* adım 5'in ham sıralaması `adim5-query.json`, kancanın stderr/ret gerekçesi
  `adim5-retrieve.err` dosyasında ayrıca saklanır;
* `OOMVM_LOCAL=0` ise adım 7 Master kararıyla `atlandi` yazılır;
* `settings.json` kanıt klasörüne kopyalanmaz.

Sentetik transkriptlerde `timestamp` alanı bilerek yoktur: damgasız satıra flush kendi
saatini verir, böylece blok her zaman bugünün daily'sine düşer ve batch yerel tarih
biçimine bağımlı olmaz.

---

## 4. Bilinen sınırlar

* `bootstrap.cmd` içindeki sabit Node MSI sürümü ve SHA değeri, nodejs.org o sürümü
  yayından kaldırdığında bayatlar.
* Sandbox imajında `curl.exe` ve `certutil` bulunduğu varsayılır; gerçek Sandbox koşumuna
  kadar doğrulanmış değildir.
* `hazirla.cmd` ilk loopback olmayan `ipconfig` eşleşmesini IPv4 adresi seçer; çok adaptörlü
  veya VPN'li hostlarda yanlış olabilir, `host.txt` elle kontrol edilmelidir.
