#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""调试用：把 MMKV / parcel 原始文件的结构扫清楚。

用法：python inspect_mmkv.py <file.bin> [<file2.bin> ...]
输出：
  - 文件大小
  - 所有 MMKV key 字符串（b'key_' 开头的可打印串）
  - 所有能成功 json.loads 的顶层 JSON 对象（含 did/token 字段时重点标注）
  - 所有 32 位 hex 串
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

HEX32 = re.compile(rb"[0-9a-fA-F]{32}")
KEYPAT = re.compile(rb"key_[A-Za-z0-9_.]{3,64}")


def balanced_json_slice(data: bytes, start: int) -> int | None:
    """返回结束位置（含），失败返回 None。"""
    if data[start] != 0x7B:
        return None
    depth = 0
    in_str = False
    esc = False
    for i in range(start, len(data)):
        c = data[i]
        if in_str:
            if esc:
                esc = False
            elif c == 0x5C:
                esc = True
            elif c == 0x22:
                in_str = False
            continue
        if c == 0x22:
            in_str = True
        elif c == 0x7B:
            depth += 1
        elif c == 0x7D:
            depth -= 1
            if depth == 0:
                return i
    return None


def scan(path: Path) -> None:
    data = path.read_bytes()
    print(f"\n{'='*70}\n文件：{path}  （{len(data)} 字节）\n{'='*70}")

    print("\n[MMKV keys]")
    for m in dict.fromkeys(KEYPAT.findall(data)):
        print("   ", m.decode("ascii", "replace"))

    print("\n[可解析的 JSON 对象]")
    i = 0
    found = 0
    while i < len(data):
        if data[i] == 0x7B:
            end = balanced_json_slice(data, i)
            if end is not None:
                raw = data[i : end + 1]
                try:
                    obj = json.loads(raw.decode("utf-8"))
                except Exception:
                    i += 1
                    continue
                found += 1
                # 顶层键名概览
                def keys_of(o):
                    if isinstance(o, dict):
                        return list(o.keys())
                    if isinstance(o, list):
                        return f"[list x{len(o)}]"
                    return type(o).__name__

                print(f"  #{found} @offset {i}  ({len(raw)} 字节)  顶层：{keys_of(obj)}")
                txt = raw.decode("utf-8", "replace")
                if "did" in txt or "token" in txt:
                    for m in re.finditer(r'"(did|token|localip|localIp|model|name)"\s*:\s*("?[^",}]*"?)', txt):
                        print(f"        {m.group(0)[:80]}")
                i = end + 1
                continue
        i += 1
    if not found:
        print("    （没有完整可解析的 JSON）")

    print("\n[32 位 hex 串]")
    for h in dict.fromkeys(HEX32.findall(data)):
        print("   ", h.decode())

    # 中文（UTF-8）附近内容抽查
    print("\n[含 '开机卡' 的上下文（±60 字节）]")
    needle = "开机卡".encode("utf-8")
    idx = 0
    shown = 0
    while shown < 6:
        j = data.find(needle, idx)
        if j < 0:
            break
        seg = data[max(0, j - 60) : j + 60].decode("utf-8", "replace").replace("\x00", "·")
        print("   ", seg)
        idx = j + 1
        shown += 1


def main() -> None:
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    for p in sys.argv[1:]:
        scan(Path(p))


if __name__ == "__main__":
    main()
