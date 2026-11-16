# Dark screensaver for OLED - v5 (2026-11-16): PIXEL BLACK + ES HOLD, NOTHING ELSE.
# Operator decision: brightness changes and the ambient-sensor pause are
# removed entirely. Black pixels on this OLED emit no light, and the
# execution request below is what keeps Windows from suspending - that is
# the whole job. Consequence accepted by the operator: the LOCK SCREEN stays
# lit (the overlay cannot paint the secure desktop and we no longer write
# brightness); locked-session SPEED parity is unaffected (measured 2026-11-10).
# State now cannot outlive the process: a kill strands nothing (execution
# requests die with the process), so there is no restore to lose and no
# brightness/sensor value to capture or mis-restore.
# Starts black immediately; any input wakes (form just hides); re-blanks
# after $IdleSeconds; retires after $DurationMinutes (0 = forever) or per the
# state-file 'until' mailbox (renewal). This process owns the journal;
# awake.ps1 only reads it and patches 'until'.
param(
    [int]$IdleSeconds = 180,
    [int]$DurationMinutes = 0
)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace U {
  public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  public static class Native {
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint f);
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
    [DllImport("user32.dll")] public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint flags);
  }
  public static class Es {
    // pwsh 7.6.9 on this box parses hex literals >= 0x80000000 as NEGATIVE
    // (even under [long]/[uint64] casts) - ES flag math MUST live here in C#.
    const uint Continuous = 0x80000000;
    const uint System     = 0x00000002;
    const uint Display    = 0x00000004;
    const uint AwayMode   = 0x00000040;   // NOT 0x80000001 - that bit is invalid
    // Win11 26300 / Modern Standby REJECTS ES_DISPLAY_REQUIRED (err 87):
    // display power is OS-owned here. So layer the attempts - never let an
    // unsupported bit sink the SYSTEM/AWAYMODE hold that does work.
    // Results are returned as "attempt=0xRET;..." for the log.
    public static string HoldBestEffort() {
      var sb = new System.Text.StringBuilder();
      uint[] attempts = {
        Continuous | System | Display | AwayMode,
        Continuous | System | AwayMode,
        Continuous | System
      };
      string[] names = { "sys+disp+away", "sys+away", "sys" };
      uint held = 0;
      for (int i = 0; i < attempts.Length; i++) {
        uint r = Native.SetThreadExecutionState(attempts[i]);
        sb.Append(names[i] + "=0x" + r.ToString("X8") + ";");
        if (r != 0) { held = r; break; }
      }
      sb.Append(" prev=0x" + held.ToString("X8") + " lasterr=" + Marshal.GetLastWin32Error() +
                "  (NOTE: nonzero return = SUCCESS; its value is the PREVIOUS state, 0 = failed attempt)");
      return sb.ToString();
    }
    public static uint Clear() { return Native.SetThreadExecutionState(Continuous); }
  }
}
'@

$darkFile = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\Awake\darkstate.json'
$script:dsLog = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\Awake\darkscreen.log'

function Log([string]$msg) { "$(Get-Date -Format 's') [pid $PID] $msg" | Add-Content -Path $script:dsLog -Encoding utf8 }
function Get-IdleSec {
    $li = New-Object U.LASTINPUTINFO
    $li.cbSize = [uint32]([System.Runtime.InteropServices.Marshal]::SizeOf($li))
    [void][U.Native]::GetLastInputInfo([ref]$li)
    $diff = ([uint32][Environment]::TickCount) - $li.dwTime
    return ($diff / 1000.0)
}
function Get-Journal {
    try { Get-Content $darkFile -Raw -ErrorAction Stop | ConvertFrom-Json } catch { $null }
}

# Flag math lives in C# (U.Es): pwsh 7.6.9 parses 0x80000000+ hex literals as
# NEGATIVE; and this OS rejects ES_DISPLAY_REQUIRED (err 87) - HoldBestEffort
# layers the attempts. Log the truth on every start (hidden process = no console).
$esResult = [U.Es]::HoldBestEffort()
Log "ES HOLD: $esResult"

# Subclassed form removed: [PSObject]::GetDerivedType does not exist in this
# runtime (pwsh 7.x). Lock/unlock witnessing uses SystemEvents.SessionSwitch
# below instead (needs no subclass); WTS registration kept as harmless bonus.
$f = New-Object System.Windows.Forms.Form
$f.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$f.StartPosition = 'Manual'
$f.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$f.BackColor = [System.Drawing.Color]::Black
$f.TopMost = $true
$f.ShowInTaskbar = $false

