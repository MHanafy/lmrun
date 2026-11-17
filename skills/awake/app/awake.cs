#:sdk Microsoft.NET.Sdk
#:property UseWindowsForms=true
#:property TargetFramework=net10.0-windows
#:property Nullable=enable
#:property PublishAot=false
#:property PublishTrimmed=false
// awake.cs - single-file successor to darkscreen.ps1 + awake.ps1 (v6 doctrine, run:
//   dotnet run awake.cs -- <verb> [options]        (verbs: start stop status renew-wait help;
//                                                        'run' is the internal detached worker)
//
// WHY (measured doctrine - full history in ../SKILL.md and ../TUNING-NOTES.md):
// - Modern Standby (S0ix) box: idle -> display blank -> S0ix, and Windows SILENTLY KILLS
//   background processes during those episodes. ES holds do NOT stop idle standby here and
//   ES_DISPLAY_REQUIRED is rejected (err 87). THE protection is injecting a no-op F15
//   keypress: injected input resets the OS idle timers so blank/standby never engage.
//   LL hooks filter LLMHF_INJECTED so the heartbeat never counts as human presence.
// - v6.2: the worker is spawned via WMI (Win32_Process.Create -> parent WmiPrvSE) so it lives
//   OUTSIDE the caller's console/job/process tree: the silent deaths previously blamed only on
//   standby were mostly agent-harness tree-reaping taking workers whose 'start' shell ended
//   (forensics 2026-11-18 - see SpawnDetached).
// - v6.1 change vs the ps1: the heartbeat runs for the WHOLE process lifetime, not only
//   while dark - closes the Active-phase race (DC blank is 180 s; the first dark-phase
//   nudge could lose to it).
// - Panel is a hardware-verified OLED (Samsung ATNA33AA08-0 / SDC41B0): black pixels emit
//   NO light, so the black overlay is genuinely ZERO light. NEVER add brightness or
//   ambient-sensor writes: WmiSetBrightness SYNCs into the power plan on this build
//   (stranded-dim-panel risk, proven 2026-11-17). Permanently out by operator rule.
// - PowerToys Awake dependency is GONE. 'keeper' mode (start without --dark) IS the
//   keep-awake. All files live in the app's OWN folder: %LOCALAPPDATA%\Awake\
//   (keeper.json = control mailbox, awake.log = journal/forensics, renewtimer.json).
//   Nothing PowerToys-related is read or written anymore.
// - Mailbox control: 'start' creates the control file (or EXTENDS a live worker - it never
//   double-arms and never re-blanks as a renewal); 'stop' just flips intent=stop in the
//   file and the worker exits CLEANLY within ~1 s (force-kill only after 5 s of silence);
//   the worker polls the file every second. Control file present + pid dead = protection is
//   GONE (reliable alarm). Nothing persists after a clean stop.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { Cli.Help(); return 0; }
            var o = Opts.Parse(args);
            return o.Verb switch
            {
                "start" => Cli.Start(o),
                "stop" => Cli.Stop(o),
                "status" => Cli.Status(o),
                "renew-wait" => Cli.RenewWait(o),
                "run" => Worker.Run(o),
                "help" => Cli.Help(),
                _ => Err(o),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("awake: " + e.Message);
            return 1;
        }
    }
    static int Err(Opts o) { Console.Error.WriteLine($"unknown verb '{o.Verb}'"); Cli.Help(); return 2; }
}

sealed class Opts
{
    public string Verb = "";
    public int Minutes, Idle = 180, Heartbeat = 30;
    public bool Dark, Json, NoTray;
    public string StateDir = DefaultDir();

    public static string DefaultDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Awake");

    public static Opts Parse(string[] args)
    {
        var o = new Opts { Verb = args[0].ToLowerInvariant() };
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            switch (a)
            {
                case "--minutes" or "-m": o.Minutes = int.Parse(args[++i]); break;
                case "--idle": o.Idle = int.Parse(args[++i]); break;
                case "--heartbeat": o.Heartbeat = int.Parse(args[++i]); break;
                case "--dark": o.Dark = true; break;
                case "--json": o.Json = true; break;
                case "--no-tray": o.NoTray = true; break;
                case "--state-dir": o.StateDir = Path.GetFullPath(args[++i]); break;
                default: throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }
        if ((o.Verb == "start" || o.Verb == "run" || o.Verb == "renew-wait") && (o.Minutes < 1 || o.Minutes > 1440))
            throw new ArgumentException("--minutes 1..1440 is required for start/run/renew-wait");
        if (o.Idle < 15 || o.Idle > 3600) throw new ArgumentException("--idle must be 15..3600");
        if (o.Heartbeat < 5 || o.Heartbeat > 300) throw new ArgumentException("--heartbeat must be 5..300");
        return o;
    }
}

