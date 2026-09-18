# TuoJie — Rhino AI Renderer 插件

AI 图生图渲染的 Rhino 8 插件。截取视口 → 调用大模型 API → 返回渲染图。支持单图和批量命名视图。

## 编译

```bash
# 一键完整构建（含 net7 Sidecar 的 win-x64 apphost）
./tools/build-release.sh net7.0-windows

# 手工等价步骤（顺序不能变）：
dotnet build Sidecar/Sidecar.csproj -f net7.0-windows
dotnet build Sidecar/Sidecar.csproj -f net48
# macOS/Linux 上 `dotnet build` 不产 apphost，必须用带 RID 的 publish 才有 TuoJieSidecar.exe
dotnet publish Sidecar/Sidecar.csproj -c Release -f net7.0-windows -r win-x64 --self-contained false
dotnet build -f net7.0-windows

# net48
dotnet build Sidecar/Sidecar.csproj -f net48
dotnet build -f net48
```

编译顺序必须 Sidecar 先于主项目。net7.0-windows 构建会复制 net48 版 Sidecar 作为兜底，当前首选布局是 `net48-sidecar\TuoJieSidecar.exe`；根目录 `TuoJieSidecar-net48.exe` 仅保留为兼容旧包/旧脚本的 legacy 路径。

## 架构

```
Rhino.exe ──→ TuoJie.rhp ──→ TuoJieSidecar.exe ──→ api.apiyi.com
              (WPF UI)       (命名管道 IPC)        (HTTPS)
```

- **Sidecar 是独立进程**，负责所有外网 HTTPS 请求。无论 Rhino 是否被防火墙拦截，Sidecar 只要被放行就能联网。
- 插件与 Sidecar 通过命名管道通信（`TuoJieSidecar-{pid}`），JSON 消息帧协议。
- Sidecar 生命周期：首次 API 调用时自动启动，Rhino 退出时自动结束。
- **net48 兜底**：net7.0 包同时附带 `net48-sidecar\TuoJieSidecar.exe`。若目标电脑缺少 .NET 7 运行时导致 Sidecar 启动失败，自动降级使用 net48 版（.NET Framework 4.8 在所有 Win10/11 上预装）。根目录 `TuoJieSidecar-net48.exe` 是 legacy 兼容路径。

## 项目结构

```
AIRenderer.csproj          # 主项目（net7.0-windows;net48, WPF, RHP）
AIRenderer.sln             # 包含主项目 + Sidecar
Sidecar/
  Sidecar.csproj           # Sidecar 控制台 (net7.0-windows;net48)
  Program.cs               # 命名管道 → HTTP 代理
Models/
  ApiProvider.cs           # ProviderItem, ApiProviderConfig, CustomProviderConfig
  RenderSettings.cs        # 渲染参数 + AspectRatio / SizeOption / 历史条目等
  ImageSizeTable.cs        # API易 -vip 的 5 比例 × 3 档尺寸表 + 「原图」吸附
  ViewRenderItem.cs        # 批量渲染单个视图项
Services/
  AIRenderService.cs       # API 调用编排（走 SidecarHttpMessageHandler）
  SidecarClient.cs         # HttpMessageHandler：拦截所有 HTTP → 命名管道
  ScreenCapture.cs         # Rhino 视口截图
  SettingsService.cs       # JSON 持久化到 %APPDATA%/AIRenderer/settings.json
  HistoryService.cs        # 生成历史（history/index.json）+ 提示词历史
  ImageUtil.cs             # 统一图片解码，避免 GDI+ 流生命周期问题
  LogAndAutoSave.cs        # 日志写入（AutoSaveService 已于清理时删除，历史图片统一走 HistoryService）
  Localization.cs          # 中英文字符串表
  LocalizationManager.cs   # WPF 本地化绑定代理
ViewModels/
  AIRenderViewModel.cs     # 主窗口 VM（原型状态机：原图/蒙版/结果/两套历史/浮层）
  BatchRenderViewModel.cs  # 批量渲染 VM（主界面不再有入口，保留可独立调用）
Views/
  AIRenderWindow.xaml      # 主窗口（原型布局，批量切换按钮已移除）
  BatchRenderWindow.xaml   # 批量渲染子窗口（保留）
  ReferenceLibraryDialog.xaml  # 参考图库弹窗（保留，主界面不再挂入口）
  Converters.cs            # WPF 值转换器（响应式布局 / 3:2 预览）
  InteractiveHelper.cs     # IsActive 附加属性（按钮选中态）
Properties/
  AssemblyInfo.cs          # 插件 GUID / 图标资源名
tools/
  diagnose.bat             # 用户端诊断脚本：检查文件/运行时/Rhino/Sidecar
  build-release.sh         # 完整发布构建（Sidecar ×2 + net7 publish win-x64 + 主工程）
  preflight-release.ps1    # Windows 发布预检
  binding_audit.py         # 绑定审计：抽取 XAML {Binding} 与 C# 声明比对
  layout_contract_check.py # 布局契约：列数/响应式/蒙版层级
```

