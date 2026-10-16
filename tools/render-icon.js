#!/usr/bin/env node
/**
 * 把 SVG 图标按各目标尺寸超采样渲染成 PNG（透明背景）。
 *
 * 为什么要超采样：Chromium 直接把 SVG 光栅化到 16×16 会偏软。
 * 先在 4× 尺寸渲染，再交给 pack-icon.py 用 LANCZOS 降采样，小尺寸锐度明显更好。
 *
 * 用法：node tools/render-icon.js <input.svg> [outDir]
 * 产物：<outDir>/_raw/raw-<size>.png
 *
 * 依赖：playwright（全局 npm 包，由 render-icon.ps1 设好 NODE_PATH）。
 */
'use strict';

const fs = require('fs');
const path = require('path');

const SIZES = [16, 24, 32, 48, 64, 128, 256, 512];
const SUPERSAMPLE = 4;

function fail(msg, code = 1) {
  console.error('[render-icon] ' + msg);
  process.exit(code);
}

async function main() {
  const argv = process.argv.slice(2);
  if (argv.length < 1) fail('用法: node render-icon.js <input.svg> [outDir]', 2);

  const svgPath = path.resolve(argv[0]);
  const outDir = path.resolve(argv[1] || path.dirname(svgPath));

  if (!fs.existsSync(svgPath)) fail('找不到 SVG：' + svgPath, 2);

  let svg = fs.readFileSync(svgPath, 'utf8');
  svg = svg.replace(/<\?xml[^>]*\?>/g, '').replace(/<!DOCTYPE[^>]*>/gi, '').trim();

  if (!/viewBox\s*=/.test(svg)) {
    fail('SVG 缺少 viewBox —— 没有它无法可靠缩放。', 3);
  }

  const { chromium } = require('playwright');
  const rawDir = path.join(outDir, '_raw');
  fs.mkdirSync(rawDir, { recursive: true });

  const browser = await chromium.launch();
  try {
    for (const size of SIZES) {
      const px = size * SUPERSAMPLE;
      const page = await browser.newPage({
        viewport: { width: px, height: px },
        deviceScaleFactor: 1,
      });
      const html = `<!doctype html><html><head><meta charset="utf-8"><style>
        html,body{margin:0;padding:0;background:transparent;width:${px}px;height:${px}px;overflow:hidden}
        svg{display:block;width:${px}px;height:${px}px}
      </style></head><body>${svg}</body></html>`;

      await page.setContent(html, { waitUntil: 'load' });
      await page.waitForTimeout(60);
      await page.screenshot({ path: path.join(rawDir, `raw-${size}.png`), omitBackground: true });
      await page.close();
      console.log(`  已渲染 ${size}×${size}（超采样 ${px}×${px}）`);
    }
  } finally {
    await browser.close();
  }

  console.log('RAW_DIR=' + rawDir);
}

main().catch(e => fail(String(e && e.stack || e)));
