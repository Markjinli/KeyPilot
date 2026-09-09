@echo off
setlocal DisableDelayedExpansion
rem Discard caller-controlled variables and use CMD's dynamic executable directory.
rem This avoids trusting poisoned SystemRoot/RANDOM values across an elevation boundary.
set "__APPDIR__="
set "RANDOM="
setlocal EnableDelayedExpansion
set "KEYPILOT_SYSTEM32=!__APPDIR__!"
if /i not "!KEYPILOT_SYSTEM32:~-10!"=="\System32\" (
  echo No changes were made. The recovery shell is not the Windows System32 CMD.
  exit /b 2
)
set "KEYPILOT_DISM=!KEYPILOT_SYSTEM32!dism.exe"
set "KEYPILOT_PNPUTIL=!KEYPILOT_SYSTEM32!pnputil.exe"
set "KEYPILOT_FINDSTR=!KEYPILOT_SYSTEM32!findstr.exe"
if not exist "!KEYPILOT_DISM!" (
  echo No changes were made. Windows DISM was not found.
  exit /b 2
)
if not exist "!KEYPILOT_FINDSTR!" (
  echo No changes were made. Windows findstr was not found.
  exit /b 2
)

rem Never expand command-line arguments in this recovery batch. CMD parses percent-expanded
rem quotes and metacharacters before execution, so command-line parameters are ignored entirely.
echo KeyPilot driver recovery - interactive fail-closed mode
echo Find the exact published package first with: pnputil /enum-drivers
set "KEYPILOT_PUBLISHED_NAME="
set /p "KEYPILOT_PUBLISHED_NAME=Published INF name (oemNN.inf): "
set "KEYPILOT_CONFIRMATION="
set /p "KEYPILOT_CONFIRMATION=Type KEYPILOT-RECOVERY: "
set "KEYPILOT_OFFLINE_INPUT="
set /p "KEYPILOT_OFFLINE_INPUT=Offline Windows drive root such as D:\ (blank for online): "

if not defined KEYPILOT_PUBLISHED_NAME goto invalid_published_name
if /i not "!KEYPILOT_CONFIRMATION!"=="KEYPILOT-RECOVERY" (
  echo No changes were made. The explicit KEYPILOT-RECOVERY token is required.
  exit /b 2
)

if /i not "!KEYPILOT_PUBLISHED_NAME:~0,3!"=="oem" goto invalid_published_name
if /i not "!KEYPILOT_PUBLISHED_NAME:~-4!"==".inf" goto invalid_published_name
set "KEYPILOT_PUBLISHED_DIGITS=!KEYPILOT_PUBLISHED_NAME:~3,-4!"
if not defined KEYPILOT_PUBLISHED_DIGITS goto invalid_published_name
set "KEYPILOT_NON_DIGITS=!KEYPILOT_PUBLISHED_DIGITS!"
for %%D in (0 1 2 3 4 5 6 7 8 9) do set "KEYPILOT_NON_DIGITS=!KEYPILOT_NON_DIGITS:%%D=!"
if defined KEYPILOT_NON_DIGITS goto invalid_published_name
goto published_name_valid
:invalid_published_name
  echo Refusing an invalid published INF name.
  exit /b 2

:published_name_valid
set "KEYPILOT_OFFLINE_ROOT="
if not defined KEYPILOT_OFFLINE_INPUT goto offline_root_valid
if not "!KEYPILOT_OFFLINE_INPUT:~1,2!"==":\" goto invalid_offline_root
if not "!KEYPILOT_OFFLINE_INPUT:~3!"=="" goto invalid_offline_root
set "KEYPILOT_DRIVE_LETTER=!KEYPILOT_OFFLINE_INPUT:~0,1!"
set "KEYPILOT_NON_LETTERS=!KEYPILOT_DRIVE_LETTER!"
for %%L in (A B C D E F G H I J K L M N O P Q R S T U V W X Y Z a b c d e f g h i j k l m n o p q r s t u v w x y z) do set "KEYPILOT_NON_LETTERS=!KEYPILOT_NON_LETTERS:%%L=!"
if defined KEYPILOT_NON_LETTERS goto invalid_offline_root
set "KEYPILOT_OFFLINE_ROOT=!KEYPILOT_OFFLINE_INPUT!"
goto offline_root_valid
:invalid_offline_root
  echo Refusing an invalid offline Windows image root. Use one drive root such as D:\
  exit /b 2

