#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""在米家 RN 插件 bundle（二进制也没关系）里找关键字符串及其上下文。

用法：python inspect_bundle.py <bundle文件> [模式1 模式2 ...]
默认模式：Profession / Basic / resetForcePower / piid
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

DEFAULT = ["Profession", "Basic", "resetForcePower", "force_poweroff", "auto_turn_on_when_powered_on"]


def main() -> None:
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    data = Path(sys.argv[1]).read_bytes()
    pats = sys.argv[2:] or DEFAULT
    print(f"[+] {len(data)} 字节，找 {len(pats)} 个模式")
    for pat in pats:
        print(f"\n{'=' * 66}\n===== {pat} =====")
        n = 0
        for m in re.finditer(re.escape(pat).encode(), data):
            n += 1
            if n > 10:
                print("    …（更多略）")
                break
            s = max(0, m.start() - 260)
            e = min(len(data), m.end() + 260)
            seg = data[s:e].decode("utf-8", "replace").replace("\x00", "·")
            print(f"--- @{m.start()} ---")
            print(seg)
        if n == 0:
            print("    （未命中）")


if __name__ == "__main__":
    main()
