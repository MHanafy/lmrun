---
name: Awake
description: Toggle this PC's keep-awake protection (awake app: display-on execution request + pure-black zero-light pixel overlay) so long AI jobs survive the screen turning off, and its dark mode for dark-room runs without the driver's screen-off throttling. Use when the user says "keep the PC awake/running" or "dark mode", is about to start or is running a long transcription/model job, asks to undo keep-awake or stop the dark screen, or asks whether the machine will sleep.
---

# Awake (display-on keep-awake + zero-light dark overlay)

This machine uses Modern Standby (S0ix): when the display turns off, Windows
freezes compute and, during those idle-standby episodes, SILENTLY KILLS
background processes (no WER event, no exit log). The AMD driver also runs a
lower power policy whenever the display is logically off. Display-off is
therefore never the answer on this box.

The protection (since v7, 2026-11-27): the `awake` worker holds
`SetThreadExecutionState(ES_CONTINUOUS|ES_SYSTEM_REQUIRED|ES_DISPLAY_REQUIRED)`
on its UI thread for its whole process lifetime. Measured on this machine: the
request is accepted AND honoured (60 s AC blank timeout, 10 min no input: no
blank, zero Kernel-Power standby events), so the display never blanks, Modern
Standby never engages, and the driver never throttles. No input injection of
any kind anymore - the old F15 keepalive was built on a misread err 87 (a wrong
flag constant) and is gone. Process death releases the request automatically;
nothing persists after a clean stop.

**Proof of protection** = `status` says RUNNING **and** `hold: ACTIVE`.
Battery rule (operator decision): the request is held on AC only. Unplugging
RELEASES it (journal `HOLD RELEASED (battery)` + tray balloon - protection is
gone while on battery, by design so the laptop can never stay awake in a bag);
re-plugging re-takes it automatically (never wakes the overlay - only human
input does). `status` shows `hold: RELEASED` whenever unprotected.

Dark mode (`start --dark`) is the operator's dark-room mode: pixels OFF (the
panel is a verified OLED - black emits zero light), display logically ON, so
neither Windows nor the AMD driver ever engages a screen-off policy. The
overlay is black, topmost, primary-screen, cursor hidden; it wakes on real
human input (keyboard, mouse with real movement, click, wheel, touch, pen - all
verified) and re-blanks after `--idle` seconds of human idle (default 180).
Brightness and the ambient-light sensor are NEVER touched (operator rule: a
WMI brightness write syncs into the power plan on this build and can strand a
dim panel).

## Tool