sealed class KeeperState
{
    [JsonPropertyName("pid")] public int Pid;
    [JsonPropertyName("mode")] public string Mode = "keeper";        // "dark" | "keeper"
    [JsonPropertyName("intent")] public string Intent = "run";       // "run" | "stop"
    [JsonPropertyName("idleSeconds")] public int IdleSeconds;
    [JsonPropertyName("heartbeatSeconds")] public int HeartbeatSeconds;
    [JsonPropertyName("minutes")] public int Minutes;
    [JsonPropertyName("until")] public string? Until;
    [JsonPropertyName("seq")] public long Seq;
    [JsonPropertyName("updatedAt")] public string UpdatedAt = "";
    [JsonPropertyName("startedAt")] public string StartedAt = "";
    [JsonPropertyName("phase")] public string Phase = "lit";          // worker mirror: "dark" | "lit"
    [JsonPropertyName("nudges")] public int Nudges;
    [JsonPropertyName("humanHook")] public bool HumanHook;
}

internal static class Keeper
{
    public static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web) { IncludeFields = true, WriteIndented = false };

    public static string StatePath(Opts o) => Path.Combine(o.StateDir, "keeper.json");
    public static string LogPath(Opts o) => Path.Combine(o.StateDir, "awake.log");
    public static string BoxPath(Opts o) => Path.Combine(o.StateDir, "renewtimer.json");

    public static KeeperState? Load(Opts o)
    {
        try { return File.Exists(StatePath(o)) ? JsonSerializer.Deserialize<KeeperState>(File.ReadAllText(StatePath(o)), J) : null; }
        catch { return null; }
    }

    public static void Save(Opts o, KeeperState st)
    {
        Directory.CreateDirectory(o.StateDir);
        string tmp = StatePath(o) + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(st, J));
        File.Move(tmp, StatePath(o), overwrite: true);
    }

    public static void Log(Opts o, string msg)
    {
        try
        {
            Directory.CreateDirectory(o.StateDir);
            File.AppendAllText(LogPath(o), $"{DateTime.Now.ToString("s", CultureInfo.InvariantCulture)} [pid {Environment.ProcessId}] {msg}\n");
        }
        catch { /* logging must never take the worker down */ }
    }

    // pid + start-time match (immune to pid reuse)
    public static int? AlivePid(KeeperState? st)
    {
        if (st == null || st.Pid <= 4) return null;   // 0/4 would be the System Idle process
        try
        {
            var p = Process.GetProcessById(st.Pid);
            if (!string.IsNullOrEmpty(st.StartedAt))
            {
                try
                {
                    var s = DateTimeOffset.Parse(st.StartedAt, CultureInfo.InvariantCulture);
                    if (Math.Abs((p.StartTime - s.LocalDateTime).TotalSeconds) > 5) return null;
                }
                catch { return null; }
            }
            return st.Pid;
        }
        catch { return null; }
    }

    public static bool KillRenewTimer(Opts o)
    {
        try
        {
            if (!File.Exists(BoxPath(o))) return false;
            var b = JsonSerializer.Deserialize<KeeperState>(File.ReadAllText(BoxPath(o)), J); // pid field reuse
            bool killed = false;
            if (b != null) { try { Process.GetProcessById(b.Pid).Kill(true); killed = true; } catch { } }
            File.Delete(BoxPath(o));
            return killed;
        }
        catch { return false; }
    }

    public static string MutexName(Opts o)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(o.StateDir.ToLowerInvariant()));
        return "Local\\awake_" + Convert.ToHexString(h, 0, 8);
    }
}
internal static class Cli
{
    public static int Help()
    {
        Console.WriteLine(
"""
awake - keep-awake + zero-light dark overlay (files: %LOCALAPPDATA%\Awake\)
  start --minutes N [--dark] [--idle S] [--heartbeat S]
        arm protection. --dark = pixel-black overlay (OLED: zero light) that wakes on
        human input and re-blanks after --idle (180) s; without --dark = lit keeper.
        The F15 heartbeat runs for the WHOLE process lifetime - it is the keep-awake.
        Re-running start while live = EXTEND the window (never re-blanks; pass --dark
        only if you also want to turn the overlay ON on a live keeper).
  stop  cooperative stop: flips the mailbox; the worker exits itself (force-kill after 5 s).
        The worker also shows a tray icon (status tooltip + Stop + Open log) while running;
        --no-tray hides it. In dark phase the overlay covers the taskbar - any human input
        wakes it first, THEN the tray is reachable.
  status [--json]
  renew-wait --minutes N   sleep (N-5) min then exit - run as agent background job; its
        completion is the agent's renewal alarm. Renew with 'start --minutes N' (NO --dark).
THE TOOL: published FRAMEWORK-DEPENDENT at <source-dir>\\bin\\awake.exe (~200 KB; needs the
.NET 10 Windows Desktop runtime). Rebuild from the source folder: dotnet publish awake.cs -c Release -o bin
(Native AOT deliberately NOT used: WinForms is unsupported by AOT/trimming; self-contained
single-file skipped per operator: extraction/startup overhead.) During development,
'dotnet run awake.cs' also works (it spawns the worker from an installed runtime copy).
""");
        return 0;
    }

