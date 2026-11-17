# Dark screensaver for OLED - v6 (2026-11-17): PIXEL BLACK + INPUT KEEPALIVE.
# v6 root cause (measured 2026-11-17): the v5 mechanism was a fiction on this Win11
# S0ix build. ES_SYSTEM/ES_AWAYMODE do NOT prevent Modern Standby entry (Kernel-Power
# 506 fired repeatedly while a held overlay was armed), and the only flag that does,
# ES_DISPLAY_REQUIRED, is rejected here (err 87 - display power is OS-owned). Every
# unattended idle window ended in display blank + S0ix, and Windows SILENTLY KILLS
# background processes during those bursts - including the "protected" overlay
# (journal left behind, no EXIT line, no WER event; reproduced live with decoys).
# A fullscreen black window does NOT reset the input idle timers - pixels are not input.
# THE v6 FIX: while DARK, inject a no-op keypress (F15 down+up) every HeartbeatSeconds.
# Injected input DOES reset the display/sleep idle timers, so blank/S0ix never fire and
# the machine provably stays awake while the overlay is dark (LOCKED too - validated
# 2026-11-17: zero 506/566 past the blank boundary while locked). Human wake/dark cycle
# uses low-level input hooks that IGNORE injected events (LLMHF_INJECTED), so the
# heartbeat never wakes the overlay itself; if the hooks cannot be installed, the
# overlay stays dark until 'light' (never wakes on its own) rather than thrashing.
# The ES hold is kept (harmless; its away-mode grace only helps during FORCED standby,
# e.g. power-button events). Nothing still persists outside this process: the heartbeat
# stops with the process, a kill strands nothing - if the overlay dies the box sleeps
# within ~5 min, so journal staleness (journal=True alive=False) is now a reliable
# "protection is GONE" signal.
# PANEL TRUTH (hardware-verified 2026-11-17, WmiMonitorID SDC41B0 = Samsung
# ATNA33AA08-0): this is an OLED. Black pixels emit NO light - the pixel-black
# overlay is genuinely ZERO LIGHT at any brightness setting. A same-day "v7"
# experiment that floored the WMI backlight was REVERTED the same day: it bought
# nothing on OLED and WmiSetBrightness was measured to SYNC INTO THE PLAN
# (VIDEONORMALLEVEL followed a 53->0 write) - a stranded-dim-panel risk class we
# deliberately keep structurally impossible. NEVER re-add brightness/sensor
# writes to this overlay. Zero-light unattended posture = do NOT lock (the
# lock screen renders CONTENT, which lights pixels; nothing here can darken the
# secure desktop without brightness writes - see SKILL.md).
# Lid close / power button / forced sleep still kill this process no matter what.
param(
    [int]$IdleSeconds = 180,
    [int]$DurationMinutes = 0,
    [int]$HeartbeatSeconds = 30
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
    // NOTE v6: even a successful sys/away hold does NOT stop idle Modern
    // Standby on S0ix hardware - the real protection is Human.Nudge() below.
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
  // v6: human-input witness + no-op keepalive.
  public static class Human {
    delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    const uint LLMHF_INJECTED = 0x00000001, LLMHF_LOWER_IL_INJECTED = 0x00000002;
    // KBDLLHOOKSTRUCT.flags is at offset 8; MSLLHOOKSTRUCT.flags at offset 12.
    static IntPtr kHook = IntPtr.Zero, mHook = IntPtr.Zero;
    static LowLevelProc kProc, mProc;   // GC roots - never let the delegates die
    static long lastHumanTicks = Environment.TickCount64;

    static IntPtr KbHook(int nCode, IntPtr wParam, IntPtr lParam) {
      if (nCode >= 0 && (Marshal.ReadInt32(lParam, 8) & (int)(LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) == 0)
        lastHumanTicks = Environment.TickCount64;
      return CallNextHookEx(kHook, nCode, wParam, lParam);
    }
    static IntPtr MsHook(int nCode, IntPtr wParam, IntPtr lParam) {
      if (nCode >= 0 && (Marshal.ReadInt32(lParam, 12) & (int)(LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) == 0)
        lastHumanTicks = Environment.TickCount64;
      return CallNextHookEx(mHook, nCode, wParam, lParam);
    }
    // Must be called from the thread that pumps messages (the WinForms UI thread).
    public static bool InstallHooks() {
      if (kHook != IntPtr.Zero && mHook != IntPtr.Zero) return true;
      kProc = KbHook; mProc = MsHook;
      kHook = SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, kProc, IntPtr.Zero, 0);
      mHook = SetWindowsHookEx(14 /*WH_MOUSE_LL*/,  mProc, IntPtr.Zero, 0);
      return kHook != IntPtr.Zero || mHook != IntPtr.Zero;
    }
    public static double IdleSeconds { get { return (Environment.TickCount64 - lastHumanTicks) / 1000.0; } }

    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2;
    const ushort VK_F15 = 0x7C;   // effectively unbound everywhere; a true no-op
    // Injected input resets the system idle timers (that is the whole point of v6),
    // while the hooks above filter it out of the HUMAN idle clock via LLMHF_INJECTED.
    public static void Nudge() {
      INPUT[] a = new INPUT[2];
      a[0].type = INPUT_KEYBOARD; a[0].u.ki.wVk = VK_F15;
      a[1].type = INPUT_KEYBOARD; a[1].u.ki.wVk = VK_F15; a[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
      SendInput((uint)a.Length, a, Marshal.SizeOf(typeof(INPUT)));
    }
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
function Get-HumanIdleSec {
    # v6 primary clock: humans only (hooks filter injected events). The hook seeds
    # from process start; if hooks are dead we return -1 and callers fall back.
    if ($script:hookOk) { return [U.Human]::IdleSeconds }
    return -1.0
}
function Get-Journal {
    try { Get-Content $darkFile -Raw -ErrorAction Stop | ConvertFrom-Json } catch { $null }
}

# Flag math lives in C# (U.Es): pwsh 7.6.9 parses 0x80000000+ hex literals as
# NEGATIVE; and this OS rejects ES_DISPLAY_REQUIRED (err 87) - HoldBestEffort
# layers the attempts. Log the truth on every start (hidden process = no console).
# v6 note: this hold is secondary redundancy; Human.Nudge() is the real keep-awake.
$esResult = [U.Es]::HoldBestEffort()
Log "ES HOLD: $esResult"

$f = New-Object System.Windows.Forms.Form
$f.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$f.StartPosition = 'Manual'
$f.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$f.BackColor = [System.Drawing.Color]::Black
$f.TopMost = $true
$f.ShowInTaskbar = $false

# State starts Active (screen is lit at process start); Add_Shown below calls
# Enter-Dark for the designed immediate blackout.
$script:isDark = $false
$script:hookOk = $false
$script:nudges = 0
# Retirement runs off the state-file 'until' (renewal mailbox): awake.ps1's
# timed 'on' - the agent's mid-task renewal - pushes 'until' forward while we
# are alive, so a forgotten short window cannot retire the hold mid-job.
$script:until = if ($DurationMinutes -gt 0) { (Get-Date).AddMinutes($DurationMinutes) } else { $null }
$script:tick = 0

function Enter-Dark {
    # Pixel black only - no brightness write. On this VERIFIED OLED panel
    # (SDC41B0 = Samsung ATNA33AA08-0) black pixels emit no light: zero glow,
    # zero power from the overlay, no burn-in wear, at any brightness setting.
    $script:isDark = $true
    $f.Show(); $f.TopMost = $true; [void]$f.Activate()
    [System.Windows.Forms.Cursor]::Hide()
    Log "DARK (human idle $([int](Get-HumanIdleSec))s)"
}
function Enter-Active {
    $script:isDark = $false
    $f.Hide()
    [System.Windows.Forms.Cursor]::Show()
    Log 'WAKE (human input)'
}

function Undo-All {
    [System.Windows.Forms.Cursor]::Show()
    [void][U.Es]::Clear()
    # v6: heartbeat is just a timer - it stopped when we stopped; nothing to restore.
    Log "EXIT: dark run over - nudges=$($script:nudges) (cursor + ES released, no state to restore)"
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
    # v6 idle clock: human-only (heartbeat-proof). Dead hooks (-1) disable only
    # the auto-WAKE cycle - the keepalive must never depend on them (Nudge is
    # pure SendInput; protection outranks the idle cycle).
    $idle = Get-HumanIdleSec
    if ($idle -ge 0) {
        if ($script:isDark -and $idle -lt 1) { Enter-Active }
        elseif (-not $script:isDark -and $idle -ge $IdleSeconds) { Enter-Dark }
    }
    if ($script:isDark) {
        $f.TopMost = $true
        # v6 keepalive: while DARK, nudge the system idle timers so Windows never
        # blanks the display / enters Modern Standby (which silently kills us).
        if ($HeartbeatSeconds -gt 0 -and $script:tick % $HeartbeatSeconds -eq 0) {
            [U.Human]::Nudge()
            $script:nudges++
            Log "KEEPALIVE #$($script:nudges) F15 injected (human idle $([int]$idle)s)"
        }
    }
})

$f.Add_Shown({
    [System.Windows.Forms.Cursor]::Hide()
    $script:hookOk = [bool][U.Human]::InstallHooks()
    # Session-state witness (no elevation, log only). The witness stays because it
    # is the only reliable lock detector on this machine (TUNING-NOTES:
    # OpenInputDesktop lies here). If a spontaneous lock precedes a mystery death,
    # darkscreen.log catches it. (Keepalive also holds LOCKED sessions - validated
    # 2026-11-17 - so a lock never silently costs protection.)
    [Microsoft.Win32.SystemEvents]::add_SessionSwitch({
        param($s, $e)
        "$(Get-Date -Format 's') WTS: $($e.Reason)" | Add-Content -Path $script:dsLog -Encoding utf8
    })
    try { [void][U.Native]::WTSRegisterSessionNotification($f.Handle, 0) } catch { }  # belt & braces; SystemEvents is the main hook

    # Journal BEFORE the blackout - awake.ps1 polls for it with our pid.
    [pscustomobject]@{
        pid = $PID; durationMinutes = $DurationMinutes; idleSeconds = $IdleSeconds
        heartbeatSeconds = $HeartbeatSeconds; humanHook = $script:hookOk
        until = $(if ($script:until) { $script:until.ToString('o') } else { $null })
    } | ConvertTo-Json -Compress | Set-Content -Path $darkFile -Encoding utf8
    Log "START v6: pixel dark + F15 keepalive every ${HeartbeatSeconds}s (human hook=$($script:hookOk))"
    if (-not $script:hookOk) { Log 'WARNING: human-input hooks NOT installed - overlay stays dark until light (no self-wake)' }

    Enter-Dark   # designed immediate blackout (pixels off from the first second)
    $t.Start()
})

[System.Windows.Forms.Application]::Run($f)
$t.Stop()
Undo-All
