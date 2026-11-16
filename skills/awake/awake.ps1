<#
On-demand keep-awake toggle for long AI runs (PowerToys Awake backend),
plus a "dark screen" mode for OLED laptops.

Usage:
  awake.ps1 on         # keep awake indefinitely (screen still blanks)
  awake.ps1 on 90      # timed: auto-release after 90 minutes (+ agent renewal loop, see below)
  awake.ps1 off        # normal power behavior (default); terminates any pending renewal timer
  awake.ps1 status     # report awake + dark + renewal-timer state
  awake.ps1 dark       # fullscreen black PIXEL overlay (v4): pixels off,
                       # display logically ON (avoids driver screen-off
                       # throttling); brightness untouched on the normal path
  awake.ps1 light      # stop dark screensaver + repair net (sensor, brightness)

Notes:
- dark is a screensaver: black immediately (pure pixels - OLED black pixels
  emit no light), any input wakes, re-blanks after $IdleSeconds idle
  (default 180), and retires on its own when Minutes is given; normal exits
  self-restore. 'light' is the safety-net stop. v5 (2026-11-16, operator
  decision): brightness and the ambient-light sensor are NEVER touched -
  black pixels + the overlay's ES hold are the whole mechanism, so state
  cannot outlive the process (a kill strands nothing). Accepted trade: the
  LOCK SCREEN stays lit when locked (locked-session speed parity unaffected).
- dark does NOT arm the lock guard anymore (default since 2026-11-10): a locked
  session keeps full speed while the overlay holds its execution request
  (measured parity 10.3-10.6 vs 10.2-10.7 s per 90 s ASR clip, zero standby
  events). Pass -LockGuard to restore the elevated helper (ONE UAC prompt,
  raised while the screen is still lit; the screensaver engages only after it
  is answered): it forces the display ON when locked. Only needed if the
  throttle-per-lock behavior ever comes back. 'light' still stands any armed
  guard down via its mailbox state file.
- The Awake toggle writes settings.json; the running module applies it ~2 s.
- Agent renewal loop (renewtimer.ps1, added 2026-11-15): a timed arm is an
  ESTIMATE and long jobs outrun it, which used to suspend the box mid-job.
  The agent therefore starts renewtimer.ps1 as a background job right after
  arming; it sleeps (window - margin) and its completion notification wakes
  the agent just before expiry. Still processing -> the agent re-runs
  'on <same minutes>' (this extends the timed window AND a live timed dark
  overlay via its state-file 'until' mailbox - seamless, no restart, and 'on'
  still never STARTS darkening) and starts the next timer. Finished -> the
  agent releases; 'off' terminates any pending timer. Nobody renews unless
  the agent acts, so a dead agent degrades to the plain timed backstop.
#>
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('on', 'off', 'status', 'dark', 'light')]
    [string]$Action,

    [Parameter(Position = 1)]
    [ValidateRange(1, 1440)]
    [int]$Minutes,

    [int]$IdleSeconds = 180,

    [switch]$LockGuard,    # opt-in elevated insurance (one UAC): hold display ON if locked
    [switch]$NoLockGuard   # deprecated: this is the default now; accepted and ignored
)

$ErrorActionPreference = 'Stop'
$path = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\Awake\settings.json'
$darkFile = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\Awake\darkstate.json'

function Get-Brightness {
    (Get-CimInstance -Namespace root\WMI -ClassName WmiMonitorBrightness).CurrentBrightness
}
# v5: brightness SETTERS removed (operator decision 2026-11-16) - dark never
# writes brightness; the read above is informational for `status` only.
function Get-DarkState {
    if (-not (Test-Path $darkFile)) { return $null }
    $st = Get-Content $darkFile -Raw | ConvertFrom-Json
    $alive = [bool](Get-Process -Id $st.pid -ErrorAction SilentlyContinue)
    [pscustomobject]@{ state = $st; overlayAlive = $alive }
}

# v5 (2026-11-16): powercfg/registry/WMI brightness helpers REMOVED per
# operator decision - dark never touches brightness or the ambient sensor.
# The verified GUIDs and the v4 sensor-pause/restore machinery are recorded
# in TUNING-NOTES.md should they ever be needed again.
$guardFile = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\Awake\lockguard-state.json'
function Get-GuardState {
    if (-not (Test-Path $guardFile)) { return $null }
    $st = Get-Content $guardFile -Raw | ConvertFrom-Json
    $alive = [bool](Get-Process -Id $st.pid -ErrorAction SilentlyContinue)
    [pscustomobject]@{ state = $st; guardAlive = $alive }
}

