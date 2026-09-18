# verify-api.ps1 - end-to-end verification of AIRenderService against a local mock API.
# ASCII-only + UTF-8 BOM on purpose (PowerShell 5.1 / ANSI codepage pitfalls).
param([string]$Repo = "E:\360MoveData\Users\Biz Johnson\Desktop\DEV\tuojiejiqi", [int]$Port = 8899)
$ErrorActionPreference = "Continue"
$out     = Join-Path $Repo "bin\Release\net7.0-windows"
$probe   = Join-Path $Repo "tools\win-verify\ApiProbe"
$log     = Join-Path $Repo "tools\win-verify\mock-requests.jsonl"
$report  = Join-Path $Repo "tools\win-verify\verify.log"
$fail = 0
Set-Content -Path $report -Value ("verify-api report " + (Get-Date -Format s)) -Encoding UTF8
function Say($ok, $name, $detail) {
  if ($null -eq $detail) { $detail = "" }
  $line = if ($ok) { "  PASS $name" } else { "  FAIL $name  -> $detail" }
  Write-Host $line; Add-Content -Path $report -Value $line -Encoding UTF8
  if (-not $ok) { $script:fail++ }
}

# 关键：先删掉上一轮的日志/报告，否则 mock 没起来时会拿旧日志"通过"，造成假阳性
Remove-Item $log -Force -ErrorAction SilentlyContinue
Remove-Item $report -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $Repo "tools\win-verify\mock.out.txt") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $Repo "tools\win-verify\mock.err.txt") -Force -ErrorAction SilentlyContinue

Write-Host "== 1/5 build probe =="
$b = & dotnet build (Join-Path $probe "ApiProbe.csproj") -c Release -p:TuoJieDir="$out" 2>&1
Add-Content -Path $report -Value $b -Encoding UTF8
Say ($LASTEXITCODE -eq 0) "probe build" ("exit=" + $LASTEXITCODE)
$probeOut = Join-Path $probe "bin\Release\net7.0-windows"

Write-Host "== 2/5 deploy probe into plugin output =="
Copy-Item (Join-Path $probeOut "ApiProbe.*") $out -Force
Copy-Item (Join-Path $out "TuoJie.rhp") (Join-Path $out "TuoJie.dll") -Force
Say (Test-Path (Join-Path $out "ApiProbe.exe")) "probe deployed" ""

Write-Host "== 2b/5 stage RhinoCommon for out-of-Rhino run =="
# net7 版 RhinoCommon 随 Rhino 8 安装包提供（NuGet 包里只有 net48），优先用 Rhino 自带的那份
$rcCandidates = @(
  "C:\Program Files\Rhino 8\System\RhinoCommon.dll",
  (Join-Path $env:USERPROFILE ".nuget\packages\rhinocommon\8.0.23304.9001\lib\net48\RhinoCommon.dll")
)
$rc = $rcCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($rc) { $rc = Get-Item $rc }
if ($rc) {
  Copy-Item $rc.FullName $out -Force
  # Rhino 目录里的伴随程序集（Eto/Rhino3dm 等）只在同名文件存在时一并带上，避免污染
  foreach ($dep in @("Rhino3dm.dll","Eto.dll","Rhino.UI.dll")) {
    $p = Join-Path (Split-Path $rc.FullName) $dep
    if (Test-Path $p) { Copy-Item $p $out -Force }
  }
  Say $true "RhinoCommon staged" $rc.FullName
} else {
  Say $false "RhinoCommon.dll not found in nuget cache" $rcPkg
}

Write-Host "== 3/5 start mock =="
# 注意：路径可能含空格（如 Biz Johnson），Start-Process -ArgumentList 不会自动加引号，
# 会把脚本路径拆成两段导致进程秒退，所以这里用 Start-Job（参数按对象传递）。
$mockOut = Join-Path $Repo "tools\win-verify\mock.out.txt"
$mockErr = Join-Path $Repo "tools\win-verify\mock.err.txt"
$mockScript = Join-Path $Repo "tools\win-verify\mock-api.ps1"
$mockJob = Start-Job -ScriptBlock {
  param($script, $port, $logPath, $outPath, $errPath)
  try { & $script -Port $port -LogPath $logPath *> $outPath } catch { $_ | Out-File $errPath -Encoding utf8 }
} -ArgumentList $mockScript, $Port, $log, $mockOut, $mockErr

