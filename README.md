# TuoJie - AI Renderer for Rhino

Rhino plugin for viewport capture and AI image generation. The plugin captures the active Rhino viewport, sends the request through a Sidecar process, and saves the generated image locally.

## Install

1. Download and unzip the matching `TuoJie-*.zip` package.
2. Open Rhino and load `TuoJie.rhp` from the extracted folder.
3. Run the `AIRender` command.
4. In settings, configure the API provider, API key, base URL, and model.

## Current API Path

For api易 / api.apiyi.com, use:

```text
https://api.apiyi.com
```

The plugin normalizes the base URL internally. Do not enter duplicate `/v1` paths such as `https://api.apiyi.com/v1/v1`.

## Release Preflight

Before sending a package to a customer, run:

```bat
tools\release-test.bat
```

This runs the automated preflight first, then points to the manual release checklist. The automated package checks are ready only when preflight ends with:

```text
Preflight passed.
```

This checks build output, package completeness, `diagnose.bat`, Sidecar startup, large screenshot pipe transport, and extracted-package behavior.

The package is customer-ready only after the manual checklist is complete:

```text
docs\RELEASE_ACCEPTANCE_CHECKLIST.md
```

## Customer Diagnostics

Each release package includes:

```bat
diagnose.bat
```

Ask the customer to run it from the plugin folder if the plugin fails to load or API calls fail. The expected result is `Issues: 0`.

## Current Features

- Normal generation captures or uploads a source image, then sends it through `TuoJieSidecar.exe`.
- APIYI `gpt-image-2` uses `/v1/images/generations`; APIYI `gpt-image-2` custom providers using `ApiFormat=openai` are routed to the generations endpoint automatically.
- Source image speed mode is available for APIYI `gpt-image-2` on `api.apiyi.com`, `vip.apiyi.com`, and `b.apiyi.com`.
- Multi-image references are supported in single-image mode:
  - `image 1` / `图1` is always the Rhino/source image.
  - Added references are displayed below the source preview as `图2`, `图3`, etc.
  - References can be added from local files or from the reference library.
  - Active references are copied to `%APPDATA%\AIRenderer\active-references\` so deleting a library image does not break the current request.
  - Individual references can be removed with the thumbnail `X`; clearing references removes active temp copies.
- Mask edit uses the source image as `image 1`; mask/debug images are written under `%APPDATA%\AIRenderer\debug\`.

## Build

```bat
dotnet build Sidecar\Sidecar.csproj -f net7.0-windows
dotnet build Sidecar\Sidecar.csproj -f net48
dotnet build -f net7.0-windows
dotnet build -f net48
```

Build output is under:

```text
bin\Release\net7.0-windows\
bin\Release\net48\
```

## Architecture

```text
Rhino.exe -> TuoJie.rhp -> TuoJieSidecar.exe -> API provider
                    named pipe IPC          HTTPS
```

All external HTTP traffic goes through `TuoJieSidecar.exe`, so a customer can block `Rhino.exe` from the network while allowing the Sidecar process.

## Runtime Targets

- Rhino 8 package: `net7.0-windows`
- Rhino 7 fallback package: `net48`
- Rhino 8 package also carries a net48 Sidecar fallback under `net48-sidecar\`
