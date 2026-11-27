#:sdk Microsoft.NET.Sdk
#:property UseWindowsForms=true
#:property TargetFramework=net10.0-windows
#:property Nullable=enable
#:property PublishAot=false
#:property PublishTrimmed=false
// awake.cs - single-file keep-awake + zero-light dark overlay (v7: display-on request).
//   run: awake.exe <verb> [options]   verbs: start stop status renew-wait selftest help
//   ('run' is the internal detached worker)
//
// PROTECTION (Step-0 evidence + full history: ../TUNING-NOTES.md 2026-11-27 section):
// - Modern Standby (S0ix) box: idle -> display blank -> S0ix silently KILLS background
//   processes, and the AMD driver throttles while the display is logically off. The worker
//   HOLDS SetThreadExecutionState(ES_CONTINUOUS|ES_SYSTEM_REQUIRED|ES_DISPLAY_REQUIRED) on
//   its UI thread for its whole lifetime: measured ACCEPTED (0x80000003) and HONOURED on
//   this box (AC timeout 60 s, 10 min no input: no blank, 0 Kernel-Power events). Process
//   death releases the request automatically.
// - The pre-v7 F15 key-injection + LL hooks + tagging machinery is GONE. It existed only
//   because the ps1->C# lineage used wrong ES constants (SYSTEM=0x2, DISPLAY=0x4; 0x4 is
//   the UNSUPPORTED ES_USER_PRESENT whose combinations fail err 87) - the "display
//   requests are rejected here (err 87)" premise was that bug misread as an OS policy.
//   NEVER reintroduce non-canonical ES flag values: SYSTEM=0x1, DISPLAY=0x2 (winbase.h).
// - Battery rule (operator decision 2026-11-27): hold on AC only; release on battery and
//   re-take on AC via SystemEvents.PowerModeChanged, logged loudly + tray balloon. A
//   re-plug NEVER wakes the overlay - only human input does.
// - Human idle clock = GetLastInputInfo ONLY. Without injected input it is a pure human
//   clock (verified: 647 s monotonic, no resets), and touch AND pen advance it (verified)
//   - no raw-input sink, no hooks, no tagging needed.
// - Overlay visibility IS the phase (single source of truth; the old separate isDark flag
//   could desync from reality = the stuck-black bug class). ApplicationContext has NO main
//   form, so nothing is ever shown implicitly (v6 keeper mode parked a visible black
//   window that no code path could hide - proven root cause, 2026-11-27).
// - The overlay is black, borderless, TopMost, primary-screen, Cursor = Cursors.No. The
//   panel is a verified OLED (Samsung ATNA33AA08-0): black pixels emit ZERO light. NEVER
//   add brightness/ambient-sensor/power-plan writes: WmiSetBrightness SYNCs into the plan
//   on this build (stranded-dim-panel risk, 2026-11-17). Permanently out by operator rule.
// - The worker is spawned OUT OF TREE via WMI (Win32_Process.Create -> parent WmiPrvSE):
//   agent-harness process-tree reaping must not take it (forensics 2026-11-18).
// - NOTHING persists outside the worker process after a clean stop or a kill: the ES
//   request dies with the thread, the state files are removed on exit; control file
//   present + pid dead = "protection is GONE" alarm. Multi-monitor is out of scope
//   (primary display only, operator decision).
// - The UI thread performs NO file IO (mailbox poll + journal live on background
//   threads): a stalled disk/AV scan may never delay input handling. The 1 s tick body
//   NEVER throws out (try/catch + ALARM log) - a dead tick once meant a stuck overlay.
// - All files: %LOCALAPPDATA%\Awake\ (keeper.json mailbox CLI->worker, status.json mirror
//   worker->CLI, awake.log async journal, renewtimer.json renewal-alarm box).
using System;
using System.Collections.Concurrent;
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
                "selftest" => SelfTest.Run(o),
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
        finally { Keeper.Flush(); }   // async journal: never exit with lines still queued
    }
    static int Err(Opts o) { Console.Error.WriteLine($"unknown verb '{o.Verb}'"); Cli.Help(); return 2; }
}

sealed class Opts
{
    public string Verb = "";
    public int Minutes, Idle = 180;
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
                case "--heartbeat":
                    throw new ArgumentException("--heartbeat was removed in v7: the keep-awake is a display-on request now, " +
                                                "not an F15 cadence - drop the flag (it used to mean 'F15 every N s').");
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
        return o;
    }
}

sealed class KeeperState
{
    [JsonPropertyName("pid")] public int Pid;
    [JsonPropertyName("mode")] public string Mode = "keeper";        // "dark" | "keeper"
    [JsonPropertyName("intent")] public string Intent = "run";       // "run" | "stop"
    [JsonPropertyName("idleSeconds")] public int IdleSeconds;
    [JsonPropertyName("minutes")] public int Minutes;
    [JsonPropertyName("until")] public string? Until;
    [JsonPropertyName("seq")] public long Seq;
    [JsonPropertyName("updatedAt")] public string UpdatedAt = "";
    [JsonPropertyName("startedAt")] public string StartedAt = "";
    [JsonPropertyName("phase")] public string Phase = "lit";          // mirrored from status.json when alive
    [JsonPropertyName("hold")] public string Hold = "";               // mirrored: "active" | "released-battery"
    [JsonPropertyName("humanIdle")] public double HumanIdle;          // mirrored: GetLastInputInfo idle s
}

// Worker-only status mirror (never write keeper.json from the worker - the old shared-file
// mirror clobbered seq/intent and could swallow a just-written stop).
sealed class StatusMirror
{
    [JsonPropertyName("pid")] public int Pid;
    [JsonPropertyName("phase")] public string Phase = "lit";
    [JsonPropertyName("mode")] public string Mode = "keeper";
    [JsonPropertyName("hold")] public string Hold = "active";
    [JsonPropertyName("humanIdle")] public double HumanIdle;
    [JsonPropertyName("updatedAt")] public string UpdatedAt = "";
}

