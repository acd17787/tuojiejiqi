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

**一条命令跑完全部 9 项，全绿或阻断：`tools\release-test.bat`**（任一项失败立即非零退出，不允许跳过）。
下表是它按序执行的内容；单独补跑某一项时按下表逐条来。

按顺序执行，全绿才继续。每一条都拦截过真实发生过的问题（「拦什么」一栏是判例）。

| # | 命令（仓库根目录） | 通过标准 | 拦什么 |
| --- | --- | --- | --- |
| 1 | `bash tools/build-release.sh` | `0 个错误 0 个警告`；末尾 `OK` 列出 TuoJie.rhp / TuoJieSidecar.exe / TuoJieSidecar-net48.exe / net48-sidecar\TuoJieSidecar.exe | 编译错误；sidecar 产物缺失（net7 apphost 未 publish 等） |
| 2 | `python tools/static_resource_order_check.py` | `PASS` | StaticResource 前向引用——**编译 0 错、窗口打开才炸** 的那类（XamlParseException） |
| 3 | `python tools/binding_audit.py` | `失效绑定: 0` | 绑定路径指向不存在的成员（静默失败：界面上什么都不显示） |
| 4 | `python tools/layout_contract_check.py` | `PASS` | 宽窄两套排版契约（卡片列/跨列、**行不得撞格**、上边距共用 layoutRow）、蒙版层级、画布尺寸绑定守卫 |
| 5 | `cd tools\ui-harness && python gen_window.py && dotnet run -c Release -- 960 1265` | `UIH PASS`，退出码 0 | 渲染回归 + 墨迹画布跟随 Viewbox + 布局自激（同高度两遍布局不一致） |
| 6 | `CONCURRENCY_PROBE=1 ApiProbe.exe`（探针 staging 见 1.2） | 快请求 < 5 秒且两个请求都成功 | 侧车并发退化成串行单实例（快请求等满连接超时后 `Pipe is broken`） |
| 7 | `CONVERSION_EQUIVALENCE_PROBE=1 ApiProbe.exe` | 全部用例逐字节相同（反向按「不透明像素一致」判定） | 图像转换路径的像素保真回归 |
| 8 | `powershell -File tools\win-verify\verify-api.ps1` | `ALL PASS` | 协议选路（API易走 generations、其它走 edits）、Bearer 头、size 语义、蒙版模型固定 sunburst、缩略图解码宽度、请求帧形状；结束时回收 6/7 的探针 staging |
| 9 | `powershell -File tools\preflight-release.ps1` | `Preflight passed.`；无 `[FAIL]`/`[BAD]` | 打包完整性、三个包的 diagnose、>1MB 大帧管道、.NET 运行时与 Rhino 检测 |

> 第 8 项放在 6/7 之后是有意的：它的收尾自清负责回收探针 staging（ApiProbe.*、
> TuoJie.dll、RhinoCommon/Rhino3dm/Eto/Rhino.UI.dll），保证跑完门禁的 bin 仍可直接打包。

需要 Rhino / Python / 网络：
- 1、9 需要 .NET SDK（1 会占用 `bin\TuoJie.rhp`——**Rhino 开着时跑不了，先关 Rhino**）。
- 6~8 需要 `RhinoCommon.dll` 等 staged 文件，`release-test.bat` 与 `verify-api.ps1` 会自动布置/回收。
- 2~5 纯文件级，任何机器可跑——这三项静态检查与渲染回归也已挂在 CI 的 push/PR 上
  （`.github/workflows/gate.yml`），合入前就被强制。

### 1.2 变更触发的附加门禁与探针部署

| 改了什么 | 追加执行 |
| --- | --- |
| `Sidecar/` 或 `Services/SidecarClient.cs` | 第 6 项并发探针必跑；net48 与 net7 两个侧车目标都要构建 |
| 图像转换（`ScreenCapture` / `ImageUtil`） | 第 7 项等价性探针必跑；改判定标准要先改探针并说明理由 |
| XAML 布局 | 第 5 项宽窗 + 窄窗各渲染一次（`-- 960 1265` 与 `-- 1500 1000`），与改动前基线做像素比对 |
| 蒙版 | 设 `TUOJIE_MASK_DEBUG=1` 复现一轮，肉眼检查 `logs\mask_debug\mask_*.png` 的透明区域与涂抹一致 |
| 死代码清理 | `python tools/dead_member_audit.py`（只读审；**输出不是删除清单**——JSON 回写字段、Rhino override、XAML 附加属性访问器即使零引用也必须保留） |

探针（第 6/7 项）的部署，`release-test.bat` 会自动做；单独补跑时手工三步：

