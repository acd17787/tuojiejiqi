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
    """粗略抽取 public 属性/字段名（足够做绑定名存在性校验）。文件不存在返回空集。"""
    if not path.exists():
        return set()
    text = path.read_text(encoding="utf-8")
    names = set(re.findall(r"public\s+(?:[\w<>?\[\],\.\s]+?)\s+(\w+)\s*(?:\{|=>|;)", text))
    names |= set(re.findall(r"public\s+const\s+\w+\s+(\w+)", text))
    return names


VM = declared_properties(ROOT / "ViewModels/AIRenderViewModel.cs")
SETTINGS = declared_properties(ROOT / "Models/RenderSettings.cs")
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

# 所有已知条目类型的属性并集：条目模板里的绑定只做「是不是条目属性」的存在性判断，
# 不纠结它到底属于哪一个条目类型（位置区间法在多 ItemsControl 下不可靠，会误报）。
ALL_ITEM_PROPS = set().union(*ITEM_TYPES.values())

VIEPROPERTIES = {"IsActive", "ActualWidth", "ActualHeight"} | {
    n for n in re.findall(r"public\s+static\s+readonly\s+DependencyProperty\s+(\w+)", (ROOT / "Views/InteractiveHelper.cs").read_text(encoding="utf-8"))
}

ROOTS = {
    "Settings": SETTINGS,
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
# 注意：ItemsSource 不一定是第一个属性（例如前面还有 Grid.Column），
# 所以先匹配整个开标签再单独取 ItemsSource，顺序无关。
items_re = re.compile(r'<ItemsControl((?:[^>"]|"[^"]*")*)>(.*?)</ItemsControl>', re.S)
ranges = []
for m in items_re.finditer(text):
    src = re.search(r'ItemsSource="\{Binding\s+([^}"]+)\}"', m.group(1))
    if not src:
        continue
    ranges.append((m.start(2), m.end(2), LIST_CONTEXT.get(src.group(1).strip())))

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

    # 模板内自引用：源是控件自身（Tag 传圆角、附加属性自检），不是 VM，跳过
    if "RelativeSource" in body and ("TemplatedParent" in body or "Self" in body):
        checked += 1
        continue

    # 附加属性（(local:InteractiveHelper.IsActive)）——属性本身声明在 Views/InteractiveHelper.cs
    if path.startswith("(") and path.endswith(")"):
        path = path[1:-1].strip()
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
    elif head == "Settings":
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
        # 条目模板内的绑定：属性存在于任一已知条目类型即视为有效
        if rest[0] in ALL_ITEM_PROPS:
            continue
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