    public static int Start(Opts o)
    {
        Directory.CreateDirectory(o.StateDir);
        var st = Keeper.Load(o);
        int? alive = Keeper.AlivePid(st);

        if (alive != null && st != null)
        {
            string oldUntil = st.Until ?? "";
            st.Until = DateTimeOffset.Now.AddMinutes(o.Minutes).ToString("o", CultureInfo.InvariantCulture);
            if (o.Dark) st.Mode = "dark";
            st.Intent = "run";
            st.Seq++;
            st.UpdatedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            Keeper.Save(o, st);
            Keeper.Log(o, $"[cli] until pushed '{oldUntil}' -> '{st.Until}' seq={st.Seq} mode={st.Mode}");
            Console.WriteLine($"Already RUNNING (pid {alive}, mode {st.Mode}) - EXTENDED until {st.Until} " +
                              "(a live worker is never re-blanked; the mailbox extends it).");
            Console.WriteLine($"Renewal loop: when the renew-wait job fires and work continues, re-run THIS command WITHOUT --dark. Finished: 'stop'.");
            return 0;
        }

        if (st != null)
        {
            try { File.Delete(Keeper.StatePath(o)); } catch { }
            Keeper.Log(o, "[cli] stale control file cleared (previous worker died - protection WAS gone)");
            Console.WriteLine("Cleared stale control file (previous worker died - that is the 'protection is GONE' alarm).");
        }
        Keeper.KillRenewTimer(o); // a fresh cycle replaces any pending renewal tick

        // The worker must NOT run from a 'dotnet run' temp build (a running image locks its
        // files and breaks every later rebuild) - in that case install a stable copy into
        // <state-dir>\runtime\ and spawn it. A PUBLISHED exe spawns itself directly.
        string exe;
        if (AppContext.BaseDirectory.Replace('/', '\\').Contains(@"\dotnet\runfile\"))
        {
            string rtDir = Path.Combine(o.StateDir, "runtime");
            Directory.CreateDirectory(rtDir);
            foreach (var src in Directory.GetFiles(AppContext.BaseDirectory, "awake.*"))
            {
                string dst = Path.Combine(rtDir, Path.GetFileName(src));
                for (int tryN = 0; ; tryN++)
                {
                    try { File.Copy(src, dst, overwrite: true); break; }
                    catch (IOException) when (tryN < 10) { Thread.Sleep(300); }   // previous worker image may linger
                }
            }
            exe = Path.Combine(rtDir, "awake.exe");
        }
        else exe = Environment.ProcessPath ?? "awake.exe";

        var workerArgs = new[] { "run", "--minutes", o.Minutes.ToString(), "--idle", o.Idle.ToString(),
                                 "--heartbeat", o.Heartbeat.ToString(), "--state-dir", o.StateDir };
        if (o.Dark) workerArgs = workerArgs.Append("--dark").ToArray();
        if (o.NoTray) workerArgs = workerArgs.Append("--no-tray").ToArray();
        string spawnedVia = SpawnDetached(exe, workerArgs);
        Keeper.Log(o, $"[cli] worker spawned via {spawnedVia}");

        for (int i = 0; i < 60; i++)
        {
            Thread.Sleep(250);
            var c = Keeper.Load(o);
            if (c != null && Keeper.AlivePid(c) != null)
            {
                string mode = c.Mode == "dark" ? "dark overlay + keeper" : "lit keeper (no window)";
                Console.WriteLine($"STARTED (pid {c.Pid}, {mode}, spawned via {spawnedVia}): F15 heartbeat every {c.HeartbeatSeconds} s " +
                                  $"for the WHOLE run holds off Modern Standby; retires {c.Until}.");
                if (c.Mode == "dark")
                    Console.WriteLine($"Overlay: pixel black NOW (OLED: zero light; brightness/sensor untouched), " +
                                      $"human input wakes, re-blanks after {c.IdleSeconds} s real idle.");
                string rt = Environment.ProcessPath ?? "awake.exe";
                Console.WriteLine($"Renewal loop: start as a background job NOW: \"{rt}\" renew-wait --minutes {o.Minutes} ; " +
                                  "when it fires: still working => re-run 'start --minutes " + o.Minutes + "' WITHOUT --dark; finished => 'stop'. " +
                                  "NEVER re-run 'start --dark' as a renewal (it turns the overlay ON while you may be at the machine).");
                return 0;
            }
        }
        Console.Error.WriteLine("worker did not claim the control file within 15 s - check awake.log");
        return 1;
    }

    // Spawn the worker OUTSIDE the caller's process tree. 2026-11-18 forensics: workers started
    // from agent harness shells died silently mid-heartbeat when the harness reaped that shell's
    // process tree (taskkill /F /T or kill-on-close job) - OS running, zero log events, stale
    // control file left behind (6 deaths overnight 2026-11-17/18, all in agent-session windows).
    // A plain child - even Hidden + ShellExecute - shares the caller's console/job AND its parent
    // link, so it dies with the tree. WMI Win32_Process.Create spawns under WmiPrvSE: different
    // parent, different job, no console. Falls back to the old in-tree ShellExecute spawn if
    // WbemScripting/WMI is unavailable (logged, so the journal always shows which path was used).
    static string SpawnDetached(string exe, string[] args)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (t != null)
            {
                dynamic locator = Activator.CreateInstance(t)!;
                dynamic svc = locator.ConnectServer(".", "root\\cimv2");
                dynamic wmiProc = svc.Get("Win32_Process");
                string cmd = "\"" + exe + "\" " + string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
                // The exe is console-subsystem (the CLI needs stdout); WMI would hand it a VISIBLE
                // allocated console (seen 2026-11-18) - STARTF_USESHOWWINDOW/SW_HIDE keeps it off-screen.
                dynamic startup = svc.Get("Win32_ProcessStartup").SpawnInstance_();
                startup.ShowWindow = 0; // SW_HIDE
                object? r = wmiProc.Create(cmd, Path.GetDirectoryName(exe), startup, null); // dispatch returns ReturnValue
                if (r == null || Convert.ToInt32(r, CultureInfo.InvariantCulture) == 0) return "wmi (out-of-tree, hidden)";
            }
        }
        catch { }
        var psi = new ProcessStartInfo { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, FileName = exe };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
        return "shell (in-tree FALLBACK - WMI unavailable)";
    }

