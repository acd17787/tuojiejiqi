"""布局契约测试：从 Converters.cs / AIRenderWindow.xaml 解析真实规则，断言宽窄两套排版。

下面用到的 ContentRoot / SourcePreviewHitArea 两个 x:Name 是**本测试的定位锚点**，
代码后置并不使用它们。删名字前先看这里——锚点没了本测试会直接报错。
"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
cs = (ROOT / 'Views/Converters.cs').read_text(encoding='utf-8')
xaml = (ROOT / 'Views/AIRenderWindow.xaml').read_text(encoding='utf-8')


def index_of(needle):
    """定位锚点；缺失时给出可读报错，而不是裸的 ValueError。"""
    pos = xaml.find(needle)
    if pos < 0:
        raise SystemExit(f'FAIL: 找不到锚点 {needle!r}——XAML 结构变了或 x:Name 被删了，'
                         f'本测试的定位方式需要同步更新')
    return pos


# 只取「承载四张卡片」的那个内容网格（含 source/result 两个 CardLayout 绑定）
root_start = index_of('Grid x:Name="ContentRoot"')
cols = re.search(r'<Grid.ColumnDefinitions>(.*?)</Grid.ColumnDefinitions>', xaml[root_start:], re.S).group(1)
n_cols = len(re.findall(r'<ColumnDefinition', cols))
print(f'Root Grid 列数 = {n_cols}')

rules = ' '.join(re.search(r'case "column":(.*?)case "span":', cs, re.S).group(1).split())
print('converter column 规则:', rules)

used = sorted(set(re.findall(r'ConverterParameter=(\w+)\.column', xaml)))
print('用到 .column 的卡片:', used)

fail = []
for slot in used:
    expected = 0 if slot in ('source', 'reference') else 2
    if expected >= n_cols:
        fail.append(f'宽窗 {slot} -> 列 {expected}，但只声明了 {n_cols} 列')
if 'narrow || leftColumn ? 0 : 2' not in rules:
    fail.append('窄窗没有全部收敛到第 0 列')
if n_cols < 3:
    fail.append(f'需要 3 列（内容/栏距/内容），实际 {n_cols}')

# 窄窗下三列都是 Star，卡片必须跨满 3 列才是整宽。
# 曾经 span=2，卡片只占 2/3 宽、右侧一大片空白，而当时的断言只查「列索引=0」，
# 查不出宽度问题——所以这里补上。
span_rule = ' '.join(re.search(r'case "span":(.*?)default:', cs, re.S).group(1).split())
print('converter span 规则:', span_rule)
if 'narrow ? 3 : 1' not in span_rule:
    fail.append('窄窗 span 不是 3：三列都是 Star，span=2 会让卡片只占 2/3 宽')
missing_span = [s for s in used if f'ConverterParameter={s}.span' not in xaml]
if missing_span:
    fail.append(f'这些卡片没绑定 ColumnSpan，窄窗下不会跨满整行: {missing_span}')

# 行号与上边距必须共用同一个「布局行」layoutRow。
# 宽窗下 result 的 SlotRow 是 1，但实际落在第 0 行；上边距若还按 SlotRow 判断，
# 右列卡片会整体比左列低 14px，两栏顶部对不齐。
row_rule = ' '.join(re.search(r'case "row":(.*?)case "column":', cs, re.S).group(1).split())
margin_rule = ' '.join(re.search(r'default:(.*)', cs, re.S).group(1).split())
print('converter row 规则:', row_rule)
if 'layoutRow' not in row_rule:
    fail.append('row 规则没有用 layoutRow 收敛宽窗行号')
if 'layoutRow == 0 ? 0 : 14' not in margin_rule:
    fail.append('上边距没有跟着 layoutRow 走：宽窗下右列卡片会整体下移 14px')
if re.search(r'Thickness\(0, row == 0', margin_rule):
    fail.append('上边距按 SlotRow 的 row 判断：宽窗 result 会拿到 14px 上边距')
if 'Converter={StaticResource PreviewHeight}' not in xaml:
    fail.append('预览缺少 3:2 高度约束')
if index_of('InkCanvas x:Name="MaskInkCanvas"') < index_of('Viewbox x:Name="SourceViewbox"'):
    fail.append('InkCanvas 声明在 Viewbox 之前，蒙版会被显示层盖住')
if 'IsHitTestVisible="{Binding IsMaskEditing}"' not in xaml:
    fail.append('InkCanvas 没有按编辑态开关命中测试')
ink_block = xaml.split('InkCanvas x:Name="MaskInkCanvas"')[1].split('/>')[0]
if 'Visibility="{Binding HasSourceImage' not in ink_block:
    fail.append('InkCanvas 空图时没有隐藏')

# 蒙版输入层之上不能再有任何吃指针的全尺寸元素：
# 灯箱按钮必须显式在编辑态关闭命中测试，并且层级要低于或等于输入层之外的显式约定
hit = xaml[index_of('x:Name="SourcePreviewHitArea"'):].split('</Button>')[0]
if 'IsHitTestVisible' not in hit or 'IsMaskEditing' not in hit:
    fail.append('透明灯箱按钮没有在蒙版编辑态关闭命中测试（会挡住 InkCanvas）')
if 'Panel.ZIndex="2"' not in hit:
    fail.append('透明灯箱按钮缺少显式 Panel.ZIndex')

def zindex(pattern, label):
    m = re.search(pattern, xaml, re.S)
    if not m:
        fail.append(f'{label} 缺少显式 Panel.ZIndex')
        return None
    return int(m.group(1))

# 预览格内的显式层级契约（从下到上）：
#   显示层 0 < 蒙版输入层 1 < 灯箱按钮 2 < 蒙版光标/工具条 6
z_view = zindex(r'x:Name="SourceViewbox"[^>]*?Panel\.ZIndex="(\d+)"', 'SourceViewbox')
z_ink = zindex(r'x:Name="MaskInkCanvas"[^>]*?Panel\.ZIndex="(\d+)"', 'MaskInkCanvas')
z_hit = zindex(r'x:Name="SourcePreviewHitArea"[^>]*?Panel\.ZIndex="(\d+)"', 'SourcePreviewHitArea')

bar_blocks = re.findall(
    r'Panel\.ZIndex="(\d+)"[^>]*\n(?:[^>]*\n){0,2}[^>]*Visibility="\{Binding Mask(?:Toolbar|AppliedBar)Visibility\}"',
    xaml)
bars = [int(v) for v in bar_blocks]
if len(bars) < 3:
    fail.append(f'蒙版工具条/状态条缺少显式 Panel.ZIndex（只找到 {len(bars)} 个）')
# 每一个蒙版浮层都必须高于灯箱按钮（max 会掩盖个别漏网的），所以取最小值比较
z_bar = min(bars) if bars else 0

if z_view is not None and z_ink is not None and z_ink <= z_view:
    fail.append(f'InkCanvas(Z={z_ink}) 不高于显示层(Z={z_view})')
if z_hit is not None and z_ink is not None and z_hit <= z_ink:
    fail.append(f'灯箱按钮(Z={z_hit}) 不低于 InkCanvas(Z={z_ink})')
if bars and z_bar <= (z_hit or 0):
    fail.append(f'蒙版浮层最低 Z={z_bar} 不高于灯箱按钮(Z={z_hit})，会被挡掉点击')

print()
if fail:
    print('FAIL:')
    for f in fail:
        print('  -', f)
    raise SystemExit(1)
print('PASS: 宽窗 4 张卡片落在 0/2 列，窄窗全部收敛到第 0 列并跨满 3 列；'
      '行号与上边距共用 layoutRow；蒙版输入层在显示层之上')
