# TuoJie Customer Release Acceptance Checklist

Use this checklist after `tools\preflight-release.bat` passes. Target package:

```text
bin\Release\net7.0-windows\
```

## 1. Automated Gate

- [ ] Run `tools\preflight-release.bat`.
- [ ] Confirm final line is `Preflight passed.`
- [ ] Confirm no `[FAIL]`, `[BAD]`, or `[MISSING]` remains in the output.
- [ ] Requires Python 3 on `PATH`（`Static Checks` 步骤会跑下面三个脚本；缺 Python 会直接 FAIL）。

`Static Checks` 步骤拦的是「编译 0 错、运行才炸」这一类问题：

| 脚本 | 拦什么 |
| --- | --- |
| `tools\static_resource_order_check.py` | `StaticResource` 前向引用。用反了 `dotnet build` 依然 0 错 0 警告，只在窗口打开时抛 `XamlParseException: 无法找到名为 X 的资源`。 |
| `tools\binding_audit.py` | 绑定路径指向不存在的成员，同样是静默失败（界面上什么都不显示）。 |
| `tools\layout_contract_check.py` | 宽窄两套排版与蒙版层级的结构契约（卡片列/跨列、上边距共用 `layoutRow`、InkCanvas 在显示层之上）。 |

> `layout_contract_check.py` 用 `ContentRoot` / `SourcePreviewHitArea` 两个 `x:Name`
> 当定位锚点，代码后置并不使用它们——清理「无用 x:Name」时别删这两个。

## 2. Rhino Main Flow

- [ ] Close all Rhino processes before copying or rebuilding the release output.
- [ ] Drag `bin\Release\net7.0-windows\TuoJie.rhp` into Rhino 8.
- [ ] Run `AIRender`; confirm the main window opens.
- [ ] Open settings and configure APIYI:
  - Base URL: `https://api.apiyi.com`
  - Fast model: `gpt-image-2.5-all`
  - Standard model: `gpt-image-2.5-vip`
  - Mask edits internally use `gpt-image-2.5-sunburst` (fixed, not configurable)
  - API key: release-test key
- [ ] Capture the active viewport and generate one image.
- [ ] Upload a local image as the source and generate one image.
- [ ] Close Rhino, reopen Rhino, run `AIRender`, and generate again without re-entering settings.

Pass criteria:

- [ ] Normal generation succeeds.
- [ ] Settings persist after Rhino restart.
- [ ] Images are saved under `%APPDATA%\AIRenderer\history`（单目录方案；`index.json` 只有路径+时间，最多 30 条）。
- [ ] No Windows crash dialog appears for `TuoJieSidecar.exe`.

## 3. APIYI Capability Checks

> **已确认的产品模型决策（2026-09-18）**：快速出图固定默认
> `gpt-image-2.5-all`，标准模式固定默认 `gpt-image-2.5-vip`，蒙版固定使用
> `gpt-image-2.5-sunburst`。第三方不需要再次询问模型名，也不要替换为旧的
> `image-2`；本节只验证真实接口调用和客户 Key 的模型权限。

Normal generation:

- [ ] Confirm `gpt-image-2.5-all`（快速）和 `gpt-image-2.5-vip`（标准）均能通过 API易成功生成。
- [ ] If testing a custom non-APIYI OpenAI-compatible host, confirm normal requests use
      `/v1/images/edits`; API易 hosts use `/v1/images/generations` regardless of the
      legacy `ApiFormat` metadata.
- [ ] Generate with a viewport around `1400x840` or `1920x1080`; confirm no `Pipe is broken`.

Render modes:

- [ ] Test **快速出图**; it must generate successfully and omit the standard `size` parameter.
- [ ] Test **标准模式**; it must generate successfully with the selected `size` parameter.

Mask edit:

- [ ] Capture a source image.
- [ ] Start mask edit.
- [ ] Use a small brush to paint one local region.
- [ ] Prompt: `只把图中涂抹区域改成浅色木饰面，其他区域保持不变。`
- [ ] Submit mask edit.

Pass criteria:

- [ ] Mask edit button is enabled in standard mode and disabled in fast mode.
- [ ] Brush size is adjustable.
- [ ] Result is visibly related to the painted region.
- [ ] Failures show API/Sidecar error detail instead of only a generic failure.
- [ ] Debug mask images are written under `%APPDATA%\AIRenderer\debug\`.

## 4. Multi-Image Reference Checks

> 参考图入口只有两个：**参考图像（可选）** 卡片里的 `添加参考图`（本地文件，最多 3 张），
> 以及 **历史记录抽屉** 里的 `添加为参考图`。参考图库弹窗（`ReferenceLibraryDialog`）
> 主界面已不再挂入口，本节不覆盖。

Local references:

- [ ] Capture or upload a source image.
- [ ] Click `添加参考图` and select two local images.
- [ ] Confirm thumbnails appear in the `参考图像（可选）` card in the same order they were selected.
- [ ] Confirm the count is capped at 3 (the `添加参考图` button disappears at 3).
- [ ] Delete one thumbnail with its `×`; confirm the remaining reference stays usable and order is preserved.
- [ ] Click a thumbnail; confirm the lightbox opens.

From the history drawer:

- [ ] Open `历史记录`, click `添加为参考图` on one entry.
- [ ] Confirm it appears as the next `图N` in the reference card.
- [ ] Delete the original history entry; confirm the reference thumbnail still renders
      (references are copies under `active-references`, not links to history files).
- [ ] Generate using the already-added reference.

Pass criteria:

- [ ] Source image is always `图1`.
- [ ] References are sent in displayed order as `图2..N`.
- [ ] Deleting a history item does not break active references already added to the current request.
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
| Normal generation | APIYI confirmed model names | Image generated and saved |
| Large screenshot | 1400x840+ | No `Pipe is broken` |
| Restart persistence | Rhino restart | Settings retained, generation succeeds |
| Render modes | 快速出图、标准模式 | Both generate; fast omits `size`, standard sends `size` |
| Mask edit | Local painted region | Result relates to mask |
| Local multi-reference | 2 references | 2 references shown in order and used |
| History multi-reference | History drawer | Add, delete, generate normally |
| Rhino blocked network | Windows Firewall | Sidecar can still generate |
| Customer diagnose | Extracted package | `Issues: 0` |

## 7. Customer Failure Collection

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
