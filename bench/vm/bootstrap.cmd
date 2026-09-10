@echo off
setlocal EnableExtensions

rem Origin of Memory 2.0 -- kabul kapisi 7 -- Windows Sandbox onyukleme.
rem Sandbox icinde LogonCommand olarak kosar. Host uzerindeki kuru kosum ayni
rem dosyayi OOMVM_* degiskenleriyle baska bir vault'a yonlendirir; butun
rem varsayilanlar Sandbox degerleridir.
rem Metin ASCII: cmd cp437 altinda Turkce harfleri bozar (BRIEF kurali).
rem Ikinci kullanim: bootstrap.cmd oomjson "<yol>" <sandbox^|local>
rem   -- yalniz oom.json yazar, zincir.cmd ADIM 7 bunu cagirir.

if /i "%~1"=="oomjson" goto :oomjson_tek

rem Host ortam degiskenleri Sandbox'a gecmez. Yaz-oku bagli cikti klasorundeki
rem ayar.cmd, hazirla.cmd anindaki OOMVM_* degerlerini Sandbox'a tasir.
set "AYAR_OUT=%OOMVM_OUT%"
if not defined AYAR_OUT set "AYAR_OUT=C:\oom-out"
if exist "%AYAR_OUT%\ayar.cmd" call "%AYAR_OUT%\ayar.cmd"
if defined OOMVM_LOCAL set "OOMVM_LOCAL=%OOMVM_LOCAL: =%"
set "AYAR_OUT="

if not defined OOMVM_BIN       set "OOMVM_BIN=C:\oom-bin"
if not defined OOMVM_VM        set "OOMVM_VM=C:\oom-vm"
if not defined OOMVM_OUT       set "OOMVM_OUT=C:\oom-out"
if not defined OOMVM_VAULT     set "OOMVM_VAULT=C:\vault"
if not defined OOMVM_HOME      set "OOMVM_HOME=%USERPROFILE%"
if not defined OOMVM_COMPANION set "OOMVM_COMPANION=Companion"
if not defined OOMVM_PROFILE   set "OOMVM_PROFILE=sandbox"
if not defined OOMVM_NODE      set "OOMVM_NODE=1"
if not defined OOMVM_LOCAL     set "OOMVM_LOCAL=1"
if not defined OOMVM_DRY       set "OOMVM_DRY=0"

rem Node LTS sabitlenmis surum ve sha256 (nodejs.org/dist/v24.21.0/SHASUMS256.txt).
set "NODE_SURUM=v24.21.0"
set "NODE_MSI=node-v24.21.0-x64.msi"
set "NODE_SHA256=bb0eaee134f9357f22aea915ee793343e627aefc1e66488164bac6915bce2cac"
set "NODE_URL=https://nodejs.org/dist/%NODE_SURUM%/%NODE_MSI%"

if not exist "%OOMVM_OUT%" mkdir "%OOMVM_OUT%" 2>nul
set "LOG=%OOMVM_OUT%\bootstrap.log"
>"%LOG%" echo [bootstrap] baslangic %DATE% %TIME%
call :say "[bootstrap] vault=%OOMVM_VAULT% bin=%OOMVM_BIN% out=%OOMVM_OUT% profil=%OOMVM_PROFILE%"

rem --- 1. host adresi -------------------------------------------------------
set "OOMVM_OLLAMA=localhost:11434"
if "%OOMVM_LOCAL%"=="1" if exist "%OOMVM_VM%\host.txt" set /p OOMVM_OLLAMA=<"%OOMVM_VM%\host.txt"
if "%OOMVM_OLLAMA%"=="" set "OOMVM_OLLAMA=localhost:11434"
if "%OOMVM_LOCAL%"=="1" call :say "[bootstrap] ollama ucu = http://%OOMVM_OLLAMA%/v1"
if "%OOMVM_LOCAL%"=="0" call :say "[bootstrap] yerel model ayagi yok (OOMVM_LOCAL=0)"

rem --- 2. bos vault iskeleti ------------------------------------------------
for %%d in (
  "%OOMVM_VAULT%"
  "%OOMVM_VAULT%\daily"
  "%OOMVM_VAULT%\knowledge"
  "%OOMVM_VAULT%\knowledge\concepts"
  "%OOMVM_VAULT%\knowledge\hubs"
  "%OOMVM_VAULT%\.oom"
  "%OOMVM_VAULT%\%OOMVM_COMPANION%"
  "%OOMVM_HOME%\.claude\projects"
) do if not exist %%d mkdir %%d 2>nul
call :say "[bootstrap] vault iskeleti kuruldu"

