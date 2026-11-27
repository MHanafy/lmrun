#:sdk Microsoft.NET.Sdk
#:property UseWindowsForms=true
#:property TargetFramework=net10.0-windows
#:property Nullable=enable
#:property PublishAot=false
#:property PublishTrimmed=false
// step0probe.cs - throwaway Step-0 harness for the awake rebuild (see TUNING-NOTES.md
// 2026-11-27 section). All ES flag math lives here, never in pwsh (7.6.9 parses
// 0x80000000+ as negative and every cast path throws - 2026-11-10 lesson).
//
// Modes (run: dotnet run step0probe.cs -- <mode> [options]):
//   es                                  0A-1: try each SetThreadExecutionState combo
//                                       (sys / sys|disp / sys|disp|away), log prev-state
//                                       + lasterr per attempt, clear after each.
//   hold --minutes N                    0A-2/3/4 + 0B-6/7/9 (+8 with operator taps):
//                                       holds CONTINUOUS|SYSTEM|DISPLAY on this thread,
//                                       heartbeat probe (1 s CPU spin + 10 s sleep, logs
//                                       iteration gaps), samples GetLastInputInfo idle
//                                       every 1 s (logs every reset), probes AsusOLEDShifter /
//                                       Armoury Crate / suspected input injectors at start
//                                       and every 60 s.
//   overlay --seconds N                 0C-10 parent: spawns 'overlay-inner' via WMI
//                                       exactly like the worker is spawned, then exits.
//   overlay-inner --seconds N           (internal) shows a TopMost black form, logs every
//                                       mouse/key/WM_INPUT event it receives, whether
//                                       Activate() takes the foreground, and the GLLII
//                                       idle clock (backstop proof).
//   events --since-minutes N            Kernel-Power 506/507/566 in the window.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Probe
{
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000002,
               ES_DISPLAY_REQUIRED = 0x00000004, ES_AWAYMODE_REQUIRED = 0x00000040;

    [DllImport("kernel32.dll", SetLastError = true)] static extern uint SetThreadExecutionState(uint f);
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    static double GllIdle()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref lii) ? unchecked((uint)(Environment.TickCount64 - lii.dwTime)) / 1000.0 : -1;
    }
    static string Now => DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    static StreamWriter? log;
    static void L(string s) { log?.WriteLine(Now + " " + s); log?.Flush(); }

    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("modes: es | hold --minutes N | overlay --seconds N | overlay-inner --seconds N | events --since-minutes N"); return 2; }
        string mode = args[0];
        int minutes = Opt(args, "--minutes", 14), seconds = Opt(args, "--seconds", 90);
        string dir = Environment.GetEnvironmentVariable("STEP0_DIR") ?? Path.Combine(Path.GetTempPath(), "opencode", "awake-step0");
        Directory.CreateDirectory(dir);

        switch (mode)
        {
            case "es": return Es();
            case "hold": return Hold(minutes, dir);
            case "overlay": return OverlayParent(seconds, dir);
            case "overlay-inner": return OverlayInner(seconds, dir);
            case "events": return Events(Opt(args, "--since-minutes", 60));
            default: Console.WriteLine("unknown mode " + mode); return 2;
        }
    }

    static int Opt(string[] a, string name, int dflt)
    {
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return int.Parse(a[i + 1], CultureInfo.InvariantCulture);
        return dflt;
    }

    // ---- 0A-1 ----
    static int Es()
    {
        Console.WriteLine($"[{Now}] sizeof(uint) flags: sys=0x{ES_SYSTEM_REQUIRED:X} disp=0x{ES_DISPLAY_REQUIRED:X} away=0x{ES_AWAYMODE_REQUIRED:X} cont=0x{ES_CONTINUOUS:X8}");
        (uint flags, string name)[] attempts =
        {
            (ES_CONTINUOUS | ES_SYSTEM_REQUIRED, "cont|sys"),
            (ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED, "cont|sys|disp"),           // THE premise check (err 87 claim)
            (ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED | ES_AWAYMODE_REQUIRED, "cont|sys|disp|away"),
            (ES_CONTINUOUS | ES_DISPLAY_REQUIRED, "cont|disp"),                                     // full rejection matrix before blocker
            (ES_DISPLAY_REQUIRED, "disp (non-continuous)"),
            (ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED, "cont|sys|away"),           // known-good reference (v6 fallback)
            (ES_CONTINUOUS, "cont (clear)"),
        };
        foreach (var (flags, name) in attempts)
        {
            uint r = SetThreadExecutionState(flags);
            int err = Marshal.GetLastWin32Error();
            // RETURN VALUE = PREVIOUS state; 0 = the call FAILED.
            Console.WriteLine($"[{Now}] SetThreadExecutionState({name} = 0x{flags:X8}) -> ret=0x{r:X8} {(r != 0 ? "ACCEPTED" : "REJECTED")} lasterr={err}");
        }
        return 0;
    }

    // ---- 0A-2/3/4 + 0B-6/7/8/9 ----
    static int Hold(int minutes, string dir)
    {
        string path = Path.Combine(dir, $"hold-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");
        log = new StreamWriter(path) { AutoFlush = true };
        L($"HOLD START pid={Environment.ProcessId} minutes={minutes} log={path}");

        uint r = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
        L($"ES hold cont|sys|disp -> ret(prev)=0x{r:X8} {(r != 0 ? "ACCEPTED" : "REJECTED err=" + Marshal.GetLastWin32Error())}");
        if (r == 0) { L("ABORT: request rejected"); try { SetThreadExecutionState(ES_CONTINUOUS); } catch { } return 1; }

        L("procs@start: " + ProcSnapshot());
        RunSampler(minutes * 60);
        SetThreadExecutionState(ES_CONTINUOUS);
        L("ES cleared, HOLD END");
        return 0;
    }

    static string ProcSnapshot()
    {
        string[] want = { "AsusOLEDShifter", "ArmouryCrate", "ArmouryCrateControlInterface", "PowerToys", "powertoys", "mstsc" };
        var hits = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try { foreach (var w in want) if (p.ProcessName.Contains(w, StringComparison.OrdinalIgnoreCase)) { hits.Add($"{p.ProcessName}:{p.Id}"); break; } }
            catch { }
        }
        return hits.Count > 0 ? string.Join(" ", hits) : "none of AsusOLEDShifter/ArmouryCrate/PowerToys running";
    }

    // Main loop = heartbeat probe on the ES-holding thread; 1 Hz GLLII sampler on another thread.
    static void RunSampler(int totalSeconds)
    {
        var stop = new ManualResetEventSlim(false);
        var sampler = new Thread(() =>
        {
            double prev = -1; long n = 0;
            while (!stop.IsSet)
            {
                double idle = GllIdle();
                n++;
                // log every second (full clock evidence) + explicit RESET line when idle drops
                if (prev >= 0 && idle < prev - 1.5) L($"GLLII RESET idle {prev:f1}s -> {idle:f1}s (input detected; gllii-sample #{n})");
                else if (n % 10 == 0) L($"GLLII idle={idle:f1}s (sample #{n})");
                prev = idle;
                Thread.Sleep(1000);
            }
        }) { IsBackground = true };
        sampler.Start();

        var sw = new Stopwatch();
        long lastGapStart = Environment.TickCount64;
        int hb = 0;
        var end = Environment.TickCount64 + totalSeconds * 1000L;
        while (Environment.TickCount64 < end)
        {
            sw.Restart(); while (sw.ElapsedMilliseconds < 1000) { }   // 1 s CPU spin
            hb++;
            long now = Environment.TickCount64;
            double gap = (now - lastGapStart) / 1000.0;               // nominal ~11 s; >=14 = freeze
            L($"HEARTBEAT #{hb} gap={gap:f1}s gllIdle={GllIdle():f1}s");
            if (gap >= 14) L($"*** HEARTBEAT GAP >= 14 s at #{hb}: {gap:f1}s ***");
            lastGapStart = now;
            if (hb % 60 == 0) L("procs: " + ProcSnapshot());
            Thread.Sleep(10000);
        }
        stop.Set();
    }

    // ---- 0C-10 ----
    static int OverlayParent(int seconds, string dir)
    {
        // spawn exactly like the worker: WMI Win32_Process.Create, SW_HIDE, out-of-tree
        string exe = Environment.ProcessPath ?? "step0probe.exe";
        string cmd = $"\"{exe}\" overlay-inner --seconds {seconds}";
        try
        {
            var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            dynamic locator = Activator.CreateInstance(t!)!;
            dynamic svc = locator.ConnectServer(".", "root\\cimv2");
            dynamic startup = svc.Get("Win32_ProcessStartup").SpawnInstance_();
            startup.ShowWindow = 0;
            object? r = svc.Get("Win32_Process").Create(cmd, null, startup, null);
            int rv = r == null ? -1 : Convert.ToInt32(r, CultureInfo.InvariantCulture);
            Console.WriteLine($"[{Now}] spawned via WMI rv={rv}; inner logs to %TEMP%\\opencode\\awake-step0\\overlay-*.log");
            return rv == 0 ? 0 : 1;
        }
        catch (Exception e) { Console.WriteLine("WMI spawn failed: " + e.Message); return 1; }
    }

    static int OverlayInner(int seconds, string dir)
    {
        string path = Path.Combine(dir, $"overlay-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");
        log = new StreamWriter(path) { AutoFlush = true };

        uint r = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
        L($"OVERLAY-INNER START pid={Environment.ProcessId} seconds={seconds} es=0x{r:X8} (holder: THIS ui thread)");

        using var f = new OverlayForm(L);
        f.Show();
        L($"form shown. hwnd={f.Handle} foreground={GetForegroundWindow() == f.Handle} (proc={Process.GetCurrentProcess().ProcessName})");
        var sw0 = Stopwatch.StartNew();
        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        int tick = 0;
        t.Tick += (s, e) =>
        {
            tick++;
            bool fg = GetForegroundWindow() == f.Handle;
            L($"tick#{tick} visible={f.Visible} foreground={fg} gllIdle={GllIdle():f1}s {OverlayForm.EvSummary()}");
            if (tick == 3 || tick == 15)
            {
                f.Activate();   // Form.Activate() is void - evidence is the foreground check below
                Thread.Sleep(150);
                L($"Activate() at t={sw0.Elapsed.TotalSeconds:f0}s -> foreground now {(GetForegroundWindow() == f.Handle ? "OURS" : "NOT ours")}");
            }
            if (tick >= seconds) { t.Stop(); L($"OVERLAY-INNER END after {tick}s {OverlayForm.EvSummary()}"); Application.Exit(); }
        };
        t.Start();
        Application.Run();
        SetThreadExecutionState(ES_CONTINUOUS);
        L("ES cleared");
        return 0;
    }

    // ---- Kernel-Power ----
    static int Events(int sinceMinutes)
    {
        var from = DateTime.Now.AddMinutes(-sinceMinutes);
        using var sys = new EventLog("System");
        int n = 0;
        foreach (EventLogEntry e in sys.Entries)
        {
            if (e.TimeGenerated < from) continue;
            if (e.Source != "Microsoft-Windows-Kernel-Power") continue;   // ProviderName is gone in .NET 5+; Source is the same string
            if (e.InstanceId is not (506 or 507 or 566)) continue;
            n++;
            Console.WriteLine($"{e.TimeGenerated:yyyy-MM-dd HH:mm:ss}  EventID={e.InstanceId}  {e.Message.Split('\n')[0]}");
        }
        Console.WriteLine($"[{Now}] {n} Kernel-Power 506/507/566 events in last {sinceMinutes} min");
        return 0;
    }
}