    public static int Stop(Opts o)
    {
        bool timerKilled = Keeper.KillRenewTimer(o);
        var st = Keeper.Load(o);
        if (st == null)
        {
            Console.WriteLine(timerKilled ? "Nothing was armed (pending renewal timer terminated)."
                                          : "Not running (no control file, nothing to stop).");
            return 0;
        }
        int? alive = Keeper.AlivePid(st);
        if (alive == null)
        {
            try { File.Delete(Keeper.StatePath(o)); } catch { }
            Keeper.Log(o, "[cli] stale control file cleaned by stop");
            Console.WriteLine("Was already dead (stale control file cleaned)." + (timerKilled ? " Pending renewal timer terminated." : ""));
            return 0;
        }

        st.Intent = "stop";
        st.Seq++;
        st.UpdatedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        Keeper.Save(o, st);
        Keeper.Log(o, $"[cli] stop requested via mailbox seq={st.Seq}");

        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(250);
            var c = Keeper.Load(o);
            if (c == null || Keeper.AlivePid(c) == null)
            {
                Console.WriteLine("Stopped cleanly (worker acknowledged the mailbox, EXIT logged)." +
                                  (timerKilled ? " Pending renewal timer terminated." : ""));
                return 0;
            }
        }
        try { var p = Process.GetProcessById(st.Pid); p.Kill(true); p.WaitForExit(3000); } catch { }
        try { if (File.Exists(Keeper.StatePath(o))) File.Delete(Keeper.StatePath(o)); } catch { }
        Keeper.Log(o, $"[cli] FORCED stop pid={st.Pid} (mailbox not acknowledged within 5 s)");
        Console.WriteLine("Stopped FORCED (worker did not acknowledge the mailbox within 5 s)." +
                          (timerKilled ? " Pending renewal timer terminated." : ""));
        return 0;
    }

    public static int Status(Opts o)
    {
        var st = Keeper.Load(o);
        int? alive = Keeper.AlivePid(st);
        if (o.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new StatusOut { State = st, Alive = alive != null }, Keeper.J));
            return 0;
        }
        if (st == null) { Console.WriteLine("Awake: OFF - normal sleep behavior (idle will blank + suspend)"); return 0; }
        if (alive == null)
        {
            Console.WriteLine($"Awake: DEAD - control file names pid {st.Pid} but it is GONE - PROTECTION IS GONE. " +
                              "Re-arm with 'start' (it clears the stale file).");
            return 0;
        }
        string mode = st.Mode == "dark"
            ? $"DARK overlay cycle (phase {st.Phase}, re-blank at {st.IdleSeconds} s human idle)"
            : "lit keeper (no window)";
        string hook = st.HumanHook ? "human hook OK" : "WARNING: human hook dead (overlay would stay dark)";
        Console.WriteLine($"Awake: RUNNING (pid {st.Pid}) - lifetime F15 keepalive every {st.HeartbeatSeconds} s " +
                          $"(nudges so far {st.Nudges}), {mode}, {hook}, retiring {st.Until}.");
        return 0;
    }

    sealed class StatusOut { public KeeperState? State { get; set; } public bool Alive { get; set; } }

    public static int RenewWait(Opts o)
    {
        int span = Math.Max(1, o.Minutes - 5);
        var box = new KeeperState { Pid = Environment.ProcessId, Minutes = o.Minutes, UpdatedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture) };
        Directory.CreateDirectory(o.StateDir);
        File.WriteAllText(Keeper.BoxPath(o), JsonSerializer.Serialize(box, Keeper.J));
        Thread.Sleep(span * 60_000);
        try
        {
            var b = JsonSerializer.Deserialize<KeeperState>(File.ReadAllText(Keeper.BoxPath(o)), Keeper.J);
            if (b != null && b.Pid == Environment.ProcessId)
            {
                b.UpdatedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                File.WriteAllText(Keeper.BoxPath(o), JsonSerializer.Serialize(b, Keeper.J));
            }
        }
        catch { }
        Console.WriteLine($"renewal tick: the armed window expires in ~5 min - still working => 'start --minutes {o.Minutes}' (NO --dark); finished => 'stop'.");
        return 0;
    }
}
internal static class Native
{
    // ---- execution state (harmless redundancy on this box - the heartbeat is the real hold) ----
    static class EsNative
    {
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint f);
    }
    public static class Es
    {
        const uint Continuous = 0x80000000, System = 0x00000002, Display = 0x00000004, AwayMode = 0x00000040;
        public static string HoldBestEffort()
        {
            var sb = new StringBuilder();
            uint[] attempts = { Continuous | System | Display | AwayMode, Continuous | System | AwayMode, Continuous | System };
            string[] names = { "sys+disp+away", "sys+away", "sys" };
            uint held = 0;
            for (int i = 0; i < attempts.Length; i++)
            {
                uint r = EsNative.SetThreadExecutionState(attempts[i]);
                sb.Append(names[i] + "=0x" + r.ToString("X8") + ";");
                if (r != 0) { held = r; break; }   // nonzero = SUCCESS (value is the PREVIOUS state)
            }
            sb.Append(" prev=0x" + held.ToString("X8") + " lasterr=" + Marshal.GetLastWin32Error());
            return sb.ToString();
        }
        public static void Clear() => EsNative.SetThreadExecutionState(Continuous);
    }

    // ---- human-only idle clock + no-op keepalive (verbatim v6 mechanism) ----
    public static class Human
    {
        delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint numDevices, uint size);

        // Touch/pen digitizers BYPASS the LL keyboard/mouse hooks (and touch's mouse-emulation
        // is LLMHF_INJECTED-filtered like our own heartbeat), so raw input is the only
        // system-wide human-touch signal. RIDEV_INPUTSINK delivers to the sink window even
        // when it is hidden/not foreground (lit phase included). Added 2026-11-18: touch on
        // the dark overlay now wakes it exactly like keyboard/mouse.
        [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }
        const uint RIDEV_INPUTSINK = 0x00000100;
        public static bool InstallTouchSink(IntPtr hwndTarget)
        {
            var rid = new RAWINPUTDEVICE[]
            {
                new() { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwndTarget }, // HID_DIGITIZER_TOUCH_SCREEN
                new() { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwndTarget }, // HID_DIGITIZER_PEN
            };
            return RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        }
        public static void Touch() => lastHumanTicks = Environment.TickCount64;

        const uint LLMHF_INJECTED = 0x00000001, LLMHF_LOWER_IL_INJECTED = 0x00000002;
        static IntPtr kHook = IntPtr.Zero, mHook = IntPtr.Zero;
        static LowLevelProc? kProc, mProc;   // GC roots - never let the delegates die
        static long lastHumanTicks = Environment.TickCount64;

        static IntPtr KbHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (Marshal.ReadInt32(lParam, 8) & (int)(LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) == 0)
                lastHumanTicks = Environment.TickCount64;
            return CallNextHookEx(kHook, nCode, wParam, lParam);
        }
        static IntPtr MsHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (Marshal.ReadInt32(lParam, 12) & (int)(LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) == 0)
                lastHumanTicks = Environment.TickCount64;
            return CallNextHookEx(mHook, nCode, wParam, lParam);
        }
        // Must run on the thread that pumps messages (the UI thread).
        public static bool InstallHooks()
        {
            if (kHook != IntPtr.Zero && mHook != IntPtr.Zero) return true;
            kProc = KbHook; mProc = MsHook;
            kHook = SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, kProc!, IntPtr.Zero, 0);
            mHook = SetWindowsHookEx(14 /*WH_MOUSE_LL*/, mProc!, IntPtr.Zero, 0);
            return kHook != IntPtr.Zero || mHook != IntPtr.Zero;
        }
        public static double IdleSeconds => (Environment.TickCount64 - lastHumanTicks) / 1000.0;

        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
        const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2;
        const ushort VK_F15 = 0x7C;   // effectively unbound everywhere; a true no-op

        public static void Nudge()
        {
            INPUT[] a = new INPUT[2];
            a[0].type = INPUT_KEYBOARD; a[0].u.ki.wVk = VK_F15;
            a[1].type = INPUT_KEYBOARD; a[1].u.ki.wVk = VK_F15; a[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput((uint)a.Length, a, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}

sealed class Overlay : Form
{
    const int WM_INPUT = 0x00FF;
    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = (Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault())?.Bounds ?? SystemInformation.VirtualScreen;
        BackColor = Color.Black;
        TopMost = true;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
    }
    // any WM_INPUT from the registered digitizer devices = a human touched the screen
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT) Native.Human.Touch();
        base.WndProc(ref m);
    }
}

