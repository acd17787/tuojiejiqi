@echo off
setlocal
cd /d "%~dp0.."
set PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0preflight-release.ps1" %*
if errorlevel 1 (
  echo.
  echo Preflight failed.
  pause
  exit /b 1
)
echo.
echo Preflight passed.
pause