## API Provider 机制

- `ApiProvider` 枚举：只剩 `ApiYi`（Google Gemini / Vertex 已彻底移除）
- `ProviderItem`：统一抽象，包含 BaseUrl / Models / AuthType / ApiFormat
- `ApiFormat`：兼容协议元数据；普通请求的最终路由按 `BaseUrl` 是否为 API易域名判断。
  API易域名走 `/v1/images/generations`，其它 OpenAI Images 兼容地址走
  `/v1/images/edits`；蒙版请求始终走 `/v1/images/edits`。不要把 `ApiFormat` 当作
  普通请求路由的唯一开关。
- 模型名只有一个来源：设置弹层的「快速模型 / 标准模型」（`FastModel` / `StdModel`），
  蒙版固定用 `RenderSettings.MaskModel`（`gpt-image-2.5-sunburst`），界面不展示
- **已确认的产品模型决策（2026-09-18，不要再次询问）**：快速出图使用
  `gpt-image-2.5-all`，标准模式使用 `gpt-image-2.5-vip`，蒙版固定使用
  `gpt-image-2.5-sunburst`。API易地址的普通请求统一走
  `/v1/images/generations`（快速不传 `size`，标准按尺寸传 `size`）；通用
  OpenAI Images 地址和所有蒙版请求走 `/v1/images/edits`。只能验证接口可用性，
  不要自行改成旧的 `image-2` 或其他模型名。
- 旧 settings.json 里的 `CustomProviders` / `BuiltInOverrides` / `SelectedProvider`
  运行时不使用，但保存时原样回写，避免升级丢配置

## 关键约束

- **net7.0-windows** 构建必须 `EnableDynamicLoading=true`（Rhino 8 netcore 模式要求）
- **net48** 构建不可 `EnableDynamicLoading`（.NET Framework 不支持 AssemblyLoadContext）
- WPF Application 必须在创建 Window 前显式初始化（`new Application { ShutdownMode = OnExplicitShutdown }`），否则 netcore 下闪退
- Sidecar 编译产物通过 CopySidecar MSBuild target 自动复制到输出目录
- 禁止在 Rhino.exe 进程内直接做 HTTPS 请求，一律走 Sidecar

## 本地化

- `Loc.CurrentLanguage` 控制当前语言，支持 zh/CN 和 en/US
- `LocalizationManager` 是 WPF 资源字典绑定代理，XAML 中通过 `{Binding [KEY], Source={StaticResource L}}` 使用
- 语言选择保存在 settings.json → `LanguageIndex`

## 分发打包

```
net7.0-windows 输出：
  TuoJie.rhp + TuoJie.deps.json + TuoJie.runtimeconfig.json
  + TuoJieSidecar.exe + TuoJieSidecar.deps.json + TuoJieSidecar.runtimeconfig.json
  + Newtonsoft.Json.dll + System.Drawing.Common.dll + Microsoft.Win32.SystemEvents.dll
  + runtimes/ + net48-sidecar/ + diagnose.bat
→ 打包为 TuoJie-Rhino8-*.zip

net48 输出：
  TuoJie.rhp + TuoJieSidecar.exe
  + Newtonsoft.Json.dll + System.Drawing.Common.dll + diagnose.bat
→ 打包为 TuoJie-Rhino7-*.zip
```

## CI

`.github/workflows/release.yml`：v* tag 触发 → 构建两个框架 → 打包 Rhino 8 / Rhino 7 zip → 创建 GitHub Release。不再发布单独 `.rhp`，避免客户拿到不完整依赖。
## Current Notes For Agents

- All external HTTP traffic should go through `TuoJieSidecar.exe`; do not reintroduce direct Rhino-process HTTP for image generation.
- APIYI `gpt-image-2.5-all` / `gpt-image-2.5-vip` normal generation uses
  `/v1/images/generations` when the host is `api.apiyi.com`, `vip.apiyi.com`, or
  `b.apiyi.com`; fast mode omits `size`, standard mode sends the selected size.
- Multi-image reference is implemented for single-image generation. The source image is always `image 1` / `图1`; active references are copied to `%APPDATA%\AIRenderer\active-references\` and sent as `image 2..N`.
- Reference-library items and active references are intentionally separate. Do not bind active references directly to library files without copying; deleting a library item must not break the current request.
- Mask edit currently remains source+mask only. Additional reference images for mask edit are out of current scope.