# --- renewal-timer mailbox (renewtimer.ps1) --------------------------------
# The timer process is dumb (sleep + notify); this side owns its lifecycle:
# 'off' and any re-arm terminate the pending one, 'status' reports it.
$renewFile = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\Awake\renewtimer.json'
function Get-RenewTimer {
    if (-not (Test-Path $renewFile)) { return $null }
    try { $b = Get-Content $renewFile -Raw | ConvertFrom-Json } catch { return $null }
    $p = Get-Process -Id $b.pid -ErrorAction SilentlyContinue
    # pid-reuse guard: only treat as pending while the pid is still a shell
    $alive = [bool]($p -and $p.ProcessName -match '^(pwsh|powershell)$')
    [pscustomobject]@{ box = $b; pending = $alive }
}
function Stop-RenewTimer {
    $t = Get-RenewTimer
    if (-not $t) { return '' }
    if ($t.pending) { Stop-Process -Id $t.box.pid -Force -ErrorAction SilentlyContinue }
    Remove-Item -Path $renewFile -Force -ErrorAction SilentlyContinue
    if ($t.pending) { return 'renewal timer terminated' } else { return 'renewal timer box cleaned' }
}
function Extend-LiveDark([int]$Minutes) {
    # Re-arm in lockstep for a timed dark overlay that is actually alive: push
    # its retirement via the state-file 'until' (darkscreen.ps1 re-reads it).
    # Never starts darkening, never touches an exited/indefinite/untimed state.
    # 2026-11-16: a renewal silently FAILED to extend a live overlay (it
    # retired on its original window at 14:05 despite an ~14:00 push) and the
    # old code had zero telemetry, so writer-vs-reader was undiagnosable.
    # Every path now leaves a line in darkscreen.log.
    $dsLog = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\Awake\darkscreen.log'
    $d = Get-DarkState
    if (-not ($d -and $d.overlayAlive -and $d.state.durationMinutes -gt 0)) {
        "$(Get-Date -Format 's') [awake] until push SKIPPED (journal=$([bool]$d) alive=$($d.overlayAlive) duration=$($d.state.durationMinutes))" |
            Add-Content -Path $dsLog -Encoding utf8
        return
    }
    $untilNew = (Get-Date).AddMinutes($Minutes)
    try {
        $st = $d.state
        $st | Add-Member -NotePropertyName until -NotePropertyValue $untilNew.ToString('o') -Force
        $st | ConvertTo-Json -Compress | Set-Content -Path $darkFile -Encoding utf8
        "$(Get-Date -Format 's') [awake] until pushed -> $($untilNew.ToString('s')) for pid $($st.pid)" |
            Add-Content -Path $dsLog -Encoding utf8
    } catch {
        "$(Get-Date -Format 's') [awake] until push FAILED: $($_.Exception.Message)" |
            Add-Content -Path $dsLog -Encoding utf8
    }
}

if ($Action -eq 'status') {
    if (Test-Path $path) {
        $p = (Get-Content $path -Raw | ConvertFrom-Json).properties
        $mode = switch ($p.mode) {
            0 { 'OFF - normal sleep behavior (screen-off will suspend)' }
            1 { "TIMED - interval $($p.intervalHours)h $($p.intervalMinutes)m, expires $($p.expirationDateTime)" }
            2 { 'INDEFINITE - system stays running, screen still turns off' }
            default { "unknown mode $($p.mode)" }
        }
        Write-Output "Awake: $mode"
    }
    $d = Get-DarkState
    if ($d) {
        if ($d.overlayAlive) { Write-Output "Dark: screensaver RUNNING (idle $($d.state.idleSeconds) s, duration $(if ($d.state.durationMinutes) { "$($d.state.durationMinutes) min" } else { 'none' })$(if ($d.state.until) { ", retiring ~$($d.state.until)" }))" }
        else { Write-Output 'Dark: overlay exited (stale journal; light or the next arm cleans it)' }
    }
    $g = Get-GuardState
    if ($g) {
        if ($g.guardAlive) { Write-Output "Lock guard: ARMED until $($g.state.until) (display stays ON if locked)" }
        else { Write-Output 'Lock guard: exited (value self-restored)' }
    }
    $r = Get-RenewTimer
    if ($r) {
        if ($r.pending) { Write-Output "Renewal timer: PENDING (window $($r.box.windowMinutes) min, ticks $($r.box.marginMinutes) min before expiry, due $($r.box.dueAt))" }
        elseif ($r.box.firedAt) { Write-Output "Renewal timer: fired $($r.box.firedAt) - renew now if still working, otherwise release (off)" }
        else { Write-Output 'Renewal timer: box present but process gone (stale) - off cleans it' }
    }
    try { Write-Output ("Brightness now: $(Get-Brightness)%") } catch { }
    return
}

