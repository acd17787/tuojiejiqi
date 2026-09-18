# TuoJie 发布验收标准

> **每一次对外发布（打 tag / 发包给客户）之前，按本文档执行验收。**
> 三层门槛，逐层放行：第一层全绿才允许打 tag，第二层全绿才允许把包交给客户，
> 第三层在正式对外版本上建议执行。
> 文档本身随代码走版本：验收发现标准缺失或过时，先改本文档再改代码。

| 版本 | 日期 | 说明 |
| --- | --- | --- |
| 当前 | 2026-09-18 | 重构为三层标准；新增 UI 自动化验收层、渲染回归门禁、打包卫生规则 |

---

## 0. 总则

| 层 | 内容 | 放行条件 | 费用 |
| --- | --- | --- | --- |
| 第一层 | 代码级自动门禁（全脚本，零人工判断） | **全绿才可打 tag** | 零（全部打在本地 mock 上） |
| 第二层 | 产品操作验收（自动化 UI 轮，跑在真实 Rhino 里） | **全绿才可发包给客户** | 零（BaseUrl 指向本地 mock） |
| 第三层 | 真实 API 抽测 + 人工目测 + 特殊环境 | 正式对外版建议执行 | 受控（明确预算） |

**阻断定义**：`[FAIL]` / `[BAD]` / `[MISSING]` / 脚本非零退出 = 阻断，必须修复或经用户明确豁免后降级记录，不允许静默跳过。

**费用红线**：任何自动化验证不得调用真实生图接口（历史约定：真实生成有成本）。
网络层验证一律指向本地 mock；真实 API 只出现在第三层的预算化抽测里。

---

## 1. 第一层：代码级自动门禁

按顺序执行，全绿才继续。每一条都拦截过真实发生过的问题（「拦什么」一栏是判例）。

| # | 命令（仓库根目录） | 通过标准 | 拦什么 |
| --- | --- | --- | --- |
| 1 | `bash tools/build-release.sh` | `0 个错误 0 个警告`；末尾 `OK` 列出 TuoJie.rhp / TuoJieSidecar.exe / TuoJieSidecar-net48.exe / net48-sidecar\TuoJieSidecar.exe | 编译错误；sidecar 产物缺失（net7 apphost 未 publish 等） |
| 2 | `python tools/static_resource_order_check.py` | `PASS` | StaticResource 前向引用——**编译 0 错、窗口打开才炸** 的那类（XamlParseException） |
| 3 | `python tools/binding_audit.py` | `失效绑定: 0` | 绑定路径指向不存在的成员（静默失败：界面上什么都不显示） |
| 4 | `python tools/layout_contract_check.py` | `PASS` | 宽窄两套排版契约（卡片列/跨列、**行不得撞格**、上边距共用 layoutRow）、蒙版层级、画布尺寸绑定守卫 |
| 5 | `cd tools\ui-harness && python gen_window.py && dotnet run -c Release -- 960 1265` | `UIH PASS`，退出码 0 | 渲染回归 + 墨迹画布跟随 Viewbox + 布局自激（同高度两遍布局不一致） |
| 6 | `powershell -File tools\win-verify\verify-api.ps1` | `ALL PASS` | 协议选路（API易走 generations、其它走 edits）、Bearer 头、size 语义、蒙版模型固定 sunburst、缩略图解码宽度、请求帧形状 |
| 7 | `CONCURRENCY_PROBE=1 ApiProbe.exe` | 快请求 < 5 秒且两个请求都成功 | 侧车并发退化成串行单实例（快请求等满连接超时后 `Pipe is broken`） |
| 8 | `CONVERSION_EQUIVALENCE_PROBE=1 ApiProbe.exe` | 全部用例逐字节相同（反向按「不透明像素一致」判定） | 图像转换路径的像素保真回归 |
| 9 | `powershell -File tools\preflight-release.ps1` | `Preflight passed.`；无 `[FAIL]`/`[BAD]` | 打包完整性、三个包的 diagnose、>1MB 大帧管道、.NET 运行时与 Rhino 检测 |

需要 Rhino / Python / 网络：
- 1、9 需要 .NET SDK（1 会占用 `bin\TuoJie.rhp`——**Rhino 开着时跑不了，先关 Rhino**）。
- 6~8 的探针需要 `RhinoCommon.dll` 等 staged 文件，`verify-api.ps1` 会自动布置并在结束时清理。
- 2~5 纯文件级，任何机器可跑。

### 1.1 变更触发的附加门禁

