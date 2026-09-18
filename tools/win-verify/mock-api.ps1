# mock-api.ps1 - local mock image API for verifying AIRenderService request/response handling.
# ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI, which corrupts non-ASCII text.
#   __B64RAW__      -> {"data":[{"b64_json":"<raw base64>"}]}
#   __B64PREFIXED__ -> {"data":[{"b64_json":"data:image/png;base64,<...>"}]}
#   __URL__         -> {"data":[{"url":"http://localhost:<port>/img/test.png"}]}
#   __DATAURL__     -> whole body is a data URL (non-JSON)
param([int]$Port = 8899, [string]$LogPath = "mock-requests.jsonl")
$ErrorActionPreference = "Stop"
$png = [Convert]::FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR42mO4o6GBFTEMLQkAe3tLAYZNzu4AAAAASUVORK5CYII=")
if (Test-Path $LogPath) { Remove-Item $LogPath -Force }
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "mock-api listening on http://localhost:$Port/  log=$LogPath"
function Get-Field([string]$body, [string]$name) {
  $m = [regex]::Match($body, 'name="?' + [regex]::Escape($name) + '"?\r?\n\r?\n([^\r\n]*)')
  if ($m.Success) { return $m.Groups[1].Value }
  $m2 = [regex]::Match($body, '"' + [regex]::Escape($name) + '"\s*:\s*"([^"]*)"')
  if ($m2.Success) { return $m2.Groups[1].Value }
  return ""
}
while ($listener.IsListening) {
  try {
    $ctx = $listener.GetContext(); $req = $ctx.Request; $res = $ctx.Response
    $body = ""
    if ($req.HasEntityBody) {
      $reader = New-Object System.IO.StreamReader($req.InputStream, $req.ContentEncoding)
      $body = $reader.ReadToEnd(); $reader.Close()
    }
    $prompt = Get-Field $body "prompt"
    $rec = [ordered]@{
      method = $req.HttpMethod; path = $req.Url.AbsolutePath; contentType = $req.ContentType
      authHeader = [string]$req.Headers["Authorization"]; bodyLength = $body.Length
      hasImage = ($body -match 'name="?image"?(?!\[)'); hasImageArr = ($body -match 'name="?image\[\]"?')
      hasMask = ($body -match 'name="?mask"?'); hasSize = ($body -match 'name="?size"?')
      hasJsonImage = $body.Contains('"image"'); model = (Get-Field $body "model")
      size = (Get-Field $body "size"); jsonSize = ([regex]::Match($body, '"size"\s*:\s*"([^"]*)"')).Groups[1].Value
      promptHead = $prompt.Substring(0, [Math]::Min(50, $prompt.Length))
    }
    ($rec | ConvertTo-Json -Compress) | Add-Content -Path $LogPath -Encoding UTF8
    if ($req.Url.AbsolutePath -eq "/img/test.png") {
      $res.ContentType = "image/png"; $res.ContentLength64 = $png.Length
      $res.OutputStream.Write($png, 0, $png.Length); $res.Close(); continue
    }
    $res.Headers["Content-Type"] = "application/json"
    if ($body.Contains("__URL__")) {
      $payload = '{"data":[{"url":"http://localhost:' + $Port + '/img/test.png"}]}'
    } elseif ($body.Contains("__DATAURL__")) {
      $res.Headers["Content-Type"] = "text/plain"; $payload = "data:image/png;base64," + "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR42mO4o6GBFTEMLQkAe3tLAYZNzu4AAAAASUVORK5CYII="
    } elseif ($body.Contains("__B64PREFIXED__")) {
      $payload = '{"data":[{"b64_json":"data:image/png;base64,' + "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR42mO4o6GBFTEMLQkAe3tLAYZNzu4AAAAASUVORK5CYII=" + '"}]}'
    } else {
      $payload = '{"data":[{"b64_json":"' + "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR42mO4o6GBFTEMLQkAe3tLAYZNzu4AAAAASUVORK5CYII=" + '"}]}'
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
    $res.ContentLength64 = $bytes.Length; $res.OutputStream.Write($bytes, 0, $bytes.Length); $res.Close()
  } catch {
    Write-Host "mock error: $_"
    try { $ctx.Response.StatusCode = 500; $ctx.Response.Close() } catch {}
  }
}
