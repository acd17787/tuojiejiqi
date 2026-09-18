@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0.."

rem ============================================================
rem  TuoJie 发布门禁第一层：一条命令跑完全部 9 项，全绿或阻断。
rem  对应 docs\RELEASE_ACCEPTANCE_CHECKLIST.md 第一层。
rem  任一项失败立即退出（非零），不允许跳过。
rem ============================================================

where bash >nul 2>&1
if errorlevel 1 (
  echo [BLOCK] bash not on PATH - build-release.sh cannot run. Install Git Bash or add it to PATH.
  exit /b 1
)
where python >nul 2>&1
if errorlevel 1 (
  echo [BLOCK] python not on PATH - static checks cannot run. Install Python 3.
  exit /b 1
)

echo ========================================
echo   TuoJie Release Gate (9 items)
echo ========================================

echo.
echo [1/9] build-release.sh (net7 + net48 + two sidecars, zero-warning gate)
call bash tools/build-release.sh
if errorlevel 1 goto :fail

echo.
echo [2/9] static_resource_order_check (StaticResource forward refs)
python tools\static_resource_order_check.py
if errorlevel 1 goto :fail

echo.
echo [3/9] binding_audit (binding paths)
python tools\binding_audit.py
if errorlevel 1 goto :fail

echo.
echo [4/9] layout_contract_check (wide/narrow layout, mask z-order, canvas binding)
python tools\layout_contract_check.py
if errorlevel 1 goto :fail

echo.
echo [5/9] ui-harness (offscreen render: canvas follows Viewbox + layout stability)
pushd tools\ui-harness
python gen_window.py
if errorlevel 1 ( popd & goto :fail )
dotnet run -c Release -- 960 1265
if errorlevel 1 ( popd & goto :fail )
dotnet run -c Release -- 1500 1000
if errorlevel 1 ( popd & goto :fail )
popd

echo.
echo [6/9] sidecar concurrency probe (staged next to the plugin output)
if not exist "bin\Release\net7.0-windows\TuoJie.rhp" (
  echo [BLOCK] bin\Release\net7.0-windows\TuoJie.rhp missing - item 1 must run first.
  goto :fail
)
dotnet build tools\win-verify\ApiProbe\ApiProbe.csproj -c Release -p:TuoJieDir="%CD%\bin\Release\net7.0-windows"
if errorlevel 1 goto :fail
copy /y tools\win-verify\ApiProbe\bin\Release\net7.0-windows\ApiProbe.* bin\Release\net7.0-windows\ >nul
copy /y bin\Release\net7.0-windows\TuoJie.rhp bin\Release\net7.0-windows\TuoJie.dll >nul
if not exist "C:\Program Files\Rhino 8\System\RhinoCommon.dll" (
  echo [BLOCK] RhinoCommon.dll not found - install Rhino 8 or stage it next to ApiProbe.exe.
  goto :fail
)
copy /y "C:\Program Files\Rhino 8\System\RhinoCommon.dll" bin\Release\net7.0-windows\ >nul
pushd bin\Release\net7.0-windows
set CONCURRENCY_PROBE=1
ApiProbe.exe
if errorlevel 1 ( popd & goto :fail )
set CONCURRENCY_PROBE=

echo.
echo [7/9] conversion equivalence probe (pixel fidelity of both image paths)
set CONVERSION_EQUIVALENCE_PROBE=1
ApiProbe.exe
if errorlevel 1 ( popd & goto :fail )
set CONVERSION_EQUIVALENCE_PROBE=
popd

echo.
echo [8/9] verify-api (protocol routing, request shapes, mask model, thumbnail decode)
rem verify-api 会重新部署探针并在结束时回收上面两项的 staging（含 Eto/Rhino.UI），
rem 所以它放在探针之后跑。
call "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File tools\win-verify\verify-api.ps1
if errorlevel 1 goto :fail

echo.
echo [9/9] preflight (packaging, diagnose x4, >1MB pipe, runtimes)
call "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File tools\preflight-release.ps1
if errorlevel 1 goto :fail

echo.
echo ========================================
echo   RELEASE GATE PASSED (9/9)
echo   Continue with tier 2 (product operation round) and tier 3
echo   in docs\RELEASE_ACCEPTANCE_CHECKLIST.md before sending to customers.
echo ========================================
exit /b 0

:fail
echo.
echo ========================================
echo   RELEASE GATE BLOCKED - do not tag, do not send the package.
echo ========================================
exit /b 1
