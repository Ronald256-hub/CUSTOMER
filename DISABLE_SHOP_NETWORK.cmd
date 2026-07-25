@echo off
setlocal
cd /d "%~dp0"
title ROBO CASK AND TAP POS - DISABLE SHOP NETWORK

if not exist "%~dp0app\network-setup.ps1" (
  echo.
  echo The network setup tool is missing.
  pause
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0app\network-setup.ps1" -Mode Disable -Portable

if errorlevel 1 (
  echo.
  echo Shop network mode could not be disabled.
  pause
  exit /b 1
)

exit /b 0
