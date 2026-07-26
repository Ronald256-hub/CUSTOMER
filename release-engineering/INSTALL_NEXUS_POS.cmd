@echo off
setlocal
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File ""%CD%\INSTALL_NEXUS_POS.ps1""'"

if errorlevel 1 (
  echo.
  echo Nexus POS installation did not complete successfully.
  echo Review C:\ProgramData\Nexus POS\Install Logs for details.
  pause
  exit /b 1
)

echo.
echo Nexus POS installation completed.
pause
