@echo off
setlocal

cd /d "%~dp0.."

echo ========================================
echo   TuoJie Release Test Gate
echo ========================================
echo.
echo [1/2] Running automated preflight...
call tools\preflight-release.bat
if errorlevel 1 (
  echo.
  echo Preflight failed. Do not send the package.
  exit /b 1
)

echo.
echo [2/2] Automated preflight passed.
echo Continue with the manual checklist:
echo   docs\RELEASE_ACCEPTANCE_CHECKLIST.md
echo.
echo Release is not customer-ready until that checklist is complete.
exit /b 0
