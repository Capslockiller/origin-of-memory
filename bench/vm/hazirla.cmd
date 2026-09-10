@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem Origin of Memory 2.0 -- kapi 7 host tarafi hazirligi.
rem 1) bench\vm\.out\ acilir (Sandbox'a yaz-oku baglanir, .gitignore'da),
rem 2) host'taki OOMVM_* degerleri .out\ayar.cmd ile Sandbox'a tasinir,
rem 3) yerel ayak aciksa host'un LAN adresi host.txt'e yazilir,
rem 4) oom-sandbox.wsb sablonu cozulmus mutlak yollarla .out\oom-sandbox.wsb olarak uretilir.
rem Depoya hicbir mutlak yol girmez: uretilen dosya da host.txt de .gitignore'dadir.

if not defined OOMVM_LOCAL set "OOMVM_LOCAL=1"
set "OOMVM_LOCAL=%OOMVM_LOCAL: =%"

set "VM=%~dp0"
if "%VM:~-1%"=="\" set "VM=%VM:~0,-1%"
for %%p in ("%VM%\..\..") do set "REPO=%%~fp"

if not exist "%VM%\.out" mkdir "%VM%\.out"
echo [hazirla] repo   = %REPO%
echo [hazirla] cikti  = %VM%\.out

if not exist "%REPO%\publish\win-x64\oom.exe" (
  echo [hazirla] UYARI publish\win-x64\oom.exe yok. Once:
  echo           dotnet publish src\Oom\Oom.csproj -c Release -o publish\win-x64
)

rem --- Sandbox'a aktarilacak ayarlar ---------------------------------------
set "AYAR=%VM%\.out\ayar.cmd"
>"%AYAR%" echo @echo off
for /f "tokens=1,* delims==" %%a in ('set OOMVM_ 2^>nul') do call :ayar_yaz "%%a" "%%b"
echo [hazirla] ayar.cmd = OOMVM_LOCAL=%OOMVM_LOCAL%

rem --- host adresi ---------------------------------------------------------
if "%OOMVM_LOCAL%"=="0" goto :host_atla
set "IP="
for /f "tokens=2 delims=:" %%a in ('ipconfig ^| findstr /c:"IPv4"') do (
  set "ADAY=%%a"
  set "ADAY=!ADAY: =!"
  if not "!ADAY!"=="127.0.0.1" if not defined IP set "IP=!ADAY!"
)
if not defined IP set "IP=localhost"
>"%VM%\host.txt" echo %IP%:11434
echo [hazirla] host.txt = %IP%:11434
echo           (Ollama host'ta 0.0.0.0 dinlemiyorsa: setx OLLAMA_HOST 0.0.0.0 + Ollama yeniden baslat)
:host_atla
if "%OOMVM_LOCAL%"=="0" echo [hazirla] host.txt atlandi (OOMVM_LOCAL=0)

rem --- cozulmus .wsb -------------------------------------------------------
set "SRC=%VM%\oom-sandbox.wsb"
set "DST=%VM%\.out\oom-sandbox.wsb"
if exist "%DST%" del /q "%DST%"
rem Satirlar gecikmeli genisletme KAPALIYKEN okunur: XML yorumundaki "!" aksi halde yutulur.
setlocal DisableDelayedExpansion
for /f "usebackq delims=" %%l in ("%SRC%") do (
  set "SATIR=%%l"
  setlocal EnableDelayedExpansion
  set "SATIR=!SATIR:%%OOM_REPO%%=%REPO%!"
  >>"%DST%" echo(!SATIR!
  endlocal
)
endlocal
echo [hazirla] uretildi: %DST%
echo.
echo Sonraki adim:  start "" "%DST%"
endlocal
exit /b 0

rem OOMVM_* degerini sondaki bosluklardan arindirip ayar.cmd'ye yazar
rem ("set OOMVM_LOCAL=0 && ..." yaziminda deger "0 " olarak gelir).
:ayar_yaz
set "AD=%~1"
set "DEGER=%~2"
:ayar_kirp
if defined DEGER if "!DEGER:~-1!"==" " set "DEGER=!DEGER:~0,-1!" & goto :ayar_kirp
>>"%AYAR%" echo set "!AD!=!DEGER!"
exit /b 0
