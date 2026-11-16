# Renewal timer for the AGENT-DRIVEN keep-awake loop (see SKILL.md).
# Dumb by design: it announces itself in a mailbox file, sleeps, exits.
# Nothing here touches power state. The process ENDING is the point: when an
# agent runs this as a background job, its completion notifies the agent
# shortly BEFORE the timed Awake window expires, and the AGENT decides -
# still working? re-arm with awake.ps1 'on <same minutes>' and start the next
# timer. Finished? release. The agent controls every renewal.
# Lifecycle of the mailbox (renewtimer.json):
#   - written here at start (pid, window, margin, dueAt)
#   - terminated and cleaned by awake.ps1 'off' (mandatory stop path)
#   - retired by awake.ps1 'on <minutes>' (a re-arm starts a fresh cycle; the
#     agent then spawns the next timer)
#   - if nobody acts (agent died), this fires once, marks itself, and the
#     un-renewed window expiring is the crash backstop.
param(
    [Parameter(Mandatory)]
    [ValidateRange(1, 1440)]
    [int]$Minutes,

    # Tick this far before the window's expiry; must stay smaller than Minutes
    # for a sane cadence (awake.ps1 arms use the same value they pass here).
    [ValidateRange(0, 120)]
    [int]$MarginMinutes = 5
)
$ErrorActionPreference = 'Continue'
$dir = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\Awake'
$box = Join-Path $dir 'renewtimer.json'

$span = $Minutes - $MarginMinutes
if ($span -lt 1) { $span = 1 }   # tiny windows: tick at 1 min rather than no-op

[pscustomobject]@{
    pid           = $PID
    windowMinutes = $Minutes
    marginMinutes = $MarginMinutes
    dueAt         = (Get-Date).AddMinutes($span).ToString('o')
} | ConvertTo-Json -Compress | Set-Content -Path $box -Encoding utf8

Start-Sleep -Seconds ([int]($span * 60))

# Fired. No decisions here - stamp the box (only if it still names us, so a
# cleaned or replaced box stays untouched) and let the notification reach the
# agent: renew (re-arm + next timer) or let the window die.
try {
    $b = Get-Content $box -Raw | ConvertFrom-Json
    if ($b.pid -eq $PID) {
        $b | Add-Member -NotePropertyName firedAt -NotePropertyValue (Get-Date).ToString('o') -Force
        $b | ConvertTo-Json -Compress | Set-Content -Path $box -Encoding utf8
    }
} catch { }
