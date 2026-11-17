# Awake skill — maintainer tuning notes

Human-facing documentation. NOT agent instructions. Written 2026-11-09 after the
original keep-awake investigation; read this when tuning the skill itself.

## Platform context

- Modern AMD OLED laptop (Ryzen AI Max-class APU with integrated Radeon
  graphics), Windows 11 build 26300.
- Modern Standby (S0 low-power idle) only — no S3, no hibernation. Screen-off is
  the OS's standby trigger on this class of machine.
- Active power plan: `PD_Standard` (an OEM clone, not a Microsoft default -
  get your scheme GUID with `powercfg /getactivescheme`). Per-plan overrides
  live under
  `HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\<scheme-guid>\`.

## How the final design came about

1. **Symptom:** transcription jobs stopped dead when the display blanked.
   Event log confirmed repeated Kernel-Power 506/566 (standby enter/exit) cycles.
2. **Toggle mechanism chosen:** PowerToys Awake module, driven by writing its
   `settings.json` (module's file watcher applies within ~2 s). Rejected the
   module's own CLI (`PowerToys.Awake.exe --use-pt-config`): silent, no log
   entries, unverifiable behavior on this build. Request-held/disappeared was
   proven against the settings write with elevated `powercfg /requests`
   (SYSTEM section lists `PowerToys.Awake.exe`).
   `mode`: 0=off, 1=timed (uses `intervalHours/Minutes` + `expirationDateTime`),
   2=indefinite; `keepDisplayOn:false` lets the screen still blank.
3. **First failure despite the skill working as designed:** Awake held its
   execution request (verified) yet the machine still suspended. Root cause #1:
   the OEM plan shipped a hidden per-plan override **Allow Away Mode Policy**
   (`SUB_SLEEP` group `238c9fa8-…`, setting `25dfa149-5dd1-4736-b5ab-e8a37b5b8187`)
   = 0 on AC. With it 0, the OS discards busy requests on the screen-off path —
   Awake is architecturally powerless. Fixed: `powercfg /setacvalueindex
   SCHEME_CURRENT 238c9fa8-… 25dfa149-… 1` + `setactive` (elevated).
4. **Residual second failure:** a real ~7 s freeze (heartbeat gap 18.4 s) ≈2 min
   after last user input even with #1 fixed. Root cause #2: hidden **System
   unattended sleep timeout** (setting `7bc4a2f9-d8fc-4469-b07b-33eb785aaca0`,
   stock 120 s). Fixed AC-only per operator request: `setacvalueindex … 0` +
   `setactive`; DC intentionally left stock (battery keeps normal behavior).
5. **Final acceptance test:** 24 heartbeats (~4.5 min) at 1-minute display
   timeout: gaps 11.0–11.8 s throughout; the only 506/507 events were harmless
   same-second aborted attempts with no coincident gap. Verdict: pass.

## Verification methodology (reuse this to tune)

- Heartbeat probe: loop of (1 s CPU spin + 10 s sleep) printing wall-clock gaps;
  nominal ≈11–11.5 s. A real suspend = gap ≥14 s or a multi-second freeze.
- Event log: `Get-WinEvent` System log, provider `Microsoft-Windows-Kernel-Power`,
  IDs 506 (enter), 507 (resume), 566 (exit). A **506+566 in the same second with
  no heartbeat gap = aborted attempt, harmless**. Judge by gap coincidence, not
  by event presence alone.
- Ground truth for requests: elevated `powercfg /requests` (SYSTEM section).
- Hidden-setting inspection: `powercfg /query` hides some settings; the registry
  tree under `Control\Power\User\PowerSchemes\<scheme>\238c9fa8-…` is the
  reliable per-plan view (readable without admin).

## Deliberately NOT done (revisit only with evidence)

- **ULPS** (`EnableUlps=1` under the AMD class key) — left on; only disable if
  multi-second GPU stalls persist with the current fixes in place.
- **PCIe ASPM** — was reset to "Maximum power savings" by OEM software once;
  left as-is (minor on an iGPU).
- **Fn+Q / ASUS performance modes** — operator stays on Standard by choice.
- **Battery (DC) side** — away mode remains disallowed on DC (Windows default),
  unattended timeout stock: Awake only protects while plugged in. Keep this
  asymmetry so the laptop can't stay awake inside a bag.

## Revert-risk watchlist

Armoury Crate / MyASUS has silently rewritten plan values before (ASPM). If the
operator reports suspends while Awake shows ON: read both setting overrides
(away mode `25dfa149-…` expecting AC=1; unattended timeout `7bc4a2f9-…`
expecting AC=0) before debugging anything else, then rerun the heartbeat probe.

## Open tuning ideas

- Optional: auto-arm from the transcription wrapper with timed mode sized to the
  run (discussed, not built — operator preferred manual/skill-triggered arming).
- If a `$PROFILE` alias is ever wanted: `function awake { & "$HOME\.config\opencode\skills\awake\awake.ps1" @args }`.

## Screen-off THROTTLING track (2026-11-10, distinct from the suspend track)

- Symptom after the suspend fixes: with display off during llama inference,
  fans quiet briefly and TPS drops hard — GPU power budget/clock policy change,
  not suspend. ULPS still enabled but there is NO supported AMD toggle
  (registry-only; and on an APU it may not even be the active mechanism).
- Findings: WMI brightness exposes 0%, but on this panel 0% == Windows slider
  minimum (very dim, text visible) — NOT backlight off. `powercfg /monitor -off`
  would be the same driver-visible "off" state that triggers throttling anyway.
- Therefore implemented `dark`/`light` in awake.ps1: brightness 0 + topmost
  black WinForms overlay (darkscreen.ps1). On OLED black pixels emit no light →
  effectively off, while the display stays logically ON so neither Windows nor
  the AMD driver ever applies a screen-off policy. The overlay process holds
  ES_DISPLAY_REQUIRED|ES_SYSTEM_REQUIRED (self-releasing on exit; any
  key/click exits it). Overlay covers the primary display only.
- ACCEPTANCE TEST RUN (2026-11-10, controlled, self-driven via llama-bench
  Qwen3-ASR-1.7B-Q8_0 Vulkan, tg128, same workload all phases):
  A unlocked+lit   106.81 ± 0.51 t/s
  B unlocked+dark  105.90 ± 1.10 (brightness asserted 0 mid-run)
  C LOCKED+dark+guard 105.22 ± 0.08  (LockWorkStation(); bench ran while
    locked; guard held console-lock-display-off=Never; zero 506/566)
  D control unlocked 104.90 ± 0.54
  => dark parity CONFIRMED; LOCKED+FULL-SPEED CONFIRMED - no throttle with
  the guard armed. The historical "locked = slow" was the console-lock
  display-off chain (and pre-fix unheld ES), both now broken. Throttle track
  CLOSED; registry ULPS edit remains deliberately NOT applied (never needed
  in any test). Residual:
  the locked phase had the guard keeping the plan's brightness on the lock
  screen; unguarded lock still blanks at 60 s and would throttle.
  - Registry restored 0x3c, state clean, nothing left armed.
- v3 (2026-11-10): `dark` redesigned as a screensaver state machine
  (GetLastInputInfo idle polling): black at start, wakes on any input, re-blanks
  after `$IdleSeconds` (default 180 s per operator's 2026-11-10 preference), auto-retires after `$DurationMinutes`
  (0=forever); Undo-All (brightness+cursor+ES+state file) on every graceful
  exit; `light` = kill + restore safety net. Lifecycle verified: running@10 s
  (black), auto-retired@75 s with brightness self-restored.
- Bugs found while building it (guards for future edits): uint32 P/Invoke args
  from hex >= 0x80000000 (SUPERSEDED - see 2026-11-10 pwsh-entry below: the
  plain [uint32] cast did NOT save us); `[type]U.Struct` fails the parser ->
  use Marshal::SizeOf($instance). Always smoke-test darkscreen with
  -RedirectStandardError - hidden-window errors are otherwise invisible.
- pwsh 7.6.9 hex-literal bug (2026-11-10, CRITICAL): on this box PowerShell
  parses 0x80000000+ literals as NEGATIVE and every cast path fails
  ([uint32]0x80000003, [uint64]-route, [long] -bor chains all throw - and the
  MethodException is non-terminating, so execution continues). Consequence:
  the overlay's ES hold SILENTLY NEVER WORKED under pwsh 7 since v1 - dark
  suspend-protection came solely from PowerToys Awake. Fix: ES flag math in
  C# (U.Es::Hold/Clear with proper uint consts, now incl. ES_AWAYMODE).
  Verify ground truth once with elevated `powercfg /requests` during dark
  (expect the pwsh entry) - operator run. Same class earlier misread as
  "negative Int32, cast [uint32]" - that guidance was wrong.
- darkscreen v3 limbo bug (2026-11-10): init set isDark=$true but nothing
  darkened until a full wake->idle cycle; only awake.ps1's own brightness
  write masked it (standalone spawn stayed lit). Fix: init Active + explicit
  Enter-Dark in Add_Shown (immediate blackout per design).
- T1 VERIFIED via elevated powercfg /requests (2026-11-10): with the layered
  fix the overlay's grant reaches the kernel - pwsh.exe appears under
  DISPLAY+AWAYMODE sections (old build: None everywhere). QUIRK: we request
  SYSTEM|AWAYMODE but powercfg lists DISPLAY+AWAYMODE - section mapping on
  win11 26300 differs from legacy semantics; entry existence is the pass
  criterion, not the section name. ES_DISPLAY_REQUIRED itself is REFUSED by
  this OS (err 87, display power is OS-owned on Modern Standby) - layered
  HoldBestEffort degrades sys+disp+away -> sys+away and logs per-attempt.
- SetThreadExecutionState RETURNS THE PREVIOUS state (0 = failure) - do not
  read the return as the new state (misread cost a confused mid-debug; log
  now labels it prev=).
- Kill-propagation trap: force-killing the overlay (Stop-Process) skips
  Undo-All, leaving brightness 0; the NEXT dark arm then saves 0 as its
  "original" and light "VERIFIED restores" 0%. Happened once 2026-11-10,
  fixed by manual write 54. Protocol: always stop with awake.ps1 light;
  if a kill was necessary, check status before re-arming.
- Lock witness (2026-11-10): darkscreen now attaches SystemEvents.SessionSwitch
  on the UI thread + registers WTS notifications -> lock/unlock timestamps
  appended to darkscreen.log, no elevation. PROVEN 2026-11-10 (SessionLock/
  SessionUnlock lines captured across real Win+L and LockWorkStation tests).
  Context: 13:12:56 standby had Security 4800/4801 audit OFF (empty log),
  reason Idle Timeout at a SessionUnlock transition; lid/button excluded.
  T2 PROVEN 2026-11-10: darkscreen.log captured 'WTS: SessionLock' 15:15:52 /
  'WTS: SessionUnlock' 15:21:42 with no elevation. KEY RESULT: machine
  survived ~6 min LOCKED with Awake OFF and zero standby events (506/566) -
  the exact 13:12 scenario - overlay's ES SYSTEM|AWAYMODE grant is the
  plausible reason (it is designed to persist through session absence).
  NOTE: 'light' kills the overlay, so no EXIT line is written on that path
  (Undo-All skipped); EXIT lines appear on duration-expiry/self exits only.
- Lock-guard track (2026-11-10): operator observed throttle while LOCKED.
  Mechanism found: plan has hidden "Console lock display off timeout"
  (SUB_VIDEO `8ec4b3a5-6868-48c2-be75-4f3044be88a7`) = 60 s AC+DC (ASUS-set)
  -> lock -> display off after 1 min -> driver screen-off throttle; overlay is
  below the secure desktop and cannot prevent it. ARSO key absent, no dynamic
  lock, no InactivityTimeoutSecs GPO -> spontaneous locks are Windows default
  "lock on waking" behavior after brief standby-stick events. Fix:
  `lockguard.ps1` - elevated helper spawned by `dark` (one UAC, `-Verb RunAs`)
  that saves ACSettingIndex, forces 0 + setactive for the dark window (default
  480 min if dark is unbounded), re-asserts once at +2 min (Armoury Crate
  watch), restores exactly at expiry, logs to lockguard.log, state in
  lockguard-state.json (visible in `status`). `-NoLockGuard` opts out; killing
  the guard early leaves value 0 = benign (lit lock screen until restore).
  Stand-down path: a non-elevated session CANNOT kill the elevated helper
  (UIPI) - so `light` writes `stop: true` into lockguard-state.json and the
  guard's 15 s poll self-restores (mailbox pattern; stop requests are always
  fail-safe direction). `light` = full release of dark + guard.
  Trade-off made explicit: locked full-speed => LIT lock screen (dark overlay
  impossible there); unlocked => dark + full speed. RESOLVED by the acceptance
  test above: driver does NOT key on session presence - locked ran at parity.
  Policy flipped accordingly (SKILL.md): locking is the safe default choice.
  MAINTENANCE NOTE - what was deliberately pruned from SKILL.md on 2026-11-10
  (kept here as history, NOT for agents): the prohibitions "do not try to
  prevent/disable/fix locks", "no sign-in-option or auditpol edits", "no
  Win+L warnings". They answered a bug that no longer exists (unheld ES +
  console-lock blanking), they duplicated the general 'the toggle is all it
  does' guardrail, and SKILL.md should carry only what helps an agent act.
  Obsolete probes from that era, do not resurrect: ARSO key
  (HKLM\...\Winlogon\ARSO - absent), dynamic lock (HKCU\...\DeviceLock -
  absent), InactivityTimeoutSecs GPO (absent), HPD sensor (none on this
  machine), Security 4800/4801 audit enabling. If a future OS update brings
  HPD or ARSO back and locks resume correlating with standby, that is when
  this list becomes live again - re-verify before re-adding anything.
- Brightness-restore incident (2026-11-10, also recorded in the project's
  incident handoff note): `light` at 13:20
  claimed "restored 54%" but panel showed 39% after the 13:38 resume. Two
  stacked causes: (1) WmiSetBrightness to a panel powered down inside a Modern
  Standby window is a SILENT no-op (return value was discarded, no readback);
  (2) the plan's Display brightness (SUB_VIDEO `aded5e82-…`) was 39 AC+DC and
  display power-on re-applies the plan level - WMI writes never persist into
  the plan. Fixed: plan level set to 54 AC+DC (durable floor survives resume;
  verified registry override 0x36). Hardened (operator-approved):
  Set-BrightnessVerified / Set-BrightVerified = write + ~2 s readback + one
  retry; `dark` engages with VERIFIED 0% or a warning; `light` prints VERIFIED
  only on confirmed readback, else "NOT verified ... re-run light after
  resume"; darkscreen Undo-All logs honest outcome to darkscreen.log (hidden
  process has no console). Enter-Active wake stays fire-and-write (verified
  write would stall every wake 2 s; exit path is the persistent one).
  Deliberately NOT done: adaptive brightness stays ON (operator choice; sensor
  can drift levels - factor in future reports), AMD driver update left to
  operator (atieclxx.exe 0xc0000005 resume crashes, driver 32.0.32015.7029).
  OPEN MYSTERY: 13:12:56 standby (506) punched through awake + overlay ES
  holds + guard; suspect lid/power-button or session-state drop of ES
  requests; elevated Security-log probe handed to operator. Operational rule:
  lid close / power button still force sleep - dark cannot protect.

## 2026-11-10 evening: hold model corrected; locked + dark + parity PROVEN (real workload)

First real unattended task (17:03-18:17 pipeline run): zero 506s, clean release,
plan repairs intact - and the 13:12 mystery class is closed (see #1/#4 below).
Evening experiment series used the real ASR path (90 s clip, 734 chars identical
output, scripts/transcribe.py llama/Vulkan) because llama-bench noise could not
resolve single-digit percentages; lesson recorded: sequential blocks on this box
need A/B/A bracketing, run-to-run variance is 2-3x when an LLM server contends.

| state (holds armed, GPU-quiet config)                  | time per 90 s clip |
| unlocked, working                                      | 10.2-10.7 s        |
| LOCKED + black (overlay idle blackout, -NoLockGuard)   | 10.3-10.6 s = parity |
| running INSIDE Modern Standby doze window              | 28-32 s (~3x)      |
| full-context LLM server contending (earlier rounds)    | 12-31 s, wild      |

Findings (all dated 2026-11-10):
1. POWERTOYS AWAKE TIMED MODE FAILED TO HOLD: 506 Idle Timeout freeze-suspend
   at 19:50:13 with mode=1 written and PowerToys.Awake pid alive - the settings
   write (19:43:25) landed while the box was frozen in its 19:42:27->19:46:39
   standby; the watcher cannot fire mid-freeze, so the hold was never applied.
   The overlay process's ES grant is the reliable hold (19:46:35->19:49:14 =
   the only clean full-speed window of that sequence). Doctrine now in SKILL.md:
   overlay RUNNING = protected; wake a dozing machine (any command) BEFORE arming.
2. Locked+dark+full-speed CONFIRMED: overlay brightness writes are desktop-
   agnostic - the lock screen itself goes black on the idle timer (Phase E at
   18:40 read 0 while locked; F3 locked+black at parity). "Locked = lit screen"
   and "dark room + locked impossible" are DEAD claims, corrected in SKILL.md.
   The 60 s console-lock display-off was never actually observed blanking the
   panel while an ES hold was live - every "blank" reading today was the
   overlay blackout. Guard demoted to insurance (F3 locked, -NoLockGuard,
   parity). Operator's UAC stance "keep as-is", but -NoLockGuard is now a
   legitimate promptless option since speed does not depend on it.
3. ~3x slowdown with identical output = running inside doze windows. Diagnostic
   signature for future "why is the job slow" reports.
4. LockWorkStation returned TRUE yet locked nothing at 19:46:40 (a standby
   transition ±1 s swallowed it). Only reliable lock detector on this machine:
   SessionLock/SessionUnlock witness lines in darkscreen.log. The
   OpenInputDesktop probe returns "Default" even while locked (proved
   20:11:23 vs the witness) - do not use deskchk-style lock checks here.
5. Adaptive brightness re-lit the black LOCK screen 0->69 during runs (sensor
   choice is the operator's; dark room keeps black).
6. Releasing while locked-and-absent froze-suspended the box within ~60 s and
   killed the harness session (19:15:56 release -> 19:16:54 506; same class at
   19:50:13). Rule in SKILL.md: unattended finish = let the timed window expire;
   explicit off+light only with a human at the machine.

## Agent-driven renewal loop (2026-11-15)

- **Failure mode fixed:** a timed arm is an estimate; long jobs outran it and the
  box suspended mid-job (window expiry released the hold).
- **Control model (operator's decision):** the AGENT decides every renewal;
  scripts hold zero renewal logic. `renewtimer.ps1` is a dumb sleeper: writes its
  pid/window to `renewtimer.json`, sleeps window-minus-margin (default 5 min),
  exits. Run as an agent background job -> process completion notifies the agent
  just before expiry -> agent re-runs `awake.ps1 on <N>` if still working and
  respawns the timer; otherwise releases. Nobody renews when the agent is dead,
  so the plain timed expiry remains the crash backstop (no watcher/cap daemon).
- **Dark overlay extension:** `on <N>` now pushes `until` in `darkstate.json`
  when a TIMED overlay is alive; darkscreen.ps1 re-reads `until` every ~15 s and
  retires on it instead of a fixed stopwatch. No restart, no flicker; `on` still
  never STARTS darkening; indefinite overlays untouched. File missing/broken ->
  original DurationMinutes retirement stands.
- **Timer lifecycle is enforced by awake.ps1:** timed `on` retires the previous
  cycle's timer; `off` kills any pending timer BEFORE writing mode 0 (pid +
  pwsh/powershell name check against pid reuse) and cleans the mailbox;
  `status` reports PENDING / fired / stale.
- **Tested 2026-11-15:** timer spawn + PENDING status; `on` terminates a live
  timer (process dead, mailbox cleaned); `on` re-run extends the expiry; timer
  fire stamps firedAt and status prompts renew-or-release; `off` cleans the fired
  box and restores mode 0; settings.json fields (customTrayTimes) survive the
  rewrites. Dark `until` path is code-reviewed only (would black the operator's
  screen to test live).
- **Field-verify on first long run:** that the PowerToys module honors an
  `expirationDateTime` rewrite while already armed (same file-watcher path as
  first arming, so expected yes - confirm via a status snapshot across a renewal).

## v4 dark: pixel-only blackness (2026-11-16) - blank-screensaver option assessed and rejected

Operator pain points that drove this revision: (1) brightness-restore
flakiness (the claimed-54/showed-39 incident class, the force-kill capturing
0% as "original" trap), (2) screen not truly black / re-lit (sensor re-light
0->69 on the lock screen). Proposal on the table: replace the manual black
screen with the OS Blank screensaver, restoring settings on finish.

**Rejected - mutual exclusion, not preference:**
- scrnsave holds NO execution request; a running screensaver is the OS's idle
  indicator. Drop the overlay process for it and protection regresses to the
  founding failure class (VIDEOIDLE 300 s AC -> panel down -> doze/throttle,
  506 events).
- A live execution hold RESETS the input-idle timer - the same timer that
  starts the OS screensaver. While the box is protected, the blank screensaver
  cannot appear. (Exactly what darkscreen's header always claimed: the ES hold
  stops "Windows' own blanking" - that includes its screensaver.)
- Timer-gated (no black-at-t=0; forcing needs the SC_SCREENSAVE desktop hack),
  and never runs on the secure desktop - would regress the proven
  locked+black+parity posture instead of serving it.

**v4 design (darkscreen.ps1 + awake.ps1, same doctrine - ES hold untouched):**
- Normal dark/active cycle NEVER writes brightness. Black pixels on this OLED
  emit no light (established above); brightness 0 was fragile redundancy whose
  only unique job was the lock screen. The whole save/restore fragility class
  is gone structurally, not mitigated.
- Secure-desktop job handled explicitly: SessionLock event writes 0 (writes
  are desktop-agnostic, proven 2026-11-10); unlock/retire restore the PLAN
  level - the value display power-on re-applies anyway, so even a lost restore
  cannot strand a dim panel (the incident's root inconsistency).
- ADAPTBRIGHT AC -> 0 for the whole window (kills the measured 0->69 re-light;
  previously the operator's adaptive-brightness choice silently defeated lock
  darkness), restored on exit. Each setactive pair also holds VIDEONORMALLEVEL
  at the current panel level so nothing flashes at arm/restore. Re-asserted
  once at ~2 min against an Armoury Crate flip (lockguard precedent).
- Journal ownership moved INTO darkscreen.ps1 (it writes its full state before
  the first blackout; awake.ps1 only reads it and patches `until`). A stale
  journal from a force-kill is repaired at the next `dark` arm or by `light`;
  repairs report honestly. No brightness value is ever captured, so the
  kill-captures-0 trap is structurally gone.
- Pre-v4 journal compatibility: `light`/heal detect absent v4 fields and fall
  back to the old captured-brightness restore - a v3-era overlay can always be
  stopped by v4 tooling.
- GUIDs verified present on PD_Standard 2026-11-16 (watch for plausible-wrong
  variants when editing): ADAPTBRIGHT = fbd9aa66-9553-4097-ba44-ed6e9d65eab8
  (AC/DC override were 1); VIDEONORMALLEVEL = aded5e82-b909-4619-9949-f5d71dac0bcb
  (0x37 = 55 AC+DC - the 2026-11-10 notes wrote 54; operator level drifted/
  adjusted since). Plan level 55 = the new restore target.
- Known one-time effect: first v4 `light` normalizes the panel from the
  sensor-drifted WMI level (was 40) to the plan level 55; the resumed sensor
  adjusts from there.
- NOT changed (operator did not select): overlay still covers the primary
  display only (a multi-monitor fix would be one form per Screen.AllScreens).

**Field-verify on first v4 run:** arm shows "sensor PAUSED (VERIFIED)" +
status Sensor line; registry ADAPTBRIGHT AC=0 during, back to 1 after exit;
WMI brightness STAYS at the sensor level during an unlocked run (no 0 writes);
lock -> dim + journal lockedDimmed, unlock -> plan-level restore (darkscreen.log
lines); kill the overlay with Stop-Process -> next `dark` prints a "Stale-state
repair" line. Dark `until` renewal path unchanged (mailbox), but was code-reviewed
only under v4 - confirm on the first renewal across a v4 overlay.

**ROOT CAUSE of the 2026-11-16 14:00 renewal failure (caught by v4 telemetry
within minutes of adding it):** ConvertFrom-Json deserializes the ISO 'until'
string into a DateTime object; the reader did `[datetime]::Parse("$u")`, and
"$u" renders that DateTime through the CURRENT CULTURE ("11/16/2026 14:13:23")
which Parse then rejects. v3's silent catch swallowed the exception every 15 s
and the overlay retired on its ORIGINAL window (EXIT 14:05:08 = arm 12:35 + 90
exactly), while status kept reporting RUNNING - the extension never landed.
v4 reader accepts the typed DateTime as-is (strings fall back to an
INVARIANT-culture parse); writer format unchanged. Do NOT reintroduce
culture-dependent date parsing anywhere in this skill; prefer typed objects
or InvariantCulture + 'o'. Same audit done: lockguard/renewtimer/status have no
other date round-trips. Both mailbox sides now log every push/skip/failure to
darkscreen.log ([awake] lines vs overlay lines), and `status` prints the
effective retirement time. Smoke test evidence (2026-11-16 14:12-14:14):
pixel-only run kept brightness at 55 throughout; sensor paused+restored
(ADAPTBRIGHT 1->0->1 verified in registry); Undo-All EXIT clean; journal
removed; synthetic round-trip PASS. Not yet exercised: lock-dim + stale-heal
(need operator present), first renewal across a v4 overlay (due 15:25 today).

## v5 (2026-11-16, same day): brightness + sensor pause REMOVED by operator decision

"Remove brightness change and pausing the sensor all together - black screen
and suspending sleep is enough." Final mechanism: pixel-black overlay + ES
hold + idle/wake state machine + until mailbox. Nothing else.
- Deleted from the codebase: all WMI brightness writes (including the
  lock-dim/unlock-restore path), the ADAPTBRIGHT pause/restore/re-assert
  machinery, the repair net (Restore-DarkJournal), all power-setting GUID
  helpers in awake.ps1, and the journal's adaptive/brightness fields.
  (GUIDs and the full v4 design remain documented above for history.)
- Journal is now {pid, durationMinutes, idleSeconds, until} - written by the
  overlay before blackout, deleted on exit; `light` = Stop-Process + file
  cleanup, because the ES request dies with the process and NOTHING can be
  stranded outside it. The kill-propagation trap is not repaired - it is
  impossible.
- Accepted trade (explicit): the LOCK SCREEN stays lit when locked-and-away
  (the secure desktop is paint-protected and the only tool that could dim it
  - the brightness write - was removed). Locked-session speed parity is a
  separate, still-true finding (ES hold). If dark-while-locked is ever wanted
  again, the v4 lock-dim design above is the only known route.
- v5 smoke (14:28): dark 90 armed via the new code - status clean, no
  brightness/sensor involvement (panel at the sensor's own 85%, untouched),
  retiring 15:58. The 15:25 renewal is the first live `on`-push across a v5
  overlay - both mailbox sides now log to darkscreen.log; verify the
  '[awake] until pushed' + 'until mailbox:' line pair once, then this is done.

## v7 same-day experiment and REVERT (2026-11-17): the "LED" claim was wrong - the panel is OLED

- Trigger: operator said "lit lock screen is a problem with LED". I interpreted
  that as "restore v4-style WMI dimming" and built v7 (backlight floors for
  dark/lock, ADAPTBRIGHT pause + keepalive re-assert, restore, strand repair in
  `light`/stale-heal, journal fields darkBrightness/lockBrightness/
  plan*Original/sensorOrig*) WITHOUT explicit operator sign-off - the operator
  pushed back ("we removed it because it was causing a problem"), rightly. The
  feature question was mine to ask first, not answer with code.
- Two durable discoveries the experiment produced:
  1. **WmiSetBrightness SYNCs into the plan**: VIDEONORMALLEVEL (aded5e82)
     AC+DC indices followed a 53->0 panel write (observed twice live). The v4
     doctrine "restore target = live plan level; writes are non-persistent"
     was FALSE - after any floor write the live plan is a self-referential 0,
     and a killed overlay strands dimness across reboots unless repaired from
     an arm-time capture. This retroactively explains the 2026-11-10
     save-0-as-original incident better than "restore failed" did.
  2. **Invoke-CimMethod binding on this build**: -ClassName
     WmiMonitorBrightnessMethods FAILS ("Type mismatch for parameter
     Timeout" / "Invalid method Parameter(s)" even with exact types). Works:
     pipe the methods INSTANCE with `@{ Brightness = [byte]N; Timeout =
     [uint32]0 }`.
- Hardware verification then killed the whole idea: Win32_ComputerSystem =
  **ASUS ProArt PX13 HN7306EAC**; WmiMonitorID mfr=**SDC** product=**41B0** =
  Samsung **ATNA33AA08-0**, a 13.3" 2880x1800 OLED (infinite contrast; ASUS
  official specs + Nanoreview + TechPowerUp panel test). Black pixels emit NO
  light - the pixel overlay was ALREADY zero-light at any brightness setting
  (the original v4-era doc line was right); the lock screen glows because it
  renders CONTENT (clock/wallpaper), not because of any backlight.
- OPERATOR DECISION (same day): REVERT v7 completely. v5/v6 doctrine stands -
  no brightness/sensor code anywhere, nothing can outlive the overlay process.
  Unattended zero-light posture = **do NOT lock**; locking = accepted lit lock
  screen (only for physical security). "Display off + full speed" is
  impossible on this box (AMD display-off throttle + the idle->blank->S0ix 506
  kill chain; injected keepalive input inseparably re-powers the display).
- Revert verified (15:42): smoke `dark 1` -> `light` with panel steady at the
  sensor's own 70% throughout; journal back to
  {pid,durationMinutes,idleSeconds,heartbeatSeconds,humanHook,until}; zero
  powercfg/WMI writes; SKILL.md doctrine rewritten (OLED verified, don't-lock
  posture, lock-validated facts kept).

## darkkeeper.cs - the C# port (2026-11-17, v6.1): single-file app, mailbox control

- Operator direction: ONE C# file run via `dotnet run` doing everything the ps1
  pair did (F15 keepalive, dark overlay, timing, status), PLUS: state files moved
  OUT of the PowerToys folder into the app's own `%LOCALAPPDATA%\DarkKeeper\`,
  `start` creates the control file / `stop` merely updates it (worker watches and
  exits ITSELF), heartbeat for the WHOLE process lifetime (v6.1), PowerToys Awake
  dependency retired (its ES hold measured inert against idle standby; "keeper"
  mode IS the keep-awake). No guardian/reviver (operator: declined), ps1 pair
  KEPT as fallback (operator: "not yet" on deleting).
- .NET 10 file-based app gotchas (SDK 10.0.401), all needed in the directives:
  `#:property Name=Value` syntax (space form = hard error); `UseWindowsForms=true`
  + `TargetFramework=net10.0-windows` (plain net10.0 -> NETSDK1136);
  `PublishAot=false`+`PublishTrimmed=false` (file-based apps default to trimmed/AOT;
  WinForms rejects -> NETSDK1175); `#:sdk Microsoft.NET.Sdk` works and the legacy
  `Microsoft.NET.Sdk.WindowsDesktop` name warns NETSDK1137. `SystemEvents` needs the
  full `Microsoft.Win32.` namespace; `Screen.PrimaryScreen` is nullable-annotated.
