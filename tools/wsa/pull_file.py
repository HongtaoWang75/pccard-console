#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""二进制安全地从 WSA 拉文件到本地（走 su -c cat + exec-out，避免 PowerShell 文本管道毁字节）。

用法：python pull_file.py <设备路径> <本地路径> [更多 路径对...]
例：  python pull_file.py /data/data/com.xiaomi.smarthome/files/mmkv/mmkv_id_local_control raw/mmkv_id_local_control.bin
"""
from __future__ import annotations

import subprocess
import sys
import time
from pathlib import Path

ADB = r"D:\adb\adb.exe"
SERIAL = "127.0.0.1:58526"


def root_cat(path: str, retries: int = 3) -> bytes:
    last = ""
    for i in range(1, retries + 1):
        r = subprocess.run(
            [ADB, "-s", SERIAL, "exec-out", f"su -c 'cat {path}'"],
            capture_output=True,
            timeout=120,
        )
        if r.returncode == 0 and r.stdout:
            return r.stdout
        last = (r.stderr or b"").decode("utf-8", "replace").strip()[:200] or f"rc={r.returncode}"
        print(f"[!] 第 {i}/{retries} 次失败：{last}")
        time.sleep(1.5)
    raise SystemExit(f"[x] 无法读取 {path}：{last}")


def main() -> None:
    args = sys.argv[1:]
    if len(args) < 2 or len(args) % 2:
        sys.exit(__doc__)
    for remote, local in zip(args[0::2], args[1::2]):
        data = root_cat(remote)
        dst = Path(local)
        dst.parent.mkdir(parents=True, exist_ok=True)
        dst.write_bytes(data)
        print(f"[+] {remote} -> {dst}  ({len(data)} 字节)")


if __name__ == "__main__":
    main()
