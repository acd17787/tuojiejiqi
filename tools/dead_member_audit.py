#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""列出「只有声明、没有任何引用」的成员，供人工确认后删除。

只做候选筛选，不做判断：名字在 .cs / .xaml 里出现次数 <= 2 的成员全部列出，
由人确认它是不是 JSON 持久化字段、XAML 绑定目标或反射入口。
JSON 字段即使零引用也必须保留（读整份→改字段→覆盖整份，丢了就是用户数据丢失），
所以本脚本的输出**不是**删除清单。
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

DECL = re.compile(
    r'^\s*(?:\[[^\]]*\]\s*)*'
    r'(?:public|internal|protected)\s+'
    r'(?:static\s+|virtual\s+|override\s+|sealed\s+|async\s+|readonly\s+|const\s+|new\s+|partial\s+)*'
    r'(?P<type>[\w<>\[\],\.\?]+)\s+'
    r'(?P<name>[A-Za-z_]\w*)\s*'
    r'(?:\(|=>|\{|;)'
)


def sources():
    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in ('.git', 'bin', 'obj', 'tools', 'node_modules')]
        for f in files:
            if f.endswith(('.cs', '.xaml')):
                yield os.path.join(base, f)


def main():
    files = list(sources())
    texts = {}
    for path in files:
        with open(path, encoding='utf-8-sig', errors='replace') as fh:
            texts[path] = fh.read()

    hits = {}
    for path, text in texts.items():
        if not path.endswith('.cs'):
            continue
        for i, line in enumerate(text.splitlines(), 1):
            m = DECL.match(line)
            if not m:
                continue
            name = m.group('name')
            if name in ('get', 'set', 'return', 'new', 'if', 'else', 'class', 'struct', 'interface'):
                continue
            hits.setdefault(name, []).append((path, i))

    candidates = []
    for name, decls in hits.items():
        if len(decls) > 1:
            continue  # 多处声明（重载/分部类），先不碰
        pattern = re.compile(r'\b' + re.escape(name) + r'\b')
        total = sum(len(pattern.findall(t)) for t in texts.values())
        if total <= 2:  # 1 = 只有声明；2 = 声明 + 自引用（如递归/自调用）
            rel = os.path.relpath(decls[0][0], ROOT)
            candidates.append((total, rel, decls[0][1], name))

    candidates.sort()
    print(f'候选（引用次数 <= 2）：{len(candidates)} 个\n')
    print(f'{"次数":<4} {"位置":<52} 成员')
    for total, rel, line, name in candidates:
        print(f'{total:<4} {rel}:{line:<40} {name}')
    print('\n注意：JSON 持久化字段 / XAML 绑定目标 / 反射入口即使零引用也不能删。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
