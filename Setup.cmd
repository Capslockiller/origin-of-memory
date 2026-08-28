@echo off
REM Double-clickable entry point for anyone who downloaded the source zip and
REM does not want to open a terminal. It only launches the existing wizard.
setlocal
cd /d "%~dp0"
where powershell >nul 2>&1 || (
  echo Windows PowerShell was not found on PATH. This project is Windows-only.
  pause
  exit /b 1
)
echo Starting the Origin of Memory setup wizard...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0kur-gui.ps1"
if errorlevel 1 (
  echo.
  echo The graphical wizard could not start. Falling back to the terminal wizard.
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0kur.ps1"
)
pause
