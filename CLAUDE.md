# TuoJie — Rhino AI Renderer 插件

AI 图生图渲染的 Rhino 8 插件。截取视口 → 调用大模型 API → 返回渲染图。支持单图和批量命名视图。

## 编译

```bash
# net7.0-windows（须先构建两个 Sidecar，net48 版作为运行时兜底）
dotnet build Sidecar/Sidecar.csproj -f net7.0-windows
dotnet build Sidecar/Sidecar.csproj -f net48
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
  RenderSettings.cs        # 渲染参数 + ReferenceImageItem / PromptTemplate 等辅助类
  ViewRenderItem.cs        # 批量渲染单个视图项
Services/
  AIRenderService.cs       # API 调用编排（走 SidecarHttpMessageHandler）
  SidecarClient.cs         # HttpMessageHandler：拦截所有 HTTP → 命名管道
  ScreenCapture.cs         # Rhino 视口截图
  SettingsService.cs       # JSON 持久化到 %APPDATA%/AIRenderer/settings.json
  LogAndAutoSave.cs        # 日志写入 + 生成图片自动保存
  Localization.cs          # 中英文字符串表
  LocalizationManager.cs   # WPF 本地化绑定代理
ViewModels/
  AIRenderViewModel.cs     # 主窗口 VM
  BatchRenderViewModel.cs  # 批量渲染 VM
Views/
  AIRenderWindow.xaml      # 主窗口（单图 + 批量模式）
  BatchRenderWindow.xaml   # 批量渲染子窗口
  SettingsWindow.xaml      # API 设置弹窗
  AddProviderDialog.xaml   # 自定义 API 提供商编辑
  ReferenceLibraryDialog.xaml  # 参考图库管理弹窗
  InputDialog.cs           # 轻量文本输入对话框
  Converters.cs            # WPF 值转换器
Properties/
  AssemblyInfo.cs          # 插件 GUID / 图标资源名
tools/
  diagnose.bat             # 用户端诊断脚本：检查文件/运行时/Rhino/Sidecar
```

## API Provider 机制

- `ApiProvider` 枚举：BltAI (Nano Banana), BltGenerations (Flux/Qwen), Gemini, VertexKey, VertexADC
- `ProviderItem`：内置或自定义服务商的统一抽象，包含 BaseUrl / Models / AuthType / ApiFormat
- `ApiFormat`：gemini（默认），openai，images_generations，responses，chat
- 内置服务商可被用户通过 Settings UI 覆盖配置（BuiltInOverrides）
- 自定义服务商通过 `CustomProviderConfig` 存储到 settings.json

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
- APIYI `gpt-image-2` normal generation uses `/v1/images/generations`, including custom providers marked `ApiFormat=openai` when the host is `api.apiyi.com`, `vip.apiyi.com`, or `b.apiyi.com`.
- Multi-image reference is implemented for single-image generation. The source image is always `image 1` / `图1`; active references are copied to `%APPDATA%\AIRenderer\active-references\` and sent as `image 2..N`.
- Reference-library items and active references are intentionally separate. Do not bind active references directly to library files without copying; deleting a library item must not break the current request.
- Mask edit currently remains source+mask only. Additional reference images for mask edit are documented as future work in `docs/MULTI_IMAGE_REFERENCE_PLAN.md`.