sealed class OverlayForm : Form
{
    readonly Action<string> logline;
    public static long moves, movesNonZero, downs, wheels, keys, inputs;
    public static long lastX = long.MinValue, lastY = long.MinValue;
    public OverlayForm(Action<string> l)
    {
        logline = l;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = (Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault())?.Bounds ?? SystemInformation.VirtualScreen;
        BackColor = Color.Black; TopMost = true; ShowInTaskbar = false; KeyPreview = true;
        Cursor = Cursors.No;
    }
    public static string EvSummary() => $"events: moves={moves}(nz {movesNonZero}) downs={downs} wheels={wheels} keys={keys} wm_input={inputs}";

    protected override void OnMouseMove(MouseEventArgs e)
    {
        moves++;
        bool nz = lastX != long.MinValue && (e.X != lastX || e.Y != lastY);
        if (nz) movesNonZero++;
        lastX = e.X; lastY = e.Y;
        if (moves <= 5 || (nz && movesNonZero <= 5)) logline($"FORM WM_MOUSEMOVE #{moves} x={e.X} y={e.Y} nonzero={nz} at {DateTime.Now:HH:mm:ss.fff}");
        base.OnMouseMove(e);
    }
    protected override void OnMouseDown(MouseEventArgs e) { downs++; logline($"FORM WM_LBUTTONDOWN #{downs} x={e.X} y={e.Y} at {DateTime.Now:HH:mm:ss.fff}"); base.OnMouseDown(e); }
    protected override void OnMouseWheel(MouseEventArgs e) { wheels++; logline($"FORM WM_MOUSEWHEEL #{wheels} at {DateTime.Now:HH:mm:ss.fff}"); base.OnMouseWheel(e); }
    protected override void OnKeyDown(KeyEventArgs e) { keys++; logline($"FORM WM_KEYDOWN #{keys} vk={e.KeyCode} at {DateTime.Now:HH:mm:ss.fff}"); base.OnKeyDown(e); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x00FF) { inputs++; logline($"FORM WM_INPUT #{inputs} at {DateTime.Now:HH:mm:ss.fff}"); }
        base.WndProc(ref m);
    }
}
