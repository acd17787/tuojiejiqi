# TuoJie Release Test Guide

This is the required customer-release test gate. A package is releasable only after automated preflight passes and the manual checklist in `RELEASE_ACCEPTANCE_CHECKLIST.md` is completed.

## 1. Automated Preflight

Run before any manual Rhino test:

```bat
tools\preflight-release.bat
```

Pass criteria:

- Final line says `Preflight passed.`
- Release `net7.0-windows` and `net48` both build.
- Both release folders include `TuoJie.rhp`, `TuoJieSidecar.exe`, `diagnose.bat`, and required dependencies.
- Both `bin\Release\net7.0-windows\diagnose.bat` and `bin\Release\net48\diagnose.bat` report `Issues: 0`.
- Large Sidecar pipe request tests pass for `TuoJieSidecar.exe` and the net48 fallback.
- Extracted-package simulation passes.

If it fails:

- Do not send the package.
- Fix the first `[FAIL]` line.
- Rerun the full preflight after the fix.

## 2. Manual Acceptance Checklist

After preflight passes, complete:

```text
docs\RELEASE_ACCEPTANCE_CHECKLIST.md
```

The checklist covers:

- Rhino 8 plugin load and `AIRender` startup.
- APIYI `gpt-image-2` normal generation.
- Large screenshot generation.
- Source image speed mode.
- Mask edit.
- Multi-image references from local files and reference library.
- Rhino firewall block with Sidecar allowed.
- Customer package `diagnose.bat`.

## 3. Customer Failure Collection

If the customer reports failure after the package is sent, ask for:

```text
diagnose.bat full output
%APPDATA%\AIRenderer\settings.json
%APPDATA%\AIRenderer\logs\sidecar_client_*.log
%APPDATA%\AIRenderer\logs\sidecar_*.log
%APPDATA%\AIRenderer\debug\mask_*.png
```

Common customer-only causes:

- Antivirus quarantines `TuoJieSidecar.exe`.
- Company firewall blocks unknown executables, not only `Rhino.exe`.
- Proxy or SSL inspection rewrites API responses.
- Plugin folder is read-only or files were copied incompletely.
- API key has no access to the selected model.
