@echo off
setlocal enabledelayedexpansion
title TuoJie Diagnostic Tool

echo ========================================
echo   TuoJie Plugin Diagnostic Tool v1.4
echo ========================================
echo Folder: %~dp0
echo.

set OK=0
set BAD=0
set WARN=0

echo --- [1/6] Package Files ---

call :need "TuoJie.rhp"
call :need "Newtonsoft.Json.dll"

if exist "%~dp0TuoJie.runtimeconfig.json" (
    echo   [OK] Rhino 8 net7 runtimeconfig found
    set PACKAGE_KIND=net7
    set /a OK+=1
    call :need "TuoJie.deps.json"
    call :need "TuoJieSidecar.exe"
    call :need "TuoJieSidecar.deps.json"
    call :need "TuoJieSidecar.runtimeconfig.json"
    if exist "%~dp0runtimes\" (
        echo   [OK] runtimes\ folder
        set /a OK+=1
    ) else (
        echo   [WARN] runtimes\ folder missing; some .NET dependencies may fail to load
        set /a WARN+=1
    )
) else (
    echo   [INFO] No net7 runtimeconfig; treating package as net48/Rhino 7 fallback package
    set PACKAGE_KIND=net48
    call :need "TuoJieSidecar.exe"
)

if exist "%~dp0net48-sidecar\TuoJieSidecar.exe" (
    echo   [OK] net48-sidecar\TuoJieSidecar.exe fallback
    set /a OK+=1
) else if exist "%~dp0TuoJieSidecar-net48.exe" (
    echo   [WARN] legacy TuoJieSidecar-net48.exe fallback found; net48-sidecar\ is preferred
    set /a WARN+=1
) else (
    if "%PACKAGE_KIND%"=="net7" (
        echo   [WARN] net48 fallback Sidecar is missing
        set /a WARN+=1
    ) else (
        echo   [INFO] net48 package uses TuoJieSidecar.exe directly
    )
)

if exist "%~dp0diagnose.bat" (
    echo   [OK] diagnose.bat
    set /a OK+=1
) else (
    echo   [WARN] diagnose.bat is not in the plugin folder
    set /a WARN+=1
)

echo.
echo --- [2/6] .NET Runtime ---

dotnet --list-runtimes >nul 2>&1
if !ERRORLEVEL! EQU 0 (
    echo   Installed .NET runtimes:
    dotnet --list-runtimes 2>nul | findstr /C:"Microsoft.NETCore.App"
    dotnet --list-runtimes 2>nul | findstr /R /C:"Microsoft.NETCore.App 7\." >nul
    if !ERRORLEVEL! EQU 0 (
        echo   [OK] .NET 7 runtime found
        set /a OK+=1
    ) else (
        if "%PACKAGE_KIND%"=="net7" (
            if exist "%~dp0TuoJieSidecar-net48.exe" (
                echo   [WARN] .NET 7 runtime not found, but net48 Sidecar fallback exists
                set /a WARN+=1
            ) else (
                echo   [BAD] .NET 7 runtime not found and no net48 Sidecar fallback exists
                set /a BAD+=1
            )
        ) else (
            echo   [INFO] .NET 7 runtime not required for net48 package
        )
    )
) else (
    if "%PACKAGE_KIND%"=="net7" (
        if exist "%~dp0TuoJieSidecar-net48.exe" (
            echo   [WARN] dotnet command not found; net48 Sidecar fallback may still work
            set /a WARN+=1
        ) else (
            echo   [BAD] dotnet command not found and no net48 Sidecar fallback exists
            set /a BAD+=1
        )
    ) else (
        echo   [INFO] dotnet command not found; not required for net48 package
    )
)

call :check_netfx48

echo.
echo --- [3/6] Rhino Install ---

set RHINO_FOUND=0
for %%p in (
    "C:\Program Files\Rhino 8\System\Rhino.exe"
    "C:\Program Files\Rhino 7\System\Rhino.exe"
) do (
    if exist %%p (
        echo   [OK] %%p
        set RHINO_FOUND=1
        set /a OK+=1
    )
)

if "%RHINO_FOUND%"=="0" (
    echo   [WARN] Rhino was not found in the default install paths
    echo          If Rhino is installed elsewhere, this is not necessarily a problem.
    set /a WARN+=1
)

echo.
echo --- [4/6] Sidecar Startup ---

set SIDECAR_OK=0
if exist "%~dp0TuoJieSidecar.exe" (
    call :test_sidecar "TuoJieSidecar.exe"
) else (
    echo   [BAD] TuoJieSidecar.exe missing
    set /a BAD+=1
)

