<#
.SYNOPSIS
    构建 PcCard 安装器：发布到 stage → ISCC 打包成单个 .exe。

.PARAMETER Version
    写入安装器的版本号，默认 0.1.0。

.PARAMETER SkipBuild
    跳过编译与汇集，直接用已有 stage 产物（迭代安装器脚本时省时间）。

.EXAMPLE
    .\tools\make-installer.ps1
    .\tools\make-installer.ps1 -Version 0.2.0 -SkipBuild
#>
[CmdletBinding()]
param(
    [string] $Version = '0.1.0',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot
$iscc     = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
$issPath  = Join-Path $repoRoot 'installer\pccard.iss'
$stageDir = 'D:\pccard-build\stage\Release'

foreach ($p in @($iscc, $issPath)) {
    if (-not (Test-Path $p)) { throw "找不到：$p" }
}

# ── 1) 编译 + 汇集（Release）──
if (-not $SkipBuild) {
    Write-Host '① 编译并汇集（Release）...' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release -Stage
    if ($LASTEXITCODE -ne 0) { throw '构建或汇集失败' }
}

# ── 2) 产物完整性校验 ──
$required = @{
    'PcCardApp.exe'                       = '图形界面 + 托盘（WPF）'
    'pccard.exe'                          = '任务执行器 / CLI'
    'PresentationFramework.dll'           = 'WPF 运行时'
    'System.Private.CoreLib.dll'          = '.NET 自包含运行时'
}
$missing = @()
foreach ($f in $required.Keys) {
    if (-not (Test-Path (Join-Path $stageDir $f))) { $missing += "$f（$($required[$f])）" }
}
if ($missing.Count -gt 0) {
    Write-Host "`n汇集产物不完整，安装器会装出残缺版本：" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  - 缺少 $_" -ForegroundColor Red }
    exit 1
}

# ── 3) 图标就位 ──
$ico = Join-Path $repoRoot 'assets\appicon.ico'
if (-not (Test-Path $ico)) {
    Write-Host '   图标缺失，先生成占位版...' -ForegroundColor Yellow
    & python (Join-Path $PSScriptRoot 'make-placeholder-icon.py')
}

$fileCount = (Get-ChildItem $stageDir -Recurse -File).Count
$sizeMB    = [math]::Round((Get-ChildItem $stageDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "   源目录 $fileCount 个文件，$sizeMB MB"

# ── 4) ISCC ──
Write-Host '② 编译安装器...' -ForegroundColor Cyan
$issDir = Split-Path -Parent $issPath
Push-Location $issDir
try {
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$stageDir" $issPath
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($code -ne 0) { throw "ISCC 编译失败（退出码 $code）" }

$setup = Join-Path $repoRoot "build\installer\PcCard-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "预期的安装器未生成：$setup" }

$setupMB = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ''
Write-Host "安装器已生成 ✓  $setupMB MB（源 $sizeMB MB，压缩率 $([math]::Round($setupMB / $sizeMB * 100, 1))%）" -ForegroundColor Green
Write-Host "  $setup" -ForegroundColor Gray
