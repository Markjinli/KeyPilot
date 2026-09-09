@echo off
setlocal
cd /d "%~dp0"

where powershell.exe >nul 2>nul
if errorlevel 1 (
  echo [ERROR] powershell.exe was not found.
  exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "KEYPILOT_EXIT=%ERRORLEVEL%"
if not "%KEYPILOT_EXIT%"=="0" echo [ERROR] KeyPilot build failed with exit code %KEYPILOT_EXIT%.
exit /b %KEYPILOT_EXIT%
