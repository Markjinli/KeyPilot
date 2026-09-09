@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Verify-Release.ps1" -PackageDirectory "%~dp0."
set "KEYPILOT_EXIT=%ERRORLEVEL%"
if not "%KEYPILOT_EXIT%"=="0" echo [ERROR] Release verification failed with exit code %KEYPILOT_EXIT%.
echo.
pause
exit /b %KEYPILOT_EXIT%
