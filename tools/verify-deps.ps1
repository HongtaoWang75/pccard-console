<#
.SYNOPSIS
    stage 产物依赖门禁：CRT 需求扫描 + WASDK 隔离断言。

.DESCRIPTION
    1) 扫描所有 .exe/.dll 的字符串引用，列出依赖 vcruntime140/msvcp140/concrt140 的文件，
       并核对它们是否已随包（collect-crt.ps1 的产物）。
    2) ★ 硬断言：pccard.exe（SYSTEM 计划任务的执行器）绝不能引用 Microsoft.WindowsAppRuntime*
       —— 它要在 Session 0 里跑，WASDK 引导器会直接炸。这是方案里的生命线。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $StageDir
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$failed = $false

# ── 1) CRT 需求扫描 ──
$crtNames = @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'concrt140.dll')
$crtPresent = @{}
foreach ($n in $crtNames) { $crtPresent[$n] = Test-Path (Join-Path $StageDir $n) }

$needers = @{}
Get-ChildItem $StageDir -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll' } | ForEach-Object {
    $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
    $text = [System.Text.Encoding]::ASCII.GetString($bytes)
    foreach ($n in $crtNames) {
        if ($text.Contains($n)) {
            if (-not $needers.ContainsKey($n)) { $needers[$n] = New-Object System.Collections.Generic.List[string] }
            $needers[$n].Add($_.Name)
        }
    }
}

Write-Host '[verify-deps] CRT 引用扫描：'
if ($needers.Count -eq 0) {
    Write-Host '    · 无任何二进制引用 VC++ 运行库（与 Phase 0 实测一致）'
} else {
    foreach ($n in $needers.Keys) {
        $state = if ($crtPresent[$n]) { '随包 ✓' } else { '缺失 ✗' }
        $sample = ($needers[$n] | Select-Object -First 3) -join ', '
        Write-Host "    · $n ← $($needers[$n].Count) 个引用（$sample …）→ $state"
        if (-not $crtPresent[$n]) { $failed = $true }
    }
}

# ── 2) WASDK 隔离断言（生命线）──
$cliExe = Join-Path $StageDir 'pccard.exe'
if (Test-Path $cliExe) {
    $text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($cliExe))
    if ($text.Contains('Microsoft.WindowsAppRuntime')) {
        Write-Host '[x] pccard.exe 竟然引用了 Microsoft.WindowsAppRuntime —— Session 0 会炸！' -ForegroundColor Red
        $failed = $true
    } else {
        Write-Host '[verify-deps] pccard.exe 未引用 WindowsAppRuntime ✓（可安全在 Session 0 运行）'
    }
} else {
    Write-Host '[!] pccard.exe 不在 stage（跳过 WASDK 断言）' -ForegroundColor Yellow
}

if ($failed) { exit 1 }
