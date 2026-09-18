# 离屏渲染验证器（UI Harness）

不启动 Rhino，把真实的 `Views/AIRenderWindow.xaml` 抽出来离屏渲染成 PNG，
并顺带跑两项结构检查。发布门禁的第一层里「渲染回归」就是它。

## 用法

```bat
cd tools\ui-harness
python gen_window.py                 :: 从仓库 XAML 生成 window.xaml（壳 + 工作区 + 浮层）
dotnet run -c Release -- 960 1265    :: 参数 = 高 宽；>1120 宽走宽窗布局，否则窄窗
```

退出码：`0` = 通过，`1` = 有检查失败。输出：

- `out/real-window.png` —— 渲染结果，供人工目测与前后比对
- 控制台：两项检查的结果 + `UIH PASS / UIH FAIL`

## 内置检查

1. **墨迹画布跟随 Viewbox**：给一张比预览格小的源图（300x200），
   `MaskInkCanvas` 的实际尺寸必须等于 `SourceViewbox` 的渲染尺寸。
   防的是「code-behind 给画布赋 Width/Height 覆盖 XAML 绑定」——
   那会让源图比预览格小时只有左上角能涂（历史上真实发生过）。
2. **布局稳定性**：窗口高度从 850 扫到 960，同高度跑两遍布局，
   结果不一致 = 自动滚动条与「预览高=宽×2/3」构成自激回路。

## 注意

- 桩 VM（`StubVm`）只覆盖渲染所需的绑定路径；改 XAML 绑定结构后若渲染空白，
  先检查 `StubVm` 是否缺属性，再怀疑 XAML。
- `window.xaml` / `out/` 是产物，不要提交。