```bat
dotnet build tools\win-verify\ApiProbe\ApiProbe.csproj -c Release -p:TuoJieDir=%CD%\bin\Release\net7.0-windows
copy /y tools\win-verify\ApiProbe\bin\Release\net7.0-windows\ApiProbe.* bin\Release\net7.0-windows\
copy /y "C:\Program Files\Rhino 8\System\RhinoCommon.dll" bin\Release\net7.0-windows\
```

### 1.3 打包卫生

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

### 2.2 自动化手段与断言标记

| 环节 | 方式 | 现状 |
| --- | --- | --- |
| 拉起插件 | `Rhino.exe /runscript=_(AIRender)`（或 Rhino 内手动执行 `AIRender`） | 手动可行 |
| UI 驱动 | WPF 控件走 UIA（`System.Windows.Automation`，或 Python + pywinauto）；按可见文本定位（「生成」「参考图像」「全部清除」…） | **驱动脚本待建**（场景与断言已在 2.3 写死，脚本建成后回填执行命令） |
| 生成类断言 | 读 `mock-requests.jsonl`，断言请求形状（路径 / size / mask / model / image 数量 / bodyLength）——`verify-api.ps1` 第 5 节的断言可整体复用 | 已有 |
| 界面状态断言 | UIA 读状态条 / toast 文本；必要时窗口截图对比 | 待建 |

**断言标记与人工代理**：`(A)` = 必须有客观依据的断言。驱动脚本建成之前，
每个 `(A)` 都标注了「人工代理」——打开对应文件或界面核对，**不允许直接目测勾掉**；
`(E)` = 人眼判断项。脚本建成后 `(A)` 全部转为自动断言。

> 在驱动脚本建成之前，2.3 的场景由人工按步骤执行、按断言逐条勾选；
> 建成之后同一张表就是自动化脚本的场景清单。**不允许因为脚本未建而跳过本层。**

### 2.3 场景表

每个场景：步骤 → 自动断言（A，含脚本建成前的人工代理）/ 人眼断言（E）。全部通过本层才算绿。

**S1 启动与单实例**
- 执行 `AIRender`，主窗口打开，无异常弹窗。
  (A)（人工代理：窗口可见 + Rhino 命令行无报错）
- 再执行一次 `AIRender`：不出第二个窗口，已有窗口到前台，Rhino 命令行输出
  `AIRender window is already open.`。(A)（人工代理：肉眼确认只有一个窗口 + 命令行输出）
- 窗口宽度 **>1120** 走宽窗布局；拖到 **≤1120（1120 整也收窄）** 后四张卡片与
  提示词 / 历史 / 状态条纵向依次排列，无遮挡。(E)

**S2 设置持久化**
- 设置 BaseUrl / API Key / 模型名，保存。
- 关闭 Rhino → 重开 → `AIRender`：设置原样恢复，直接可生成。
  (A)（人工代理：记事本打开 `%APPDATA%\AIRenderer\settings.json`，核对
  `BaseUrl` / `ApiKey` / `FastModel` / `StdModel` 与设置界面一致）

