@echo off
setlocal
cd /d "%~dp0"
if "%~1"=="" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\hic.ps1" up
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\hic.ps1" %*
)
if errorlevel 1 (
  echo.
  echo HIC Launcher stopped safely. No existing services were restarted.
  pause
)
