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

# 墨迹画布的尺寸只能由 XAML 绑定给出。code-behind 一旦给它赋 Width/Height，就会把绑定
# 替换成本地值（WPF 语义），画布变成源图像素尺寸：源图比预览格小时只有左上角能涂。
# 这个覆盖在界面上看不出异常（涂抹位置仍然对），所以必须靠断言守住。
csharp = (ROOT / 'Views/AIRenderWindow.xaml.cs').read_text(encoding='utf-8')
if re.search(r'MaskInkCanvas\.(Width|Height)\s*=[^=]', csharp):
    fail.append('code-behind 给 MaskInkCanvas 赋了 Width/Height，会覆盖 XAML 上的尺寸绑定')
if 'Width="{Binding ActualWidth, ElementName=SourceViewbox}"' not in ink_block:
    fail.append('InkCanvas 的宽度没有绑定到 Viewbox 的实际渲染尺寸')
if 'Height="{Binding ActualHeight, ElementName=SourceViewbox}"' not in ink_block:
    fail.append('InkCanvas 的高度没有绑定到 Viewbox 的实际渲染尺寸')

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

# ── 行不得相撞 ────────────────────────────────────────────────────────────
# 曾经漏掉的一类：窄窗下四张卡片走 0/1/2/3 行，而提示词块硬编码在 2 行、历史面板在
# 3 行，于是「参考图像」和「模型设置」被后声明的两块盖住，两张卡片直接看不见。
# 上面那些断言只查列 / 跨列 / 上边距，查不出这种撞行，所以这里按同一个转换器规则
# 算出每一块在宽窄两种模式下的行号，断言互不重复。
TAG = re.compile(r'''
    <!--.*?-->                                     |
    </(?P<close>[A-Za-z_][\w:.]*)\s*>              |
    <(?P<name>[A-Za-z_][\w:.]*)
      (?P<attrs>(?:"[^"]*"|'[^']*'|[^>"'])*?)      # 必须非贪婪：贪婪会把自闭合的 / 一起吃进去
      (?P<selfclose>/?)>
''', re.S | re.X)


def content_root_children():
    """ContentRoot 的直接子元素 -> [(行号, 标签, attrs)]

    跳过属性元素语法（<Grid.RowDefinitions> / <Grid.Margin> 这类带点的标签）：
    它们不是可视子元素，只负责给父元素设属性，不占网格行。
    区域必须从标签的 `<` 开始，从标签中间开始会让深度整体错位（曾经因此只解析到 1 块）。
    """
    root_tag = '<Grid x:Name="ContentRoot">'
    start = index_of(root_tag)
    region = xaml[start:]
    base_line = xaml[:start].count('\n') + 1
    out, depth = [], 0
    for m in TAG.finditer(region):
        if m.group(0).startswith('<!--'):
            continue
        if m.group('close'):
            depth -= 1
            if depth == 0:
                break          # ContentRoot 闭合，后面的兄弟元素（toast / 灯箱）不属于这个网格
            continue
        name = m.group('name')
        if depth == 1 and '.' not in name:
            out.append((base_line + region[:m.start()].count('\n'), name, m.group('attrs') or ''))
        if not m.group('selfclose'):
            depth += 1
    return out


# 从转换器源码里读规则，不在这里另写一份
slotrow_body = re.search(r'private static int SlotRow\(string slot\)\s*\{(.*?)\n        \}', cs, re.S).group(1)
slot_rows = {m.group(1): int(m.group(2)) for m in re.finditer(r'case "(\w+)": return (\d+);', slotrow_body)}
slot_default = int(re.search(r'default: return (\d+);', slotrow_body).group(1))
fixed_body = re.search(r'switch \(slot\)\s*\{(.*?)\n            \}', cs, re.S).group(1)
fixed_rows = {m.group(1): (int(m.group(2)), int(m.group(3)))
              for m in re.finditer(r'case "(\w+)": return narrow \? (\d+) : (\d+);', fixed_body)}
if not fixed_rows:
    fail.append('转换器里没有「提示词 / 历史 / 状态栏」的窄窗行映射，它们会和卡片撞行')
