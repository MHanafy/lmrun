---
name: Awake
description: Toggle this PC's keep-awake protection (awake app: no-op F15 key-injection keepalive + pure-black zero-light pixel overlay) so long AI jobs survive the screen turning off, and its dark mode for dark-room runs without the driver's screen-off throttling. Use when the user says "keep the PC awake/running" or "dark mode", is about to start or is running a long transcription/model job, asks to undo keep-awake or stop the dark screen, or asks whether the machine will sleep.
---

# Awake (screen-off keep-running toggle + dark screensaver)

This machine uses Modern Standby (S0ix): when the display turns off, Windows
freezes compute and, during those idle-standby episodes, SILENTLY KILLS
background processes - measured 2026-11-17: no WER event, no exit log, journal
left behind; reproduced with decoy processes AND with the overlay itself while
its execution-request hold was armed. The v5 idea that the ES hold was "the
authoritative proof of protection" is FALSE on this build: ES_SYSTEM/AWAYMODE
do not stop Modern-Standby entry, and the only flag that would
(ES_DISPLAY_REQUIRED) is rejected here (err 87 - display power is OS-owned).
The real mechanism since v6: the worker injects a no-op F15 keypress every
HeartbeatSeconds. Injected input resets the system idle timers, so
the display never blanks and Modern Standby never engages. Proof of protection
(since v6.4.2, see CORRECTION below): `status` RUNNING **and** fresh
`KEEPALIVE #n ... sent=2` lines with `session idle 0s` in awake.log. A growing
KEEPALIVE count alone is NOT proof - pre-v6.4.2 builds logged phantom nudges.
Since v6.1 / awake (2026-11-17) the heartbeat runs for the WHOLE process
lifetime, not only while dark - closing the Active-phase race where the DC
blank timeout (180 s) could beat the first dark-phase nudge.
**v6.4.2 CORRECTION (2026-11-23): the C# worker's heartbeat silently NEVER
RAN until this version.** The INPUT union declared only KEYBDINPUT (24 B), so
Marshal.SizeOf(INPUT) was 32 instead of the x64-required 40 (union must size to
MOUSEINPUT) and every SendInput died with err 87 ERROR_INVALID_PARAMETER - the
return value was unchecked, so KEEPALIVE log lines claimed injections that never
happened (forensic fingerprint: worker-side `session idle` grows exactly
+HeartbeatSeconds per KEEPALIVE; a real nudge resets it to 0). During that era
the box survived dark runs on the ES sys+away hold + the unattended-sleep-timeout
fix while the OS blanked the display at its own timeout (AC 300 s / DC 180 s) and
OEM layers (AMD power policy, AsusOLEDShifter - the box runs it) took over the
screen under the overlay: this is the confirmed root cause of the operator's
"stuck black screen, clicking/keys ignored until tray stop" reports. Fix: union
carries MOUSEINPUT (START line now logs sizeof(INPUT)=40) and the heartbeat
CHECKS SendInput - failures log 'ALARM: F15 injection FAILED' once a minute.
Proof of protection is a KEEPALIVE line containing `sent=2` with `session idle 0s`.
The PowerToys
Awake dependency is RETIRED (its ES hold was measured inert against idle
standby here; the "keeper" mode of awake IS the keep-awake) - the legacy
PowerToys-based ps1 scripts remain in the folder as fallback until deleted.
The AMD driver also runs a lower power policy whenever the display
is logically off, so dark mode keeps the display ON (via the keepalive) and makes
it black with an OLED overlay instead.

## Tool

