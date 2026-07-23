@echo off
setlocal
cd /d "%~dp0"
title ROBO CASK AND TAP POS - PORTABLE TEST
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0app\launcher.ps1" -Portable
if errorlevel 1 (
  echo.
  echo The software did not start. Run REPAIR_AND_DIAGNOSE.cmd for details.
  pause
)
