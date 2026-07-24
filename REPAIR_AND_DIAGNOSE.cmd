@echo off
setlocal
cd /d "%~dp0"
title ROBO CASK AND TAP POS - DIAGNOSTICS

if not exist "%~dp0app\diagnose.ps1" (
  echo.
  echo The diagnostic tool is missing:
  echo %~dp0app\diagnose.ps1
  pause
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0app\diagnose.ps1" -Portable

if errorlevel 1 (
  echo.
  echo One or more diagnostic checks failed.
  echo Review DIAGNOSTIC_REPORT.txt in the portable data folder.
  pause
  exit /b 1
)

exit /b 0
