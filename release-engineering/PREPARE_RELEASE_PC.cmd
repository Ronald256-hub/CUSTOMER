@echo off
setlocal
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File ""%CD%\INSTALL_BUILD_PREREQUISITES.ps1"" -IncludeInnoSetup -IncludeNodeJs'"

if errorlevel 1 (
  echo.
  echo Nexus POS release prerequisite installation did not complete.
  pause
  exit /b 1
)

echo.
echo Nexus POS release computer preparation completed.
pause
