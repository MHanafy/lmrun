---
name: Awake
description: Toggle this PC's keep-awake state (PowerToys Awake) so long AI jobs survive the screen turning off, and its dark-screensaver mode (pure black pixel overlay) for dark-room runs without the driver's screen-off throttling. Use when the user says "keep the PC awake/running" or "dark mode", is about to start or is running a long transcription/model job, asks to undo keep-awake or stop the dark screen, or asks whether the machine will sleep.
---

# Awake (screen-off keep-running toggle + dark screensaver)

This machine uses Modern Standby: when the display turns off, Windows freezes
compute (measured signature: ~3x slowdown, then full suspend). The hold that
counts is the execution request the `dark` overlay process makes for its whole
life - `status` showing the screensaver RUNNING is the authoritative proof of
protection. The PowerToys Awake toggle is a supplement that only engages if
the machine is already awake when its settings file is written (2026-11-10:
armed while the box was dozing, PowerToys never applied it, and a freeze-
suspend landed 59 s after the overlay was stopped; if the machine may already
be dozing, run a short command to wake it, then arm). The AMD driver also
runs a lower power policy whenever the display is logically off, so `dark`
keeps the display ON and makes it black with an OLED overlay instead.

## Tool

Run the bundled script (same directory as this skill file):

```powershell
& "$HOME\.config\opencode\skills\awake\awake.ps1" on        # indefinite, screen still blanks
& "$HOME\.config\opencode\skills\awake\awake.ps1" on 90     # timed: auto-release after 90 minutes
& "$HOME\.config\opencode\skills\awake\awake.ps1" off       # normal power behavior (default)
& "$HOME\.config\opencode\skills\awake\awake.ps1" status    # report current mode
& "$HOME\.config\opencode\skills\awake\awake.ps1" dark      # dark screensaver: black now (pure black pixels - brightness untouched), any input wakes, re-blanks after 180 s idle (override: -IdleSeconds N)
& "$HOME\.config\opencode\skills\awake\awake.ps1" dark 90   # ...and auto-retire after 90 minutes (match the awake window)
& "$HOME\.config\opencode\skills\awake\awake.ps1" light     # stop dark screensaver immediately (safety net - normal exits self-restore)
& "$HOME\.config\opencode\skills\awake\renewtimer.ps1" -Minutes 90  # renewal timer: sleeps (window - 5 min) then exits - run AS A BACKGROUND JOB right after arming; its completion notifies the agent just before the window dies (see Renewal loop)
# opt-in only, no UAC on the plain dark path: add -LockGuard to raise the elevated helper (see below)
```

**Renewal loop - the AGENT controls renewals** (added 2026-11-15). A timed
window is an estimate and long jobs outrun it (the classic failure: a 3 h
transcription suspended at minute 90). So the standard timed arm is THREE
actions: `on <N>`, `dark <N>`, and starting `renewtimer.ps1 -Minutes <N>` as a
background job via the agent's own background mechanism (NOT Start-Process -
a detached process cannot notify the agent). The timer itself is dumb: it
sleeps N-5 minutes and exits; the completion notification is the retrigger
point. When it fires, the agent decides:
- **still processing the armed task** => re-run `awake.ps1 on <same N>` (this
  extends the timed window AND a live timed dark overlay in lockstep via the
  overlay state file's `until` mailbox - seamless, no restart, no flicker;
  `on` never STARTS darkening), then start the next timer, and keep working;
- **task finished** => run the release sequence; the `off` step terminates any
  pending renewal timer mechanically, even if the agent forgot;
- **`status` shows OFF** (the operator flipped the toggle) => do NOT renew, do
  NOT respawn the timer - report it and let the loop end.
Only the agent renews, so a dead or idle agent degrades to the plain timed
backstop: the un-renewed window simply expires and the box sleeps. `status`
reports the pending timer; `off` always kills it - a release must never leave
a renewal timer behind.