n_rows = len(re.findall(r'<RowDefinition', re.search(
    r'<Grid\.RowDefinitions>(.*?)</Grid\.RowDefinitions>', xaml[root_start:], re.S).group(1)))

# 解析失败不能让这个检查变成静默通过：正常应该正好 4 张卡片 + 提示词 / 历史 / 状态栏。
children = content_root_children()
if len(children) != 7:
    fail.append(f'ContentRoot 直接子元素解析出 {len(children)} 块，预期 7 块'
                f'（4 张卡片 + 提示词 + 提示词历史 + 状态栏）——解析规则需要跟着 XAML 改')


def row_in(slot, narrow):
    if slot in fixed_rows:
        return fixed_rows[slot][0 if narrow else 1]
    r = slot_rows.get(slot, slot_default)
    return r if narrow else (0 if r <= 1 else 1)


# 列与跨列的规则同样从转换器读，避免这里另写一份
col_body = re.search(r'case "column":(.*?)case "span":', cs, re.S).group(1)
m = re.search(r'narrow \|\| leftColumn \? (\d+) : (\d+)', col_body)
col_narrow, col_wide_right = int(m.group(1)), int(m.group(2))
span_body = re.search(r'case "span":(.*?)default:', cs, re.S).group(1)
m = re.search(r'narrow \? (\d+) : (\d+)', span_body)
span_narrow, span_wide = int(m.group(1)), int(m.group(2))


def placement(attrs, slot, narrow):
    """(行, 起始列, 跨列数)：字面量直接取，绑定到转换器的按上面的规则算"""
    def value(prop, default):
        mm = re.search(r'Grid\.%s="([^"]+)"' % prop, attrs)
        if not mm:
            return default
        v = mm.group(1)
        if v.lstrip('-').isdigit():
            return int(v)
        if 'ConverterParameter' not in v:
            return default
        if prop == 'Row':
            return row_in(slot, narrow)
        if prop == 'Column':
            return col_narrow if (narrow or slot in ('source', 'reference')) else col_wide_right
        return span_narrow if narrow else span_wide

    return value('Row', 0), value('Column', 0), value('ColumnSpan', 1)


for narrow, label in ((False, '宽窗'), (True, '窄窗')):
    placed = []
    for line, tag, attrs in children:
        row_attr = re.search(r'Grid\.Row="([^"]+)"', attrs)
        cp = re.search(r'ConverterParameter=(\w+)\.row', row_attr.group(1)) if row_attr else None
        if cp:
            slot, name = cp.group(1), cp.group(1)
        else:
            # 没有绑定到转换器：行列都按字面量取，slot 留空表示不需要查规则
            slot, name = '', f'第 {line} 行的 <{tag}>'
        placed.append((placement(attrs, slot, narrow), name, line))

    # 同一行且列区间相交 = 两块叠在一起（宽窗下 source/result 共享第 0 行但列不同，不算）
    for i in range(len(placed)):
        for j in range(i + 1, len(placed)):
            (r1, c1, s1), n1, l1 = placed[i]
            (r2, c2, s2), n2, l2 = placed[j]
            if r1 == r2 and c1 < c2 + s2 and c2 < c1 + s1:
                fail.append(f'{label}第 {r1} 行第 {c1}~{c1 + s1 - 1} 列被两块占住：'
                            f'{n1}（第 {l1} 行）与 {n2}（第 {l2} 行）')

    rows_used = [p[0][0] for p in placed]
    if rows_used and max(rows_used) >= n_rows:
        fail.append(f'{label}用到第 {max(rows_used)} 行，但只声明了 {n_rows} 行')
    print(f'{label}占用: ' + ', '.join(
        f'r{r}c{c}' + (f'+{s}' if s > 1 else '') + f'={n}' for (r, c, s), n, _ in placed))

print()
if fail:
    print('FAIL:')
    for f in fail:
        print('  -', f)
    raise SystemExit(1)
print('PASS: 宽窗 4 张卡片落在 0/2 列，窄窗全部收敛到第 0 列并跨满 3 列；'
      '行号与上边距共用 layoutRow；宽窄两种模式下 7 块互不撞格；蒙版输入层在显示层之上')