**S2b 升级安装（覆盖装）**
- 准备一份**旧版本** settings.json（从上一版客户机拷贝，或手工构造含
  `ApiKeys` / `CustomApiKeys` / `SelectedModel` / `CustomProviders` /
  `BuiltInOverrides` 的旧结构），放进 `%APPDATA%\AIRenderer\`，再装新包。
- 打开插件：新字段正常、旧字段不被丢弃。
  (A)（人工代理：核对 settings.json——ApiKey 正确迁移；`CustomProviders` /
  `BuiltInOverrides` 等旧字段**原样保留**，因为保存流程是「读整份 → 改字段 →
  覆盖整份」，这些字段运行时不读但必须回写）
- 参考图 / 提示词历史随升级保留。

**S3 快速出图一轮**
- 选「快速出图」，输入提示词，生成。
- (A) mock 收到 `POST /v1/images/generations`、**不带 `size`**、带 `image[]`（原图为图1）。
  （人工代理：打开 `tools\win-verify\mock-requests.jsonl` 找最后一条，核对
  `path` 与 `jsonSize` 为空）
- (A) 响应 mock 返回 b64_json，界面出现结果图，历史抽屉 +1，`%APPDATA%\AIRenderer\history` 新增 PNG。
  （人工代理：数抽屉计数与 history 目录文件数）
- (E) 结果图清晰、无坏块。

**S4 标准模式一轮（含大视口端到端）**
- 选「标准模式」+ 2K + 16:9，用**常规小视口**生成。
- (A) 请求带 `size=2048x1152`。（人工代理：核对 mock-requests.jsonl 最后一条的 `size`）
- (E) 比例与像素读数一致。
- **大视口端到端**：把 Rhino 视口铺到 **1920×1080 或更大**，重新截图作为原图，再生成一轮。
  历史上 `Pipe is broken` 就发生在这条「大截图 → 编码 → 大帧发送」的链路上，
  第一层的 >1MB 大帧测试只覆盖管道层，拦不住这条链的回归。
  (A)（人工代理：mock-requests.jsonl 最后一条 `bodyLength > 1000000`）
  (E) 全程无 `Pipe is broken`，正常出图。

**S5 蒙版修改一轮**
- 标准模式，涂抹一小块区域，提示词：`只把图中涂抹区域改成浅色木饰面，其他区域保持不变。`
- (A) 请求走 `/v1/images/edits`，带 `mask`，模型 = `gpt-image-2.5-sunburst`。
  （人工代理：核对 mock-requests.jsonl 的 `hasMask=true` 与 `model` 字段）
- (E) 结果只改了涂抹区域；蒙版编辑入口在快速模式下置灰并有提示。

**S6 参考图全生命周期**
- 上传 2 张本地参考图 → 缩略图按选择顺序出现。(E)
- 多选超过 3 张 → 只收前 3，第 4 张拒绝。
  (A)（人工代理：数卡片缩略图恒 ≤3）
- 「全部清除」→ 全部消失，`active-references` 副本被删，settings.json 列表为空。
  (A)（人工代理：看 `%APPDATA%\AIRenderer\active-references\` 文件数与
  settings.json 的 `ReferenceImages` 为 `[]`）
- 重新添加 2 张 → 生成 → (A) `image[]` = 原图 + 2 参考，顺序与显示一致。
  （人工代理：核对 mock-requests.jsonl 的 `hasImageArr` 与条数）
- **关窗重开** → 参考图为空（会话内保留、关窗即清）。
  (A)（人工代理：卡片无缩略图 + settings.json `ReferenceImages` 为 `[]`）
- 生成时的状态条出现「· 参考 N 张」。(A/E)

**S7 历史记录**
- 生成数次 → 抽屉计数「N / 30」；超过 30 条自动淘汰最旧。
  (A)（人工代理：数 `%APPDATA%\AIRenderer\history` 的 PNG 文件数 ≤30，
  对照 `index.json` 条数）
- 缩略图点击开灯箱；灯箱「下载」存出的 PNG 与历史图**像素尺寸一致、肉眼无差异**。
  （人工代理：比对两张图的属性尺寸。像素级逐字节一致留给驱动脚本——
  下载是重编码，字节本就不同，别拿文件大小当依据）
- 历史条目「添加为参考图」→ 出现在参考卡；删除该历史条目不影响已加的参考图，
  再生成一次正常。(A)（人工代理：看缩略图仍在 + mock 收到带参考图的请求）

**S8 提示词历史**
- 生成后提示词入历史；重复提示词去重置顶；点历史条目回填输入框。(E)

**S9 错误路径（不崩）**
- 停掉 mock 再生成 → 状态条与 toast 给出可读错误（接口错误 / 连接失败），**插件不退出、不白屏**。
- 恢复 mock 后再生成 → 正常。

**S10 中断路径（状态机复位）**
- 生成中再点「生成」：按钮禁用点不动，mock **只收到一个**请求（不重复计费）。
  (A)（人工代理：数 mock-requests.jsonl 新增条数）
- 生成中途关窗：窗口正常关闭、无崩溃；重开后参考图为空（关窗清理）、
  无残留 `TuoJieSidecar.exe` 进程。
  (A)（人工代理：任务管理器搜 `TuoJieSidecar`）
- 生成中退出 Rhino：Rhino 正常退出，侧车随之退出，无挂起进程。(A)
- 蒙版编辑中切「快速出图」：提示切换标准模式，蒙版状态复位而不是带病提交。(E)

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
- [ ] **干净机器安装（无 .NET 7 运行时）**：用一台**未安装 .NET 7 运行时**的机器
  （或临时卸载 / 未装的虚拟机、沙箱），解压客户包，拖 `TuoJie.rhp` 进 Rhino，生成一轮。
  (A) 自动降级到 net48 侧车：`sidecar_client_*.log` 出现
  `Using net48 Sidecar (fallback mode)` 或 `fallback=True`；(E) 生成成功。
  再跑一次 `diagnose.bat` → `Issues: 0`、sidecar 启动 OK、net48 兜底信息在、
  无 `[MISSING]`/`[BAD]`。
  ——「有 .NET 7 的机器能跑」谁都会测；客户机上真正没底的是**降级切换本身**，
  这条必须在发布前真实触发一次。
- [ ] **升级安装（覆盖装）真机版**：在装过旧版插件的机器上直接装新包（对应第二层 S2b），
  旧 settings.json 的 API Key / 参考图 / 提示词历史不丢，旧字段原样回写。

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