if ($Action -eq 'dark') {
    $d = Get-DarkState
    if ($d -and $d.overlayAlive) { Write-Output 'Dark already ON.'; return }
    # Stale journal (previous overlay force-killed): v5 state cannot outlive
    # the process - the ES hold died with it and brightness/sensor were never
    # touched, so cleaning the file IS the full repair.
    $heal = ''
    if ($d) { Remove-Item -Path $darkFile -Force -ErrorAction SilentlyContinue; $heal = 'stale journal cleaned (nothing else can be stranded)' }
    # Lock guard is OPT-IN (-LockGuard): with the overlay's ES hold live a locked
    # session measures at parity with unlocked (2026-11-10: locked 10.3-10.6 s vs
    # unlocked 10.2-10.7 s on the same 90 s ASR clip, zero standby events), so the
    # default path never raises UAC. When opted in, the prompt must be answerable
    # on a lit screen, so the overlay only starts once the guard signals (its
    # state file appears) or the guard path is settled (cancelled / already armed).
    $guardMsg = ''
    if (-not $LockGuard) {
        $guardMsg = 'Lock guard off (default): locked sessions keep full speed - measured parity.'
    } else {
        $g = Get-GuardState
        if ($g -and $g.guardAlive) {
            $guardMsg = "Lock guard already armed until $($g.state.until)."
        }
        else {
            Remove-Item -Path $guardFile -Force -ErrorAction SilentlyContinue   # clear stale so its appearance == accepted
            $guardMin = if ($Minutes) { $Minutes } else { 480 }
            $ErrorActionPreference = 'Continue'
            $spawned = $false
            try {
                Start-Process pwsh -Verb RunAs -WindowStyle Hidden -ArgumentList @(
                    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'lockguard.ps1'),
                    '-Minutes', "$guardMin"
                )
                $spawned = $true
                Write-Output 'UAC prompt raised - accept it; the screen goes dark only afterwards...'
            } catch {
                $guardMsg = 'Lock guard NOT armed (UAC cancelled or failed): no harm - default mode, locked sessions still measured at parity.'
            }
            $ErrorActionPreference = 'Stop'
            if ($spawned) {
                $waited = 0
                while ($waited -lt 180) {
                    $g2 = Get-GuardState
                    if ($g2 -and $g2.guardAlive) { $guardMsg = "Lock guard ARMED until $($g2.state.until) (display stays ON if locked)."; break }
                    Start-Sleep 2; $waited += 2
                }
                if (-not $guardMsg) {
                    $guardMsg = 'Lock guard NOT armed (no UAC response within 3 min): proceeding without it - default mode is parity anyway.'
                }
            }
        }
    }
    # Screen goes dark now - the prompt is already answered.
    # The overlay owns its journal (pid, duration, until) and writes it
    # BEFORE the first blackout - poll for it instead of writing a pre-spawn state.
    $cmdArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'darkscreen.ps1'),
        '-IdleSeconds', "$IdleSeconds"
    )
    if ($Minutes) { $cmdArgs += @('-DurationMinutes', "$Minutes") }
    $proc = Start-Process pwsh -PassThru -WindowStyle Hidden -ArgumentList $cmdArgs
    $j = $null
    for ($i = 0; $i -lt 12; $i++) {
        Start-Sleep 1
        $d2 = Get-DarkState
        if ($d2 -and $d2.state.pid -eq $proc.Id) { $j = $d2.state; break }
    }
    $dur = if ($Minutes) { "auto-retires after $Minutes min" } else { 'runs until light' }
    if ($heal) { Write-Output "Note: $heal" }
    if (-not $j) {
        Write-Output "Dark screensaver started (pid $($proc.Id), $dur) - WARNING: no journal within 12 s; check darkscreen.log."
    } else {
        Write-Output "Dark screensaver ON - pixel black now (brightness/sensor untouched), any input wakes, re-blanks after $IdleSeconds s idle ($dur)."
    }
    Write-Output $guardMsg
    return
}

