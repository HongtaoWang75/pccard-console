<#
.SYNOPSIS
    把 MSVC 运行库（Microsoft.VC143.CRT）拷进 stage 作为廉价保险。

.DESCRIPTION
    Phase 0 实测：flowget 的 283 个二进制里**没有任何一个**引用 vcruntime140/msvcp140
    （WASDK 原生 DLL 静态链接了 MSVC 运行库），真正用到的是 UCRT —— 而 UCRT 的
    api-ms-win-crt-* 在 Server 2022 是内置 API set。所以严格说 CRT 不是必需。

    但目标机一旦因缺 DLL 起不来（0xc0000135），远程排障的代价远大于这 1.9 MB。
    所以随包带上（app-local）。

    ★ 绝不拷贝 ucrtbase.dll / api-ms-win-crt-*.dll —— 那会用旧副本覆盖系统 UCRT，
      反而制造不兼容。只拷 Microsoft.VC143.CRT 文件夹里的内容。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $StageDir
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$roots = @(
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Redist\MSVC',
    'C:\Program Files (x86)\Microsoft Visual Studio\2026\BuildTools\VC\Redist\MSVC'
)

$crt = $null
foreach ($root in $roots) {
    if (-not (Test-Path $root)) { continue }
    $latest = Get-ChildItem $root -Directory |
        Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending |
        Select-Object -First 1
    if ($latest) {
        $cand = Join-Path $latest.FullName 'x64\Microsoft.VC143.CRT'
        if (Test-Path $cand) { $crt = $cand; break }
    }
}

if (-not $crt) {
    Write-Host '[!] 未找到 MSVC redist 目录（跳过 CRT 保险；不影响构建）' -ForegroundColor Yellow
    exit 0
}

New-Item -ItemType Directory -Force $StageDir | Out-Null
$files = Get-ChildItem $crt -File
foreach ($f in $files) {
    Copy-Item $f.FullName (Join-Path $StageDir $f.Name) -Force
}
$mb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 2)
Write-Host "[+] CRT 保险已随包：$($files.Count) 个文件 / $mb MB（来自 $crt）"