# State starts Active (screen is lit at process start); Add_Shown below calls
# Enter-Dark for the designed immediate blackout. (v3 bug: initialized $true
# without darkening - limbo state, hidden by awake.ps1's own brightness write.)
$script:isDark = $false
# Retirement runs off the state-file 'until' (renewal mailbox): awake.ps1's
# timed 'on' - the agent's mid-task renewal - pushes 'until' forward while we
# are alive, so a forgotten short window cannot retire the hold mid-job.
$script:until = if ($DurationMinutes -gt 0) { (Get-Date).AddMinutes($DurationMinutes) } else { $null }
$script:tick = 0

function Enter-Dark {
    # Pixel black only - no brightness write (OLED black pixels emit nothing).
    $script:isDark = $true
    $f.Show(); $f.TopMost = $true; [void]$f.Activate()
    [System.Windows.Forms.Cursor]::Hide()
}
function Enter-Active {
    $script:isDark = $false
    $f.Hide()
    [System.Windows.Forms.Cursor]::Show()
}

function Undo-All {
    [System.Windows.Forms.Cursor]::Show()
    [void][U.Es]::Clear()
    # v5: there is nothing else to restore - brightness and the ambient sensor
    # were never touched. Hidden process has no console; log for postmortems.
    Log 'EXIT: pixel-only run - no state to restore (cursor + ES released)'
    Remove-Item -Path $darkFile -Force -ErrorAction SilentlyContinue
}

$t = New-Object System.Windows.Forms.Timer
$t.Interval = 1000
$t.Add_Tick({
    $script:tick++
    # Renewal mailbox: re-read 'until' every ~15 s so an agent re-arm (awake.ps1
    # 'on <minutes>' pushing it forward) extends this hold without a restart.
    # Missing/broken file or absent 'until' => keep the current retirement time.
    # NOTE (root cause of the silent 2026-11-16 14:00 renewal failure):
    # ConvertFrom-Json returns the ISO 'until' as a DateTime whose "$u"
    # rendering goes through the CURRENT CULTURE ("11/16/2026 14:13:23") and
    # [datetime]::Parse then rejects it. Accept the typed DateTime as-is; only
    # genuine strings get an INVARIANT-culture parse. Never reintroduce
    # culture-dependent date handling here.
    if ($DurationMinutes -gt 0 -and $script:tick % 15 -eq 0) {
        try {
            $raw = (Get-Content $darkFile -Raw -ErrorAction Stop | ConvertFrom-Json).until
            if ($raw) {
                $nu = if ($raw -is [datetime]) { $raw } else { [datetime]::Parse("$raw", [System.Globalization.CultureInfo]::InvariantCulture) }
                if ($nu -ne $script:until) { Log "until mailbox: '$script:until' -> '$nu' (tick $script:tick)"; $script:until = $nu }
            }
        } catch { Log "until mailbox read failed at tick $script:tick : $($_.Exception.Message)" }
    }
    if ($DurationMinutes -gt 0 -and $script:until -and (Get-Date) -ge $script:until) { $f.Close(); return }
    $idle = Get-IdleSec
    if ($script:isDark -and $idle -lt 1) { Enter-Active }
    elseif (-not $script:isDark -and $idle -ge $IdleSeconds) { Enter-Dark }
    elseif ($script:isDark) { $f.TopMost = $true }
})

$f.Add_Shown({
    [System.Windows.Forms.Cursor]::Hide()
    # Session-state witness (no elevation, log only - the v4 lock-dim was
    # removed with the operator's 2026-11-16 decision; the witness stays
    # because it is the only reliable lock detector on this machine
    # (TUNING-NOTES: OpenInputDesktop lies here). If a spontaneous lock
    # precedes an idle-standby mystery, darkscreen.log catches it.
    [Microsoft.Win32.SystemEvents]::add_SessionSwitch({
        param($s, $e)
        "$(Get-Date -Format 's') WTS: $($e.Reason)" | Add-Content -Path $script:dsLog -Encoding utf8
    })
    try { [void][U.Native]::WTSRegisterSessionNotification($f.Handle, 0) } catch { }  # belt & braces; SystemEvents is the main hook

    # Journal BEFORE the blackout - awake.ps1 polls for it with our pid.
    [pscustomobject]@{
        pid = $PID; durationMinutes = $DurationMinutes; idleSeconds = $IdleSeconds
        until = $(if ($script:until) { $script:until.ToString('o') } else { $null })
    } | ConvertTo-Json -Compress | Set-Content -Path $darkFile -Encoding utf8
    Log 'START v5: pixel-only dark, no brightness/sensor involvement'

    Enter-Dark   # designed immediate blackout (pixels off from the first second)
    $t.Start()
})

[System.Windows.Forms.Application]::Run($f)
$t.Stop()
Undo-All
