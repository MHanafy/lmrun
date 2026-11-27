# awake — keep a Windows laptop running while AI jobs work

Modern Standby (S0ix) laptops freeze compute the moment the display turns off, and
during those idle-standby episodes Windows **silently kills background processes** —
no crash event, no log line. Long AI jobs (model transcriptions, big builds) are
exactly what gets taken out, usually unattended. This skill arms protection that
measurably prevents it — on the machine it was tuned for, an ASUS ProArt PX13
(Modern Standby, Samsung OLED, AMD APU, Windows 11 26300).

## How it works

`awake` is a single-file C# app (`app/awake.cs`, .NET 10). `start` spawns a detached
worker that holds **`SetThreadExecutionState(ES_CONTINUOUS|ES_SYSTEM_REQUIRED|
ES_DISPLAY_REQUIRED)`** on its UI thread for its whole lifetime: on this box the
request is measured accepted *and honoured* — the display never blanks, Modern
Standby never engages, and the AMD screen-off throttle never applies. Process death
releases the request; nothing persists after a clean stop. The request is held on AC
only (releases on battery, re-takes on AC — a laptop must never stay awake in a bag).

The pre-v7 design injected a no-op F15 keypress instead; it existed only because the
original PowerShell code used wrong ES flag constants (`0x4` = the unsupported
`ES_USER_PRESENT` fails with err 87, which was misread as "display requests are
rejected here"). All of that machinery — injection, low-level hooks, event tagging —
is gone; `GetLastInputInfo` is now a pure human clock.

Optionally (`--dark`) the worker also shows a pure-black topmost overlay: on the
verified OLED panel, black pixels emit zero light, so the screen is physically dark
while the display stays logically ON. It wakes on real human input (keys, mouse,
touch, pen — verified) and re-blanks after N seconds of human idle. Brightness and
the ambient-light sensor are never touched. `status` alarms (DEAD) when the control
file outlives its pid — worker dead means protection gone.

## Use

```powershell
$dk = "<skill-dir>\app\bin\awake.exe"
& $dk start --minutes 90 --dark    # arm: keep-awake + dark overlay
& $dk status                       # RUNNING + hold: ACTIVE = protected
& $dk start --minutes 90           # renewal of a live worker = extend (never re-blanks)
& $dk stop                         # cooperative stop (worker exits itself, ~1 s)
& $dk renew-wait --minutes 90      # renewal alarm - run as an agent background job
# options: --idle <s> (re-dark delay, default 180) --state-dir <path> (isolated instance)
# --heartbeat is removed (v7) and hard-errors if passed.
```

CLI and worker talk through a small mailbox in `%LOCALAPPDATA%\Awake\`
(`keeper.json` control file, `status.json` worker mirror, `awake.log` journal):
`stop` only flips an intent flag and the worker retires itself cleanly; the
force-kill is a backstop.

## Build & test

```powershell
dotnet publish "<skill-dir>\app\awake.cs" -c Release -o "<skill-dir>\app\bin"
& "<skill-dir>\app\bin\awake.exe" selftest --state-dir <tmp>   # isolated end-to-end
```

Framework-dependent publish (~200 KB, needs the .NET 10 Windows Desktop runtime;
`app\bin\` is gitignored). Native AOT deliberately not used — WinForms is unsupported
by AOT/trimming. During development `dotnet run awake.cs -- <verb>` works from `app\`.

## Files

| file | role |
| --- | --- |
| `app/awake.cs` | the app (source-only; the current implementation) |
| `app/step0/` | the throwaway 2026-11-27 verification harness (Step-0 evidence in TUNING-NOTES) |
| `SKILL.md` | the opencode skill: doctrine, command surface, renewal loop, rules |
| `TUNING-NOTES.md` | maintainer history: measurements, bug post-mortems, decisions |

The tuning is Windows 11 + AMD APU (Modern Standby) specific — treat it as a worked
example of diagnosing your own machine. Install by placing this folder under
`~\.config\opencode\skills\awake`.
