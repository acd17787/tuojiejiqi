#!/usr/bin/env bash
# 完整发布构建（macOS / Windows 均可运行）。
# 顺序很重要：两个 Sidecar 都先于主工程；net7 Sidecar 需要额外 publish 出 win-x64 apphost，
# 否则 bin 里不会有 TuoJieSidecar.exe（macOS 上 `dotnet build` 不产 apphost）。
set -euo pipefail

cd "$(dirname "$0")/.."

TFM="${1:-net7.0-windows}"

export DOTNET_ROOT="${DOTNET_ROOT:-$PWD/.tools/dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$PWD/.tools/home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$PWD/.tools/nuget}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

echo "==> 1/4 Sidecar net7.0-windows (build)"
dotnet build Sidecar/Sidecar.csproj -c Release -f net7.0-windows

echo "==> 2/4 Sidecar net48 (build，作为运行时兜底)"
dotnet build Sidecar/Sidecar.csproj -c Release -f net48

echo "==> 3/4 Sidecar net7.0-windows (publish win-x64，产出 TuoJieSidecar.exe)"
dotnet publish Sidecar/Sidecar.csproj -c Release -f net7.0-windows -r win-x64 --self-contained false

echo "==> 4/4 主工程 $TFM"
dotnet build AIRenderer.csproj -c Release -f "$TFM"

OUT="bin/Release/$TFM"
echo
echo "Output check: $OUT"
missing=0
files=("TuoJie.rhp" "TuoJieSidecar.exe")
if [ "$TFM" = "net7.0-windows" ]; then
  files+=("TuoJieSidecar-net48.exe" "net48-sidecar/TuoJieSidecar.exe")
fi
for f in "${files[@]}"; do
  if [ -f "$OUT/$f" ]; then
    echo "  OK   $f"
  else
    echo "  MISS $f"
    missing=1
  fi
done
exit $missing
