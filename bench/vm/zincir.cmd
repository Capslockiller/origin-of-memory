@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem Origin of Memory 2.0 -- kabul kapisi 7 zinciri.
rem Sandbox icinde /login bittikten sonra elle kosulur:  C:\oom-vm\zincir.cmd
rem Host uzerinde kuru kosum:  OOMVM_DRY=1 ile ADIM 2 ve ADIM 5'in claude ayagi
rem atlanir, kurulum yalniz --dry-run kosar; gercek settings.json'a, APPDATA\Claude'a,
rem Baslat menusune ve Gorev Zamanlayici'ya dokunulmaz.
rem Her adim %OOMVM_OUT%\zincir.log dosyasina  [ADIM n] ok^|hata  satiri yazar.

if not defined OOMVM_VM    set "OOMVM_VM=C:\oom-vm"
if not defined OOMVM_OUT   set "OOMVM_OUT=C:\oom-out"
if not defined OOMVM_VAULT set "OOMVM_VAULT=C:\vault"
if not defined OOMVM_HOME  set "OOMVM_HOME=%USERPROFILE%"
if not defined OOMVM_DRY   set "OOMVM_DRY=0"
if not defined OOMVM_TZ    set "OOMVM_TZ=+03:00"
if not defined OOMVM_TASK  set "OOMVM_TASK=OdenaOS Memory Sweep"
rem state.db kokunu ariyoruz; host kuru kosumunda tek bir vault hash klasoruyle sinirlanir.
if not defined OOMVM_STATEDIR set "OOMVM_STATEDIR=%LOCALAPPDATA%\oom"
rem %LOCALAPPDATA% cozulemezse oom state kokunu %TEMP%\oom-local\oom altina kurar.
if not exist "%OOMVM_STATEDIR%" if exist "%TEMP%\oom-local\oom" set "OOMVM_STATEDIR=%TEMP%\oom-local\oom"

rem Hooklar zaten kurulumla kayitli; ozyineleme muhafizi asla elle kurulmaz.
set "OOM_INVOKED_BY="
set "OOM_FAKE_NOW="

set "OOM=%OOMVM_VAULT%\.oom\oom.exe"
set "PROJELER=%OOMVM_HOME%\.claude\projects"
if not exist "%OOMVM_OUT%" mkdir "%OOMVM_OUT%" 2>nul
set "LOG=%OOMVM_OUT%\zincir.log"
set "HATA=0"
set "SORU=Turkce tokenizasyon olcusu icin ne karar verildi?"

>"%LOG%" echo [zincir] baslangic %DATE% %TIME% kuru=%OOMVM_DRY%
call :say "[zincir] vault=%OOMVM_VAULT% out=%OOMVM_OUT% home=%OOMVM_HOME%"
if not exist "%OOM%" goto :exe_yok

rem ==========================================================================
rem ADIM 1 -- oom install + oom doctor
rem ==========================================================================
set "KURFLAG="
if "%OOMVM_DRY%"=="1" set "KURFLAG=--dry-run"
"%OOM%" install --vault "%OOMVM_VAULT%" %KURFLAG% >"%OOMVM_OUT%\adim1-install.txt" 2>&1
set "RC=%ERRORLEVEL%"
type "%OOMVM_OUT%\adim1-install.txt" >>"%LOG%"
"%OOM%" doctor --vault "%OOMVM_VAULT%" --json >"%OOMVM_OUT%\doctor-1.json" 2>>"%LOG%"
if "%OOMVM_DRY%"=="1" goto :adim1_kuru

set "HOOKN=0"
for /f "delims=" %%c in ('findstr /i /c:"oom.exe" "%OOMVM_HOME%\.claude\settings.json" 2^>nul') do set /a HOOKN+=1
set "GOREV=yok"
schtasks /query /tn "%OOMVM_TASK%" >nul 2>&1
if not errorlevel 1 set "GOREV=var"
set "STATEDB="
for /f "delims=" %%f in ('dir /b /s /o-d "%OOMVM_STATEDIR%\state.db" 2^>nul') do if not defined STATEDB set "STATEDB=%%f"
if not defined STATEDB set "STATEDB=yok"
if not "%RC%"=="0" goto :adim1_hata
if "%HOOKN%"=="0" goto :adim1_hata
if "%GOREV%"=="yok" goto :adim1_hata
if "%STATEDB%"=="yok" goto :adim1_hata
call :adim 1 ok "kurulum rc=%RC% hook-satiri=%HOOKN% gorev=%GOREV% state.db=%STATEDB%"
goto :adim2

