@echo off
REM Double-clickable entry point for the operations panel.
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0beyin.ps1"