The **lock guard is opt-in** (`-LockGuard`) since 2026-11-10: no UAC prompt on
the normal path. It was demoted because measurement showed it unnecessary -
a locked session keeps full speed while the overlay holds its execution
request. With `-LockGuard` an elevated helper (ONE UAC prompt, raised FIRST
while the screen is still lit; screensaver engages only after it is answered)
forces the display ON if locked; restore it only if lock-throttling behavior
ever comes back. When armed it self-restores at window expiry or within ~15 s
of `light`; `status` reports its state. If an armed guard ever dies with its
setting stuck at 0 (benign: locks keep the display on), restore it with the
user's approval:
`powercfg /setacvalueindex SCHEME_CURRENT SUB_VIDEO 8ec4b3a5-6868-48c2-be75-4f3044be88a7 60` + `setactive`.

Since v5 (2026-11-16, operator decision) `dark` NEVER touches brightness or
the ambient-light sensor - black pixels plus the overlay's execution-request
hold are the whole mechanism. Nothing persists outside the overlay process,
so a force-kill can strand nothing (the ES request dies with it) and there is
no restore that can fail; `light` = stop + journal cleanup. Accepted trade:
the LOCK SCREEN stays LIT when you lock and step away (the overlay cannot
paint the secure desktop) - locked sessions keep full speed, they are just no
longer dark. Lid close / power button still force sleep no matter what is
armed - never close the lid during an armed run.

`dark` is the operator's dark-room mode: pixels off (OLED), display logically
ON, so neither Windows nor the AMD driver ever engages its screen-off power
policy. The detached overlay holds display+system execution requests for its
whole life and releases hold + cursor whenever it exits (idle cycle,
duration expiry, or kill via `light`). It is part of the standard arm at task
start (see Rules). Invoking it MID-task on the operator's behalf without being
asked remains forbidden - the screen must never go dark while they are actively
working in front of it.

Prefer a timed mode (`on <minutes>`) when the user names a job with a rough
duration, so the machine reverts to normal sleep on its own - and pair every
timed arm with its renewal timer (see Renewal loop); a timed arm without the
timer is an incomplete arm.

## Rules

- **Arm/stop protocol**: at the START of a task that needs it, arm the standard
  setup - `on <minutes>` AND `dark <same minutes>` AND the renewal timer as a
  background job, always timed (see Renewal loop above; the timer is what keeps
  an undersized window from suspending a long job). Dark is armed by default
  (operator's standard config: the screensaver
  only blanks after 3 min of no input, so watching progress is unaffected);
  omit `dark` only if the user opts out for that task. Expect no UAC prompt
  on this default path (the lock guard is opt-in). Honor every timer
  notification while armed: still working => renew (`on <same minutes>`) and
  respawn the timer; finished => release; status says OFF => stand down and
  report, never renew. Stop as the
  FINAL action before ending the turn: `off` (terminates any pending renewal
  timer), plus `light` if `status` shows a
  screensaver OR lock guard still running (light also stands the guard down -
  it self-restores within ~15 s). Timed expiry is the backstop, the explicit
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
- The script writes `%LOCALAPPDATA%\Microsoft\PowerToys\Awake\settings.json`;
  the running Awake module applies it within ~2 seconds. If PowerToys is not
  running, the script warns; the change applies when PowerToys starts.
- Verify only with `status`. Elevated `powercfg /requests` is the ground truth
  but triggers UAC - only run it if the user explicitly wants proof.
- If a job is still suspending with Awake ON, do not escalate settings; check
  for Kernel-Power event id 506 after the screen blanked and report findings.
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
- Remind the user, when arming indefinitely on a laptop: switch Awake OFF before
  unplugging or packing the machine.
- **Locking remains the recommended posture when the operator steps away** -
  secure AND full speed: measured on the real ASR workload, locked+black
  10.3-10.6 s vs unlocked 10.2-10.7 s per identical 90 s clip, zero standby
  events. Since v5 the lock screen itself stays LIT (brightness writes were
  removed; the overlay cannot paint the secure desktop) - the operator
  accepted this trade: locked runs are secure and fast, just not dark.
  Do not warn about locks and
  do not try to prevent them; just size the timed `dark` window so the hold
  outlives the job. On unattended finishes prefer letting the timed window
  expire over an explicit
  `off`+`light` while nobody is at the machine - release drops the hold and the
  box suspends within ~1 min, killing anything still running (2026-11-10, twice).