rem --- 3. minimal companion ------------------------------------------------
set "CP=%OOMVM_VAULT%\%OOMVM_COMPANION%"
>"%CP%\Last-Session.md" echo # Son Oturum
>>"%CP%\Last-Session.md" echo - Konu: OdenaOS 2.0 temiz Windows zinciri (kapi 7).
>>"%CP%\Last-Session.md" echo - Karar: zincir tek komutla kosulacak, her adim gunluge yazilacak.
>>"%CP%\Last-Session.md" echo - Kalan: /login sonrasi zincir.cmd.
>"%CP%\Threads.md" echo # Aktif Threadler
>>"%CP%\Threads.md" echo - kapi-7: temiz Windows zinciri
>>"%CP%\Threads.md" echo - kapi-10: yerel backend olcumu
>"%CP%\Kurallar.md" echo # Kurallar
>>"%CP%\Kurallar.md" echo - Sentetik kosum gercek arsive dokunmaz.
>>"%CP%\Kurallar.md" echo - Her adim kanitiyla birlikte gunluge yazilir.
>>"%CP%\Kurallar.md" echo - Olculmeyen ozellik varsayilan olamaz.
>"%CP%\Duzeltmeler.md" echo # Duzeltmeler
>>"%CP%\Duzeltmeler.md" echo - Kuru kosumda gercek settings.json'a dokunulmaz.
>"%CP%\Journal.md" echo # Journal
>>"%CP%\Journal.md" echo - Sandbox her acilista temiz Windows verir; kalici hicbir sey yoktur.
call :say "[bootstrap] companion dosyalari yazildi (%OOMVM_COMPANION%)"

rem --- 4. exe ve yapilandirma ----------------------------------------------
copy /y "%OOMVM_BIN%\oom.exe" "%OOMVM_VAULT%\.oom\oom.exe" >>"%LOG%" 2>&1
if errorlevel 1 call :say "[bootstrap] HATA oom.exe kopyalanamadi"
if exist "%OOMVM_BIN%\e_sqlite3.dll" copy /y "%OOMVM_BIN%\e_sqlite3.dll" "%OOMVM_VAULT%\.oom\e_sqlite3.dll" >>"%LOG%" 2>&1

set "VJ=%OOMVM_VAULT:\=\\%"
>"%OOMVM_VAULT%\.oom\vault.json" echo {"vault": "%VJ%", "schema": 1}
call :oomjson "%OOMVM_VAULT%\.oom\oom.json" "%OOMVM_PROFILE%"
call :say "[bootstrap] vault.json ve oom.json yazildi"
copy /y "%OOMVM_VAULT%\.oom\oom.json" "%OOMVM_OUT%\oom.json" >nul 2>&1

rem --- 5. Node LTS + claude code -------------------------------------------
if "%OOMVM_NODE%"=="0" goto :node_bitti
where node >nul 2>&1
if not errorlevel 1 goto :node_var
call :say "[node] %NODE_SURUM% indiriliyor"
curl.exe -fsSL -o "%TEMP%\%NODE_MSI%" "%NODE_URL%" >>"%LOG%" 2>&1
if errorlevel 1 goto :node_indirilemedi
set "NODE_GERCEK="
for /f "skip=1 delims=" %%h in ('certutil -hashfile "%TEMP%\%NODE_MSI%" SHA256') do if not defined NODE_GERCEK set "NODE_GERCEK=%%h"
set "NODE_GERCEK=%NODE_GERCEK: =%"
if /i not "%NODE_GERCEK%"=="%NODE_SHA256%" goto :node_hash_hata
call :say "[node] sha256 dogrulandi"
start "" /wait msiexec /i "%TEMP%\%NODE_MSI%" /qn /norestart
set "PATH=%PATH%;%ProgramFiles%\nodejs"
call :say "[node] kurulum tamam"
call npm i -g @anthropic-ai/claude-code >>"%LOG%" 2>&1
if errorlevel 1 call :say "[npm] HATA @anthropic-ai/claude-code kurulamadi"
if not errorlevel 1 call :say "[npm] @anthropic-ai/claude-code kuruldu"
goto :node_bitti

:node_var
call :say "[node] zaten kurulu, indirme atlandi"
goto :node_bitti
:node_indirilemedi
call :say "[node] HATA msi indirilemedi: %NODE_URL%"
goto :node_bitti
:node_hash_hata
call :say "[node] HATA sha256 uyusmuyor - beklenen %NODE_SHA256% bulunan %NODE_GERCEK%"
del /q "%TEMP%\%NODE_MSI%" 2>nul
goto :node_bitti
:node_bitti

