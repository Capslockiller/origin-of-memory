@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem Origin of Memory 2.0 -- kapi 7 host tarafi hazirligi.
rem 1) bench\vm\.out\ acilir (Sandbox'a yaz-oku baglanir, .gitignore'da),
rem 2) host'un LAN adresi bench\vm\host.txt'e yazilir (Sandbox oradan Ollama'ya baglanir),
rem 3) oom-sandbox.wsb sablonu cozulmus mutlak yollarla .out\oom-sandbox.wsb olarak uretilir.
rem Depoya hicbir mutlak yol girmez: uretilen dosya da host.txt de .gitignore'dadir.

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

rem --- host adresi ---------------------------------------------------------
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

rem --- cozulmus .wsb -------------------------------------------------------
set "SRC=%VM%\oom-sandbox.wsb"
set "DST=%VM%\.out\oom-sandbox.wsb"
if exist "%DST%" del /q "%DST%"
for /f "usebackq delims=" %%l in ("%SRC%") do (
  set "SATIR=%%l"
  set "SATIR=!SATIR:%%OOM_REPO%%=%REPO%!"
  >>"%DST%" echo(!SATIR!
)
echo [hazirla] uretildi: %DST%
echo.
echo Sonraki adim:  start "" "%DST%"
endlocal
exit /b 0