**Implementation: `app\awake.cs`** - single-file C# app published
FRAMEWORK-DEPENDENT into **`<skill-dir>\app\bin\awake.exe`** (~200 KB; .NET 10
Windows Desktop runtime installed here; `app\bin\` is gitignored):

```powershell
$dk = "<skill-dir>\app\bin\awake.exe"   # e.g. C:\src\lmrun\skills\awake\app\bin\awake.exe
& $dk start --minutes 90 --dark   # standard arm: keep-awake + dark overlay NOW (re-blanks after 180 s human idle)
& $dk start --minutes 90          # keep-awake only (lit keeper, NO window)
& $dk status [--json]             # RUNNING + hold state + overlay phase + human idle + retire time
& $dk start --minutes 90          # RENEWAL while live = EXTEND (NEVER pass --dark as a renewal)
& $dk stop                        # cooperative stop via mailbox (force after 5 s); also kills the renewal timer
& $dk renew-wait --minutes 90     # renewal alarm: sleeps (window-5 min) then exits - run AS an agent background job
# options: --idle <s> (human idle before re-dark, default 180, min 15), --state-dir <path> (isolated instance),
#          --no-tray (suppress the tray icon: live status tooltip + "Hide black screen (keep armed)" + Open log + "Stop awake").
#          --heartbeat is REMOVED (v7): passing it is a hard error.
# rebuild after source changes:
#   dotnet publish "<skill-dir>\app\bin\..\awake.cs" -c Release -o "<skill-dir>\app\bin"
# isolated end-to-end test (uses synthetic input internally; safe, needs a temp dir):
#   & $dk selftest --state-dir <tmp>
```

Mailbox semantics (files in `%LOCALAPPDATA%\Awake\`: `keeper.json` control
file, `status.json` worker mirror, `awake.log` journal, `renewtimer.json` timer
box): `start` creates the control file or EXTENDS a live worker (single
instance via named mutex; an extend never re-blanks the screen); `stop` writes
`intent:"stop"` and the worker exits cleanly within ~1 s (`EXIT` logged, files
removed, request released); control file present + pid dead = **"protection is
GONE" alarm** (the next `start` clears it - check `status` at task checkpoints
on long runs). The worker is spawned **out-of-tree via WMI** so agent-shell
process-tree reaping cannot kill it. The tray's "Hide black screen (keep
armed)" dismisses the overlay for good this run (worker demotes to keeper,
never re-blanks) while the request stays armed - re-arm dark via stop +
`start --dark`.

**Renewal loop - the AGENT controls renewals.** A timed window is an estimate
and long jobs outrun it. The standard timed arm is TWO actions:
`start --minutes <N> --dark` and starting `renew-wait --minutes <N>` as a
background job via the agent's own background mechanism (NOT Start-Process - a
detached process cannot notify the agent). When the timer fires, the agent
decides:
- **still processing the armed task** => re-run `start --minutes <same N>`
  WITHOUT `--dark` (against a live worker this is an EXTEND: seamless, no
  restart, and it can never black the screen on a working operator), start the
  next timer, keep working. NEVER pass `--dark` in a renewal - against a dead
  worker it RE-ARMS darkening and can black the screen out from under the
  operator (report the death instead and decide with them);
- **task finished** => run `stop` (also kills any pending renewal timer);
- **`status` shows OFF** (the operator stopped it) => do NOT renew, do NOT
  respawn the timer - report it and let the loop end.
Only the agent renews, so a dead or idle agent degrades to the plain timed
backstop: the un-renewed window expires and the box sleeps. `stop` always kills
the timer box - a release must never leave a renewal timer behind.

## Rules

- **Arm/stop protocol**: at the START of a task that needs it, arm the standard
  setup - `start --minutes <N> --dark` AND the `renew-wait --minutes <N>` job
  as a background job. Dark is armed by default (the overlay only blanks after
  3 min of no human input, so watching progress is unaffected); skipped only
  if the user opts out. No UAC prompt on this default path. Honor every timer
  notification while armed: still working => renew (`start --minutes <same N>`,
  NO `--dark`) and respawn the timer; finished => `stop`; status says OFF =>
  stand down and report, never renew. Stop as the FINAL action before ending
  the turn. The build/validate/test step IS part of the task: release only
  after it finishes. If work resumes after a release, re-arm first (full
  standard setup, timer included).
- `TUNING-NOTES.md` beside this file is human maintainer documentation - do not
  read or follow it during task execution; only the operator, when tuning the
  skill itself, uses it.
- Only arm awake when the user asks for it or clearly intends a long unattended
  run; otherwise leave it OFF. Never change `powercfg` plans, sleep timeouts or
  ASUS/Fn+Q modes yourself - the toggle is all it does.
- awake writes ONLY under `%LOCALAPPDATA%\Awake\` (or the `--state-dir`).
- Verify only with `status` (RUNNING + `hold: ACTIVE`). Elevated
  `powercfg /requests` is the ground truth but triggers UAC - only run it if
  the user explicitly wants proof (the worker lists under DISPLAY and SYSTEM).
- If a job is still suspending while armed, do not escalate settings; check
  Kernel-Power event id 506 after the blank AND cross-check the journal:
  `HOLD ... HELD` at start, `DARK`/`WAKE` phase lines, `ALARM:` lines for
  failures. A 506 while `status` shows `hold: ACTIVE` means the mechanism
  stopped working (e.g. silent worker death - control file + dead pid): report
  it. `HOLD RELEASED (battery)` + 506s is the battery rule working as designed.
- **The unattended dark-room posture is DON'T LOCK** (operator decision): walk
  away with the session unlocked and the overlay engaged - on this verified-OLED
  panel that is genuinely zero light. Locking stays a free choice for physical
  security, with exactly one cost: the lock screen renders content (lit
  pixels); nothing here may darken the secure desktop. Lid close / power button
  / forced sleep still kill the process and force sleep no matter what is armed.
  Do not warn about locks and do not try to prevent them; just size the timed
  window so the hold outlives the job.
- On unattended finishes prefer letting the timed window expire over an explicit
  `stop` while nobody is at the machine - release drops the request and the box
  suspends within ~5 min, killing anything still running.
- Remind the user: protection is AC-only. Unplugging mid-run releases it
  (loudly); run `stop` before packing the machine.
