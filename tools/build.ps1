<#
.SYNOPSIS
    构建 PcCard。默认只编译；-Stage 会发布两个 exe 到同一目录（模拟安装后布局）+ 产检。

.DESCRIPTION
    本机两个环境事实必须在这一层处理（与 flowget 相同）：
      1) PATH 上的 dotnet 没有任何 SDK，真正的 SDK 在 D:\dotnet-sdk
      2) NuGet 全局缓存被改到 D:\nuget-packages
    看到 WMC9999 / WMC1509 时先往上翻找 C# 编译错误 —— 那是第一趟编译失败引发的误导性报错。

    ★ stage 布局 = 安装后的 {app}：PcCardApp.exe（GUI+托盘）+ pccard.exe（任务执行器）
      共享一份自包含运行时。产检硬断言：
        · 不允许出现 PcCard.exe / pccard 之外的可解析名（PATHEXT 大小写坑）
        · 不允许 runtimes\ 子目录（说明 RuntimeIdentifier 没生效）
        · pccard.exe 不得引用 WindowsAppRuntime（Session 0 生命线，由 verify-deps 保证）

.PARAMETER Configuration
    Debug（默认）或 Release。

.PARAMETER Clean
    先删 bin/obj 做全量构建。

.PARAMETER Stage
    发布到 D:\pccard-build\stage\<配置>\ 并做产物校验。

.PARAMETER Test
    同时跑单元测试。

.EXAMPLE
    .\tools\build.ps1
    .\tools\build.ps1 -Configuration Release -Stage -Test
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $Clean,
    [switch] $Stage,
    [switch] $Test
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repoRoot  = Split-Path -Parent $PSScriptRoot
$dotnetExe = 'D:\dotnet-sdk\dotnet.exe'
$srcDir    = Join-Path $repoRoot 'src'

$projects = @(
    @{ Name = 'PcCard.Core'; Path = Join-Path $srcDir 'PcCard.Core\PcCard.Core.csproj' }
    @{ Name = 'PcCard.Cli';  Path = Join-Path $srcDir 'PcCard.Cli\PcCard.Cli.csproj' }
    @{ Name = 'PcCard.App';  Path = Join-Path $srcDir 'PcCard.App\PcCard.App.csproj' }
    @{ Name = 'PcCard.Core.Tests'; Path = Join-Path $repoRoot 'tests\PcCard.Core.Tests\PcCard.Core.Tests.csproj' }
)

if (-not (Test-Path $dotnetExe)) { throw "找不到 .NET SDK：$dotnetExe" }

# ── 环境（每次显式设，不依赖用户环境变量）──
$env:DOTNET_ROOT    = 'D:\dotnet-sdk'
$env:NUGET_PACKAGES = 'D:\nuget-packages'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # 英文输出便于匹配错误关键字

# ── 清理 ──
if ($Clean) {
    foreach ($p in $projects) {
        $dir = Split-Path -Parent $p.Path
        foreach ($d in 'bin', 'obj') {
            $t = Join-Path $dir $d
            if (Test-Path $t) { Remove-Item $t -Recurse -Force }
        }
    }
    Write-Host '已清理 bin/obj' -ForegroundColor DarkGray
}

# ── 编译 ──
Write-Host "编译 [$Configuration] ..." -ForegroundColor Cyan
$failed = @()
foreach ($p in $projects) {
    if (-not (Test-Path $p.Path)) { Write-Host "  跳过 $($p.Name)（工程不存在）" -ForegroundColor DarkGray; continue }

    $out = & $dotnetExe build $p.Path -c $Configuration -p:RuntimeIdentifier=win-x64 --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ✗ $($p.Name)" -ForegroundColor Red
        $out | Where-Object { $_ -match 'error' } | Select-Object -First 12 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        $failed += $p.Name
    } else {
        Write-Host "  ✓ $($p.Name)" -ForegroundColor Green
    }
}

