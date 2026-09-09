@echo off
setlocal DisableDelayedExpansion
set "KEYPILOT_PACKAGE=%~dp0"
set "KEYPILOT_SELF=%~f0"
set "__APPDIR__="
setlocal EnableDelayedExpansion
set "KEYPILOT_SYSTEM32=!__APPDIR__!"
if /i not "!KEYPILOT_SYSTEM32:~-10!"=="\System32\" (
  echo [ERROR] The uninstaller shell is not the Windows System32 CMD.
  exit /b 2
)
cd /d "!KEYPILOT_PACKAGE!"
set "KEYPILOT_POWERSHELL=!KEYPILOT_SYSTEM32!WindowsPowerShell\v1.0\powershell.exe"
set "KEYPILOT_FLTMC=!KEYPILOT_SYSTEM32!fltmc.exe"
set "KEYPILOT_UNINSTALL_SCRIPT=!KEYPILOT_PACKAGE!installer\Uninstall-KeyPilot.ps1"

if not exist "!KEYPILOT_POWERSHELL!" (
  echo [ERROR] Windows PowerShell was not found.
  exit /b 2
)
if not exist "!KEYPILOT_FLTMC!" (
  echo [ERROR] Windows fltmc.exe was not found.
  exit /b 2
)
if not exist "!KEYPILOT_UNINSTALL_SCRIPT!" (
  echo [ERROR] KeyPilot uninstaller was not found.
  exit /b 2
)

"!KEYPILOT_FLTMC!" >nul 2>nul
if errorlevel 1 (
  set "KEYPILOT_ELEVATE=!KEYPILOT_SELF!"
  "!KEYPILOT_POWERSHELL!" -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:KEYPILOT_ELEVATE -Verb RunAs"
  exit /b !ERRORLEVEL!
)

rem Exit this batch before the elevated script removes the installation directory.
rem The fixed command reads the script path only from the inherited environment variable, so
rem spaces and command metacharacters in the package path cannot become PowerShell syntax.
start "KeyPilot Uninstaller" "!KEYPILOT_POWERSHELL!" -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Milliseconds 500; & $env:KEYPILOT_UNINSTALL_SCRIPT"
if errorlevel 1 (
  echo [ERROR] Could not start the KeyPilot uninstaller.
  exit /b 1
)
exit /b 0
