param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $repoRoot "bin\Release"
$tempRoot = Join-Path $env:TEMP ("TuoJiePreflight_" + [Guid]::NewGuid().ToString("N"))

$script:Failed = $false

function Step($name) {
    Write-Host ""
    Write-Host "== $name ==" -ForegroundColor Cyan
}

function Pass($message) {
    Write-Host "  [OK] $message" -ForegroundColor Green
}

function Fail($message) {
    Write-Host "  [FAIL] $message" -ForegroundColor Red
    $script:Failed = $true
}

function NeedFile($folder, $name) {
    $path = Join-Path $folder $name
    if (Test-Path $path) { Pass "$name" } else { Fail "$name missing in $folder" }
}

function NeedDir($folder, $name) {
    $path = Join-Path $folder $name
    if (Test-Path $path -PathType Container) { Pass "$name\" } else { Fail "$name\ missing in $folder" }
}

function Run($command, $workDir = $repoRoot) {
    Write-Host "  > $command" -ForegroundColor DarkGray
    cmd /c "cd /d `"$workDir`" && $command"
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $command"
    }
}

function RunDiagnose($folder) {
    $diagnose = Join-Path $folder "diagnose.bat"
    if (!(Test-Path $diagnose)) {
        Fail "diagnose.bat missing in $folder"
        return
    }

    $output = cmd /c "cd /d `"$folder`" && echo. | diagnose.bat" 2>&1
    $output | ForEach-Object { Write-Host "  $_" }
    if ($output -match "Issues:\s+0") {
        Pass "diagnose.bat reports Issues: 0"
    } else {
        Fail "diagnose.bat did not report Issues: 0"
    }
}

function TestLargeSidecarPipe($folder, $exeName) {
    $exe = Join-Path $folder $exeName
    if (!(Test-Path $exe)) {
        Fail "$exeName missing for large pipe test"
        return
    }

    $pipeName = "TuoJiePreflight-" + [Guid]::NewGuid().ToString("N")
    $body = [Convert]::ToBase64String((New-Object byte[] (2 * 1024 * 1024)))
    $json = '{"Id":"preflight","Method":"POST","Url":"http://127.0.0.1:1/preflight","Headers":{"Content-Type":["application/octet-stream"]},"Body":"' + $body + '"}'
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $length = [BitConverter]::GetBytes($bytes.Length)

    $process = Start-Process -FilePath $exe -ArgumentList "`"$pipeName`" 0" -PassThru -WindowStyle Hidden
    try {
        Start-Sleep -Milliseconds 800
        if ($process.HasExited) {
            Fail "$exeName exited before pipe test"
            return
        }

        $client = New-Object IO.Pipes.NamedPipeClientStream(".", $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::None)
        try {
            $client.Connect(10000)
            $client.Write($length, 0, $length.Length)
            $client.Write($bytes, 0, $bytes.Length)
            $client.Flush()

            $lenBuffer = New-Object byte[] 4
            ReadExact $client $lenBuffer 4
            $responseLength = [BitConverter]::ToInt32($lenBuffer, 0)
            if ($responseLength -le 0 -or $responseLength -gt 1048576) {
                Fail "invalid Sidecar response length: $responseLength"
                return
            }

            $responseBuffer = New-Object byte[] $responseLength
            ReadExact $client $responseBuffer $responseLength
            $responseText = [Text.Encoding]::UTF8.GetString($responseBuffer)
            if ($responseText -match '"Id":"preflight"' -or $responseText -match '"Id":\s*"preflight"') {
                Pass "$exeName accepts >1MB pipe request"
            } else {
            Fail "$exeName returned unexpected pipe response"
            }
        } catch {
            Fail "$exeName large pipe test failed: $($_.Exception.Message)"
        } finally {
            $client.Dispose()
        }
    } finally {
        if (!$process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
    }
}

function ReadExact($stream, [byte[]]$buffer, [int]$count) {
    $offset = 0
    while ($offset -lt $count) {
        $read = $stream.Read($buffer, $offset, $count - $offset)
        if ($read -le 0) {
            throw "Pipe closed while reading"
        }
        $offset += $read
    }
}

try {
    Step "Build"
    if ($SkipBuild) {
        Pass "build skipped"
    } else {
        Run "dotnet build Sidecar\Sidecar.csproj -c Release -f net48"
        Run "dotnet build Sidecar\Sidecar.csproj -c Release -f net7.0-windows"
        Run "dotnet build AIRenderer.csproj -c Release -f net48"
        Run "dotnet build AIRenderer.csproj -c Release -f net7.0-windows"
        Pass "all Release targets built"
    }

    $net7 = Join-Path $releaseRoot "net7.0-windows"
    $net48 = Join-Path $releaseRoot "net48"

    Step "Static Checks"
    # These three cover the "builds clean, breaks at runtime" class:
    #   static_resource_order_check - StaticResource forward references. XAML
    #     compiler does not catch these; the window throws XamlParseException on open.
    #   binding_audit - binding paths pointing at members that do not exist
    #     (silent failure: the UI just shows nothing).
    #   layout_contract_check - wide/narrow layout and mask z-order contracts.
    # NOTE: keep this file ASCII-only and without BOM. Windows PowerShell 5.1 reads
    # .ps1 as ANSI when there is no BOM, so non-ASCII text breaks the parser.
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($null -eq $python) {
        Fail "python not found on PATH; static checks cannot run (install Python 3, or run tools\*.py by hand)"
    } else {
        Run "python tools\static_resource_order_check.py"
        Run "python tools\binding_audit.py"
        Run "python tools\layout_contract_check.py"
        Pass "static checks passed"
    }

    Step "Release Files net7"
    foreach ($file in @(
        "TuoJie.rhp",
        "TuoJie.deps.json",
        "TuoJie.runtimeconfig.json",
        "Newtonsoft.Json.dll",
        "TuoJieSidecar.exe",
        "TuoJieSidecar.dll",
        "TuoJieSidecar.deps.json",
        "TuoJieSidecar.runtimeconfig.json",
        "TuoJieSidecar-net48.exe",
        "TuoJieSidecar-net48.exe.config",
        "diagnose.bat"
    )) { NeedFile $net7 $file }
    NeedDir $net7 "runtimes"
    NeedDir $net7 "net48-sidecar"
    foreach ($file in @(
        "TuoJieSidecar.exe",
        "TuoJieSidecar.exe.config",
        "Newtonsoft.Json.dll"
    )) { NeedFile (Join-Path $net7 "net48-sidecar") $file }

    Step "Release Files net48"
    foreach ($file in @(
        "TuoJie.rhp",
        "Newtonsoft.Json.dll",
        "TuoJieSidecar.exe",
        "TuoJieSidecar.exe.config",
        "diagnose.bat"
    )) { NeedFile $net48 $file }

    Step "Diagnostics"
    RunDiagnose $net7
    RunDiagnose $net48

    Step "Large Pipe Requests"
    TestLargeSidecarPipe $net7 "TuoJieSidecar.exe"
    TestLargeSidecarPipe (Join-Path $net7 "net48-sidecar") "TuoJieSidecar.exe"
    TestLargeSidecarPipe $net48 "TuoJieSidecar.exe"

    Step "Extracted Package Simulation"
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $simNet7 = Join-Path $tempRoot "net7"
    $simNet48 = Join-Path $tempRoot "net48"
    Copy-Item $net7 $simNet7 -Recurse
    Copy-Item $net48 $simNet48 -Recurse
    RunDiagnose $simNet7
    RunDiagnose $simNet48

    Step "Summary"
    if ($script:Failed) {
        Write-Host "Preflight failed." -ForegroundColor Red
        exit 1
    }

    Write-Host "Preflight passed." -ForegroundColor Green
    exit 0
} finally {
    if (Test-Path $tempRoot) {
        Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