# 轮询直到 mock 真可达（最多 10 秒）
$ready = $false
for ($i = 0; $i -lt 20; $i++) {
  try {
    $r = Invoke-WebRequest -Uri "http://localhost:$Port/health" -UseBasicParsing -TimeoutSec 2
    if ($r.StatusCode -eq 200) { $ready = $true; break }
  } catch { Start-Sleep -Milliseconds 500 }
}
Say $ready "mock reachable on port $Port" ("job=" + $mockJob.State)
if (-not $ready) {
  Add-Content -Path $report -Value "--- mock stdout ---" -Encoding UTF8
  if (Test-Path $mockOut) { Add-Content -Path $report -Value (Get-Content $mockOut) -Encoding UTF8 }
  Add-Content -Path $report -Value "--- mock stderr ---" -Encoding UTF8
  if (Test-Path $mockErr) { Add-Content -Path $report -Value (Get-Content $mockErr) -Encoding UTF8 }
}

Write-Host "== 4/5 run probe (real Sidecar round trip) =="
$env:MOCK_BASE = "http://localhost:$Port"
Push-Location $out
$probeOut2 = & (Join-Path $out "ApiProbe.exe") 2>&1
$probeExit = $LASTEXITCODE
Pop-Location
Add-Content -Path $report -Value $probeOut2 -Encoding UTF8
$probeOut2 | Write-Host
Say ($probeExit -eq 0) "probe exit code" ("exit=" + $probeExit)

Write-Host "== 5/5 assert request shapes from mock log =="
$recs = @()
if (Test-Path $log) { $recs = Get-Content $log | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json } }
Say ($ready) "assertions are backed by a live mock" ("ready=" + $ready)
Say ($recs.Count -ge 6) "mock recorded >= 6 requests" ("count=" + $recs.Count)
$edits = @($recs | Where-Object { $_.path -eq "/v1/images/edits" })
$gen   = @($recs | Where-Object { $_.path -eq "/v1/images/generations" })
Say ($edits.Count -ge 6) "non-apiyi host uses /v1/images/edits" ("edits=" + $edits.Count)
Say ($gen.Count -eq 0) "did not route to generations" ("gen=" + $gen.Count)
$posts = @($recs | Where-Object { $_.method -eq "POST" })
Say (@($posts | Where-Object { $_.authHeader -ne "Bearer probe-key" }).Count -eq 0) "all POST requests carry Bearer probe-key" ("posts=" + $posts.Count)
Say (@($edits | Where-Object { -not ($_.hasImage -or $_.hasImageArr) }).Count -eq 0) "every edits request has image part" ""
# 快速模式：唯一一条不带 size 的 POST（promptHead 里含 system prompt，不能靠关键词识别）
$noSize = @($posts | Where-Object { -not $_.hasSize })
Say ($noSize.Count -eq 1) "fast mode: exactly one POST without size" ("withoutSize=" + $noSize.Count)
Say (@($noSize | Where-Object { $_.path -eq "/v1/images/edits" }).Count -eq 1) "the size-less POST is an edits call" ""
$std = @($recs | Where-Object { $_.hasSize })
Say (@($std | Where-Object { $_.size -eq "2048x1152" }).Count -ge 4) "standard mode size=2048x1152" ("withSize=" + $std.Count)
$mk = @($recs | Where-Object { $_.hasMask })
Say ($mk.Count -eq 1) "exactly one request carries mask" ("mask=" + $mk.Count)
Say (@($mk | Where-Object { $_.model -eq "gpt-image-2.5-sunburst" }).Count -eq 1) "mask request uses gpt-image-2.5-sunburst" ""

try { Stop-Job $mockJob -ErrorAction SilentlyContinue; Remove-Job $mockJob -Force -ErrorAction SilentlyContinue } catch {}
Remove-Item (Join-Path $out "ApiProbe.*") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $out "TuoJie.dll") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $out "RhinoCommon.dll") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $out "Rhino3dm.dll") -Force -ErrorAction SilentlyContinue

Write-Host ""
if ($fail -eq 0) { Write-Host "ALL PASS" } else { Write-Host ("FAILED " + $fail + " checks") }
Add-Content -Path $report -Value ("TOTAL FAIL=" + $fail) -Encoding UTF8
exit $fail
