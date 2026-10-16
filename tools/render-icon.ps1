<#
.SYNOPSIS
    从 SVG 生成全套图标产物（PNG 各尺寸 + 多尺寸 ICO + 对照预览图）。

.DESCRIPTION
    两步走：
      1) render-icon.js   —— 用 Playwright 把 SVG 超采样（4×）渲染成 PNG
      2) pack-icon.py     —— LANCZOS 降采样、打包 ICO、生成浅/深背景对照预览图

    Playwright 是全局 npm 包，从任意目录 require 不到，所以这里显式设 NODE_PATH。

.PARAMETER Svg
    输入 SVG 路径。必须带 viewBox。

.EXAMPLE
    .\tools\render-icon.ps1 assets\appicon.svg
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $Svg = 'assets\appicon.svg'
)

$ErrorActionPreference = 'Stop'

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($Svg)) { $Svg = Join-Path $repoRoot $Svg }
if (-not (Test-Path $Svg)) { throw "找不到 SVG：$Svg" }

# Playwright 的全局 node_modules
$env:NODE_PATH = 'C:\Users\Wang\AppData\Roaming\npm\node_modules'

# PIL 解释器（本机装在 3.14.6；找不到就回退到 PATH 上的 python）
$python = 'E:\王鸿韬文件夹\编程\Python\解释器\3.14.6\python.exe'
if (-not (Test-Path $python)) { $python = 'python' }

$outDir = Split-Path -Parent $Svg

Write-Host '① 渲染 SVG（超采样 4×）...' -ForegroundColor Cyan
& node (Join-Path $PSScriptRoot 'render-icon.js') $Svg $outDir
if ($LASTEXITCODE -ne 0) { throw "render-icon.js 失败（退出码 $LASTEXITCODE）" }

Write-Host "`n② 降采样 + 打包 + 生成预览..." -ForegroundColor Cyan
& $python (Join-Path $PSScriptRoot 'pack-icon.py') $Svg
if ($LASTEXITCODE -ne 0) { throw "pack-icon.py 失败（退出码 $LASTEXITCODE）" }

Write-Host "`n完成 ✓ 产物在 $outDir" -ForegroundColor Green
Write-Host "  检查预览图判断小尺寸可读性：$outDir\preview.png" -ForegroundColor Gray
