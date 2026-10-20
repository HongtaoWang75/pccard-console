# Register (or remove) the PcCard keepalive task for the current user.
#   PcCard\Keepalive : every 6 hours (start 09:17) + StartWhenAvailable
#                      (catches up after sleep/off when the user logs on).
# No admin rights required (per-user interactive task). An ONLOGON variant
# is NOT used: machine-wide triggers require admin; the 6-hourly + SWA
# combination covers normal desktop usage.
param([switch]$Unregister)

$taskName = 'PcCard\Keepalive'
$script = Join-Path $PSScriptRoot 'refresh-and-push.ps1'
$cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$script`""

if ($Unregister) {
    schtasks /Delete /TN $taskName /F 2>$null
    exit $LASTEXITCODE
}

if (-not (Test-Path $script)) { Write-Error "script not found: $script"; exit 2 }

schtasks /Create /TN $taskName /TR $cmd /SC HOURLY /MO 6 /ST 09:17 /IT /F | Out-Null

# Inject <StartWhenAvailable>true</StartWhenAvailable> in schema order
# (before IdleSettings / other later elements), then re-import the XML.
$xml = schtasks /Query /TN $taskName /XML | Out-String
if ($xml -notmatch '<StartWhenAvailable>') {
    $m = [regex]::Match($xml, '(?s)<Settings>.*?</Settings>')
    $settings = $m.Value
    $anchor = $null
    foreach ($a in '<RunOnlyIfNetworkAvailable', '<IdleSettings', '<NetworkSettings', '<ExecutionTimeLimit', '<Priority', '<Hidden') {
        if ($settings.Contains($a)) { $anchor = $a; break }
    }
    if ($anchor) {
        $settings2 = $settings.Replace($anchor, '<StartWhenAvailable>true</StartWhenAvailable>' + $anchor)
        $xml2 = $xml.Replace($settings, $settings2)
        $f = Join-Path $env:TEMP 'pccard-keepalive.xml'
        [IO.File]::WriteAllText($f, $xml2, [Text.UnicodeEncoding]::new($false, $true))
        schtasks /Create /TN $taskName /XML $f /F | Out-Null
    }
}

schtasks /Query /TN $taskName /V /FO LIST | Select-String 'TaskName|Next Run Time|Status'
$swa = [regex]::Match((schtasks /Query /TN $taskName /XML | Out-String), '<StartWhenAvailable>(\w+)').Groups[1].Value
Write-Output "StartWhenAvailable=$swa"
