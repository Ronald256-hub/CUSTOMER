@echo off
setlocal
cd /d "%~dp0"
title ROBO CASK AND TAP POS - SECURE SETUP

if not exist "%~dp0setup\installer.ps1" (
  echo.
  echo The installer is missing:
  echo %~dp0setup\installer.ps1
  pause
  exit /b 1
)

if not exist "%~dp0app\runtime\Robo.Pos.Server.exe" (
  echo.
  echo The secure Windows runtime is missing.
  echo Expected:
  echo %~dp0app\runtime\Robo.Pos.Server.exe
  echo.
  echo This package is incomplete and cannot be installed.
  pause
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0setup\installer.ps1"

if errorlevel 1 (
  echo.
  echo Setup did not complete successfully.
  echo Review README_FIRST.txt or run REPAIR_AND_DIAGNOSE.cmd.
  pause
  exit /b 1
)

exit /b 0