if "%SIDECAR_OK%"=="0" (
    if exist "%~dp0net48-sidecar\TuoJieSidecar.exe" (
        call :test_sidecar "net48-sidecar\TuoJieSidecar.exe"
    ) else if exist "%~dp0TuoJieSidecar-net48.exe" (
        call :test_sidecar "TuoJieSidecar-net48.exe"
    )
)

if "%SIDECAR_OK%"=="0" (
    echo   [BAD] No Sidecar executable could start
    echo         Check antivirus quarantine, file permissions, and .NET runtime installation.
    set /a BAD+=1
)

echo.
echo --- [5/6] Plugin Load Hints ---

if exist "%~dp0TuoJie.rhp" (
    echo   [OK] TuoJie.rhp exists
    set /a OK+=1
) else (
    echo   [BAD] TuoJie.rhp missing
    set /a BAD+=1
)

if "%PACKAGE_KIND%"=="net7" (
    findstr /C:".NETCoreApp" "%~dp0TuoJie.deps.json" >nul 2>&1
    if !ERRORLEVEL! EQU 0 (
        echo   [OK] TuoJie.deps.json contains .NETCoreApp target
        set /a OK+=1
    ) else (
        echo   [WARN] Could not confirm .NETCoreApp target in TuoJie.deps.json
        set /a WARN+=1
    )
) else (
    echo   [INFO] net48 package does not require TuoJie.deps.json/runtimeconfig.json
)

echo.
echo --- [6/6] Firewall Hint ---

netsh advfirewall firewall show rule name="TuoJieSidecar" >nul 2>&1
if !ERRORLEVEL! EQU 0 (
    echo   [OK] Firewall rule named TuoJieSidecar exists
    set /a OK+=1
) else (
    echo   [INFO] No firewall rule named TuoJieSidecar
    echo          If API calls fail with a socket/firewall error, run the matching command in ADMIN CMD:
    echo.
    if exist "%~dp0TuoJieSidecar.exe" (
        echo          netsh advfirewall firewall add rule name="TuoJieSidecar" dir=out program="%~dp0TuoJieSidecar.exe" action=allow
    )
    if exist "%~dp0TuoJieSidecar-net48.exe" (
        echo          netsh advfirewall firewall add rule name="TuoJieSidecar-net48" dir=out program="%~dp0TuoJieSidecar-net48.exe" action=allow
    )
    if exist "%~dp0net48-sidecar\TuoJieSidecar.exe" (
        echo          netsh advfirewall firewall add rule name="TuoJieSidecar-net48" dir=out program="%~dp0net48-sidecar\TuoJieSidecar.exe" action=allow
    )
    echo.
)

echo ========================================
echo   Summary
echo ========================================
echo   Passed: !OK!
echo   Warnings: !WARN!
echo   Issues: !BAD!
echo.

if !BAD! EQU 0 (
    echo Package looks usable. If Rhino still cannot load it, send this full window output.
) else (
    echo Issues found. Please share the [BAD] or [MISSING] lines above.
)

echo.
echo Press any key to exit...
pause >nul
exit /b

:need
if exist "%~dp0%~1" (
    echo   [OK] %~1
    set /a OK+=1
) else (
    echo   [MISSING] %~1
    set /a BAD+=1
)
exit /b

:check_netfx48
set NETFX_RELEASE=
for /f "tokens=3" %%r in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul ^| findstr /C:"Release"') do set NETFX_RELEASE=%%r
if defined NETFX_RELEASE (
    set /a NETFX_RELEASE_DEC=!NETFX_RELEASE!
    if !NETFX_RELEASE_DEC! GEQ 528040 (
        echo   [OK] .NET Framework 4.8 or newer found
        set /a OK+=1
    ) else (
        echo   [WARN] .NET Framework release !NETFX_RELEASE_DEC! is older than 4.8
        set /a WARN+=1
    )
) else (
    echo   [WARN] .NET Framework 4.x release key not found
    set /a WARN+=1
)
exit /b

:test_sidecar
set SIDECAR_EXE=%~1
echo   Testing %SIDECAR_EXE% ...
rem Diagnostic mode verifies that the executable and runtime can start without entering pipe-server mode.
"%~dp0%SIDECAR_EXE%" --diag >nul 2>&1
if !ERRORLEVEL! EQU 0 (
    echo   [OK] %SIDECAR_EXE% can start
    set SIDECAR_OK=1
    set /a OK+=1
) else (
    echo   [FAIL] %SIDECAR_EXE% failed to start
    set /a WARN+=1
)
exit /b