- Real bugs the test cycle caught (kept as lessons):
  1. `JsonSerializer` silently ignores public FIELDS unless
     `IncludeFields=true` - the control file serialized as `{}` while every field
     "looked right" (attributes do not override IncludeFields).
  2. `Process.GetProcessById(0)` succeeds (System Idle Process) - a pid<=4 guard is
     required or a broken file reads as "alive".
  3. The stop-guard must test `intent=="stop"` ONLY: the control file legitimately
     carries the WORKER's pid (CLI preserves it), so "pid != self => external" is
     false and swallowed the stop (test escalated to FORCED - correct backstop,
     wrong trigger).
  4. **`dotnet run` + detached worker = build-lock**: spawning the worker from the
     temp build image locks it; the NEXT `dotnet run` (status!) tries to copy
     apphost.exe over the running image and fails (MSB3026/27 x10). Fix: `start`
     installs `runtime\darkkeeper.exe` in the state dir and spawns from there; all
     later verbs use the installed copy; the temp build stays rebuildable.
- Verified end-to-end (scratch instance, `%TEMP%\opencode\dktest`): start/claim,
  extend via mailbox (single instance), cooperative stop in 1.2 s with EXIT + file
  removal, force escalation, DEAD alarm + stale-clear on next start, renew-wait box
  lifecycle, dark overlay immediate blackout, lifetime KEEPALIVE in BOTH phases,
  auto-retire `EXIT: retired (window ended)` with zero leftovers. Live PS v6 run
  (pid 37236) was never disturbed during development (`--state-dir` isolation).