:adim1_kuru
call :adim 1 ok "kuru kosum: install --dry-run rc=%RC%, doctor --json yazildi; hook/gorev/state.db denetimi atlandi (gercek ayarlara dokunulmadi)"
goto :adim2
:adim1_hata
call :adim 1 hata "kurulum rc=%RC% hook-satiri=%HOOKN% gorev=%GOREV% state.db=%STATEDB%"

rem ==========================================================================
rem ADIM 2 -- gercek oturum (SessionStart/UserPromptSubmit/SessionEnd hooklari)
rem ==========================================================================
:adim2
if "%OOMVM_DRY%"=="1" goto :adim2_kuru
claude -p "Bugun OdenaOS 2.0 zincir testi. Bir cumleyle selam ver." --max-turns 1 >"%OOMVM_OUT%\adim2-claude.txt" 2>>"%LOG%"
set "RC=%ERRORLEVEL%"
set "BOY=0"
for %%f in ("%OOMVM_OUT%\adim2-claude.txt") do set "BOY=%%~zf"
if not "%RC%"=="0" goto :adim2_hata
if "%BOY%"=="0" goto :adim2_hata
call :adim 2 ok "claude -p rc=%RC% cikti=%BOY% bayt; hooklar kurulumdan kayitli"
goto :adim3
:adim2_hata
call :adim 2 hata "claude -p rc=%RC% cikti=%BOY% bayt"
goto :adim3
:adim2_kuru
call :adim 2 atlandi "kuru kosum: gercek claude -p oturumu host'ta kosulmaz; yerine sentetik transkript birakilir"
call :transkript "%PROJELER%\C--vault-proje-1" "vm-11111111-aaaa-4001-8001-000000000001"

rem ==========================================================================
rem ADIM 3 -- 8 saat beklemeden sweep, tek daily blogu
rem ==========================================================================
:adim3
"%OOM%" sweep --vault "%OOMVM_VAULT%" >"%OOMVM_OUT%\adim3-sweep.txt" 2>&1
set "RC=%ERRORLEVEL%"
type "%OOMVM_OUT%\adim3-sweep.txt" >>"%LOG%"
call :daily_bul
set "BLOK=0"
if defined DAILY for /f "delims=" %%c in ('findstr /c:"### Oturum" "%DAILY%" 2^>nul') do set /a BLOK+=1
if not defined DAILY goto :adim3_hata
if not "%BLOK%"=="1" goto :adim3_hata
call :adim 3 ok "daily=%DAILY% blok=%BLOK% (tek blok, kopya yok) rc=%RC%"
goto :adim4
:adim3_hata
call :adim 3 hata "daily=%DAILY% blok=%BLOK% rc=%RC% (beklenen: tam 1 blok)"

rem ==========================================================================
rem ADIM 4 -- OOM_FAKE_NOW ile compile: concept + root map + index
rem ==========================================================================
:adim4
set "GUN="
if defined DAILY for %%f in ("%DAILY%") do set "GUN=%%~nf"
if not defined GUN goto :adim4_hata
set "OOM_FAKE_NOW=%GUN%T19:00:00%OOMVM_TZ%"
call :say "[zincir] OOM_FAKE_NOW=%OOM_FAKE_NOW%"
set /a DERLEME_DENEME=0
>"%OOMVM_OUT%\adim4-compile.txt" type nul
:adim4_derle
set /a DERLEME_DENEME+=1
>>"%OOMVM_OUT%\adim4-compile.txt" echo [deneme !DERLEME_DENEME!]
"%OOM%" compile --vault "%OOMVM_VAULT%" >>"%OOMVM_OUT%\adim4-compile.txt" 2>&1
set "RC=%ERRORLEVEL%"
set "KAVRAM=0"
for /f "delims=" %%c in ('dir /b "%OOMVM_VAULT%\knowledge\concepts\*.md" 2^>nul') do set /a KAVRAM+=1
if not "!KAVRAM!"=="0" goto :adim4_derleme_bitti
if !DERLEME_DENEME! LSS 3 (
  call :say "[zincir] adim4 deneme !DERLEME_DENEME! kavram uretmedi; yeniden deneniyor"
  goto :adim4_derle
)
:adim4_derleme_bitti
set "OOM_FAKE_NOW="
type "%OOMVM_OUT%\adim4-compile.txt" >>"%LOG%"
rem sweep kendi derlemesini ayrik surecte baslatabilir; ikisinden hangisi yazarsa
rem yazsin, uc denemeden sonra da kavram yoksa en fazla 5 dakika beklenir.
set /a BEKLE=0
:adim4_bekle
set "KAVRAM=0"
for /f "delims=" %%c in ('dir /b "%OOMVM_VAULT%\knowledge\concepts\*.md" 2^>nul') do set /a KAVRAM+=1
if not "%KAVRAM%"=="0" goto :adim4_olc
if %BEKLE% GEQ 30 goto :adim4_olc
set /a BEKLE+=1
ping -n 11 127.0.0.1 >nul 2>&1
goto :adim4_bekle
:adim4_olc
set "IDX=yok"
set "IDXFULL=yok"
set "KLOG=yok"
if exist "%OOMVM_VAULT%\knowledge\index.md" set "IDX=var"
if exist "%OOMVM_VAULT%\knowledge\index-full.md" set "IDXFULL=var"
if exist "%OOMVM_VAULT%\knowledge\log.md" set "KLOG=var"
if "%KAVRAM%"=="0" goto :adim4_hata
if "%IDX%"=="yok" goto :adim4_hata
if "%IDXFULL%"=="yok" goto :adim4_hata
if "%KLOG%"=="yok" goto :adim4_hata
call :adim 4 ok "kavram=%KAVRAM% index.md=%IDX% index-full.md=%IDXFULL% log.md=%KLOG% rc=%RC% deneme=%DERLEME_DENEME%"
goto :adim5
:adim4_hata
call :adim 4 hata "kavram=%KAVRAM% index.md=%IDX% index-full.md=%IDXFULL% log.md=%KLOG% rc=%RC% deneme=%DERLEME_DENEME%"

