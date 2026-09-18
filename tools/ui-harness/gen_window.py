"""把真实 AIRenderWindow.xaml 的「工作区」和几个浮层抽出来，套一个壳做离屏渲染，
用来验证改版后的实际观感（不需要启动 Rhino）。

注意：Toast / 灯箱是根网格的子元素、不在「工作区」里，必须单独抽出来一起拼进壳里，
否则渲染永远照不到它们（曾经因此漏掉一整个「文字被裁半行」的问题）。

用法：python gen_window.py  （在 tools/ui-harness/ 下执行，产物 window.xaml 供 UiHarness 渲染）
"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Views" / "AIRenderWindow.xaml"
xaml = SRC.read_text(encoding="utf-8")


def extract_from(text, marker):
    i = text.index(marker)
    start = text.index("<", i + len(marker))
    name = re.match(r"<(\w+)", text[start:]).group(1)
    depth = 0
    tagre = re.compile(r'<(/?)(\w+)((?:"[^"]*"|\'[^\']*\'|[^>"\'])*?)(/?)>')
    for m in tagre.finditer(text, start):
        closing, tname, selfclose = m.group(1), m.group(2), m.group(4)
        if tname != name:
            continue
        if closing:
            depth -= 1
            if depth == 0:
                return text[start:m.end()]
        elif selfclose:
            if depth == 0:
                return text[start:m.end()]
        else:
            depth += 1
    raise SystemExit("unbalanced")


r0 = xaml.index("<Window.Resources>") + len("<Window.Resources>")
r1 = xaml.index("</Window.Resources>")
resources = "\n".join(l for l in xaml[r0:r1].splitlines() if "svc:LocalizationManager" not in l)

workspace = extract_from(xaml, "<!-- ═══════════════════════ 工作区")
workspace = workspace.replace('<Grid Grid.Row="1">', '<Grid Grid.Row="1" x:Name="Workspace">', 1)

# 浮层：Toast / 灯箱（根网格子元素），单独抽取
overlays = []
for marker in ("<!-- ═══════════════════════ 提示（toast）",
               "<!-- ═══════════════════════ 大图预览（灯箱）"):
    try:
        overlays.append(extract_from(xaml, marker))
    except (ValueError, SystemExit):
        print("warn: 未找到浮层", marker)

# 事件处理器在离屏加载时找不到代码隐藏，全部剥掉
EVENT = ("Click", "SelectionChanged", "Checked", "Unchecked", "TextChanged", "ValueChanged",
         "MouseDown", "MouseUp", "MouseLeftButtonUp", "MouseLeftButtonDown", "MouseMove",
         "KeyDown", "KeyUp", "Loaded", "SizeChanged", "PreviewKeyDown", "LostFocus", "GotFocus")
ENUM = {"True", "False", "Visible", "Collapsed", "Hidden", "Auto", "Uniform", "Fill", "None",
        "Both", "Horizontal", "Vertical", "OneWay", "TwoWay", "Default", "Ink", "Stretch"}
pat = re.compile(r'\s+([\w:]+)="([^"]*)"')


def strip(m):
    name, val = m.group(1), m.group(2)
    if val in ENUM:
        return m.group(0)
    # 代码隐藏的处理器都长成 Xxx_Yyy，属性值里几乎不会出现下划线
    if re.fullmatch(r"[A-Za-z_]\w*_\w+", val):
        return ""
    if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", val) and any(name.endswith(e) for e in EVENT):
        return ""
    return m.group(0)


parts = "\n".join(pat.sub(strip, p) for p in [workspace] + overlays)

out = f'''<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="clr-namespace:AIRenderer.Views;assembly=harness"
      x:Name="Shell"
      TextElement.FontFamily="Microsoft YaHei UI, Microsoft YaHei, PingFang SC, Segoe UI"
      TextOptions.TextFormattingMode="Display" UseLayoutRounding="True">
  <Grid.Resources>
{resources}
  </Grid.Resources>
  <Grid.RowDefinitions>
    <RowDefinition Height="Auto"/>
    <RowDefinition Height="*"/>
  </Grid.RowDefinitions>
{parts}
</Grid>
'''
Path("window.xaml").write_text(out, encoding="utf-8")
print("wrote window.xaml", len(out), "含浮层:", len(overlays))