- Deliberate carry-overs from v6 doctrine: F15 no-op + LLMHF_INJECTED hook filtering;
  ES sys+away best-effort hold (logged, secondary); dead hooks => stay dark, never
  thrash; immediate blackout on arm; invariant-culture dates; brightness/sensor
  writes PERMANENTLY OUT; nothing persists outside the process after a clean stop.
- RENAMED darkkeeper -> awake (operator: "why the silly name?") right after the
  publish decision: source `awake.cs`, process/identity `awake`, state dir
  `%LOCALAPPDATA%\Awake\`, and the build now lives IN THE SOURCE FOLDER at
  `<skill-dir>\app\bin\` with `app\.gitignore` (bin/, obj/, *.csproj) - operator's
  call over my habit of parking builds under %LOCALAPPDATA%. Names in the port
  sections above are the pre-rename ones (kept as history); the retired
  `%LOCALAPPDATA%\DarkKeeper\darkkeeper.log` is kept for forensics.
- Ready-to-run EXE (operator: "native AOT"; settled to publish - AOT is a hard NO for
  WinForms: unsupported by AOT/trimming, and dropping WinForms for raw Win32 buys
  only startup ms). Self-contained single-file (49 MB) built and verified, then
  SKIPPED per operator (startup overhead): final shape is FRAMEWORK-DEPENDENT publish
  `dotnet publish awake.cs -c Release -o app\bin` -> ~200 KB,
  ~250 ms cold / ~120 ms warm, needs the Windows Desktop runtime (installed on this
  box). Verified full lifecycle (start/extend/stop/status, real state dir). `start` spawns
  Environment.ProcessPath when it is a published exe; the runtime-copy install only
  applies to `dotnet run` temp builds (where the lock problem exists). Late fix to
  the port: the claiming Save now runs AFTER hook install - initially the CLI could
  snapshot the claim mid-bootstrap and print a false "human hook dead" warning.
