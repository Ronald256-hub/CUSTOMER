@echo off
setlocal
cd /d "%~dp0"
title ROBO CASK AND TAP POS - PORTABLE

if not exist "%~dp0app\runtime\Robo.Pos.Server.exe" (
  echo.
  echo The secure Windows runtime is missing.
  echo Expected:
  echo %~dp0app\runtime\Robo.Pos.Server.exe
  echo.
  echo Rebuild the release package before running this file.
  pause
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0app\launcher.ps1" -Portable

if errorlevel 1 (
  echo.
  echo ROBO CASK AND TAP POS could not start.
  echo Run REPAIR_AND_DIAGNOSE.cmd for details.
  pause
  exit /b 1
)

exit /b 0