internal static class Worker
{
    public static int Run(Opts o)
    {
        using var mtx = new Mutex(false, Keeper.MutexName(o));
        if (!mtx.WaitOne(0)) { Keeper.Log(o, "RUN refused: another worker owns this instance"); return 2; }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var now = DateTimeOffset.Now;
        var st = new KeeperState
        {
            Pid = Environment.ProcessId,
            Mode = o.Dark ? "dark" : "keeper",
            Intent = "run",
            IdleSeconds = o.Idle,
            HeartbeatSeconds = o.Heartbeat,
            Minutes = o.Minutes,
            Until = now.AddMinutes(o.Minutes).ToString("o", CultureInfo.InvariantCulture),
            Seq = 0,
            UpdatedAt = now.ToString("o", CultureInfo.InvariantCulture),
            StartedAt = now.ToString("o", CultureInfo.InvariantCulture),
        };
        // NOTE (2026-11-17): the claiming Save must happen AFTER hook install (below) -
        // it used to run first, and the CLI polled the claim inside that window, printing
        // a false "human hook dead" warning for a perfectly healthy worker.
        Keeper.Log(o, "ES HOLD: " + Native.Es.HoldBestEffort());

        var f = new Overlay();
        var ctx = new ApplicationContext(f);
        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        bool hookOk = false, isDark = false, shutting = false;
        int tick = 0, nudges = 0;
        long lastSeq = -1;
        TrayUi? tray = null;

        void EnterDark()
        {
            if (shutting) return;
            isDark = true;
            f.Show(); f.TopMost = true; f.Activate();
            Cursor.Hide();
            Keeper.Log(o, $"DARK (human idle {HumanIdleLabel()})");
        }
        void EnterLit()
        {
            isDark = false;
            f.Hide();
            Cursor.Show();
            Keeper.Log(o, "WAKE (human input)");
        }
        string HumanIdleLabel() => hookOk ? $"{(int)Native.Human.IdleSeconds}s" : "hooks-dead";
        string RetireLabel()
        {
            try { return DateTimeOffset.Parse(st.Until!, CultureInfo.InvariantCulture).ToString("HH:mm"); } catch { return "?"; }
        }
        void Cleanup(string why)
        {
            if (shutting) return;
            shutting = true;
            t.Stop();
            try { tray?.Dispose(); } catch { } tray = null;
            Cursor.Show();
            Native.Es.Clear();
            try { var c = Keeper.Load(o); if (c == null || c.Pid == Environment.ProcessId) File.Delete(Keeper.StatePath(o)); } catch { }
            Keeper.Log(o, $"EXIT: {why} - nudges={nudges} (cursor + ES released, control file removed)");
            ctx.ExitThread();
        }

        Microsoft.Win32.SystemEvents.SessionSwitch += (s, e) => Keeper.Log(o, "WTS: " + e.Reason);
        f.FormClosing += (s, e) => { if (!shutting) { Cleanup("window closed"); e.Cancel = true; } };

        t.Tick += (s, e) =>
        {
            if (shutting) return;
            tick++;

            // mailbox (every second): the control file is the command channel
            KeeperState? ex = null;
            bool fileGone = false;
            try
            {
                if (File.Exists(Keeper.StatePath(o))) ex = Keeper.Load(o);
                else fileGone = true;
            }
            catch (Exception mx) { Keeper.Log(o, $"mailbox read failed (tick {tick}): {mx.Message}"); }
            if (fileGone || ex == null) { Cleanup("control file removed (treated as stop)"); return; }

            if (ex.Seq != lastSeq)
            {
                lastSeq = ex.Seq;
                if (string.Equals(ex.Intent, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    Cleanup("stop requested via mailbox");
                    return;
                }
                if (!string.Equals(ex.Until, st.Until, StringComparison.Ordinal))
                {
                    Keeper.Log(o, $"until mailbox: '{st.Until}' -> '{ex.Until}' (tick {tick})");
                    st.Until = ex.Until;
                }
                if (ex.HeartbeatSeconds >= 5 && ex.HeartbeatSeconds != st.HeartbeatSeconds)
                {
                    Keeper.Log(o, $"heartbeat mailbox: {st.HeartbeatSeconds} -> {ex.HeartbeatSeconds}");
                    st.HeartbeatSeconds = ex.HeartbeatSeconds;
                }
                if (ex.IdleSeconds >= 15 && ex.IdleSeconds != st.IdleSeconds) st.IdleSeconds = ex.IdleSeconds;
                if (ex.Mode != st.Mode)
                {
                    Keeper.Log(o, $"mode mailbox: '{st.Mode}' -> '{ex.Mode}'");
                    st.Mode = ex.Mode;
                    if (st.Mode == "dark") EnterDark(); else EnterLit();
                }
            }

            if (!string.IsNullOrEmpty(st.Until) &&
                DateTimeOffset.Now >= DateTimeOffset.Parse(st.Until, CultureInfo.InvariantCulture))
            {
                Cleanup("retired (window ended)");
                return;
            }

            // v6.1: LIFETIME heartbeat - protection is decoupled from the dark/active cycle
            if (st.HeartbeatSeconds > 0 && tick % st.HeartbeatSeconds == 0)
            {
                Native.Human.Nudge();
                nudges++;
                Keeper.Log(o, $"KEEPALIVE #{nudges} F15 injected (human idle {HumanIdleLabel()}, phase {(isDark ? "dark" : "lit")})");
            }

            // dark/active cycle (dark mode only; dead hooks => stay dark, never thrash)
            double idle = hookOk ? Native.Human.IdleSeconds : -1;
            if (st.Mode == "dark" && idle >= 0)
            {
                if (isDark && idle < 1) EnterLit();
                else if (!isDark && idle >= st.IdleSeconds) EnterDark();
            }
            if (isDark) f.TopMost = true;

            if (tick % 10 == 0)   // light status mirror for 'status' (own seq stays)
            {
                st.Phase = isDark ? "dark" : "lit";
                st.Nudges = nudges;
                st.UpdatedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                try { Keeper.Save(o, st); } catch { }
                tray?.Update(isDark);
            }
        };

        // bootstrap on the UI thread (form handle exists once we touch it; keeper never shows it)
        var _handle = f.Handle;
        hookOk = Native.Human.InstallHooks();
        st.HumanHook = hookOk;
        bool touchOk = Native.Human.InstallTouchSink(_handle);
        Keeper.Save(o, st);
        Keeper.Log(o, $"START cs v1: {(o.Dark ? "pixel dark + " : "keeper, ")}LIFETIME F15 keepalive every {o.Heartbeat}s (human hook={hookOk}, touch sink={touchOk})");
        if (!hookOk) Keeper.Log(o, "WARNING: human-input hooks NOT installed - overlay stays dark until stop (no self-wake)");
        if (!touchOk) Keeper.Log(o, "WARNING: touch sink NOT registered - touch will NOT wake the overlay");
        if (st.Mode == "dark") EnterDark();
        t.Start();

        if (!o.NoTray)
            try
            {
                tray = new TrayUi(Keeper.LogPath(o),
                    () => $"awake - {(st.Mode == "dark" ? "dark+keeper" : "keeper")} - {(isDark ? "DARK" : "lit")} - nudge #{nudges} - retires {RetireLabel()}",
                    () => Cleanup("stop requested from tray"));
                tray.Balloon($"Armed ({st.Mode}): F15 keep-awake live, retires {RetireLabel()}. Stop from this menu.");
            }
            catch (Exception tx) { Keeper.Log(o, $"tray unavailable: {tx.Message}"); tray = null; }

        Application.Run(ctx);
        Cleanup("message loop ended");
        return 0;
    }
}

// Tray icon for the live worker: at-a-glance status (tooltip + menu header) and a
// one-click cooperative stop, so the console can stay hidden. Pure notification - it
// touches NONE of the protection path (heartbeat/hooks/mailbox unchanged); Stop reuses
// the exact cooperative Cleanup() the CLI mailbox uses. CAVEAT (by design): during the
// dark phase the overlay covers the taskbar (zero-light doctrine), so the tray is only
// clickable after human input wakes the overlay - the CLI 'stop' always works. A hard-
// killed worker's icon ghosts in the tray until hovered - standard Windows behavior.
sealed class TrayUi : IDisposable
{
    readonly NotifyIcon icon;
    readonly ToolStripMenuItem header;
    readonly ContextMenuStrip menu;
    readonly Icon iconDark, iconLit;
    readonly Func<string> statusText;