**Current implementation: `app\awake.cs`** - a single-file C# app published FRAMEWORK-DEPENDENT
into **`<skill-dir>\app\bin\awake.exe`** (~200 KB; needs the .NET 10 Windows Desktop
runtime, already installed here; the repo tracks only the source - `app\bin\` is gitignored.
Self-contained publish deliberately skipped: extraction/startup overhead):

```powershell
$dk = "<skill-dir>\app\bin\awake.exe"   # e.g. C:\src\lmrun\skills\awake\app\bin\awake.exe
& $dk start --minutes 90 --dark   # standard arm: keep-awake + dark overlay NOW (black after 180 s real idle)
& $dk start --minutes 90          # keep-awake only (lit keeper, no window)
& $dk status [--json]             # truth = control file + pid liveness
& $dk start --minutes 90          # RENEWAL while live = EXTEND (NEVER pass --dark as a renewal)
& $dk stop                        # cooperative stop via mailbox (force after 5 s); also kills the renewal timer
& $dk renew-wait --minutes 90     # renewal alarm: sleeps (window-5 min) then exits - run AS an agent background job
# options: --idle <s> (human idle before re-dark, default 180, min 15), --heartbeat <s> (F15 cadence, default 30), --state-dir <path> (alternate instance, for testing),
#          --no-tray (suppress the worker's tray icon: live status tooltip + "Hide black screen (keep armed)" + Open log + "Stop awake" menu;
#          in the dark phase the overlay covers the taskbar, so the tray is reachable only after human input wakes - CLI stop always works.
#          "Hide black screen" shows when the menu opens around a dark phase: dismisses the screen FOR GOOD this run - the worker
#          demotes to keeper (never re-blanks, status reflects it) while keepalive stays armed; re-arm dark via stop + start --dark.
#          Offer it when the operator wants the black screen gone but the job protected; it is manual (GUI) only - agents keep using CLI stop.)
# rebuild after source changes:
#   dotnet publish "<skill-dir>\app\awake.cs" -c Release -o "<skill-dir>\app\bin"
# (Native AOT deliberately NOT used: WinForms is unsupported by AOT/trimming; self-contained
#  single-file also NOT used: operator skipped it for extraction/startup overhead - this box
#  has the desktop runtime, framework-dependent is instant: ~250 ms cold / ~120 ms warm.)
# Development without publishing also works from "<skill-dir>\app": `dotnet run awake.cs -- <verb>`
# (it spawns the worker from an installed runtime copy to avoid locking the temp build).
```

Mailbox semantics (files in `%LOCALAPPDATA%\Awake\`: `keeper.json` control
file, `awake.log` journal, `renewtimer.json` timer box): `start` creates the
control file or EXTENDS a live worker (single instance via named mutex; an extend
never re-blanks the screen); `stop` just writes `intent:"stop"` and the worker
exits cleanly within ~1 s (cursor shown, ES cleared, `EXIT` logged, file deleted);
control file present + pid dead = **"protection is GONE" alarm** (the next
`start` clears it - check `status` at task checkpoints on long runs).
**Silent-killer ROOT CAUSE identified 2026-11-18** (six overnight deaths: heartbeat
cut mid-`KEEPALIVE`, no `EXIT`, zero WER/event-log traces, OS running the whole
time, no Kernel-Power 506 anywhere near the deaths): NOT standby - the agent
harness reaps its shell's **process tree** when the session that ran `start`
ends, and the old in-tree `Process.Start` worker was a child of that shell.
The old in-tree ShellExecute spawn was a child of the agent shell, so harness tree-reaping took it.
Fix (v6.2): the worker is spawned **out-of-tree via WMI `Win32_Process.Create`**
(parent = `WmiPrvSE.exe`, no shared console/job). The exe is console-subsystem (the CLI needs
stdout) so the WMI call passes `Win32_ProcessStartup.ShowWindow = 0` (SW_HIDE) - otherwise the
worker shows a console window; `status` is the UI and `MainWindowHandle` is 0. Verify the parent with
`Get-CimInstance Win32_Process -Filter "ProcessId=<pid>"`), with the old
ShellExecute spawn kept only as fallback; `awake.log` records
`[cli] worker spawned via wmi (out-of-tree)` (or `shell ... FALLBACK`) per arm.

**Legacy fallback (PowerToys-based, superseded 2026-11-17, kept until deleted):**

```powershell
& "<skill-dir>\awake.ps1" on 90 / off / status / dark 90 / light
& "<skill-dir>\renewtimer.ps1" -Minutes 90
# <skill-dir> = the directory of this skill file (e.g. C:\src\lmrun\skills\awake).
```

**Renewal loop - the AGENT controls renewals** (added 2026-11-15). A timed
window is an estimate and long jobs outrun it (the classic failure: a 3 h
transcription suspended at minute 90). So the standard timed arm is TWO
actions: `start --minutes <N> --dark` and starting `renew-wait --minutes <N>`
as a background job via the agent's own background mechanism (NOT Start-Process -
a detached process cannot notify the agent). The timer itself is dumb: it
sleeps N-5 minutes and exits; the completion notification is the retrigger
point. When it fires, the agent decides:
- **still processing the armed task** => re-run `start --minutes <same N>`
  WITHOUT `--dark` (against a live worker this is an EXTEND: it pushes the
  `until` mailbox - seamless, no restart, no flicker, and it can never
  black the screen on a working operator), start the next timer, keep working.
  NEVER pass `--dark` in a renewal - against a dead worker `start --dark`
  RE-ARMS darkening and can black the screen out from under the operator
  (report the death instead and decide with them);
- **task finished** => run `stop` (terminates the worker and any pending
  renewal timer mechanically, even if the agent forgot);
- **`status` shows OFF** (the operator stopped it) => do NOT renew, do NOT
  respawn the timer - report it and let the loop end.
Only the agent renews, so a dead or idle agent degrades to the plain timed
backstop: the un-renewed window simply expires and the box sleeps. `stop`
always kills the timer box - a release must never leave a renewal timer behind.

The **lock guard is legacy ps1-only** (`-LockGuard` opt-in since 2026-11-10;
awake does not implement it - it was demoted precisely because measurement
showed it unnecessary, see below). No UAC prompt on the normal path either way.
Measurement: a locked session keeps full speed while the keepalive runs.
With `-LockGuard` an elevated helper (ONE UAC prompt, raised FIRST
while the screen is still lit; screensaver engages only after it is answered)
forces the display ON if locked; restore it only if lock-throttling behavior
ever comes back. When armed it self-restores at window expiry or within ~15 s
of `light`; `status` reports its state. If an armed guard ever dies with its
setting stuck at 0 (benign: locks keep the display on), restore it with the
user's approval:
`powercfg /setacvalueindex SCHEME_CURRENT SUB_VIDEO 8ec4b3a5-6868-48c2-be75-4f3044be88a7 60` + `setactive`.

Since v5 (2026-11-16, operator decision) dark mode NEVER touches brightness or the
ambient-light sensor. v6 (2026-11-17) replaced the (ineffective) ES-hold
protection story with the input keepalive: the worker injects a
no-op F15 keypress every `--heartbeat` (default 30) s - injected input
resets Windows' display/sleep idle timers, so blanking and Modern Standby never
engage; wake/re-blank use low-level hooks that see HUMAN input only (injected
events are filtered, so the heartbeat never wakes the overlay) PLUS, since 2026-11-18,
a raw-input sink (RIDEV_INPUTSINK, HID digitizer page 0x0D usages 0x04/0x05) on the
overlay's hidden window - LL hooks are blind to TOUCH (its mouse-emulation carries
LLMHF_INJECTED), so touch/pen wake only works through that sink; the START log line
shows `touch sink=True/False`. Since v6.1
(awake, 2026-11-17) the heartbeat runs for the WHOLE process lifetime.
Nothing
persists outside the worker process - the heartbeat stops with it, a kill
strands nothing (the control file is the only trace, and `start` clears it);
but that also means worker dead = protection GONE and the box
sleeps within ~5 min (`status` reporting DEAD with a control file present is
the reliable alarm). PANEL TRUTH, hardware-verified
2026-11-17 (`WmiMonitorID` SDC41B0 = Samsung ATNA33AA08-0, the ASUS ProArt PX13
panel): this is an **OLED** - black pixels emit NO light, so the dark overlay is
a genuinely ZERO-LIGHT screen at any brightness setting (and costs the panel no
wear). Brightness and the ambient sensor are therefore irrelevant to darkness
and stay untouched; a same-day "v7" experiment writing the WMI backlight was
reverted the same day (`WmiSetBrightness` syncs into the plan setting - a
stranded-dim-panel risk class with no upside here). UNATTENDED ZERO-LIGHT
POSTURE (operator decision 2026-11-17): step away WITHOUT locking - the lock
screen renders content (clock/wallpaper = lit pixels) and nothing may darken
the secure desktop. Lid close / power button / forced sleep still kill the
process and force sleep no matter what is armed - never close the lid during an
armed run. LOCKED-SESSION BEHAVIOR VALIDATED (2026-11-17): with the box locked
and unattended, the keepalive stream continued and produced ZERO Kernel-Power
506/566 events past the 300 s blank-timeout boundary - injected input DOES hold
a locked session out of Modern Standby; the overlay witnessed SessionLock and
stayed dark+healthy throughout.

Dark mode (`start --dark`) is the operator's dark-room mode: pixels OFF (verified OLED = zero
light), display logically ON (the keepalive prevents the OS ever blanking it), so neither Windows nor the
AMD driver ever engages its screen-off power policy - and the S0ix standby that
kills background processes never engages either. The detached worker releases
cursor + holds whenever it exits (the idle cycle never ends the process; duration
expiry or `stop` do). It is part of the standard arm at task
start (see Rules). Invoking it MID-task on the operator's behalf without being
asked remains forbidden - the screen must never go dark while they are actively
working in front of it (2026-11-17: an arm/re-arm while the operator was at the
keyboard got the overlay force-killed within minutes - Task-Manager style,
journal intact).

Prefer a timed mode (`on <minutes>`) when the user names a job with a rough
duration, so the machine reverts to normal sleep on its own - and pair every
timed arm with its renewal timer (see Renewal loop); a timed arm without the
timer is an incomplete arm.

## Rules

- **Arm/stop protocol**: at the START of a task that needs it, arm the standard
  setup - `start --minutes <N> --dark` AND the `renew-wait --minutes <N>` job as a
  background job (see Renewal loop above; the timer is what keeps
  an undersized window from suspending a long job). Dark is armed by default
  (operator's standard config: the overlay
  only blanks after 3 min of no input, so watching progress is unaffected);
  dark mode is skipped only if the user opts out for that task. Expect no UAC prompt
  on this default path. Honor every timer
  notification while armed: still working => renew (`start --minutes <same N>`,
  NO `--dark`) and respawn the timer; finished => `stop`; status says OFF =>
  stand down and report, never renew. Stop as the
  FINAL action before ending the turn - the cooperative `stop` terminates the
  worker and any pending renewal timer. Timed expiry is the backstop, the explicit
  stop is the courtesy - never leave either armed indefinitely behind you.
  The build/validate/test step IS part of the task: release only after it
  finishes, never when the deliverable files are written (2026-11-10: a
  session released at 18:13 then built un-armed at 18:16 - within minutes of
  the 5-min display timeout). If work resumes after a release, re-arm first
  (full standard setup, timer included).
- `TUNING-NOTES.md` beside this file is human maintainer documentation - do not
  read or follow it during task execution; only the operator, when tuning the
  skill itself, uses it.
- Only arm awake when the user asks for it or clearly intends a long unattended
  run; otherwise leave it OFF. Never change `powercfg` plans, sleep timeouts,
  ULPS, or ASUS/Fn+Q modes yourself (the built-in lock guard, the documented
  repairs below, and their documented manual restores are the only exceptions,
  and only with the user's approval) - the toggle is all it does.
- awake writes ONLY under `%LOCALAPPDATA%\Awake\` (control file, journal,
  timer box, `runtime\` copy) - PowerToys is not involved. The legacy ps1 scripts
  use `%LOCALAPPDATA%\Microsoft\PowerToys\Awake\` instead (`settings.json` for the
  Awake module, `darkstate.json` + `darkscreen.log` for the old overlay).
- Verify only with `status`. Elevated `powercfg /requests` is the ground truth
  but triggers UAC - only run it if the user explicitly wants proof.
- If a job is still suspending while armed, do not escalate settings; check
  for Kernel-Power event id 506 after the screen blanked AND cross-check
  the journal (`%LOCALAPPDATA%\Awake\awake.log`; `darkscreen.log` for
  legacy ps1 runs): the worker logs `KEEPALIVE #n ... sent=2` every landed
  nudge - a gap between the last KEEPALIVE and the 506 means the process (not
  the mechanism) died at that moment; find out what killed it. A 506 while
  KEEPALIVE lines keep streaming means the MECHANISM stopped working: look for
  `ALARM: F15 injection FAILED sent=... err=...` (v6.4.2: injection rejected
  by the OS while the worker lives - protection is only the ES hold, report
  it) and for the pre-v6.4.2 phantom fingerprint (KEEPALIVE lines with
  `session idle` growing +heartbeat s per nudge instead of resetting to 0).
  `ALARM: input-blind hooks` is the separate LL-hook-starvation fingerprint
  (wakes still work via the session cross-check).
- Known root cause on this machine (found 2026-11-09): OEM plans (e.g.
  `PD_Standard`) can carry a hidden per-plan override disabling the **Away Mode
  Policy** (sleep subgroup `238c9fa8-0aad-41ed-83f4-97be242c8f20`, setting
  `25dfa149-5dd1-4736-b5ab-e8a37b5b8187`). With it 0, the OS ignores Awake's
  request entirely and screen-off always suspends. Diagnose (no admin needed):
  `reg query HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\<active-scheme-guid>\238c9fa8-0aad-41ed-83f4-97be242c8f20\25dfa149-5dd1-4736-b5ab-e8a37b5b8187`
  (scheme guid from `powercfg /getactivescheme`). If `ACSettingIndex` is 0, the
  repair - only with the user's approval, it needs elevation - is
  `powercfg /setacvalueindex SCHEME_CURRENT 238c9fa8-0aad-41ed-83f4-97be242c8f20 25dfa149-5dd1-4736-b5ab-e8a37b5b8187 1`
  followed by `powercfg /setactive SCHEME_CURRENT`. OEM utilities (Armoury Crate)
  may silently reset plan values, so re-check this override whenever Awake
  "shows ON" but suspends still happen.
- Second factor, fixed 2026-11-09: the hidden **"System unattended sleep
  timeout"** (sleep subgroup setting `7bc4a2f9-d8fc-4469-b07b-33eb785aaca0`,
  stock 120 s) caused a real ~7 s standby freeze ≈2 min after the last user
  input even with Awake armed and away mode allowed. It is now set to 0
  (Never) on AC only via
  `powercfg /setacvalueindex SCHEME_CURRENT SUB_SLEEP 7bc4a2f9-d8fc-4469-b07b-33eb785aaca0 0`
  (+ `powercfg /setactive SCHEME_CURRENT`); DC intentionally left at stock.
  Verify with `reg query HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\<scheme>\238c9fa8-0aad-41ed-83f4-97be242c8f20\7bc4a2f9-d8fc-4469-b07b-33eb785aaca0`
  expecting `ACSettingIndex 0x0`. A 506+566 pair in the same second with no
  heartbeat gap is a harmless aborted attempt; look for Kernel-Power 506
  events that coincide with the job's time gaps.
