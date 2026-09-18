#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""静态检查 XAML 里的 StaticResource 前向引用。

为什么需要这个：StaticResource 是**解析顺序**查找，先定义后使用。用反了
（BasedOn / Template / Value 指向一个还没解析到的资源）时，
`dotnet build` 依然报 0 错 0 警告——XAML 编译器不校验这个，只在运行时
XamlReader 加载时抛 XamlParseException：「无法找到名为 X 的资源」。

也就是说「编译通过」不等于「窗口能打开」。这个检查把该错误提前到构建期。

只报「本文件里定义了、但定义位置在使用之后」的键；键根本不在本文件里的
（来自 App.xaml / 主题 / 框架）不报，那些本来就可能来自其它字典。
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_TARGETS = ['Views/AIRenderWindow.xaml']

# 任何带 x:Key 的元素都算定义（Style / ControlTemplate / Geometry / 各种 Brush …）
DEFINITION = re.compile(r'<([A-Za-z][\w:.]*)[^>]*?\bx:Key="([^"]+)"')
USAGE = re.compile(r'\{StaticResource\s+([^}]+?)\s*\}')


def check(path):
    text = Path(path).read_text(encoding='utf-8-sig')

    # 行号：offset -> line
    line_starts = [0]
    for i, ch in enumerate(text):
        if ch == '\n':
            line_starts.append(i + 1)

    def line_of(offset):
        lo, hi = 0, len(line_starts) - 1
        while lo < hi:
            mid = (lo + hi + 1) // 2
            if line_starts[mid] <= offset:
                lo = mid
            else:
                hi = mid - 1
        return lo + 1

    definitions = {}
    for m in DEFINITION.finditer(text):
        kind, key = m.group(1), m.group(2)
        definitions.setdefault(key, (line_of(m.start()), kind))

    problems = []
    for m in USAGE.finditer(text):
        key = m.group(1)
        if key not in definitions:
            continue  # 来自其它资源字典，本文件管不着
        def_line, kind = definitions[key]
        use_line = line_of(m.start())
        if use_line < def_line:
            problems.append((key, kind, def_line, use_line))

    return problems


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('-')]
    targets = args or DEFAULT_TARGETS
    failed = False

    for target in targets:
        path = Path(target)
        if not path.is_absolute():
            path = ROOT / target
        if not path.exists():
            print(f'FAIL: 找不到 {path}')
            failed = True
            continue

        problems = check(path)
        rel = path.relative_to(ROOT) if path.is_relative_to(ROOT) else path
        if problems:
            failed = True
            print(f'FAIL: {rel} 有 {len(problems)} 处 StaticResource 前向引用')
            for key, kind, def_line, use_line in sorted(problems, key=lambda p: p[3]):
                print(f'  - 第 {use_line} 行用到 "{key}"，但它在第 {def_line} 行才定义（{kind}）')
            print('    StaticResource 先定义后使用；模板要排在用到它的样式之前。')
        else:
            print(f'PASS: {rel} 的 StaticResource 都是先定义后使用')

    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
