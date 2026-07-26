@echo off
setlocal
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\release-engineering\BOOTSTRAP_WINDOWS_RELEASE.ps1" -SourceRoot "%CD%" %*
set EXIT_CODE=%ERRORLEVEL%

if not "%EXIT_CODE%"=="0" (
  echo.
  echo Nexus POS Windows release build failed with exit code %EXIT_CODE%.
  pause
  exit /b %EXIT_CODE%
)

echo.
echo Nexus POS Windows release build completed successfully.
pause
