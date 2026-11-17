# awake — keep a Windows laptop running while AI jobs work

Modern Standby (S0ix) laptops freeze compute the moment the display turns off, and
during those idle-standby episodes Windows **silently kills background processes** —
no crash event, no log line. Long AI jobs (model transcriptions, big builds) are
exactly what gets taken out, usually unattended. This skill arms protection that
measurably prevents it.

## How it works

`awake` is a single-file C# app (`app/awake.cs`, .NET 10). `start` spawns a detached
worker that injects a **no-op F15 keypress** for the whole run: injected input resets
Windows' display/sleep idle timers, so the display never blanks and Modern Standby
never engages. Proof of protection is the ABSENCE of Kernel-Power 506 standby events,
not a settings toggle — `SetThreadExecutionState` holds were measured **inert** against
idle standby on this machine, and PowerToys Awake is retired as a dependency.

Optionally (`--dark`) it also shows a pure-black topmost overlay: on an OLED, black
pixels emit zero light, so the screen is physically dark while the display stays
logically ON — neither Windows nor the AMD driver engages its screen-off power policy.
Human input wakes it instantly (injected F15 is filtered out by the low-level hooks);
it re-blanks after N seconds of real idle. Brightness and the ambient-light sensor are
never touched. Nothing persists after a clean stop — worker dead means protection
gone, which is why `status` alarms when the control file outlives its pid.

## Use

```powershell
$dk = "<skill-dir>\app\bin\awake.exe"
& $dk start --minutes 90 --dark    # arm: keep-awake + dark overlay
& $dk status                       # RUNNING + nudge count = protected
& $dk start --minutes 90           # renewal of a live worker = extend (never re-blanks)
& $dk stop                         # cooperative stop (worker exits itself, ~1 s)
& $dk renew-wait --minutes 90      # renewal alarm - run as an agent background job
# options: --idle <s> (re-dark delay, default 180) --heartbeat <s> (F15 cadence,
# default 30) --state-dir <path> (isolated instance for testing)
```

CLI and worker talk through a small mailbox in `%LOCALAPPDATA%\Awake\`
(`keeper.json` control file + `awake.log` journal): `stop` only flips an intent flag
and the worker retires itself cleanly; the force-kill is a backstop.

## Build

```powershell
dotnet publish "<skill-dir>\app\awake.cs" -c Release -o "<skill-dir>\app\bin"
```

Framework-dependent publish (~200 KB, needs the .NET 10 Windows Desktop runtime;
`app\bin\` is gitignored). Native AOT deliberately not used — WinForms is unsupported
by AOT/trimming. During development `dotnet run awake.cs -- <verb>` works from `app\`.

## Files

| file | role |
| --- | --- |
| `app/awake.cs` | the app (source-only; the current implementation) |
| `SKILL.md` | the opencode skill: doctrine, command surface, renewal loop, rules |
| `TUNING-NOTES.md` | maintainer history: measurements, bug post-mortems, decisions |
| `awake.ps1` / `darkscreen.ps1` / `renewtimer.ps1` | legacy PowerShell fallback (superseded) |
| `lockguard.ps1` | legacy opt-in lock-session guard (demed: locks keep full speed) |

The tuning is Windows 11 + AMD APU (Modern Standby) specific — treat it as a worked
example of diagnosing your own machine. Install by placing this folder under
`~\.config\opencode\skills\awake`.