    public TrayUi(string logPath, Func<string> statusText, Action stop)
    {
        this.statusText = statusText;
        iconDark = MakeIcon(filled: true);   // dark phase: filled disc
        iconLit = MakeIcon(filled: false);   // lit/keeper: open ring
        header = new ToolStripMenuItem(statusText()) { Enabled = false };
        var openLog = new ToolStripMenuItem("Open log", null, (s, e) =>
        {
            try { Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true }); } catch { }
        });
        var stopItem = new ToolStripMenuItem("Stop awake", null, (s, e) => stop());
        menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { header, new ToolStripSeparator(), openLog, new ToolStripSeparator(), stopItem });
        icon = new NotifyIcon { Icon = iconLit, Text = "awake", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (s, e) => Balloon(statusText());
    }

    public void Update(bool isDark)
    {
        try
        {
            string s = statusText();
            icon.Text = s.Length > 127 ? s[..127] : s;  // NotifyIcon hard limit
            header.Text = s;
            icon.Icon = isDark ? iconDark : iconLit;
        }
        catch { }
    }

    public void Balloon(string text)
    {
        try { icon.ShowBalloonTip(4000, "awake", text, ToolTipIcon.Info); } catch { }
    }

    // drawn at runtime (no .ico asset; single-file doctrine). white glyph + soft dark
    // rim so it reads on both dark and light taskbars.
    static Icon MakeIcon(bool filled)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            if (filled)
            {
                using var sh = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
                g.FillEllipse(sh, 3, 3, 12, 12);
                g.FillEllipse(Brushes.White, 2, 2, 11, 11);
            }
            else
            {
                using var dark = new Pen(Color.FromArgb(120, 0, 0, 0), 4f);
                using var white = new Pen(Color.White, 2f);
                g.DrawEllipse(dark, 3.5f, 3.5f, 9, 9);
                g.DrawEllipse(white, 3.5f, 3.5f, 9, 9);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        try { icon.Visible = false; } catch { }
        icon.Dispose(); menu.Dispose(); iconDark.Dispose(); iconLit.Dispose();
    }
}
