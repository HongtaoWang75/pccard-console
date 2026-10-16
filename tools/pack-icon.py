#!/usr/bin/env python3
"""把 render-icon.js 的超采样产物降采样、打包多尺寸 .ico，并生成效果预览图。

用法：python tools/pack-icon.py <assets/appicon.svg>

产物（都在 SVG 同目录，文件前缀取 SVG 文件名）：
    png/appicon-<n>.png   各尺寸 PNG
    appicon.ico           多尺寸 Windows 图标（16/24/32/48/64/128/256）
    appicon-256.png       256 PNG 便捷副本
    appicon-512.png       512 PNG 便捷副本
    preview.png           浅色 + 深色背景对照预览（判断小尺寸可读性用）
"""
import io
import os
import struct
import sys

from PIL import Image, ImageDraw, ImageFont

ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
PNG_SIZES = ICO_SIZES + [512]

LIGHT_BG = (243, 243, 243, 255)
DARK_BG = (31, 31, 31, 255)
LABEL_LIGHT = (90, 90, 90, 255)
LABEL_DARK = (170, 170, 170, 255)


def load_font(size: int):
    for path in (r"C:\Windows\Fonts\msyh.ttc", r"C:\Windows\Fonts\segoeui.ttf"):
        if os.path.exists(path):
            try:
                return ImageFont.truetype(path, size)
            except OSError:
                pass
    return ImageFont.load_default()


def build_preview(finals: dict, out_path: str) -> None:
    """浅色/深色两条对照带，按尺寸从左到右排列（只放 16–256）。"""
    shown = [s for s in ICO_SIZES if s in finals]
    if not shown:
        return

    pad, gap, label_h, row_gap = 28, 26, 22, 14
    band_h = 300

    total_w = pad * 2 + sum(finals[s].width for s in shown) + gap * (len(shown) - 1)
    total_h = pad * 2 + band_h * 2 + label_h * 2 + row_gap

    canvas = Image.new("RGBA", (total_w, total_h), (255, 255, 255, 255))
    draw = ImageDraw.Draw(canvas)
    font = load_font(13)

    y = pad
    for bg, label_color in ((LIGHT_BG, LABEL_LIGHT), (DARK_BG, LABEL_DARK)):
        draw.rectangle([pad - 10, y, total_w - pad + 10, y + band_h + label_h], fill=bg)

        x = pad
        for s in shown:
            icon = finals[s]
            iy = y + (band_h - icon.height) // 2
            canvas.alpha_composite(icon, (x, iy))
            text = f"{s}px"
            tw = draw.textlength(text, font=font)
            draw.text((x + (icon.width - tw) / 2, y + band_h + 2), text,
                      font=font, fill=label_color)
            x += icon.width + gap

        y += band_h + label_h + row_gap

    canvas.save(out_path)
    print(f"  预览图（{total_w}×{total_h}）→ {out_path}")


def main() -> int:
    if len(sys.argv) < 2:
        print("用法: python pack-icon.py <assets/appicon.svg>", file=sys.stderr)
        return 2

    svg_path = os.path.abspath(sys.argv[1])
    out_dir = os.path.dirname(svg_path)
    raw_dir = os.path.join(out_dir, "_raw")
    png_dir = os.path.join(out_dir, "png")
    name = os.path.splitext(os.path.basename(svg_path))[0]   # appicon

    if not os.path.isdir(raw_dir):
        print(f"找不到超采样产物目录 {raw_dir}\n请先运行 render-icon.js", file=sys.stderr)
        return 3

    os.makedirs(png_dir, exist_ok=True)

    finals = {}
    for s in PNG_SIZES:
        raw = os.path.join(raw_dir, f"raw-{s}.png")
        if not os.path.exists(raw):
            print(f"  跳过 {s}px（缺少 {raw}）")
            continue
        with Image.open(raw) as im:
            im = im.convert("RGBA").resize((s, s), Image.LANCZOS)
        finals[s] = im
        im.save(os.path.join(png_dir, f"{name}-{s}.png"))
        print(f"  {s}×{s} PNG")

    if not finals:
        print("没有任何尺寸可用", file=sys.stderr)
        return 4

    # ── 打包 ICO：PNG 压缩的 ICONDIR ──
    ico_sizes = [s for s in ICO_SIZES if s in finals]
    blobs = []
    for s in ico_sizes:
        buf = io.BytesIO()
        finals[s].save(buf, format="PNG", optimize=True)
        blobs.append(buf.getvalue())

    ico = io.BytesIO()
    ico.write(struct.pack("<HHH", 0, 1, len(ico_sizes)))
    offset = 6 + 16 * len(ico_sizes)
    for s, data in zip(ico_sizes, blobs):
        dim = 0 if s >= 256 else s
        ico.write(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
    for data in blobs:
        ico.write(data)

    ico_path = os.path.join(out_dir, f"{name}.ico")
    with open(ico_path, "wb") as f:
        f.write(ico.getvalue())
    print(f"  ICO（{len(ico_sizes)} 个尺寸，{os.path.getsize(ico_path) / 1024:.1f} KB）→ {ico_path}")

    if 256 in finals:
        finals[256].save(os.path.join(out_dir, f"{name}-256.png"))
    if 512 in finals:
        finals[512].save(os.path.join(out_dir, f"{name}-512.png"))

    build_preview(finals, os.path.join(out_dir, "preview.png"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
