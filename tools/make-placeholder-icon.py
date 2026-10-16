#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成占位应用图标（Kimi 的正式设计到位后替换本产物）。

产出：
  assets/appicon.ico        —— 多尺寸 ICO（16/24/32/48/64/128/256）
  assets/appicon-256.png    —— 预览图
用法：python make-placeholder-icon.py
"""
from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw

HERE = Path(__file__).parent.parent
ASSETS = HERE / "assets"
S = 256
TEAL = (1, 125, 123, 255)       # 米家插件「开机」主色 #017D7B
WHITE = (255, 255, 255, 255)


def main() -> None:
    ASSETS.mkdir(exist_ok=True)
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # 圆角底板
    d.rounded_rectangle([0, 0, S - 1, S - 1], radius=58, fill=TEAL)

    # 电源符号：开口圆环 + 竖线（开口朝上）
    cx = cy = S // 2
    r = 74
    ring_w = 24
    d.arc([cx - r, cy - r + 16, cx + r, cy + r + 16], start=118, end=422, width=ring_w, fill=WHITE)

    bar_w = 24
    d.rounded_rectangle(
        [cx - bar_w // 2, cy - r - 6, cx + bar_w // 2, cy + 2],
        radius=bar_w // 2,
        fill=WHITE,
    )

    img.save(ASSETS / "appicon-256.png")
    img.save(
        ASSETS / "appicon.ico",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    print(f"[+] 已写出 {ASSETS / 'appicon.ico'} 与 appicon-256.png（占位版）")


if __name__ == "__main__":
    main()