if ($Action -eq 'light') {
    $done = @()
    $d = Get-DarkState
    if ($d) {
        Stop-Process -Id $d.state.pid -Force -ErrorAction SilentlyContinue
        Remove-Item -Path $darkFile -Force -ErrorAction SilentlyContinue
        # v5: the ES hold died with the process and brightness/sensor were
        # never touched - stopping the overlay IS the full restore.
        $done = @('dark screensaver stopped (nothing else to restore)')
    } else {
        $done += 'Dark mode was not on (brightness untouched)'
    }
    # Guard stand-down: we cannot kill the elevated helper (UIPI) - leave a stop
    # request in its state file; its <=15 s poll self-restores and cleans up.
    $g = Get-GuardState
    if ($g) {
        if ($g.guardAlive) {
            [pscustomobject]@{ pid = $g.state.pid; scheme = $g.state.scheme; origAC = $g.state.origAC; origDC = $g.state.origDC; minutes = $g.state.minutes; until = $g.state.until; stop = $true } |
                ConvertTo-Json -Compress | Set-Content -Path $guardFile -Encoding utf8
            $done += 'Lock guard told to stand down (restores within ~15 s)'
        } else {
            Remove-Item -Path $guardFile -Force -ErrorAction SilentlyContinue
            $done += 'Lock guard already exited (stale state cleaned)'
        }
    }
    Write-Output ($done -join '; ')
    return
}

# on / off (PowerToys Awake)
if (-not (Test-Path $path)) {
    Write-Error "Awake settings not found at $path - install/enable PowerToys Awake first."
}
if (-not (Get-Process PowerToys -ErrorAction SilentlyContinue)) {
    Write-Warning 'PowerToys is not running - the change applies once PowerToys starts.'
}

$settings = Get-Content $path -Raw | ConvertFrom-Json
$p = $settings.properties

if ($Action -eq 'on') {
    # A timed 'on' is ALSO the agent's mid-task renewal: it extends a live timed
    # dark overlay in lockstep and retires the previous cycle's timer (the next
    # timer's spawn command is echoed after the write).
    $renewNote = Stop-RenewTimer
    if ($Minutes) {
        $p.mode = 1
        $p.intervalHours = [math]::Floor($Minutes / 60)
        $p.intervalMinutes = $Minutes % 60
        $p.expirationDateTime = [datetimeoffset]::Now.AddMinutes($Minutes).ToString('o')
        Extend-LiveDark $Minutes
    }
    else {
        $p.mode = 2   # indefinite: never expires, a renewal timer is pointless
    }
    $p.keepDisplayOn = $false   # screen still blanks on its own schedule
}
else {
    # Release ends the agent loop too: kill any pending renewal timer FIRST so
    # nothing can fire after the toggle went off, then release Awake.
    $renewNote = Stop-RenewTimer
    $p.mode = 0
}

Set-Content -Path $path -Value ($settings | ConvertTo-Json -Compress -Depth 10) -NoNewline -Encoding utf8
Start-Sleep 2   # let the Awake module's settings watcher react

$mode = switch ($p.mode) {
    0 { 'OFF - normal sleep behavior (screen-off will suspend)' }
    1 { "TIMED - interval $($p.intervalHours)h $($p.intervalMinutes)m, expires $($p.expirationDateTime)" }
    2 { 'INDEFINITE - system stays running, screen still turns off' }
    default { "unknown mode $($p.mode)" }
}
Write-Output "Awake: $mode$(if ($renewNote) { " ($renewNote)" })"
if ($Action -eq 'on' -and $Minutes) {
    Write-Output "Renewal loop: start as a background job NOW: pwsh -NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\renewtimer.ps1`" -Minutes $Minutes - when it fires: still working => re-run this 'on' command and start the next timer; finished => release (off)."
}