if ($failed.Count -gt 0) {
    Write-Host "`n构建失败：$($failed -join ', ')" -ForegroundColor Red
    Write-Host '提示：若看到 WMC9999 / WMC1509，先往上翻找 C# 编译错误 ——' -ForegroundColor Yellow
    Write-Host '      第一趟编译失败会让 XAML 编译器报出误导性的 NRE。' -ForegroundColor Yellow
    exit 1
}

# ── 单元测试 ──
if ($Test) {
    Write-Host "`n单元测试 ..." -ForegroundColor Cyan
    & $dotnetExe test (Join-Path $repoRoot 'tests\PcCard.Core.Tests\PcCard.Core.Tests.csproj') -c $Configuration --nologo 2>&1 |
        Select-Object -Last 4 | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0) { Write-Host '单元测试失败' -ForegroundColor Red; exit 1 }
}

if (-not $Stage) {
    Write-Host "`n构建成功 ✓（未汇集；加 -Stage 发布到 stage 目录）" -ForegroundColor Green
    exit 0
}

# ── 汇集：两个 exe + 共享运行时，全部同层（与安装后的 {app} 一致）──
$stageRoot = 'D:\pccard-build'
$stageDir  = Join-Path $stageRoot "stage\$Configuration"
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force $stageDir | Out-Null

Write-Host "`n汇集到 $stageDir ..." -ForegroundColor Cyan
foreach ($name in 'PcCard.Cli', 'PcCard.App') {   # Cli 先发（App 会覆盖共享文件，多出的 WASDK 文件保留）
    $proj = ($projects | Where-Object { $_.Name -eq $name }).Path
    & $dotnetExe publish $proj -c $Configuration -r win-x64 --self-contained true -o $stageDir --nologo 2>&1 |
        Where-Object { $_ -match 'error' } | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    if ($LASTEXITCODE -ne 0) { throw "发布 $name 失败" }
    Write-Host "  ✓ $name" -ForegroundColor Green
}

# ── CRT 保险 + 依赖门禁 ──
& (Join-Path $PSScriptRoot 'collect-crt.ps1') -StageDir $stageDir
& (Join-Path $PSScriptRoot 'verify-deps.ps1') -StageDir $stageDir
if ($LASTEXITCODE -ne 0) { Write-Host '依赖门禁未通过' -ForegroundColor Red; exit 1 }

# ── 产物校验 ──
$expect = 'PcCardApp.exe', 'pccard.exe', 'Microsoft.WindowsAppRuntime.dll'
$fail = @()
foreach ($f in $expect) {
    if (-not (Test-Path (Join-Path $stageDir $f))) { $fail += "$f 未生成" }
}
if (Test-Path (Join-Path $stageDir 'runtimes')) { $fail += 'runtimes\ 子目录存在 —— RuntimeIdentifier 没生效' }

# ★ 防回归：stage 里不得出现 .cmd/.bat 垫片。
#   本应用不需要垫片（计划任务直接调 pccard.exe）；而 .cmd 会引入
#   PATHEXT 顺序 + OEM 代码页解析（不能带 BOM/中文）两个历史坑，直接禁掉。
foreach ($f in Get-ChildItem $stageDir -File | Where-Object { $_.Extension -in '.cmd', '.bat' }) {
    $fail += "$($f.Name) —— 不允许 .cmd/.bat 垫片随包（PATHEXT/编码坑）"
}

if ($fail.Count -gt 0) {
    Write-Host "`n产物校验失败：" -ForegroundColor Red
    $fail | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

$sizeMB = [math]::Round((Get-ChildItem $stageDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
$count  = (Get-ChildItem $stageDir -Recurse -File).Count
Write-Host "`n汇集完成 ✓  $count 个文件，$sizeMB MB" -ForegroundColor Green
Write-Host "  试试： & '$stageDir\pccard.exe' doctor" -ForegroundColor Gray