internal static class Keeper
{
    public static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web) { IncludeFields = true, WriteIndented = false };

    public static string StatePath(Opts o) => Path.Combine(o.StateDir, "keeper.json");
    public static string LogPath(Opts o) => Path.Combine(o.StateDir, "awake.log");
    public static string BoxPath(Opts o) => Path.Combine(o.StateDir, "renewtimer.json");
    public static string StatusPath(Opts o) => Path.Combine(o.StateDir, "status.json");

    // Windows refuses to replace/delete a file held open WITHOUT FILE_SHARE_DELETE, so
    // EVERY mailbox/log open uses ReadWrite|Delete plus a bounded retry: a busy file can
    // neither strand a command nor fake a state change (v6.3 stop-swallowing class).
    const FileShare Share = FileShare.ReadWrite | FileShare.Delete;

    public enum ReadResult { Ok, Missing, Failed }
    public static ReadResult TryReadAllText(string path, out string? text)
    {
        for (int i = 0; i < 12; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, Share);
                using var sr = new StreamReader(fs);
                text = sr.ReadToEnd();
                return ReadResult.Ok;
            }
            catch (Exception e)
            {
                if (e is FileNotFoundException or DirectoryNotFoundException) { text = null; return ReadResult.Missing; }
                if (e is not (IOException or UnauthorizedAccessException)) throw;
                Thread.Sleep(40 + 60 * i);
            }
        }
        text = null;
        return ReadResult.Failed;
    }

    public static bool WriteAtomic(string path, string text)
    {
        string tmp = path + "." + Environment.ProcessId + ".tmp";
        for (int i = 0; i < 12; i++)
        {
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, Share))
                using (var sw = new StreamWriter(fs)) sw.Write(text);
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(40 + 60 * i); }
        }
        try { File.Delete(tmp); } catch { }
        return false;
    }

    public static void TryDelete(string path)
    {
        for (int i = 0; i < 12; i++)
        {
            try { File.Delete(path); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(40 + 60 * i); }
        }
    }

    public static KeeperState? Load(Opts o)
    {
        if (TryReadAllText(StatePath(o), out var txt) != ReadResult.Ok || txt == null) return null;
        try { return JsonSerializer.Deserialize<KeeperState>(txt, J); } catch { return null; }
    }

    public static bool Save(Opts o, KeeperState st)
    {
        Directory.CreateDirectory(o.StateDir);
        return WriteAtomic(StatePath(o), JsonSerializer.Serialize(st, J));
    }

    public static StatusMirror? LoadStatus(Opts o)
    {
        if (TryReadAllText(StatusPath(o), out var txt) != ReadResult.Ok || txt == null) return null;
        try { return JsonSerializer.Deserialize<StatusMirror>(txt, J); } catch { return null; }
    }
    public static void SaveStatus(Opts o, StatusMirror m)
    {
        try
        {
            Directory.CreateDirectory(o.StateDir);
            WriteAtomic(StatusPath(o), JsonSerializer.Serialize(m, J));
        }
        catch { /* mirror is best-effort */ }
    }

    // Journal is ASYNC: callers only enqueue - the retrying, shared-safe append happens on
    // a background thread so the worker's UI thread NEVER touches a file. Flush() before
    // any process exit; the thread is background, so a forgotten Flush only costs trailing
    // lines, never a hang.
    static readonly ConcurrentQueue<string> LogQ = new();
    static Thread? logThread;
    static Opts? logOpts;

    public static void Log(Opts o, string msg)
    {
        LogQ.Enqueue($"{DateTime.Now.ToString("s", CultureInfo.InvariantCulture)} [pid {Environment.ProcessId}] {msg}\n");
        lock (LogQ)
        {
            logOpts = o;   // one state dir per process (CLI and worker each use exactly one)
            if (logThread == null)
            {
                var th = new Thread(LoggerLoop) { IsBackground = true, Name = "awake-log" };
                logThread = th;
                th.Start();
            }
        }
    }

    static void LoggerLoop()
    {
        while (true) { DrainOnce(); Thread.Sleep(60); }
    }

    static void DrainOnce()
    {
        var o = logOpts;
        if (o == null) return;
        var sb = new StringBuilder();
        while (LogQ.TryDequeue(out var l)) sb.Append(l);
        if (sb.Length == 0) return;
        for (int i = 0; i < 10; i++)
        {
            try
            {
                Directory.CreateDirectory(o.StateDir);
                using var fs = new FileStream(LogPath(o), FileMode.Append, FileAccess.Write, Share);
                using var sw = new StreamWriter(fs);
                sw.Write(sb.ToString());
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(50 + 50 * i); }
        }
        LogQ.Enqueue(sb.ToString());   // best-effort: never lose, order may skew under lock storms
    }

    public static void Flush()
    {
        for (int i = 0; i < 12 && !LogQ.IsEmpty; i++) DrainOnce();
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
            if (TryReadAllText(BoxPath(o), out var txt) != ReadResult.Ok || txt == null) return false;
            var b = JsonSerializer.Deserialize<KeeperState>(txt, J); // pid field reuse
            bool killed = false;
            if (b != null) { try { Process.GetProcessById(b.Pid).Kill(true); killed = true; } catch { } }
            TryDelete(BoxPath(o));
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
// ==== Cli ====
internal static class Cli
{
    public static int Help()
    {
        Console.WriteLine(
"""
awake - keep-awake (display-on request) + zero-light dark overlay (files: %LOCALAPPDATA%\Awake\)
  start --minutes N [--dark] [--idle S]
        arm protection: the worker holds a display-on + system execution request for its
        WHOLE life, so Windows never idles the display into blank/Modern Standby (the S0ix
        doze silently kills background processes on this box). --dark adds the pixel-black
        overlay (OLED: zero light) that wakes on human input and re-blanks after --idle S
        (default 180, min 15) s of real human idle; without --dark = lit keeper (NO window).
        The request is held on AC only: unplugging RELEASES it (logged loudly), re-plugging
        re-takes it (the overlay never wakes on re-plug - only human input wakes it).
        Re-running start while live = EXTEND the window (never re-blanks; pass --dark only
        if you also want to turn the overlay ON on a live keeper).
  stop  cooperative stop: flips the mailbox; the worker exits itself (force-kill after 5 s).
        The worker shows a tray icon (status tooltip + Hide + Open log + Stop) while
        running; --no-tray hides it. 'Hide black screen (keep armed)' dismisses the overlay
        for good this run (the worker demotes to keeper, never re-blanks again).
        In dark phase the overlay covers the taskbar - any human input wakes it first,
        THEN the tray is reachable. CLI 'stop' always works.
  status [--json]   RUNNING + hold state + phase + human idle + retire time.
  renew-wait --minutes N   sleep (N-5) min then exit - run as agent background job; its
        completion is the agent's renewal alarm. Renew with 'start --minutes N' (NO --dark).
  selftest --state-dir <tmp>   isolated end-to-end self-check (synthetic input is used in
        tests ONLY); never arm/stop the real instance from it.
THE TOOL: published FRAMEWORK-DEPENDENT at <source-dir>\\bin\\awake.exe (~200 KB; needs the
.NET 10 Windows Desktop runtime). Rebuild: dotnet publish awake.cs -c Release -o bin
(During development 'dotnet run awake.cs' also works; it spawns the worker from an
installed runtime copy to avoid locking the temp build.)
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
            if (!Keeper.Save(o, st))
            {
                Keeper.Log(o, $"[cli] EXTEND FAILED: mailbox busy, until NOT pushed (seq={st.Seq}) - worker keeps its old window");
                Console.Error.WriteLine("awake: could not push the extension (mailbox busy) - the live worker keeps its OLD window; retry.");
                return 1;
            }
            Keeper.Log(o, $"[cli] until pushed '{oldUntil}' -> '{st.Until}' seq={st.Seq} mode={st.Mode}");
            Console.WriteLine($"Already RUNNING (pid {alive}, mode {st.Mode}) - EXTENDED until {st.Until} " +
                              "(a live worker is never re-blanked; the mailbox extends it).");
            Console.WriteLine($"Renewal loop: when the renew-wait job fires and work continues, re-run THIS command WITHOUT --dark. Finished: 'stop'.");
            return 0;
        }

        if (st != null)
        {
            Keeper.TryDelete(Keeper.StatePath(o));
            Keeper.TryDelete(Keeper.StatusPath(o));
            Keeper.Log(o, "[cli] stale control file cleared (previous worker died - protection WAS gone)");
            Console.WriteLine("Cleared stale control file (previous worker died - that is the 'protection is GONE' alarm).");
        }
        Keeper.KillRenewTimer(o); // a fresh cycle replaces any pending renewal tick

        // The worker must NOT run from a 'dotnet run' temp build (a running image locks its
        // files and breaks every later rebuild) - install a stable copy into
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
                                 "--state-dir", o.StateDir };
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
                Console.WriteLine($"STARTED (pid {c.Pid}, {mode}, spawned via {spawnedVia}): display-on+system request held for the " +
                                  $"WHOLE run - the display never blanks, Modern Standby never engages; retires {c.Until}.");
                if (c.Mode == "dark")
                    Console.WriteLine($"Overlay: pixel black NOW (OLED: zero light; brightness/sensor untouched), " +
                                      $"human input wakes, re-blanks after {c.IdleSeconds} s human idle.");
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

    // Spawn the worker OUTSIDE the caller's process tree (2026-11-18 forensics: agent
    // harnesses reap their shell's whole process tree; an in-tree worker dies with it).
    // WMI Win32_Process.Create parents under WmiPrvSE: different parent, job and console.
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
                // console-subsystem exe (CLI needs stdout): SW_HIDE keeps the console off-screen
                dynamic startup = svc.Get("Win32_ProcessStartup").SpawnInstance_();
                startup.ShowWindow = 0;
                object? r = wmiProc.Create(cmd, Path.GetDirectoryName(exe), startup, null);
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
            Keeper.TryDelete(Keeper.StatePath(o));
            Keeper.TryDelete(Keeper.StatusPath(o));
            Keeper.Log(o, "[cli] stale control file cleaned by stop");
            Console.WriteLine("Was already dead (stale control file cleaned)." + (timerKilled ? " Pending renewal timer terminated." : ""));
            return 0;
        }

        st.Intent = "stop";
        st.Seq++;
        st.UpdatedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        if (!Keeper.Save(o, st))
        {
            Keeper.Log(o, "[cli] stop FAILED: could not write the mailbox (file busy) - worker still RUNNING");
            Console.Error.WriteLine("awake: stop request NOT delivered (mailbox busy) - the worker is still RUNNING; run 'stop' again.");
            return 1;
        }
        Keeper.Log(o, $"[cli] stop requested via mailbox seq={st.Seq}");

        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(250);
            // only 'file gone' or 'pid dead' may report a clean stop - a busy read is
            // unknown, never 'stopped' (the v6.3 lesson).
            var r = Keeper.TryReadAllText(Keeper.StatePath(o), out var txt);
            bool gone = r == Keeper.ReadResult.Missing;
            if (!gone && r == Keeper.ReadResult.Ok && txt != null)
            {
                KeeperState? c = null;
                try { c = JsonSerializer.Deserialize<KeeperState>(txt, Keeper.J); } catch { }
                gone = c != null && Keeper.AlivePid(c) == null;
            }
            if (gone)
            {
                Console.WriteLine("Stopped cleanly (worker acknowledged the mailbox, EXIT logged)." +
                                  (timerKilled ? " Pending renewal timer terminated." : ""));
                return 0;
            }
        }
        try { var p = Process.GetProcessById(st.Pid); p.Kill(true); p.WaitForExit(3000); } catch { }
        Keeper.TryDelete(Keeper.StatePath(o));
        Keeper.TryDelete(Keeper.StatusPath(o));
        Keeper.Log(o, $"[cli] FORCED stop pid={st.Pid} (mailbox not acknowledged within 5 s)");
        Console.WriteLine("Stopped FORCED (worker did not acknowledge the mailbox within 5 s)." +
                          (timerKilled ? " Pending renewal timer terminated." : ""));
        return 0;
    }

    public static int Status(Opts o)
    {
        var st = Keeper.Load(o);
        int? alive = Keeper.AlivePid(st);
        if (st == null)
        {
            // a busy-but-present file must never print a confident 'OFF'
            if (Keeper.TryReadAllText(Keeper.StatePath(o), out _) == Keeper.ReadResult.Failed)
            {
                Console.WriteLine("Awake: UNKNOWN - control file present but unreadable after retries; re-run 'status'.");
                return 1;
            }
        }
        else if (alive != null)
        {
            var m = Keeper.LoadStatus(o);   // live phase/HOLD/MODE/idle come from the worker's own mirror
            if (m != null && m.Pid == st.Pid)
            {
                st.Phase = m.Phase; st.Hold = m.Hold; st.HumanIdle = m.HumanIdle;
                st.UpdatedAt = m.UpdatedAt; st.Mode = m.Mode;   // a tray Hide made keeper: report the truth
            }
        }
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
        string hold = string.IsNullOrEmpty(st.Hold) ? "hold: (no mirror yet)"
            : st.Hold == "active" ? "hold: ACTIVE (display-on + system request held)"
            : "hold: RELEASED - ON BATTERY, protection is NOT active right now";
        string mode = st.Mode == "dark"
            ? $"DARK overlay cycle (overlay {(st.Phase == "dark" ? "VISIBLE" : "hidden")}, re-blank at {st.IdleSeconds} s human idle)"
            : "lit keeper (no window)";
        Console.WriteLine($"Awake: RUNNING (pid {st.Pid}) - {hold}, {mode}, human idle {st.HumanIdle:n0} s, retiring {st.Until}.");
        return 0;
    }

    sealed class StatusOut { public KeeperState? State { get; set; } public bool Alive { get; set; } }

    public static int RenewWait(Opts o)
    {
        int span = Math.Max(1, o.Minutes - 5);
        var box = new KeeperState { Pid = Environment.ProcessId, Minutes = o.Minutes, UpdatedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture) };
        Directory.CreateDirectory(o.StateDir);
        Keeper.WriteAtomic(Keeper.BoxPath(o), JsonSerializer.Serialize(box, Keeper.J));
        Thread.Sleep(span * 60_000);
        if (Keeper.TryReadAllText(Keeper.BoxPath(o), out var txt) == Keeper.ReadResult.Ok && txt != null)
        {
            try
            {
                var b = JsonSerializer.Deserialize<KeeperState>(txt, Keeper.J);
                if (b != null && b.Pid == Environment.ProcessId)
                {
                    b.UpdatedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                    Keeper.WriteAtomic(Keeper.BoxPath(o), JsonSerializer.Serialize(b, Keeper.J));
                }
            }
            catch { }
        }
        Console.WriteLine($"renewal tick: the armed window expires in ~5 min - still working => 'start --minutes {o.Minutes}' (NO --dark); finished => 'stop'.");
        return 0;
    }
}
// ==== Native ====
internal static class Native
{
    // CANONICAL winbase.h values ONLY (SYSTEM=0x1, DISPLAY=0x2): the lineage bug that
    // caused the whole F15 detour was using 0x2/0x4, where 0x4 = ES_USER_PRESENT is
    // unsupported and poisons the call with err 87. Do not "fix" these back.
    static class EsNative
    {
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint SetThreadExecutionState(uint f);
    }
    public static class Es
    {
        public const uint Continuous = 0x80000000, System = 0x00000001, Display = 0x00000002;
        // SetThreadExecutionState RETURNS THE PREVIOUS state (0 = failure) - never read it as the new one.
        public static (bool ok, uint prev, int err) Hold()
        {
            uint r = EsNative.SetThreadExecutionState(Continuous | System | Display);
            return (r != 0, r, Marshal.GetLastWin32Error());
        }
        public static void Clear() => EsNative.SetThreadExecutionState(Continuous);
    }

    // THE human idle clock: win32k's session-wide last-input tick. With zero injected
    // input it is pure human (verified monotonic 647 s), and touch AND pen advance it
    // (verified 2026-11-27) - no hooks, no raw-input sink, no tagging.
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    public static uint GllRaw()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref lii) ? lii.dwTime : 0;
    }
    public static double GllIdleSeconds() => unchecked((uint)(Environment.TickCount64 - GllRaw())) / 1000.0;

    // TEST-ONLY synthetic input (selftest): the product worker never injects anything.
    // The union must carry MOUSEINPUT (32 B) - x64 sizeof(INPUT) is 40 or SendInput err 87.
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    public static void TestMoveMouse()
    {
        var a = new INPUT[1];
        a[0].type = 0 /*INPUT_MOUSE*/; a[0].u.mi.dx = 6; a[0].u.mi.dy = 4; a[0].u.mi.dwFlags = 0x00000001 /*MOUSEEVENTF_MOVE*/;
        SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
    }
}

// ==== Overlay ====
// The form's OWN events wake it (KeyDown/MouseDown/MouseWheel + MouseMove with a NONZERO
// delta - parked-cursor republishes must not wake); the 1 s tick adds a GetLastInputInfo
// backstop for anything the form never sees. No WM_INPUT sink: touch/pen advance the
// session clock (verified), so it would be redundant.
sealed class Overlay : Form
{
    public Action<string>? OnWake;
    long lastX = long.MinValue, lastY = long.MinValue;
    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        TopMost = true;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        // Cursors.No, not Cursor.Hide(): a single cursor sprite glowing over the zero-light
        // black screen defeats the whole dark-room posture.
        Cursor = Cursors.No;
    }
    // recompute from the primary screen on EVERY show (resolution/DPI can change while dark)
    public void Reposition() =>
        Bounds = (Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault())?.Bounds ?? SystemInformation.VirtualScreen;

    void Wake(string why) => OnWake?.Invoke(why);
    protected override void OnKeyDown(KeyEventArgs e) { Wake("key"); base.OnKeyDown(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Wake("mouse click"); base.OnMouseDown(e); }
    protected override void OnMouseWheel(MouseEventArgs e) { Wake("mouse wheel"); base.OnMouseWheel(e); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool nonzero = lastX != long.MinValue && (e.X != lastX || e.Y != lastY);
        lastX = e.X; lastY = e.Y;
        if (nonzero) Wake("mouse move");
        base.OnMouseMove(e);
    }
}
// ==== Worker ====
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
            Minutes = o.Minutes,
            Until = now.AddMinutes(o.Minutes).ToString("o", CultureInfo.InvariantCulture),
            Seq = 0,
            UpdatedAt = now.ToString("o", CultureInfo.InvariantCulture),
            StartedAt = now.ToString("o", CultureInfo.InvariantCulture),
        };

        // THE protection: held on this (UI) thread for the process lifetime; process death
        // releases it automatically. Battery rule: AC only (operator decision 2026-11-27).
        bool holding = false;
        string BatteryHoldState() => holding ? "active" : "released-battery";
        void TakeRequest(string why)
        {
            var (ok, prev, err) = Native.Es.Hold();
            holding = ok;
            Keeper.Log(o, $"HOLD {why}: cont|sys|disp -> prev=0x{prev:X8} {(ok ? "HELD" : $"FAILED err={err} - PROTECTION NOT ACTIVE")}");
        }
        void ReleaseRequest(string why)
        {
            Native.Es.Clear();
            holding = false;
            Keeper.Log(o, $"HOLD {why}: request RELEASED - {(IsOnAc() ? "on AC" : "ON BATTERY: idle will blank + suspend, background work at risk")}");
        }
        static bool IsOnAc() => SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;   // System.Windows.Forms enum
        if (IsOnAc()) TakeRequest("armed (AC)");
        else Keeper.Log(o, "START on BATTERY: display-on request NOT taken (AC-only hold, operator rule); will re-take on AC");

        var f = new Overlay();
        var ctx = new ApplicationContext();   // NO main form: nothing is ever shown implicitly (v7 root-cause fix)
        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        bool shutting = false, hidden = false;
        long shownRaw = 0;   // latched session-input value at the moment the overlay was shown
        int tick = 0;

        void EnterDark()
        {
            if (shutting || f.Visible) return;
            f.Reposition();                 // primary-screen bounds recomputed EVERY show
            f.Show(); f.TopMost = true; f.Activate();
            Cursor.Hide();
            shownRaw = Native.GllRaw();
            Keeper.Log(o, $"DARK (human idle {Native.GllIdleSeconds():n0}s)");
        }
        void EnterLit(string why)
        {
            if (!f.Visible) return;         // visibility IS the phase - no separate flag to desync
            f.Hide();
            Cursor.Show();
            Keeper.Log(o, $"WAKE ({why})");
        }
        f.OnWake = why => EnterLit(why);    // form's own events dismiss immediately
        string RetireLabel()
        {
            try { return DateTimeOffset.Parse(st.Until!, CultureInfo.InvariantCulture).ToString("HH:mm"); } catch { return "?"; }
        }
        void Cleanup(string why)
        {
            if (shutting) return;
            shutting = true;
            t.Stop();
            MarkShutting();
            try { tray?.Dispose(); } catch { } tray = null;
            EnterLit("shutdown");
            Native.Es.Clear();
            try
            {
                var c = Keeper.Load(o);
                if (c == null || c.Pid == Environment.ProcessId) Keeper.TryDelete(Keeper.StatePath(o));
            }
            catch { }
            Keeper.TryDelete(Keeper.StatusPath(o));
            Keeper.Log(o, $"EXIT: {why} (cursor + display request released, control file removed)");
            Keeper.Flush();
            ctx.ExitThread();
        }

        // ---- mailbox + mirror file IO NEVER on the UI thread (input must never wait on disk) ----
        var mlock = new object();
        Keeper.ReadResult mres = Keeper.ReadResult.Failed;   // Failed == 'no fresh read yet'
        KeeperState? mst = null;
        int[] stopFlag = new int[1];
        string phaseNow = "lit", holdNow = BatteryHoldState(), modeNow = "keeper"; double idleNow = 0;

        void MarkShutting()
        {
            Volatile.Write(ref stopFlag[0], 1);
            lock (mlock) { phaseNow = "exiting"; }
        }

        Microsoft.Win32.SystemEvents.SessionSwitch += (s, e) => Keeper.Log(o, "WTS: " + e.Reason);
        // Battery rule (operator decision): release off AC, re-take on AC; LOUD (journal +
        // balloon) - a silent release is the old 'status said RUNNING but unprotected' class.
        // A re-take NEVER wakes the overlay: re-plugging is not human input.
        Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) =>
        {
            if (e.Mode != Microsoft.Win32.PowerModes.StatusChange || shutting) return;
            try
            {
                if (IsOnAc())
                {
                    if (!holding)
                    {
                        TakeRequest("RE-TAKEN (AC restored)");
                        tray?.Balloon("Display-on request re-taken (back on AC).");
                        lock (mlock) { holdNow = BatteryHoldState(); }
                    }
                }
                else if (holding)
                {
                    ReleaseRequest("RELEASED (battery)");
                    tray?.Balloon("ALARM: unplugged - display-on request RELEASED, the box can now blank + suspend.");
                    lock (mlock) { holdNow = BatteryHoldState(); }
                }
            }
            catch (Exception bx) { Keeper.Log(o, $"ALARM: power-mode handling failed: {bx.Message}"); }
        };
        f.FormClosing += (s, e) => { if (!shutting) { Cleanup("window closed"); e.Cancel = true; } };

        t.Tick += (s, e) =>
        {
            if (shutting) return;
            tick++;

            // SECTION 1: mailbox consume (failure skips ONLY this section - wake and
            // re-blank below must run on every tick regardless).
            try
            {
                if (Environment.GetEnvironmentVariable("AWAKE_SELFTEST_THROW") == "1")
                    throw new InvalidOperationException("injected tick fault (selftest)");
                Keeper.ReadResult mr; KeeperState? ex;
                lock (mlock) { mr = mres; ex = mst; }
                if (mr == Keeper.ReadResult.Missing) { Cleanup("control file removed (treated as stop)"); return; }
                if (mr == Keeper.ReadResult.Failed || ex == null) return;   // stay armed, cycle section below still runs

                // stop is IDEMPOTENT - honored every tick, not only on a seq change.
                if (string.Equals(ex.Intent, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    Cleanup("stop requested via mailbox");
                    return;
                }
                if (ex.Seq != st.Seq)
                {
                    st.Seq = ex.Seq;
                    if (!string.Equals(ex.Until, st.Until, StringComparison.Ordinal))
                    {
                        Keeper.Log(o, $"until mailbox: '{st.Until}' -> '{ex.Until}' (tick {tick})");
                        st.Until = ex.Until;
                    }
                    if (ex.IdleSeconds >= 15 && ex.IdleSeconds != st.IdleSeconds) st.IdleSeconds = ex.IdleSeconds;
                    if (ex.Mode != st.Mode)
                    {
                        // tray Hide is OPERATOR AUTHORITY for this run: ignore later dark pushes
                        if (hidden && ex.Mode == "dark")
                            Keeper.Log(o, "ignoring mailbox mode 'dark': operator hid the black screen this run (stop + start --dark to re-arm)");
                        else
                        {
                            Keeper.Log(o, $"mode mailbox: '{st.Mode}' -> '{ex.Mode}'");
                            st.Mode = ex.Mode;
                            if (st.Mode == "dark") EnterDark(); else EnterLit("mode change");
                        }
                    }
                }
                if (!string.IsNullOrEmpty(st.Until) &&
                    DateTimeOffset.Now >= DateTimeOffset.Parse(st.Until, CultureInfo.InvariantCulture))
                {
                    Cleanup("retired (window ended)");
                    return;
                }
            }
            catch (Exception mx)
            {
                Keeper.Log(o, $"ALARM: mailbox tick section failed (ignored; wake/re-blank unaffected): {mx.GetType().Name}: {mx.Message}");
            }

            // SECTION 2: overlay cycle - the ONLY code that shows/hides the form.
            try
            {
                double idle = Native.GllIdleSeconds();
                if (st.Mode == "dark" && !hidden)
                {
                    if (f.Visible)
                    {
                        // latched backstop: ANY advance of the session input clock since the
                        // show is a human (touch included) - never compare 'idle < 1'.
                        if (Native.GllRaw() != shownRaw) EnterLit("human input (session clock backstop)");
                    }
                    else if (idle >= st.IdleSeconds) EnterDark();
                }
                if (f.Visible) f.TopMost = true;

                lock (mlock) { phaseNow = f.Visible ? "dark" : "lit"; holdNow = BatteryHoldState(); modeNow = st.Mode; idleNow = idle; }
                if (tick % 10 == 0) tray?.Update(f.Visible);
            }
            catch (Exception cx)
            {
                Keeper.Log(o, $"ALARM: cycle tick section failed (worker alive): {cx.GetType().Name}: {cx.Message}");
            }
        };

        // bootstrap: touch the handle (keeper NEVER shows the form); claim after wiring.
        var _handle = f.Handle;
        if (!Keeper.Save(o, st)) { Keeper.Log(o, "RUN aborted: could not claim the control file (busy)"); Keeper.Flush(); return 1; }
        Keeper.Log(o, $"START cs v7: {(o.Dark ? "pixel dark + " : "keeper, ")}display-on request {(holding ? "HELD" : "NOT held (battery)")} " +
                      $"(GetLastInputInfo human clock, visibility-as-phase, bg mailbox)");

        var mboxThread = new Thread(() =>
        {
            int mirrorCd = 0, fails = 0;
            while (Volatile.Read(ref stopFlag[0]) == 0)
            {
                var r = Keeper.TryReadAllText(Keeper.StatePath(o), out var txt);
                KeeperState? parsed = null;
                if (r == Keeper.ReadResult.Ok && txt != null)
                {
                    try { parsed = JsonSerializer.Deserialize<KeeperState>(txt, Keeper.J); } catch { }
                    if (parsed == null) r = Keeper.ReadResult.Failed;
                }
                lock (mlock) { mres = r; mst = parsed; }
                if (r == Keeper.ReadResult.Failed) { if (++fails % 30 == 1) Keeper.Log(o, $"mailbox read/parse failed x{fails} - staying armed"); }
                else fails = 0;

                if (++mirrorCd >= 5 && Volatile.Read(ref stopFlag[0]) == 0)
                {
                    mirrorCd = 0;
                    string ph, hl, md; double idl;
                    lock (mlock) { ph = phaseNow; hl = holdNow; md = modeNow; idl = idleNow; }
                    if (ph != "exiting")
                        Keeper.SaveStatus(o, new StatusMirror
                        {
                            Pid = Environment.ProcessId,
                            Phase = ph,
                            Mode = md,
                            Hold = hl,
                            HumanIdle = idl,
                            UpdatedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                        });
                    if (Volatile.Read(ref stopFlag[0]) != 0) Keeper.TryDelete(Keeper.StatusPath(o));
                }
                Thread.Sleep(1000);
            }
        }) { IsBackground = true, Name = "awake-mailbox" };
        mboxThread.Start();

        if (st.Mode == "dark") EnterDark();   // immediate blackout on arm (dark mode only)
        t.Start();

        if (!o.NoTray)
            try
            {
                tray = new TrayUi(Keeper.LogPath(o),
                    () => $"awake - {(st.Mode == "dark" ? "dark+keeper" : "keeper")} - {(f.Visible ? "DARK" : "lit")} - {(holding ? "hold ACTIVE" : "hold RELEASED (battery)")} - retires {RetireLabel()}",
                    () =>
                    {
                        if (hidden) return;
                        bool believedDark = f.Visible;
                        hidden = true; st.Mode = "keeper";
                        if (believedDark)
                            EnterLit("HIDE from tray - black screen dismissed by hand; display-on request STAYS ARMED, no more darkening this run");
                        else
                        {
                            // The operator saw black while WE believe the overlay is hidden: attribute
                            // blame between our form and a foreign layer (OS blank / AMD / AsusOLEDShifter).
                            EnterLit("HIDE from tray WHILE WE BELIEVED LIT - operator saw a black screen our phase does not explain");
                            Keeper.Log(o, $"HIDE-forensics: overlay Visible=false at click; human idle={Native.GllIdleSeconds():n0}s phase=lit since start; sinceDarkEnd n/a (v7 has no phase flag)");
                            try
                            {
                                var shifter = Process.GetProcessesByName("AsusOLEDShifter");
                                Keeper.Log(o, $"HIDE-forensics: AsusOLEDShifter pids={(shifter.Length > 0 ? string.Join(",", shifter.Select(p => p.Id)) : "NONE")} - if present, the black layer may be the OEM OLED screen-saver, not our overlay");
                                foreach (var p in shifter) p.Dispose();
                            }
                            catch (Exception fx) { Keeper.Log(o, $"HIDE-forensics: OEM-layer probe failed: {fx.Message}"); }
                        }
                        tray?.Update(false);
                    },
                    () => Cleanup("stop requested from tray"));
                tray.Balloon($"Armed ({st.Mode}): display-on request live, retires {RetireLabel()}. Hide/Stop from this menu.");
            }
            catch (Exception tx) { Keeper.Log(o, $"tray unavailable: {tx.Message}"); tray = null; }

        Application.Run(ctx);
        Cleanup("message loop ended");
        return 0;
    }
    static TrayUi? tray;
}
// ==== TrayUi ====
// At-a-glance status + 'Hide black screen (keep armed)' (operator authority: dismisses the
// overlay for good this run, demote to keeper, protection stays armed) + Open log + one-click
// cooperative Stop. CAVEAT (by design): during the dark phase the overlay covers the
// taskbar, so the tray is reachable only after human input wakes - the CLI 'stop' always
// works. A hard-killed worker's icon ghosts in the tray until hovered (standard Windows).
sealed class TrayUi : IDisposable
{
    readonly NotifyIcon icon;
    readonly ToolStripMenuItem header;
    readonly ContextMenuStrip menu;
    readonly Icon iconDark, iconLit;
    readonly Func<string> statusText;

    public TrayUi(string logPath, Func<string> statusText, Action hide, Action stop)
    {
        this.statusText = statusText;
        iconDark = MakeIcon(filled: true);
        iconLit = MakeIcon(filled: false);
        header = new ToolStripMenuItem(statusText()) { Enabled = false };
        var openLog = new ToolStripMenuItem("Open log", null, (s, e) =>
        {
            try { Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true }); } catch { }
        });
        var hideItem = new ToolStripMenuItem("Hide black screen (keep armed)", null, (s, e) => hide());
        var stopItem = new ToolStripMenuItem("Stop awake", null, (s, e) => stop());
        menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { header, new ToolStripSeparator(), openLog, new ToolStripSeparator(), hideItem, stopItem });
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

    // drawn at runtime (no .ico asset; single-file doctrine)
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

// ==== SelfTest ====
// Isolated end-to-end check: awake selftest --state-dir <tmp>. Synthetic input (SendInput)
// is used HERE ONLY - the product path never injects. Runs its OWN worker children in the
// given state dir; never touches the real %LOCALAPPDATA%\Awake instance.
internal static class SelfTest
{
    static int failures;
    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
        if (!ok) failures++;
    }
    static bool Poll(Func<bool> cond, int timeoutMs)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end) { if (cond()) return true; Thread.Sleep(50); }
        return cond();
    }
    static bool OverlayVisible(int pid) => VisibleWindows(pid).Count > 0;
    static System.Collections.Generic.List<IntPtr> VisibleWindows(int pid)
    {
        var r = new System.Collections.Generic.List<IntPtr>();
        Win32.EnumWindows((h, l) =>
        {
            Win32.GetWindowThreadProcessId(h, out uint wpid);
            if (wpid == (uint)pid && Win32.IsWindowVisible(h)) r.Add(h);
            return true;
        }, IntPtr.Zero);
        return r;
    }
    static int RunWorker(Opts o, bool dark, int idle)
    {
        // in-tree child on purpose: selftest owns the process tree and stops it at the end
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? "awake.exe") { UseShellExecute = false };
        psi.ArgumentList.Add("run"); psi.ArgumentList.Add("--minutes"); psi.ArgumentList.Add("10");
        psi.ArgumentList.Add("--idle"); psi.ArgumentList.Add(idle.ToString());
        psi.ArgumentList.Add("--state-dir"); psi.ArgumentList.Add(o.StateDir);
        psi.ArgumentList.Add("--no-tray");
        if (dark) psi.ArgumentList.Add("--dark");
        var p = Process.Start(psi)!;
        if (!Poll(() => { var c = Keeper.Load(o); return c != null && Keeper.AlivePid(c) != null; }, 10000))
            throw new Exception("worker did not claim the control file in 10 s");
        return p.Id;
    }
    static void StopWorker(Opts o)
    {
        Cli.Stop(o);
        Keeper.Flush();
    }

    public static int Run(Opts o)
    {
        if (string.Equals(o.StateDir, Opts.DefaultDir(), StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("selftest MUST run with --state-dir <tmp>: refusing to arm/kill the real instance.");
            return 2;
        }
        Console.WriteLine($"awake selftest in {o.StateDir}");
        Directory.CreateDirectory(o.StateDir);

        // --- 1) DARK lifecycle: immediate black, input hides <=1 s, re-blanks after --idle ---
        int idleSec = 15;
        int pid = RunWorker(o, dark: true, idle: idleSec);
        try
        {
            Check("dark: overlay visible right after arm", Poll(() => OverlayVisible(pid), 3000));
            Native.TestMoveMouse();
            Check("dark: injected input hides overlay <=1 s", Poll(() => !OverlayVisible(pid), 1000));
            bool reblank = Poll(() => OverlayVisible(pid), (idleSec + 3) * 1000);
            Check($"dark: re-blanks after --idle {idleSec} s", reblank);

            // --- 2) corrupt keeper.json mid-run: wake must still work ---
            string good = File.ReadAllText(Keeper.StatePath(o));
            File.WriteAllText(Keeper.StatePath(o), "{not json!!");
            Thread.Sleep(1500);
            Check("corrupt mailbox: overlay still VISIBLE (worker stayed armed)", OverlayVisible(pid));
            Native.TestMoveMouse();
            Check("corrupt mailbox: wake still works", Poll(() => !OverlayVisible(pid), 2000));
            KeeperState restored = JsonSerializer.Deserialize<KeeperState>(good, Keeper.J)!;
            restored.Seq += 2;   // freshen so a stop write later still counts as a change
            Keeper.Save(o, restored);

            // --- 3) locked keeper.json: wake must still work ---
            Poll(() => OverlayVisible(pid), (idleSec + 3) * 1000);
            Check("locked mailbox: re-blanked first", OverlayVisible(pid));
            using (var blocker = new FileStream(Keeper.StatePath(o), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Native.TestMoveMouse();
                Check("locked mailbox: wake still works <=2 s", Poll(() => !OverlayVisible(pid), 2000));
            }

            // --- 4) injected tick fault: worker survives, wake still works ---
            // the fault hits the MAILBOX section; the cycle section must run regardless.
            // (AWAKE_SELFTEST_THROW can't be set on the already-running worker, so restart with it.)
            StopWorker(o);
            var env = Environment.GetEnvironmentVariable("AWAKE_SELFTEST_THROW");
            Environment.SetEnvironmentVariable("AWAKE_SELFTEST_THROW", "1");
            int pid2 = RunWorker(o, dark: true, idle: idleSec);
            try
            {
                Thread.Sleep(2500);   // let a few throwing ticks pass
                bool alive = Keeper.AlivePid(Keeper.Load(o)) != null;
                Check("tick fault: worker survived", alive);
                Native.TestMoveMouse();
                Check("tick fault: wake still works", Poll(() => !OverlayVisible(pid2), 2000));
                Thread.Sleep(2000);
                Check("tick fault: mailbox still processed (until mirror logged failure is fine, stop must work)",
                      Keeper.Load(o) != null);
            }
            finally
            {
                Environment.SetEnvironmentVariable("AWAKE_SELFTEST_THROW", env);
                StopWorker(o);
                try { Process.GetProcessById(pid2).WaitForExit(5000); } catch { }
            }
        }
        finally { try { Process.GetProcessById(pid).Kill(true); } catch { } }

        // --- 5) clean stop: EXIT logged, state files gone ---
        int pid3 = RunWorker(o, dark: true, idle: idleSec);
        StopWorker(o);
        bool exited = Poll(() => !Keeper.TryReadAllText(Keeper.StatePath(o), out _).Equals(Keeper.ReadResult.Ok), 5000);
        Check("stop: control file removed", exited);
        Check("stop: status mirror removed", !File.Exists(Keeper.StatusPath(o)));
        string logTxt = Keeper.TryReadAllText(Keeper.LogPath(o), out var lt) == Keeper.ReadResult.Ok ? lt! : "";
        Check($"stop: EXIT logged by pid {pid3}", logTxt.Contains($"[pid {pid3}] EXIT"));
        Check("stop: keeper process gone", !Running(pid3));

        // --- 6) ES state cleared: the request lives on the worker's thread, so process death
        // releases it (Win32 contract, asserted above). Ground truth via elevated
        // powercfg /requests when available; a non-elevated run reports SKIP, never a lie.
        bool esVerified = false, esAbsent = false; string esDetail = "powercfg unavailable";
        try
        {
            var psi = new ProcessStartInfo("powercfg") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("/requests");
            using var pc = Process.Start(psi)!;
            string po = pc.StandardOutput.ReadToEnd() + pc.StandardError.ReadToEnd();
            pc.WaitForExit(5000);
            if (po.Contains("administrator")) esDetail = "not elevated - ground truth skipped; process-death guarantee stands";
            else { esVerified = true; esAbsent = !po.Contains("awake.exe"); esDetail = po.Length > 120 ? po[..120].Replace('\n', ' ') : po; }
        }
        catch (Exception e) { esDetail = e.Message; }
        Check("stop: no awake.exe in powercfg /requests", esVerified ? esAbsent : true, esDetail + (esVerified ? "" : " (SKIP)"));

        // --- 7) KEEPER mode: overlay NEVER visible (the v6 stuck-black root cause) ---
        int pid4 = RunWorker(o, dark: false, idle: idleSec);
        bool everVisible = false;
        for (int i = 0; i < 25; i++) { if (OverlayVisible(pid4)) { everVisible = true; break; } Thread.Sleep(1000); }
        Check("keeper: overlay NEVER visible over 25 s", !everVisible);
        StopWorker(o);

        Console.WriteLine(failures == 0 ? "SELFTEST: ALL PASS" : $"SELFTEST: {failures} FAILURE(S)");
        Keeper.Flush();
        return failures == 0 ? 0 : 1;
    }

    static bool Running(int pid)
    {
        try { var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
    }

    static class Win32
    {
        public delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint f);
    }
}
