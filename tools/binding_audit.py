#!/usr/bin/env python3
"""
绑定审计：抽取 AIRenderWindow.xaml 里所有 {Binding ...} 路径，与 C# 声明比对，列出失效绑定。

覆盖：
  - 主 VM（AIRenderViewModel）属性 / 命令
  - Settings.*（RenderSettings）
  - BatchVM.*（BatchRenderViewModel）——本界面已不再使用，仍会按需校验
  - DataTemplate 内部上下文（AspectRatio / SizeOption / PromptHistoryItem /
    GenerationHistoryItem / ReferenceImageItem / string）
  - RelativeSource AncestorType=Window 的 DataContext.* 一律按主 VM 校验
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def declared_properties(path: Path):
    """粗略抽取 public 属性/字段名（足够做绑定名存在性校验）。"""
    text = path.read_text(encoding="utf-8")
    names = set(re.findall(r"public\s+(?:[\w<>?\[\],\.\s]+?)\s+(\w+)\s*(?:\{|=>|;)", text))
    names |= set(re.findall(r"public\s+const\s+\w+\s+(\w+)", text))
    return names


VM = declared_properties(ROOT / "ViewModels/AIRenderViewModel.cs")
SETTINGS = declared_properties(ROOT / "Models/RenderSettings.cs")
BATCH = declared_properties(ROOT / "ViewModels/BatchRenderViewModel.cs")
ITEM_TYPES = {
    "AspectRatio": declared_properties(ROOT / "Models/RenderSettings.cs"),
    "SizeOption": declared_properties(ROOT / "Models/RenderSettings.cs"),
    "PromptHistoryItem": declared_properties(ROOT / "Models/RenderSettings.cs"),
    "GenerationHistoryItem": declared_properties(ROOT / "Models/RenderSettings.cs"),
    "ReferenceImageItem": declared_properties(ROOT / "Models/RenderSettings.cs"),
    "string": {"Length"},
}
# 列表成员的绑定上下文
LIST_CONTEXT = {
    "Settings.AspectRatios": "AspectRatio",
    "Settings.ImageSizes": "SizeOption",
    "PromptHistory": "PromptHistoryItem",
    "GenerationHistory": "GenerationHistoryItem",
    "ActiveReferences": "ReferenceImageItem",
    "Toasts": "string",
}

VIEPROPERTIES = {"IsActive", "ActualWidth", "ActualHeight"} | {
    n for n in re.findall(r"public\s+static\s+readonly\s+DependencyProperty\s+(\w+)", (ROOT / "Views/InteractiveHelper.cs").read_text(encoding="utf-8"))
}

ROOTS = {
    "Settings": SETTINGS,
    "BatchVM": BATCH,
    "__vm__": VM,
    "__item__": None,  # 由模板上下文决定
}

XAML = ROOT / "Views/AIRenderWindow.xaml"
text = XAML.read_text(encoding="utf-8")

# 去掉注释，避免把注释里的示例当成绑定
text = re.sub(r"<!--.*?-->", "", text, flags=re.S)

binding_re = re.compile(r"\{Binding\s+([^}]*)\}")
failures = []
checked = 0

# 找出 ItemsControl 的 ItemsSource -> 其 ItemTemplate 的上下文（按出现顺序配对）
items_re = re.compile(
    r"ItemsControl\s+ItemsSource=\"\{Binding\s+([^}\"]+)\}\"(.*?)</ItemsControl>", re.S
)
ranges = []
for m in items_re.finditer(text):
    source = m.group(1).strip()
    ranges.append((m.start(2), m.end(2), LIST_CONTEXT.get(source)))

# 布尔标志：解析 DataTemplate 中出现的 Tag / Click 等无关绑定


# 只作用于某个 DataTemplate 集合项的样式（Style 定义在 Window.Resources 里，
# 但绑定上下文是该项的类型），按 Style x:Key 声明映射。
STYLE_CONTEXT = {
    "RatioChip": "AspectRatio",
    "SizeChip": "SizeOption",
}
for m in re.finditer(r"<Style\s+x:Key=\"(\w+)\"", text):
    style_ctx = STYLE_CONTEXT.get(m.group(1))
    if not style_ctx:
        continue
    end = text.find("</Style>", m.end())
    if end < 0:
        continue
    ranges.append((m.start(), end, style_ctx))


def context_for(pos: int):
    for start, end, ctx in ranges:
        if start <= pos <= end:
            return ctx
    return None


for m in binding_re.finditer(text):
    body = m.group(1).strip()
    pos = m.start()
    # 拆掉 Converter / Mode / RelativeSource 等设置
    path = body.split(",")[0].strip()
    if path.startswith("Path="):
        path = path[5:].strip()
    if not path:
        continue

    # 附加属性（local:InteractiveHelper.IsActive）——属性本身声明在 Views/InteractiveHelper.cs
    if ":" in path and "." in path and not path.startswith("DataContext."):
        path = path.split(".", 1)[1]
    if path in VIEPROPERTIES:
        checked += 1
        continue

    # 模板里通过 RelativeSource 回主 VM 取命令
    if "RelativeSource" in body and "AncestorType=Window" in body and path.startswith("DataContext."):
        path = path[len("DataContext."):]
        ctx_name = None
    else:
        ctx_name = context_for(pos)

    # 模板里显式写类型名（如 AspectRatio.ToolTip）
    head = path.split(".")[0]
    if ctx_name and head in ITEM_TYPES:
        table = ITEM_TYPES[head]
        rest = path.split(".")[1:]
        root = head
    elif head in ("Settings", "BatchVM"):
        table = ROOTS[head]
        rest = path.split(".")[1:]
        root = head
    elif ctx_name:
        table = ITEM_TYPES.get(ctx_name, VM)
        rest = path.split(".")
        root = ctx_name
    else:
        table = VM
        rest = path.split(".")
        root = "__vm__"

    checked += 1
    if rest and rest[0] not in table:
        failures.append((pos, path, root))

text_lines = text.splitlines()


def line_of(pos: int) -> int:
    return text.count("\n", 0, pos) + 1


print(f"绑定路径总数: {checked}")
if failures:
    print(f"失效绑定: {len(failures)}")
    for pos, path, root in failures:
        print(f"  line {line_of(pos)}: {path}   (root={root})")
    sys.exit(1)
print("失效绑定: 0")
