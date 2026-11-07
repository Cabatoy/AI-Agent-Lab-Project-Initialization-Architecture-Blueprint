@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\hic-native.ps1"
if errorlevel 1 (
  echo.
  echo Native preview did not start. Existing HIC services were not modified.
  pause
)