rem --- 6. elle yapilacak tek adim ------------------------------------------
call :say "==============================================================="
call :say " SIRADAKI ADIM ELLE YAPILIR:"
call :say " 1) Sandbox icinde bir terminal ac."
call :say " 2) claude   komutunu calistir, acilan ekranda  /login  yap (bir kez)."
call :say " 3) Giris bittikten sonra:  C:\oom-vm\zincir.cmd"
call :say " Gunluk: %LOG%"
call :say "==============================================================="
endlocal
exit /b 0

rem ==========================================================================
:say
>>"%LOG%" echo %~1
echo %~1
goto :eof

rem ==========================================================================
:oomjson_tek
set "AYAR_OUT=%OOMVM_OUT%"
if not defined AYAR_OUT set "AYAR_OUT=C:\oom-out"
if exist "%AYAR_OUT%\ayar.cmd" call "%AYAR_OUT%\ayar.cmd"
if defined OOMVM_LOCAL set "OOMVM_LOCAL=%OOMVM_LOCAL: =%"
set "AYAR_OUT="
if not defined OOMVM_VM set "OOMVM_VM=C:\oom-vm"
if not defined OOMVM_LOCAL set "OOMVM_LOCAL=1"
if "%OOMVM_LOCAL%"=="1" if not defined OOMVM_OLLAMA if exist "%OOMVM_VM%\host.txt" set /p OOMVM_OLLAMA=<"%OOMVM_VM%\host.txt"
if not defined OOMVM_OLLAMA set "OOMVM_OLLAMA=localhost:11434"
if not defined OOMVM_COMPANION set "OOMVM_COMPANION=Companion"
call :oomjson "%~2" "%~3"
endlocal
exit /b 0

rem Spec 4.1 varsayilanlari. profil=local ise iki zincir de yalniz yerel modeli
rem kullanir (kapi 7 ADIM 7: claude yokken tek basina flush).
:oomjson
set "OJ=%~1"
set "PROFIL=%~2"
if not defined OOMVM_LOCAL_FAST  set "OOMVM_LOCAL_FAST=qwen3:8b"
if not defined OOMVM_LOCAL_SMART set "OOMVM_LOCAL_SMART=qwen3:14b"
set "OOMVM_ROOT=%%USERPROFILE%%\\.claude\\projects"
if "%OOMVM_DRY%"=="1" set "OOMVM_ROOT=%OOMVM_HOME:\=\\%\\.claude\\projects"
>"%OJ%" echo {
>>"%OJ%" echo   "backend": {
if "%OOMVM_LOCAL%"=="0" goto :oomjson_claude
if /i "%PROFIL%"=="local" goto :oomjson_local
>>"%OJ%" echo     "flush": ["claude", "local"],
>>"%OJ%" echo     "compile": ["claude"],
goto :oomjson_ortak
:oomjson_claude
>>"%OJ%" echo     "flush": ["claude"],
>>"%OJ%" echo     "compile": ["claude"],
goto :oomjson_ortak
:oomjson_local
>>"%OJ%" echo     "flush": ["local"],
>>"%OJ%" echo     "compile": ["local"],
:oomjson_ortak
>>"%OJ%" echo     "claude": { "fast": "claude-haiku-4-5-20251001", "smart": "claude-sonnet-5", "configDir": ".oom\\claude-config" },
>>"%OJ%" echo     "local": { "url": "http://%OOMVM_OLLAMA%/v1", "fast": "%OOMVM_LOCAL_FAST%", "smart": "%OOMVM_LOCAL_SMART%", "embed": "nomic-embed-text" }
>>"%OJ%" echo   },
>>"%OJ%" echo   "retrieveMode": "bm25",
>>"%OJ%" echo   "sweep": { "everyHours": 8, "sinceHours": 8, "minTurns": 3, "maxSessionsPerRun": 20, "roots": ["%OOMVM_ROOT%"] },
>>"%OJ%" echo   "compile": { "eveningHour": 18, "minIntervalHours": 20, "maxDailiesPerRun": 3 },
>>"%OJ%" echo   "context": { "companionDir": "%OOMVM_COMPANION%", "capChars": 16000, "statusLine": true },
>>"%OJ%" echo   "retrieve": { "top": 3, "perNoteChars": 1500, "totalChars": 4500, "minOverlap": 3, "strictScore": 0.0 },
>>"%OJ%" echo   "mcp": { "enabled": true },
>>"%OJ%" echo   "notify": { "toast": true },
>>"%OJ%" echo   "extensions": []
>>"%OJ%" echo }
goto :eof