| 改了什么 | 追加执行 |
| --- | --- |
| `Sidecar/` 或 `Services/SidecarClient.cs` | 第 7 项并发探针必跑；net48 与 net7 两个侧车目标都要构建 |
| 图像转换（`ScreenCapture` / `ImageUtil`） | 第 8 项等价性探针必跑；改判定标准要先改探针并说明理由 |
| XAML 布局 | 第 5 项宽窗 + 窄窗各渲染一次（`-- 960 1265` 与 `-- 1500 1000`），与改动前基线做像素比对 |
| 蒙版 | 设 `TUOJIE_MASK_DEBUG=1` 复现一轮，肉眼检查 `logs\mask_debug\mask_*.png` 的透明区域与涂抹一致 |
| 死代码清理 | `python tools/dead_member_audit.py`（只读审；**输出不是删除清单**——JSON 回写字段、Rhino override、XAML 附加属性访问器即使零引用也必须保留） |

### 1.2 打包卫生

- `verify-api.ps1` 结束时会自清它铺进 `bin` 的验证文件（ApiProbe.*、TuoJie.dll、
  RhinoCommon/Rhino3dm/Eto/Rhino.UI.dll）并停掉 mock 作业——不要删这段逻辑。
- 手工跑过探针的话，打包前检查 `bin\Release\net7.0-windows\` 里**不得**出现
  `ApiProbe.*`、`RhinoCommon.dll`、`Eto.dll`、`Rhino.UI.dll`、`TuoJie.dll`——
  打包步骤是 `cp *.dll` / `cp *.json`，这些残留会被原样打进客户包。

---

## 2. 第二层：产品操作验收（自动化 UI 轮）

在真实 Rhino 8 里操作真实插件界面跑一轮完整产品流程。**全部打在本地 mock 上，零费用。**

### 2.1 环境

1. 用第一层产出的发布包（不是 `bin` 散文件）：解压 zip，把 `TuoJie.rhp` 拖进 Rhino 8。
2. 启动本地 mock：`powershell -File tools\win-verify\mock-api.ps1 -Port 8899`
   （它把每个请求记录到 `tools\win-verify\mock-requests.jsonl`——UI 轮的断言后端）。
3. 插件设置里：BaseUrl = `http://127.0.0.1:8899`，API Key = `release-test key`
   （任意非空即可），保存。
4. 结束后：恢复 BaseUrl 为 `https://api.apiyi.com/v1`；停掉 mock。

### 2.2 自动化手段

| 环节 | 方式 | 现状 |
| --- | --- | --- |
| 拉起插件 | `Rhino.exe /runscript=_(AIRender)`（或 Rhino 内手动执行 `AIRender`） | 手动可行 |
| UI 驱动 | WPF 控件走 UIA（`System.Windows.Automation`，或 Python + pywinauto）；按可见文本定位（「生成」「参考图像」「全部清除」…） | **驱动脚本待建**（场景与断言已在 2.3 写死，脚本建成后回填执行命令） |
| 生成类断言 | 读 `mock-requests.jsonl`，断言请求形状（路径 / size / mask / model / image 数量）——`verify-api.ps1` 第 5 节的断言可整体复用 | 已有 |
| 界面状态断言 | UIA 读状态条 / toast 文本；必要时窗口截图对比 | 待建 |

> 在驱动脚本建成之前，2.3 的场景由人工按步骤执行、按断言逐条勾选；
> 建成之后同一张表就是自动化脚本的场景清单。**不允许因为脚本未建而跳过本层。**

### 2.3 场景表

每个场景：步骤 → 自动断言（A）/ 人眼断言（E）。全部通过本层才算绿。

**S1 启动与单实例**
- 执行 `AIRender`，主窗口打开，无异常弹窗。(A: 进程内出现窗口标题 `TuoJie AI Renderer`)
- 再执行一次 `AIRender`：不出第二个窗口，已有窗口到前台，Rhino 命令行输出
  `AIRender window is already open.`。
- 窗口宽度 ≥1120 走宽窗布局；拖窄到 ≤1120 后四张卡片与提示词 / 历史 / 状态条
  纵向依次排列，无遮挡（呼应布局契约）。(E)

**S2 设置持久化**
- 设置 BaseUrl / API Key / 模型名，保存。
- 关闭 Rhino → 重开 → `AIRender`：设置原样恢复，直接可生成。(A: settings.json 字段比对)

**S3 快速出图一轮**
- 选「快速出图」，输入提示词，生成。
- (A) mock 收到 `POST /v1/images/generations`、**不带 `size`**、带 `image[]`（原图为图1）。
- (A) 响应 mock 返回 b64_json，界面出现结果图，历史抽屉 +1，`%APPDATA%\AIRenderer\history` 新增 PNG。
- (E) 结果图清晰、无坏块。

**S4 标准模式一轮**
- 选「标准模式」+ 2K + 16:9，生成。
- (A) 请求带 `size=2048x1152`。
- (E) 比例与像素读数一致。

