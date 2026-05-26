# TuoJie Customer Release Acceptance Checklist

Use this checklist after `tools\preflight-release.bat` passes. Target package:

```text
bin\Release\net7.0-windows\
```

## 1. Automated Gate

- [ ] Run `tools\preflight-release.bat`.
- [ ] Confirm final line is `Preflight passed.`
- [ ] Confirm no `[FAIL]`, `[BAD]`, or `[MISSING]` remains in the output.

## 2. Rhino Main Flow

- [ ] Close all Rhino processes before copying or rebuilding the release output.
- [ ] Drag `bin\Release\net7.0-windows\TuoJie.rhp` into Rhino 8.
- [ ] Run `AIRender`; confirm the main window opens.
- [ ] Open settings and configure APIYI:
  - Base URL: `https://api.apiyi.com`
  - Model: `gpt-image-2`
  - API key: release-test key
- [ ] Capture the active viewport and generate one image.
- [ ] Upload a local image as the source and generate one image.
- [ ] Close Rhino, reopen Rhino, run `AIRender`, and generate again without re-entering settings.

Pass criteria:

- [ ] Normal generation succeeds.
- [ ] Settings persist after Rhino restart.
- [ ] Images are saved under `%APPDATA%\AIRenderer\generated`.
- [ ] No Windows crash dialog appears for `TuoJieSidecar.exe`.

## 3. APIYI Capability Checks

Normal generation:

- [ ] Confirm `gpt-image-2` generation succeeds with APIYI.
- [ ] Confirm custom APIYI providers using `ApiFormat=openai` still generate through the generations route.
- [ ] Generate with a viewport around `1400x840` or `1920x1080`; confirm no `Pipe is broken`.

Source image speed mode:

- [ ] Test `speed`; must pass.
- [ ] Test `balanced`; must pass.
- [ ] Test `quality`; observe only. It may be slow, but the UI and Sidecar must not crash.

Mask edit:

- [ ] Capture a source image.
- [ ] Start mask edit.
- [ ] Use a small brush to paint one local region.
- [ ] Prompt: `只把图中涂抹区域改成浅色木饰面，其他区域保持不变。`
- [ ] Submit mask edit.

Pass criteria:

- [ ] Mask edit button is enabled when the provider/model supports it.
- [ ] Brush size is adjustable.
- [ ] Result is visibly related to the painted region.
- [ ] Failures show API/Sidecar error detail instead of only a generic failure.
- [ ] Debug mask images are written under `%APPDATA%\AIRenderer\debug\`.

## 4. Multi-Image Reference Checks

Local references:

- [ ] Capture or upload a source image.
- [ ] Click `添加多图参考`.
- [ ] Select two local reference images.
- [ ] Confirm thumbnails appear below the source preview as `图2` and `图3`.
- [ ] Delete one thumbnail with `X`; confirm numbering updates.
- [ ] Click clear references; confirm thumbnails disappear.

Reference library:

- [ ] Save one generated result into the reference library.
- [ ] Click `从图库添加`.
- [ ] Select multiple reference-library images.
- [ ] Click `添加选中`.
- [ ] Confirm selected images appear below the source preview.
- [ ] Delete the original library image.
- [ ] Generate using the already-added active reference.

Pass criteria:

- [ ] Source image is always `图1`.
- [ ] References are sent in displayed order as `图2..N`.
- [ ] Deleting a library image does not break active references already added to the current request.
- [ ] This prompt style works:

```text
保留图1的空间结构和相机角度。
参考图2的材质。
参考图3的灯光氛围。
生成现代室内效果图。
```

## 5. Firewall Scenario

Use this only when validating the customer requirement that Rhino cannot access the network.

- [ ] Block `Rhino.exe` outbound in Windows Firewall.
- [ ] Allow `TuoJieSidecar.exe` outbound.
- [ ] Generate one image from Rhino.
- [ ] Confirm Task Manager shows `TuoJieSidecar.exe` during generation.

Pass criteria:

- [ ] Generation succeeds while `Rhino.exe` is blocked.
- [ ] Sidecar logs exist:

```text
%APPDATA%\AIRenderer\logs\sidecar_client_*.log
%APPDATA%\AIRenderer\logs\sidecar_*.log
```

## 6. Customer Package Diagnose

Run from the final extracted zip folder:

```bat
diagnose.bat
```

Pass criteria:

- [ ] `Issues: 0`
- [ ] Rhino install detection is acceptable.
- [ ] Sidecar startup is OK.
- [ ] net48 fallback information is present.
- [ ] No `[MISSING]` or `[BAD]` line remains.

## Minimum Release Gate Matrix

| Feature | Required Environment | Pass Criteria |
|---|---|---|
| Release package | Dev machine | `Preflight passed.` |
| Rhino 8 plugin load | Dev Rhino 8 | `AIRender` opens |
| Normal generation | APIYI `gpt-image-2` | Image generated and saved |
| Large screenshot | 1400x840+ | No `Pipe is broken` |
| Restart persistence | Rhino restart | Settings retained, generation succeeds |
| Speed mode | `speed`, `balanced` | Image generated |
| Mask edit | Local painted region | Result relates to mask |
| Local multi-reference | 2 references | `图2/图3` shown and used |
| Library multi-reference | Library multi-select | Add, delete, generate normally |
| Rhino blocked network | Windows Firewall | Sidecar can still generate |
| Customer diagnose | Extracted package | `Issues: 0` |
