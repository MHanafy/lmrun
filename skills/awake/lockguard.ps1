# Elevated lock-guard: for a window of minutes, forces the power plan's
# "Console lock display off timeout" (AC) to Never so the display stays on
# when the session is locked (the driver's screen-off throttle otherwise
# engages ~60 s after lock). Self-restores the original value when the window
# ends - no second prompt, restores even if whoever spawned us vanishes.
# Spawned by awake.ps1 'dark' via one UAC; meant to run elevated.
param(
    [Parameter(Mandatory)][int]$Minutes
)
$ErrorActionPreference = 'Continue'
$vid = '7516b95f-f776-4464-8c53-06167f40cc99'
$set = '8ec4b3a5-6868-48c2-be75-4f3044be88a7'
$dir = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\Awake'
$stateFile = Join-Path $dir 'lockguard-state.json'
$logFile = Join-Path $dir 'lockguard.log'
function Log([string]$msg) { "$(Get-Date -Format 's') [pid $PID] $msg" | Add-Content -Path $logFile -Encoding utf8 }

$schemeLine = (powercfg /getactivescheme) -join ' '
$scheme = [regex]::Match($schemeLine, '[0-9a-fA-F-]{36}').Value
if (-not $scheme) { Log 'FATAL: active scheme GUID not found'; exit 1 }
$regKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\$scheme\$vid\$set"

# Record originals so restore is exact (AC is what we change; DC is just recorded).
$orig = $null
$origDC = $null
if (Test-Path $regKey) {
    $k = Get-ItemProperty -Path $regKey -ErrorAction SilentlyContinue
    $orig = $k.ACSettingIndex
    $origDC = $k.DCSettingIndex
}

[pscustomobject]@{
    pid = $PID; scheme = $scheme; origAC = $orig; origDC = $origDC
    minutes = $Minutes; until = (Get-Date).AddMinutes($Minutes).ToString('o')
} | ConvertTo-Json -Compress | Set-Content -Path $stateFile -Encoding utf8

powercfg /setacvalueindex $scheme $vid $set 0 | Out-Null
powercfg /setactive $scheme | Out-Null
Log "STARTED ($Minutes min): console-lock-display-off AC -> Never (was AC=$orig DC=$origDC, scheme $scheme)"
Write-Host "Lock guard ACTIVE for $Minutes minutes: if the session gets locked, the display stays ON (no throttling). Restores automatically."

# Poll loop (also re-asserts the value once, in case Armoury Crate reshuffles
# the plan, and obeys a stop request written by awake.ps1 'light' - we cannot
# be killed from a non-elevated session, so the mailbox is the stand-down path).
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$reasserted = $false
$stoppedEarly = $false
while ($sw.Elapsed.TotalMinutes -lt $Minutes) {
    Start-Sleep -Seconds 15
    try {
        if ((Get-Content $stateFile -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json).stop) {
            $stoppedEarly = $true
            break
        }
    } catch { }   # file mid-rewrite or absent: keep running
    if (-not $reasserted -and $sw.Elapsed.TotalMinutes -ge 2) {
        $cur = (Get-ItemProperty -Path $regKey -Name ACSettingIndex -ErrorAction SilentlyContinue).ACSettingIndex
        if ($cur -ne 0) {
            powercfg /setacvalueindex $scheme $vid $set 0 | Out-Null
            powercfg /setactive $scheme | Out-Null
            Log "re-asserted: AC was $cur -> 0"
        }
        $reasserted = $true
    }
}
if ($stoppedEarly) { Log 'STOP requested via state file - restoring early' }

# Restore exactly what was there.
if ($null -ne $orig) {
    powercfg /setacvalueindex $scheme $vid $set ([int]$orig) | Out-Null
} elseif (Test-Path $regKey) {
    Remove-ItemProperty -Path $regKey -Name ACSettingIndex -ErrorAction SilentlyContinue
}
powercfg /setactive $scheme | Out-Null
Log "EXPIRED: AC restored to $orig"
Remove-Item -Path $stateFile -Force -ErrorAction SilentlyContinue