**S5 蒙版修改一轮**
- 标准模式，涂抹一小块区域，提示词：`只把图中涂抹区域改成浅色木饰面，其他区域保持不变。`
- (A) 请求走 `/v1/images/edits`，带 `mask`，模型 = `gpt-image-2.5-sunburst`。
- (E) 结果只改了涂抹区域；蒙版编辑入口在快速模式下置灰并有提示。

**S6 参考图全生命周期**
- 上传 2 张本地参考图 → 缩略图按选择顺序出现。(E)
- 多选超过 3 张 → 只收前 3，第 4 张拒绝。(A: 列表长度恒 ≤3)
- 「全部清除」→ 全部消失，`active-references` 副本被删，settings.json 列表为空。(A)
- 重新添加 2 张 → 生成 → (A) `image[]` = 原图 + 2 参考，顺序与显示一致。
- **关窗重开** → 参考图为空（会话内保留、关窗即清）。(A)
- 生成时的状态条出现「· 参考 N 张」。(A/E)

**S7 历史记录**
- 生成数次 → 抽屉计数「N / 30」；超过 30 条自动淘汰最旧。(A: 目录文件数)
- 缩略图点击开灯箱；灯箱「下载」存出的 PNG 与原文件像素一致。(A)
- 历史条目「添加为参考图」→ 出现在参考卡；删除该历史条目不影响已加的参考图。(A)

**S8 提示词历史**
- 生成后提示词入历史；重复提示词去重置顶；点历史条目回填输入框。(A/E)

**S9 错误路径（不崩）**
- 停掉 mock 再生成 → 状态条与 toast 给出可读错误（接口错误 / 连接失败），**插件不退出、不白屏**。
- 恢复 mock 后再生成 → 正常。

---

## 3. 第三层：真实 API 与人工抽测

正式对外版建议执行；预算固定，不随意加量。

- [ ] **真实 API 抽测（预算：3 张）**：恢复 `https://api.apiyi.com/v1` + 客户 Key，
  快速出图、标准模式、蒙版修改各生成 **1 张**，确认客户 Key 对三个默认模型
  （`gpt-image-2.5-all` / `gpt-image-2.5-vip` / `gpt-image-2.5-sunburst`）有权限。
  模型权限决策（2026-09-18）：固定这三个模型，不再询问第三方，也不回退旧 `image-2`。
- [ ] **视觉质量目测**：真实渲染结果与场景的对应关系（材质 / 灯光 / 构图）——自动化判不了。
- [ ] **防火墙场景**：防火墙拦 `Rhino.exe` 出站、放行 `TuoJieSidecar.exe`，生成成功；
  侧车日志存在（`%APPDATA%\AIRenderer\logs\sidecar_client_*.log` 与 `sidecar_*.log`）。
- [ ] **干净机器安装**：解压客户包到全新目录，拖 `TuoJie.rhp` 进 Rhino，跑一次 `diagnose.bat`
  → `Issues: 0`、sidecar 启动 OK、net48 兜底信息在、无 `[MISSING]`/`[BAD]`。

---

## 4. 最低发布门禁矩阵

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
| Reference lifecycle | Close & reopen window | References cleared on close; cap 3 never exceeded |
| Rhino blocked network | Windows Firewall | Sidecar can still generate |
| Customer diagnose | Extracted package | `Issues: 0` |

---

## 5. 客户故障收集

客户反馈失败时，收集：

```text
diagnose.bat full output
%APPDATA%\AIRenderer\settings.json
%APPDATA%\AIRenderer\logs\sidecar_client_*.log
%APPDATA%\AIRenderer\logs\sidecar_*.log
```

蒙版问题额外：让客户设环境变量 `TUOJIE_MASK_DEBUG=1` 复现一次，收集
`%APPDATA%\AIRenderer\logs\mask_debug\mask_*.png`（默认关闭，不设不写）。

常见客户侧原因：

- Antivirus quarantines `TuoJieSidecar.exe`.
- Company firewall blocks unknown executables, not only `Rhino.exe`.
- Proxy or SSL inspection rewrites API responses.
- Plugin folder is read-only or files were copied incompletely.
- API key has no access to the selected model.

---

## 6. 发布记录模板

每个版本把下表填全，随发布说明一起存档（贴到 GitHub Release 正文或内部记录）：

```text
版本：vX.Y.Z        提交：<commit hash>        日期：<yyyy-MM-dd>
第一层：build-release [OK]  static_resource [OK]  binding [OK]
        layout [OK]  ui-harness [OK]  verify-api [OK]
        concurrency [OK]  conversion [OK]  preflight [OK]
第二层：S1 [OK] S2 [OK] S3 [OK] S4 [OK] S5 [OK]
        S6 [OK] S7 [OK] S8 [OK] S9 [OK]
第三层：真 API 抽测 [OK/未执行]  目测 [OK]  防火墙 [OK/未执行]  干净机器 [OK/未执行]
执行人：            阻断与豁免记录：
```