rem ==========================================================================
rem ADIM 5 -- yeni oturumda enjeksiyon
rem ==========================================================================
:adim5
rem Soru derlenen kavramin kendisinden turetilir: dosya adi kavramin ASCII slug'idir
rem ve spec 6.4 kapisi diyakritiksiz istemi tam olarak o forma bakarak esler.
set "SLUG="
for /f "delims=" %%f in ('dir /b "%OOMVM_VAULT%\knowledge\concepts\*.md" 2^>nul') do if not defined SLUG set "SLUG=%%~nf"
if defined SLUG set "SORU=!SLUG:-= ! icin ne karar verildi?"
call :say "[zincir] adim5 soru: %SORU%"
if "%OOMVM_DRY%"=="1" goto :adim5_hook
claude -p "%SORU%" --max-turns 1 --output-format json >"%OOMVM_OUT%\adim5-claude.json" 2>>"%LOG%"
call :say "[zincir] adim5 claude -p rc=%ERRORLEVEL%"
:adim5_hook
rem Ham siralama da kanit olarak saklanir: kanca sussa bile skorun ne oldugu gorulsun.
"%OOM%" retrieve --vault "%OOMVM_VAULT%" --query "%SORU%" --json >"%OOMVM_OUT%\adim5-query.json" 2>>"%LOG%"
echo {"prompt": "%SORU%"}| "%OOM%" retrieve --hook --vault "%OOMVM_VAULT%" >"%OOMVM_OUT%\adim5-retrieve.json" 2>"%OOMVM_OUT%\adim5-retrieve.err"
type "%OOMVM_OUT%\adim5-retrieve.err" >>"%LOG%"
set "ENJ=yok"
findstr /c:"additionalContext" "%OOMVM_OUT%\adim5-retrieve.json" >nul 2>&1
if not errorlevel 1 set "ENJ=var"
set "NEDEN="
for /f "delims=" %%r in ('type "%OOMVM_OUT%\adim5-retrieve.err" 2^>nul') do if not defined NEDEN set "NEDEN=%%r"
if "%ENJ%"=="yok" goto :adim5_hata
if "%OOMVM_DRY%"=="1" goto :adim5_kuru
call :adim 5 ok "retrieve --hook enjeksiyon blogu=%ENJ%; claude -p json ciktisi adim5-claude.json"
goto :adim6
:adim5_kuru
call :adim 5 ok "kuru kosum: yalniz retrieve --hook kosuldu, enjeksiyon blogu=%ENJ%; gercek claude -p atlandi"
goto :adim6
:adim5_hata
call :adim 5 hata "retrieve --hook enjeksiyon blogu yok; kanca gerekcesi: %NEDEN% (ham siralama adim5-query.json)"