:offline_root_valid
set "KEYPILOT_TEMP_ATTEMPTS=0"
:create_recovery_temp
set /a KEYPILOT_TEMP_ATTEMPTS+=1 >nul
if !KEYPILOT_TEMP_ATTEMPTS! GTR 8 (
  echo Refusing removal because a private recovery temporary directory could not be created.
  exit /b 2
)
set "KEYPILOT_RECOVERY_TEMP=!KEYPILOT_SYSTEM32!config\KeyPilotRecovery-!RANDOM!-!RANDOM!-!RANDOM!"
2>nul md "!KEYPILOT_RECOVERY_TEMP!"
if errorlevel 1 goto create_recovery_temp
set "KEYPILOT_DRIVER_INFO=!KEYPILOT_RECOVERY_TEMP!\DriverInfo.txt"

if not defined KEYPILOT_OFFLINE_ROOT goto inspect_online
if not exist "!KEYPILOT_OFFLINE_ROOT!Windows\System32\Config\SYSTEM" (
  call :cleanup_recovery_temp
  echo Refusing an offline path that is not a Windows image drive root.
  exit /b 2
)
"!KEYPILOT_DISM!" /Image:"!KEYPILOT_OFFLINE_ROOT!" /Get-DriverInfo /Driver:"!KEYPILOT_PUBLISHED_NAME!" /English >"!KEYPILOT_DRIVER_INFO!" 2>&1
goto inspect_result

:inspect_online
"!KEYPILOT_DISM!" /online /Get-DriverInfo /Driver:"!KEYPILOT_PUBLISHED_NAME!" /English >"!KEYPILOT_DRIVER_INFO!" 2>&1

:inspect_result
if errorlevel 1 (
  type "!KEYPILOT_DRIVER_INFO!"
  call :cleanup_recovery_temp
  echo Refusing removal because DISM could not inspect the requested package.
  exit /b 2
)
"!KEYPILOT_FINDSTR!" /r /i /x /c:"Original File Name[ ]*:[ ]*KeyPilotFilter\.inf[ ]*" "!KEYPILOT_DRIVER_INFO!" >nul
if errorlevel 1 (
  call :cleanup_recovery_temp
  echo Refusing removal: the package is not KeyPilotFilter.
  exit /b 2
)
"!KEYPILOT_FINDSTR!" /r /i /x /c:"Provider Name[ ]*:[ ]*KeyPilot[ ]*" "!KEYPILOT_DRIVER_INFO!" >nul
if errorlevel 1 (
  call :cleanup_recovery_temp
  echo Refusing removal: provider identity did not match KeyPilot.
  exit /b 2
)
"!KEYPILOT_FINDSTR!" /r /i /x /c:"Class Name[ ]*:[ ]*Extension[ ]*" "!KEYPILOT_DRIVER_INFO!" >nul
if errorlevel 1 (
  call :cleanup_recovery_temp
  echo Refusing removal: class identity did not match Extension.
  exit /b 2
)
call :cleanup_recovery_temp

if defined KEYPILOT_OFFLINE_ROOT goto remove_offline
if not exist "!KEYPILOT_PNPUTIL!" (
  echo Removal was not attempted because Windows PnPUtil was not found.
  exit /b 2
)
"!KEYPILOT_PNPUTIL!" /delete-driver "!KEYPILOT_PUBLISHED_NAME!" /uninstall /force
set "KEYPILOT_PNP_EXIT=!ERRORLEVEL!"
if not "!KEYPILOT_PNP_EXIT!"=="0" if not "!KEYPILOT_PNP_EXIT!"=="3010" (
  echo Removal failed. Boot Safe Mode or Windows Recovery and run this script again as administrator.
  exit /b 1
)
"!KEYPILOT_PNPUTIL!" /scan-devices
if "!KEYPILOT_PNP_EXIT!"=="3010" echo Windows reported that a reboot is required.
echo Recovery removal requested. Reboot Windows.
exit /b 0

:remove_offline
"!KEYPILOT_DISM!" /Image:"!KEYPILOT_OFFLINE_ROOT!" /Remove-Driver /Driver:"!KEYPILOT_PUBLISHED_NAME!"
set "KEYPILOT_DISM_EXIT=!ERRORLEVEL!"
if not "!KEYPILOT_DISM_EXIT!"=="0" if not "!KEYPILOT_DISM_EXIT!"=="3010" (
  echo Offline removal failed. Verify the Windows image drive letter and package name.
  exit /b 1
)
echo Offline recovery removal requested. Reboot into Windows.
exit /b 0

:cleanup_recovery_temp
del /q "!KEYPILOT_DRIVER_INFO!" >nul 2>&1
rd "!KEYPILOT_RECOVERY_TEMP!" >nul 2>&1
exit /b 0
