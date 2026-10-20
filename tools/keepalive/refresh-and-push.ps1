# PcCard keepalive: silently refresh the Xiaomi cloud session on this PC
# (mijiaAPI's local login state, no QR needed) and push the new credentials
# to the Aliyun deployment.
#
# Runs as the logged-on user; needs no admin rights locally.
# Logs: %LOCALAPPDATA%\PcCardKeepalive\keepalive-YYYYMMDD.log (30-day retention)
# Exit: 0 ok | 2 venv missing | 3 login state missing (QR re-scan required)
#       4 refresh produced nothing | 5 scp failed | 6 remote import unconfirmed
$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # pccard/
$credDir = Join-Path $root 'tools\credential'
$venvPy  = 'D:\pccard-build\venv-mijia\Scripts\python.exe'
$logDir  = Join-Path $env:LOCALAPPDATA 'PcCardKeepalive'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir ('keepalive-{0:yyyyMMdd}.log' -f (Get-Date))
$tmp = $null

function Log([string]$msg) {
    $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $msg
    Add-Content -Path $log -Value $line -Encoding UTF8
    Write-Output $line
}

# Native helper via Start-Process + file redirection:
#  - deterministic UTF-8 decoding (PS 5.1 otherwise decodes child output with
#    the console ANSI codepage -> mojibake for Chinese)
#  - no NativeCommandError records on child stderr
#  - ssh/scp join remaining argv with spaces, so plain-space command strings
#    arrive intact (no embedded quotes anywhere -> use the 8.3 short path).
function Invoke-Native([string]$file, [string[]]$arguments) {
    $tag = [guid]::NewGuid().ToString('N')
    $outFile = Join-Path $env:TEMP "pccard-ka-$tag.out"
    $errFile = Join-Path $env:TEMP "pccard-ka-$tag.err"
    try {
        $p = Start-Process -FilePath $file -ArgumentList $arguments -Wait -PassThru `
                -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        $stdout = if (Test-Path $outFile) { Get-Content $outFile -Raw -Encoding UTF8 } else { '' }
        $stderr = if (Test-Path $errFile) { Get-Content $errFile -Raw -Encoding UTF8 } else { '' }
        $combined = ('' + $stdout + $stderr).Trim()
        return @{ Code = $p.ExitCode; Output = $combined }
    }
    finally {
        Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue
    }
}

try {
    Log '=== keepalive start ==='
    if (-not (Test-Path $venvPy)) { Log "SKIP: venv python missing ($venvPy)"; exit 2 }
    if (-not (Test-Path (Join-Path $credDir '.auth-xiaomiio.json'))) {
        Log 'SKIP: local login state missing (.auth-xiaomiio.json) - QR re-scan required'
        exit 3
    }

    $tmp = Join-Path $env:TEMP 'pccard-refresh.json'
    if (Test-Path $tmp) { Remove-Item $tmp -Force }

    Push-Location $credDir
    try {
        $r = Invoke-Native $venvPy @('acquire_qr.py', '--out', $tmp)
    }
    finally { Pop-Location }
    Log ('refresh: ' + $r.Output)
    if ($r.Code -ne 0 -or -not (Test-Path $tmp)) { Log 'FAIL: refresh produced no credentials file'; exit 4 }

    $r = Invoke-Native 'scp' @($tmp, 'ali:C:/Windows/Temp/pccard-refresh.json')
    if ($r.Code -ne 0) { Log ('FAIL: scp -> ' + $r.Output); exit 5 }

    # 8.3 short path avoids embedded quotes through PS 5.1 -> ssh -> cmd
    $r = Invoke-Native 'ssh' @('ali', 'type C:\Windows\Temp\pccard-refresh.json | C:\PROGRA~1\PcCard\pccard.exe credential import --stdin')
    Log ('import: ' + $r.Output)
    if ($r.Output -notmatch 'sid=xiaomiio') { Log 'FAIL: remote import did not confirm'; exit 6 }
    Invoke-Native 'ssh' @('ali', 'del C:\Windows\Temp\pccard-refresh.json') | Out-Null

    Log 'OK: session refreshed and imported to aliyun'
    exit 0
}
catch {
    Log ('FAIL: ' + $_.Exception.Message)
    exit 1
}
finally {
    if ($tmp -and (Test-Path $tmp)) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
    Get-ChildItem $logDir -Filter 'keepalive-*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-30) } |
        Remove-Item -Force -ErrorAction SilentlyContinue
}