- Remind the user, when arming indefinitely on a laptop: run `stop` (or flip the
  legacy toggle OFF) before unplugging or packing the machine.
- **The unattended dark-room posture is DON'T LOCK** (operator decision
  2026-11-17): walk away with the session unlocked and the overlay engaged -
  on this verified-OLED panel that is genuinely zero light, and protection is
  identical unlocked vs locked. Locking stays a free choice for physical
  security, with exactly one cost: the lock screen renders content (lit
  pixels); nothing here may darken the secure desktop (brightness writes are
  permanently out - they sync into the plan and strand dimness). True
  "display off + full speed" is IMPOSSIBLE on this S0ix box: display-off is
  precisely what triggers the AMD power-throttle policy and the
  idle->blank->S0ix 506 kill chain, and injected keepalive input always
  re-powers the display - do not attempt it. Protection under a lock IS
  validated (2026-11-17: zero 506/566 past the blank boundary while locked,
  overlay alive; ASR speed parity locked vs unlocked), so a lock encountered
  mid-run costs darkness, never safety.
  Do not warn about locks and
  do not try to prevent them; just size the timed window so the hold
  outlives the job. On unattended finishes prefer letting the timed window
  expire over an explicit
  `stop` while nobody is at the machine - release drops the keepalive and
  the box suspends within ~5 min, killing anything still running (2026-11-10, twice).
