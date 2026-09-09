@echo off
setlocal DisableDelayedExpansion
set "KEYPILOT_PACKAGE=%~dp0"
set "KEYPILOT_SELF=%~f0"
set "__APPDIR__="
setlocal EnableDelayedExpansion
set "KEYPILOT_SYSTEM32=!__APPDIR__!"
if /i not "!KEYPILOT_SYSTEM32:~-10!"=="\System32\" (
  echo [ERROR] The installer shell is not the Windows System32 CMD.
  exit /b 2
)
cd /d "!KEYPILOT_PACKAGE!"
set "KEYPILOT_POWERSHELL=!KEYPILOT_SYSTEM32!WindowsPowerShell\v1.0\powershell.exe"
set "KEYPILOT_FLTMC=!KEYPILOT_SYSTEM32!fltmc.exe"

if not exist "!KEYPILOT_POWERSHELL!" (
  echo [ERROR] Windows PowerShell was not found.
  exit /b 2
)
if not exist "!KEYPILOT_FLTMC!" (
  echo [ERROR] Windows fltmc.exe was not found.
  exit /b 2
)

"!KEYPILOT_FLTMC!" >nul 2>nul
if errorlevel 1 (
  set "KEYPILOT_ELEVATE=!KEYPILOT_SELF!"
  "!KEYPILOT_POWERSHELL!" -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:KEYPILOT_ELEVATE -Verb RunAs"
  exit /b !ERRORLEVEL!
)

"!KEYPILOT_POWERSHELL!" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "!KEYPILOT_PACKAGE!installer\Install-KeyPilot.ps1"
set "KEYPILOT_EXIT=!ERRORLEVEL!"
if not "!KEYPILOT_EXIT!"=="0" (
  echo.
  echo [ERROR] KeyPilot installation failed with exit code !KEYPILOT_EXIT!.
)
echo.
pause
exit /b !KEYPILOT_EXIT!