rem ==========================================================================
rem ADIM 6 -- doctor: kapsama 100, ret 0
rem ==========================================================================
:adim6
"%OOM%" doctor --vault "%OOMVM_VAULT%" --json >"%OOMVM_OUT%\doctor-6.json" 2>>"%LOG%"
set "KAPSAMA=hayir"
set "RET=hayir"
findstr /c:"\"coverage\":1" "%OOMVM_OUT%\doctor-6.json" >nul 2>&1
if not errorlevel 1 set "KAPSAMA=evet"
findstr /c:"\"rejection_rate\":0" "%OOMVM_OUT%\doctor-6.json" >nul 2>&1
if not errorlevel 1 set "RET=evet"
if "%KAPSAMA%"=="hayir" goto :adim6_hata
if "%RET%"=="hayir" goto :adim6_hata
call :adim 6 ok "kapsama-100=evet ret-0=evet (doctor-6.json)"
goto :adim7
:adim6_hata
call :adim 6 hata "kapsama-100=%KAPSAMA% ret-0=%RET% (doctor-6.json)"

rem ==========================================================================
rem ADIM 7 -- claude yokken yerel model tek basina flush
rem ==========================================================================
:adim7
copy /y "%OOMVM_VAULT%\.oom\oom.json" "%OOMVM_VAULT%\.oom\oom.json.sandbox" >nul 2>&1
call "%OOMVM_VM%\bootstrap.cmd" oomjson "%OOMVM_VAULT%\.oom\oom.json" local
copy /y "%OOMVM_VAULT%\.oom\oom.json" "%OOMVM_OUT%\oom-local.json" >nul 2>&1
call :transkript "%PROJELER%\C--vault-proje-2" "vm-22222222-bbbb-4002-8002-000000000002"
"%OOM%" sweep --vault "%OOMVM_VAULT%" >"%OOMVM_OUT%\adim7-sweep.txt" 2>&1
set "RC=%ERRORLEVEL%"
type "%OOMVM_OUT%\adim7-sweep.txt" >>"%LOG%"
call :daily_bul
set "BLOK7=0"
if defined DAILY for /f "delims=" %%c in ('findstr /c:"### Oturum" "%DAILY%" 2^>nul') do set /a BLOK7+=1
copy /y "%OOMVM_VAULT%\.oom\oom.json.sandbox" "%OOMVM_VAULT%\.oom\oom.json" >nul 2>&1
del /q "%OOMVM_VAULT%\.oom\oom.json.sandbox" 2>nul
if "%BLOK7%"=="0" goto :adim7_hata
if "%BLOK7%"=="1" goto :adim7_hata
call :adim 7 ok "yerel-tek profil, daily blok sayisi %BLOK7% (ADIM 3 sonrasi +1); backend kaniti state.db calls tablosunda, dogrula.py okur"
goto :topla
:adim7_hata
call :adim 7 hata "yerel-tek profil, daily blok sayisi %BLOK7% rc=%RC% (beklenen: 2 veya daha fazla)"

rem ==========================================================================
rem Kanit toplama
rem ==========================================================================
:topla
if not exist "%OOMVM_OUT%\daily" mkdir "%OOMVM_OUT%\daily" 2>nul
if not exist "%OOMVM_OUT%\knowledge" mkdir "%OOMVM_OUT%\knowledge" 2>nul
xcopy /y /e /i /q "%OOMVM_VAULT%\daily" "%OOMVM_OUT%\daily" >>"%LOG%" 2>&1
xcopy /y /e /i /q "%OOMVM_VAULT%\knowledge" "%OOMVM_OUT%\knowledge" >>"%LOG%" 2>&1
for /f "delims=" %%f in ('dir /b /s /o-d "%OOMVM_STATEDIR%\state.db" 2^>nul') do if not exist "%OOMVM_OUT%\state.db" copy /y "%%f" "%OOMVM_OUT%\state.db" >>"%LOG%" 2>&1
if "%OOMVM_DRY%"=="1" goto :ayar_atla
if exist "%OOMVM_HOME%\.claude\settings.json" copy /y "%OOMVM_HOME%\.claude\settings.json" "%OOMVM_OUT%\settings.json" >>"%LOG%" 2>&1
goto :bitir
:ayar_atla
call :say "[zincir] kuru kosum: settings.json kopyalanmadi (gercek dosyaya dokunulmaz)"

:bitir
call :say "[zincir] bitti hata=%HATA%  -- host tarafinda: python bench\vm\dogrula.py"
endlocal & exit /b %HATA%

:exe_yok
call :say "[zincir] HATA oom.exe bulunamadi: %OOM% -- once bootstrap.cmd"
endlocal & exit /b 2

rem ==========================================================================
:say
>>"%LOG%" echo %~1
echo %~1
goto :eof

