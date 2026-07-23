@echo off
setlocal
cd /d "%~dp0"
title ROBO CASK AND TAP POS SETUP
where powershell.exe >nul 2>&1
if errorlevel 1 (
  echo Windows PowerShell is missing. This software requires standard Windows PowerShell.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0setup\installer.ps1"
if errorlevel 1 (
  echo.
  echo Setup encountered an error. Run REPAIR_AND_DIAGNOSE.cmd or read README_FIRST.txt.
  pause
)
