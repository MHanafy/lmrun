---
name: Awake
description: Toggle this PC's keep-awake state (PowerToys Awake) so long AI jobs survive the screen turning off, and its dark-screensaver mode (pure black pixel overlay) for dark-room runs without the driver's screen-off throttling. Use when the user says "keep the PC awake/running" or "dark mode", is about to start or is running a long transcription/model job, asks to undo keep-awake or stop the dark screen, or asks whether the machine will sleep.
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
The real mechanism since v6: the `dark` overlay injects a no-op F15 keypress
every 30 s while darkened. Injected input resets the system idle timers, so
the display never blanks and Modern Standby never engages - `status` showing
the screensaver RUNNING (and a growing KEEPALIVE count in darkscreen.log) is
the proof of protection, verified by the ABSENCE of Kernel-Power 506 events.
The PowerToys Awake toggle remains as a nominal window holder / harmless
redundancy (if the machine may already be dozing, run a short command to wake
it before arming). The AMD driver also runs a lower power policy whenever the display
is logically off, so `dark` keeps the display ON (via the keepalive) and makes
it black with an OLED overlay instead.

## Tool

Run the bundled script (same directory as this skill file):

```powershell
& "<skill-dir>\awake.ps1" on        # nominal timed-window holder / redundancy
& "<skill-dir>\awake.ps1" on 90     # timed: auto-release after 90 minutes
& "<skill-dir>\awake.ps1" off       # normal power behavior (default)
& "<skill-dir>\awake.ps1" status    # report current mode
& "<skill-dir>\awake.ps1" dark      # dark screensaver: black now (verified OLED: black pixels = zero light; brightness/sensor never touched); F15 keepalive every 30 s while dark keeps the box out of Modern Standby; HUMAN input wakes, re-blanks after 180 s real idle (overrides: -IdleSeconds N, -HeartbeatSeconds N)
& "<skill-dir>\awake.ps1" dark 90   # ...and auto-retire after 90 minutes (match the awake window)
& "<skill-dir>\awake.ps1" light     # stop dark screensaver (= stop the keepalive) + kill any pending renewal timer; the normal stop
& "<skill-dir>\renewtimer.ps1" -Minutes 90  # renewal timer: sleeps (window - 5 min) then exits - run AS A BACKGROUND JOB right after arming; its completion notifies the agent just before the window dies (see Renewal loop)
# <skill-dir> = the directory of this skill file (e.g. C:\src\lmrun\skills\awake).
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
  `on` never STARTS darkening), then start the next timer, and keep working.
  NEVER "fix" a renewal by re-running `dark` - if the overlay is already alive
  `dark` is a no-op anyway, and if it died, re-arming it can black the screen
  out from under a working operator (see Rules; report the death instead and
  decide with the operator);
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

Since v5 (2026-11-16, operator decision) `dark` NEVER touches brightness or the
ambient-light sensor. v6 (2026-11-17) replaced the (ineffective) ES-hold
protection story with the input keepalive: while DARK, the overlay injects a
no-op F15 keypress every `$HeartbeatSeconds` (default 30) s - injected input
resets Windows' display/sleep idle timers, so blanking and Modern Standby never
engage; wake/re-blank use low-level hooks that see HUMAN input only (injected
events are filtered, so the heartbeat never wakes the overlay). Nothing
persists outside the overlay process - the heartbeat stops with it, a kill
strands nothing; but that also means overlay dead = protection GONE and the box
sleeps within ~5 min (a stale journal `journal=True alive=False` or
`status: overlay exited` is now a reliable alarm). PANEL TRUTH, hardware-verified
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

`dark` is the operator's dark-room mode: pixels OFF (verified OLED = zero
light), display logically ON (the keepalive prevents the OS ever blanking it), so neither Windows nor the
AMD driver ever engages its screen-off power policy - and the S0ix standby that
kills background processes never engages either. The detached overlay releases
cursor + holds whenever it exits (idle cycle never ends the process; duration
expiry or kill via `light` do). It is part of the standard arm at task
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
- If a job is still suspending while armed, do not escalate settings; check
  for Kernel-Power event id 506 after the screen blanked AND cross-check
  `darkscreen.log`: the v6 overlay logs `KEEPALIVE #n` every nudge - a gap
  between the last KEEPALIVE and the 506 means the overlay (not the mechanism)
  died at that moment; find out what killed it. A 506 while KEEPALIVE lines
  keep streaming would mean the keepalive itself stopped working - report it.
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
  do not try to prevent them; just size the timed `dark` window so the hold
  outlives the job. On unattended finishes prefer letting the timed window
  expire over an explicit
  `off`+`light` while nobody is at the machine - release drops the keepalive and
  the box suspends within ~5 min, killing anything still running (2026-11-10, twice).