:adim
>>"%LOG%" echo [ADIM %~1] %~2 %~3
echo [ADIM %~1] %~2 %~3
if /i "%~2"=="hata" set /a HATA+=1
goto :eof

rem Sweep ciktisindaki "daily: <yol>" satiri; yoksa daily\ icindeki en yeni dosya.
:daily_bul
set "DAILY="
if exist "%OOMVM_OUT%\adim7-sweep.txt" call :daily_satir "%OOMVM_OUT%\adim7-sweep.txt"
if not defined DAILY if exist "%OOMVM_OUT%\adim3-sweep.txt" call :daily_satir "%OOMVM_OUT%\adim3-sweep.txt"
if defined DAILY if exist "!DAILY!" goto :eof
set "DAILY="
for /f "delims=" %%f in ('dir /b /o-d "%OOMVM_VAULT%\daily\*.md" 2^>nul') do if not defined DAILY set "DAILY=%OOMVM_VAULT%\daily\%%f"
goto :eof

rem Tek dosyadan "daily: <yol>" satirini okur (birden fazla dosya verilirse findstr
rem satirin basina dosya adini ekler ve ayristirma bozulur).
:daily_satir
for /f "tokens=1,* delims=:" %%a in ('findstr /b /c:"daily: " "%~1" 2^>nul') do set "DAILY=%%b"
if defined DAILY set "DAILY=!DAILY:~1!"
goto :eof

rem Gercek Claude Code JSONL bicimi (src\Oom\Ingest\Samples\claude-code-real-shape.jsonl).
rem timestamp alani bilerek yoktur: damgasiz satira flush kendi saatini verir, boylece
rem blok her zaman bugunun daily'sine duser ve batch'te yerel tarih bicimine bagimlilik olmaz.
:transkript
set "TDIR=%~1"
set "TSID=%~2"
if not exist "%TDIR%" mkdir "%TDIR%" 2>nul
set "TF=%TDIR%\%TSID%.jsonl"
>"%TF%" echo {"sessionId": "%TSID%", "cwd": "C:\\vault\\proje", "gitBranch": "main", "userType": "external", "version": "2.1.263", "isSidechain": false, "type": "user", "uuid": "u1", "parentUuid": null, "message": {"role": "user", "content": "Turkce tokenizasyon konusunu bugun kapatalim; olcuyu nasil sabitliyoruz?"}}
>>"%TF%" echo {"sessionId": "%TSID%", "cwd": "C:\\vault\\proje", "isSidechain": false, "type": "assistant", "uuid": "a1", "parentUuid": "u1", "message": {"role": "assistant", "content": [{"type": "thinking", "thinking": "bu blok ozete girmemeli"}, {"type": "text", "text": "Olcuyu tek sayiya indirelim: I ve i katlamasi ozel tabloyla yapilacak, kultur bagimli ToLower kullanilmayacak."}]}}
>>"%TF%" echo {"sessionId": "%TSID%", "cwd": "C:\\vault\\proje", "isSidechain": false, "type": "user", "uuid": "u2", "parentUuid": "a1", "message": {"role": "user", "content": "Peki olcuyu nerede sabitliyoruz, testte mi harness'ta mi?"}}
>>"%TF%" echo {"sessionId": "%TSID%", "cwd": "C:\\vault\\proje", "isSidechain": false, "type": "assistant", "uuid": "a2", "parentUuid": "u2", "message": {"role": "assistant", "content": [{"type": "text", "text": "Olcu harness'ta sabitlenir: tokenizer surumu hic degismez, karsilastirma yalniz retrieve surumleri arasinda yapilir."}]}}
>>"%TF%" echo {"sessionId": "%TSID%", "cwd": "C:\\vault\\proje", "isSidechain": false, "type": "user", "uuid": "u3", "parentUuid": "a2", "message": {"role": "user", "content": "Tamam, o zaman karari yazalim ve yarin olcelim."}}
>>"%TF%" echo {"sessionId": "%TSID%", "cwd": "C:\\vault\\proje", "isSidechain": false, "type": "assistant", "uuid": "a3", "parentUuid": "u3", "message": {"role": "assistant", "content": [{"type": "text", "text": "Karar: Turkce tokenizasyon olcusu icin I ve i katlamasi ozel tabloyla yapilacak, kultur bagimli ToLower kullanilmayacak. Ogrenilen: sentetik kosum gercek arsive hic dokunmuyor. Yapilacak: yarin olc."}]}}
>>"%TF%" echo {"type": "summary", "summary": "Turkce tokenizasyon oturumu", "leafUuid": "a3"}
call :say "[zincir] sentetik transkript: %TF%"
goto :eof
