#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
原创性自检：本插件与"参考项目"之间还剩多少连续相同的代码。

    用法：
        python3 tools/check-overlap.py <参考项目的源码目录> [更多参考目录...]

    例：
        python3 tools/check-overlap.py /path/to/RandomPicker

为什么不用"相同行数占比"
----------------------
把文件拆成行、排序去重、数交集，会把**分散的样板行**也算成重合：
`{`、`return null;`、`using System;` 这些任何实现都躲不掉，于是数字天然偏高，
反而看不出真问题。

这里量的是**最长连续相同代码块** —— 一路复制过来的文件必然存在几十上百行的
连续相同段；而独立写出来的实现，最长的那段通常只剩属性转发、序列化选项
这类绕不开的样板，几行而已。

判定标准
--------
    最长连续相同块 ≤ 10 行   →  视为独立实现
    超过 10 行               →  需要重写，块越长问题越大
"""

import os
import sys

# 只比对这些扩展名
EXTENSIONS = ('.cs', '.axaml')
LIMIT = 10


def meaningful_lines(path):
    """取出有效行：去掉空行和注释行，并抹平首尾空白。"""
    try:
        with open(path, encoding='utf-8') as handle:
            raw = handle.readlines()
    except OSError:
        return []

    result = []
    for line in raw:
        stripped = line.strip()
        if not stripped or stripped.startswith('//'):
            continue
        result.append(stripped)
    return result


def longest_common_block(left, right):
    """
    最长连续公共子序列（滚动数组版 DP）。

    返回 (长度, 那一段的原文)。
    """
    if not left or not right:
        return 0, []

    previous = [0] * (len(right) + 1)
    best = 0
    best_end = 0

    for i in range(1, len(left) + 1):
        current = [0] * (len(right) + 1)
        for j in range(1, len(right) + 1):
            if left[i - 1] == right[j - 1]:
                current[j] = previous[j - 1] + 1
                if current[j] > best:
                    best = current[j]
                    best_end = i
        previous = current

    return best, left[best_end - best:best_end]


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2

    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    ours = os.path.join(here, 'src', 'ClassIsland.Toolbox')

    references = []
    for root in argv[1:]:
        if not os.path.isdir(root):
            print(f'找不到参考目录：{root}')
            return 2
        references.append(os.path.abspath(root))

    rows = []
    skip = ('obj', 'bin', 'dist', '.git')
    for folder, dirs, files in os.walk(ours):
        dirs[:] = [d for d in dirs if d not in skip]
        for name in files:
            if not name.endswith(EXTENSIONS):
                continue

            mine = os.path.join(folder, name)
            relative = os.path.relpath(mine, ours)

            mine_lines = meaningful_lines(mine)
            if not mine_lines:
                continue

            worst = 0
            worst_block = []
            for reference in references:
                theirs = os.path.join(reference, relative)
                if not os.path.exists(theirs):
                    continue
                length, block = longest_common_block(mine_lines, meaningful_lines(theirs))
                if length > worst:
                    worst, worst_block = length, block

            rows.append((worst, relative, len(mine_lines), worst_block))

    if not rows:
        print('没有可比对的文件。')
        return 0

    rows.sort(reverse=True)

    failed = 0
    print(f'{"最长连续块":>10}  {"有效行":>7}  文件')
    print('-' * 72)
    for worst, relative, total, _ in rows:
        mark = '  ✗' if worst > LIMIT else '  ✓'
        print(f'{worst:>10}  {total:>7}  {relative}{mark}')
        if worst > LIMIT:
            failed += 1

    print('-' * 72)
    if failed:
        print(f'{failed} 个文件超过 {LIMIT} 行上限，需要重写。')
        print('\n最严重的那一段：')
        for line in rows[0][3][:20]:
            print('    | ' + line)
        return 1

    print(f'全部文件的最长连续相同块都在 {LIMIT} 行以内。')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
