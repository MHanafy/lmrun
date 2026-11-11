#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""
lmrun - launch any llama.cpp-compatible inference engine with a live throughput dashboard.

What it does
  * starts the engine from a profile in lmrun.json (path + version + arguments)
  * keeps a 3-line dashboard at the top of the terminal that never scrolls away:
      line 1: engine/version, uptime, since-start averages (prefill t/s, generation t/s)
      line 2: the last completed request (prefill t/s, gen t/s, ttft, duration, cache)
      line 3: separator for the log area
  * streams the log below the header but hides routine chatter, so warnings, errors and
    anything unusual stays visible
  * tees the raw, unfiltered log to a file and appends one JSON record per run to
    runs.jsonl, so performance can be reported per engine/version later

Usage
  lmrun.py                              run the default profile
  lmrun.py --profile NAME               run a different profile from lmrun.json
  lmrun.py --engine NAME / --bin PATH   pick an engine from config, or point at a binary
  lmrun.py -- <engine args...>          ignore the profile arguments entirely
  lmrun.py --list                       show configured engines and profiles
  lmrun.py --report                     aggregate runs.jsonl by engine/version
  lmrun.py --replay log.txt             analyse an existing engine log instead of serving

Averages are token-weighted, not a mean of per-request numbers:
  avg t/s = total tokens / total time, where per-request time is derived from the
  token counts and *_tps fields the engine reports (prefill counts only tokens that
  were really computed, i.e. prompt_tokens - cached_tokens).

Engines that do not emit `timestamp [LEVEL] [tag] key=value` records can set
"log": {"style": "plain"} in lmrun.json: everything is passed through and only the
wall clock is tracked.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import os
import queue
import re
import shutil
import signal
import subprocess
import sys
import threading
import time
import unicodedata
from collections import defaultdict
from datetime import datetime

APP = "lmrun"
HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CONFIG = os.path.join(HERE, "lmrun.json")
HEADER_ROWS = 3

# --------------------------------------------------------------------- log line shapes

LINE_RE = re.compile(
    r"^(?P<date>\d{4}-\d{2}-\d{2})[ T](?P<time>\d{2}:\d{2}:\d{2})(?:[.,]\d+)?"
    r"\s+\[(?P<sev>[A-Za-z]+)\]\s*(?:\[(?P<tag>[^\]]+)\])?\s?(?P<rest>.*)$"
)
KV_RE = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)=(\"[^\"]*\"|[^\s]+)")
VERSION_RE = re.compile(r"v?(\d+\.\d+(?:\.\d+)?(?:[-.][0-9A-Za-z][0-9A-Za-z.-]*)?)")

# Sensible defaults for a kv-style engine log; every key can be overridden per engine.
LOG_DEFAULTS = {
    "style": "kv",
    "pattern": None,
    "quiet_tags": ["cache", "slot", "metrics"],
    "quiet_events_by_tag": {
        "http": ["received", "completed", "cancelled", "stream", "started", "queued"],
    },
    "received_events": ["received"],
    "finish_events": ["completed", "cancelled", "failed"],
    "completion_events": ["completed"],
    "fields": {},
    "hide": [],
    "show": [],
}

# --------------------------------------------------------------------------- ansi bits


class C:
    """ANSI styles; replaced with empty strings when colour is off."""

    RESET = "\x1b[0m"
    BOLD = "\x1b[1m"
    DIM = "\x1b[2m"
    RED = "\x1b[31m"
    GREEN = "\x1b[32m"
    YELLOW = "\x1b[33m"
    BLUE = "\x1b[34m"
    MAGENTA = "\x1b[35m"
    CYAN = "\x1b[36m"
    WHITE = "\x1b[37m"


def disable_color() -> None:
    for name in [n for n in vars(C) if not n.startswith("_")]:
        setattr(C, name, "")


OSC_RE = re.compile(r"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)")
CSI_RE = re.compile(r"\x1b\[[0-9;?]*[ -/]*[@-~]")


def strip_ansi(s: str) -> str:
    return CSI_RE.sub("", OSC_RE.sub("", s))


def vlen(s: str) -> int:
    """Terminal width of a string (ANSI stripped, wide CJK counted as 2)."""
    n = 0
    for ch in strip_ansi(s):
        if unicodedata.category(ch) in ("Mn", "Me", "Cf"):
            continue
        n += 2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1
    return n


def enable_vt_mode() -> None:
    """Ask the Windows console to interpret ANSI escapes (no-op on Windows Terminal)."""
    if os.name != "nt":
        return
    try:
        kernel32 = ctypes.windll.kernel32
        for handle_id in (-11, -12):  # std output, std error
            handle = kernel32.GetStdHandle(handle_id)
            mode = ctypes.c_uint32()
            if kernel32.GetConsoleMode(handle, ctypes.byref(mode)):
                kernel32.SetConsoleMode(handle, mode.value | 0x0004)
    except Exception:
        pass


# ---------------------------------------------------------------------------- config


class ConfigError(SystemExit):
    pass


def expand(path: str) -> str:
    """Resolve %VAR% style environment references and ~ in config paths."""
    return os.path.abspath(os.path.expanduser(os.path.expandvars(str(path))))


HOME_TOKEN = "%LMRUN_HOME%"


def subst_home(token, base_dir: str) -> str:
    """Replace %LMRUN_HOME% with the folder lmrun lives in, so a config can ship its own files."""
    text = str(token)
    if HOME_TOKEN.lower() in text.lower():
        text = re.sub(re.escape(HOME_TOKEN), lambda _m: base_dir, text, flags=re.I)
    return os.path.expandvars(text)


def load_config(path: str) -> dict:
    try:
        with open(path, "r", encoding="utf-8-sig") as fh:
            cfg = json.load(fh)
    except FileNotFoundError:
        hint = ""
        example = os.path.join(os.path.dirname(path), "lmrun.example.json")
        if os.path.exists(example):
            hint = f"\n       first run? copy lmrun.example.json to {os.path.basename(path)} and edit it"
        raise ConfigError(f"{APP}: no config at {path}{hint}\n"
                          f"       or point at another one with --config PATH")
    except (ValueError, OSError) as exc:
        raise ConfigError(f"{APP}: cannot read {path}: {exc}")
    if not isinstance(cfg, dict):
        raise ConfigError(f"{APP}: {path} must contain a JSON object")
    return cfg


class Engine:
    """A configured engine: where it lives, which version it is, how it logs."""

    def __init__(self, name: str, cfg: dict, base_dir: str):
        cfg = cfg or {}
        self.name = name
        self.base_dir = base_dir
        self.path = self._resolve(cfg.get("path"), base_dir)
        self.version = cfg.get("version")
        self.family = cfg.get("family") or "llama.cpp-compatible"
        self.version_args = cfg.get("version_args", ["--version"])
        self.notes = cfg.get("notes", "")
        # Anything else you put on the engine (build, hardware, quantisation, ...) is
        # carried into every history record, so reports can slice by it later.
        self.meta = {k: v for k, v in cfg.items()
                     if k not in ("path", "version", "version_args", "log", "env", "notes",
                                  "restart")}
        self.extra = cfg.get("env", {}) or {}
        log = dict(LOG_DEFAULTS)
        log.update(cfg.get("log", {}) or {})
        self.log = log
        try:
            self.line_re = re.compile(log["pattern"]) if log.get("pattern") else LINE_RE
        except re.error as exc:
            print(f"{APP}: engine {name!r} has a bad log.pattern ({exc}); using the default",
                  file=sys.stderr)
            self.line_re = LINE_RE

    @staticmethod
    def _resolve(raw, base_dir: str):
        """Engine paths may be absolute, ~-relative, %LMRUN_HOME%-based, a bare command
        name resolved through PATH (e.g. "python"), or relative to the config file."""
        if not raw:
            return None
        raw = subst_home(raw, base_dir)
        if not (os.sep in raw or "/" in raw or "." in raw):
            return raw
        if not os.path.isabs(raw):
            raw = os.path.join(base_dir, raw)
        return os.path.abspath(os.path.expanduser(raw))

    def launch_argv(self, args):
        return [self.path, *[subst_home(a, self.base_dir) for a in args]]

    def detect_version(self, timeout: float = 10.0):
        """Read the version out of the binary when the config does not pin one."""
        if not self.path or not self.version_args:
            return None
        try:
            out = subprocess.run(self.launch_argv(self.version_args), capture_output=True,
                                 text=True, encoding="utf-8", errors="replace", timeout=timeout)
            text = (out.stdout or "") + (out.stderr or "")
        except Exception:
            return None
        for line in text.splitlines():
            m = VERSION_RE.search(line)
            if m:
                return m.group(0).lstrip("vV"), line.strip()[:120]
        return None

    def label(self) -> str:
        return f"{self.name} {self.version}" if self.version else self.name


class Profile:
    def __init__(self, name: str, cfg: dict):
        cfg = cfg or {}
        self.name = name
        self.engine = cfg.get("engine")
        self.args = list(cfg.get("args", []))
        self.model = cfg.get("model") or ""
        self.notes = cfg.get("notes", "")


def resolve(argv_engine, argv_bin, opts_profile, cfg: dict, config_dir: str):
    """Pick engine + profile from the config, letting the command line override."""
    engines = cfg.get("engines", {}) or {}
    profiles = cfg.get("profiles", {}) or {}
    prof_name = opts_profile or cfg.get("default_profile")
    if prof_name and prof_name not in profiles:
        raise ConfigError(f"{APP}: no profile {prof_name!r} in config "
                          f"(have: {', '.join(sorted(profiles)) or 'none'})")
    prof = Profile(prof_name, profiles.get(prof_name, {})) if prof_name else Profile(None, {})

    eng_name = argv_engine or prof.engine or cfg.get("default_engine")
    if not eng_name and len(engines) == 1:
        eng_name = next(iter(engines))
    if eng_name and eng_name not in engines:
        raise ConfigError(f"{APP}: no engine {eng_name!r} in config "
                          f"(have: {', '.join(sorted(engines)) or 'none'})")
    eng = Engine(eng_name or "custom", engines.get(eng_name, {}), config_dir)
    if argv_bin:
        eng.path = expand(argv_bin)
    return eng, prof


def history_target(raw, config_dir: str) -> str:
    """History file path: absolute, %VAR%- or ~-based, else relative to the config file."""
    raw = str(raw)
    if os.path.isabs(raw) or "%" in raw or raw.startswith("~"):
        return expand(raw)
    return os.path.abspath(os.path.join(config_dir, raw))


def probe_arg(args: list, flag: str):
    """Value of a `--flag VALUE` style argument."""
    try:
        return args[args.index(flag) + 1]
    except (ValueError, IndexError):
        return None


def int_arg(args: list, flag: str):
    raw = probe_arg(args, flag)
    try:
        return int(float(raw)) if raw else None
    except (TypeError, ValueError):
        return None


# --------------------------------------------------------------------- restart policy

RESTART_DEFAULTS = {"codes": (75,), "max": 3, "delay_s": 5.0, "reset_after_s": 900.0}


def restart_policy(profile_raw, engine_raw):
    """Restart budget from an optional `restart` key: profile wins over engine.

    Shapes: `true` -> defaults (restart on exit 75, gufo's device-lost EX_TEMPFAIL);
    `[75, 1]` -> list of eligible exit codes; or an object with keys `codes`,
    `max` (consecutive restarts before giving up), `delay_s` (pause before
    relaunch) and `reset_after_s` (an attempt that lived this long clears the
    consecutive counter, so a crash loop stops but a healthy day does not).
    Absent or `false` -> None: one attempt, the historical behaviour.
    """
    raw = None
    for src in (profile_raw, engine_raw):
        if isinstance(src, dict) and src.get("restart") is not None:
            raw = src["restart"]
    if raw is None or raw is False:
        return None
    if raw is True:
        raw = {}
    if isinstance(raw, (list, tuple)):
        raw = {"codes": list(raw)}
    if not isinstance(raw, dict):
        print(f"{APP}: ignoring malformed restart config {raw!r}", file=sys.stderr)
        return None
    d = dict(RESTART_DEFAULTS)
    try:
        d["codes"] = frozenset(int(c) for c in raw.get("codes", d["codes"]))
        d["max"] = int(raw.get("max", d["max"]))
        d["delay_s"] = max(0.0, float(raw.get("delay_s", d["delay_s"])))
        d["reset_after_s"] = float(raw.get("reset_after_s", d["reset_after_s"]))
    except (TypeError, ValueError) as exc:
        print(f"{APP}: ignoring malformed restart config ({exc})", file=sys.stderr)
        return None
    if not d["codes"] or d["max"] <= 0:
        return None
    return d


# ---------------------------------------------------------------------------- parsing


class LogRules:
    """How to read one engine's log lines (per-engine, from the config)."""

    def __init__(self, engine: Engine, hide: list, show: list):
        log = engine.log
        self.style = log["style"]
        self.line_re = engine.line_re
        self.quiet_tags = set(log["quiet_tags"])
        self.quiet_events = {k: set(v) for k, v in (log["quiet_events_by_tag"] or {}).items()}
        self.received = set(log["received_events"])
        self.finish = set(log["finish_events"])
        self.completed = set(log["completion_events"])
        self.fields = dict(log["fields"] or {})
        self.hide = [re.compile(p, re.I) for p in list(log["hide"]) + list(hide)]
        self.show = [re.compile(p, re.I) for p in list(log["show"]) + list(show)]

    def parse(self, line: str):
        """-> (severity, tag, {key: value}, datetime|None); None when not a log record."""
        if self.style != "kv":
            return None
        m = self.line_re.match(line)
        if not m:
            return None
        fields = {}
        for key, val in KV_RE.findall(m.group("rest")):
            if key not in fields:
                fields[key] = val.strip('"')
        for target, source in self.fields.items():
            if source in fields:
                fields[target] = fields[source]
        try:
            when = datetime.strptime(f"{m.group('date')} {m.group('time')}", "%Y-%m-%d %H:%M:%S")
        except (ValueError, IndexError):
            when = None
        return m.group("sev").upper(), (m.group("tag") or "").lower(), fields, when

    def is_quiet(self, sev: str, tag: str, fields: dict, raw_text: str) -> bool:
        """True when the line is routine chatter that should not reach the log pane."""
        for pat in self.show:
            if pat.search(raw_text):
                return False
        for pat in self.hide:
            if pat.search(raw_text):
                return True
        if sev != "INFO":
            return False
        if tag in self.quiet_tags:
            return True
        events = self.quiet_events.get(tag)
        return bool(events and fields.get("event") in events)


def num(fields: dict, key: str, cast=float, default=None):
    raw = fields.get(key)
    if raw is None:
        return default
    try:
        return cast(raw)
    except (TypeError, ValueError):
        try:
            return cast(float(raw))  # tolerate "108.0" in an integer field
        except (TypeError, ValueError):
            return default


# ---------------------------------------------------------------------------- stats


class Stats:
    def __init__(self, started: float, context=None, model="", engine="", version="",
                 profile="", family="", engine_meta=None):
        self.started = started
        self.context = context
        self.model = model
        self.engine = engine
        self.version = version
        self.profile = profile
        self.family = family
        self.engine_meta = engine_meta or {}

        self.restarts = 0  # engine relaunches by the restart policy (not counted as requests)

        self.done = 0
        self.errors = 0
        self.inflight = 0

        self.prefill_tokens = 0
        self.prefill_seconds = 0.0
        self.gen_tokens = 0
        self.gen_seconds = 0.0
        self.cached_tokens = 0
        self.prompt_tokens = 0
        self.duration_seconds = 0.0
        self.draft_accepted = 0
        self.draft_proposed = 0
        self.last: dict | None = None
        self.last_seen = 0.0
        self.last_dt: datetime | None = None
        self.error_ids: set = set()
        self.log_first: datetime | None = None
        self.log_last: datetime | None = None

    # --- updates ---------------------------------------------------------------

    def note_status(self, status, request_id=None) -> None:
        """Count a non-2xx response once per request id."""
        status = (status or "").strip()
        if not status or status.startswith("2"):
            return
        if request_id:
            if request_id in self.error_ids:
                return
            self.error_ids.add(request_id)
        self.errors += 1

    def add_completed(self, f: dict, when=None) -> None:
        prefill_tok = num(f, "prefill_tokens", int, 0) or 0
        gen_tok = num(f, "generated_tokens", int, 0) or 0
        prefill_tps = num(f, "prefill_tps", float, 0.0) or 0.0
        decode_tps = num(f, "decode_tps", float, 0.0) or 0.0

        self.done += 1
        self.prefill_tokens += prefill_tok
        self.gen_tokens += gen_tok
        if prefill_tok > 0 and prefill_tps > 0:
            self.prefill_seconds += prefill_tok / prefill_tps
        if gen_tok > 0 and decode_tps > 0:
            self.gen_seconds += gen_tok / decode_tps

        self.cached_tokens += num(f, "cached_tokens", int, 0) or 0
        self.prompt_tokens += num(f, "prompt_tokens", int, 0) or 0
        self.duration_seconds += (num(f, "duration_ms", float, 0.0) or 0.0) / 1000.0
        self.draft_accepted += num(f, "draft_accepted", int, 0) or 0
        self.draft_proposed += num(f, "draft_proposed", int, 0) or 0

        self.note_status(f.get("status"), f.get("request"))

        self.last = {
            "id": f.get("request", "?"),
            "status": (f.get("status") or "").strip(),
            "outcome": f.get("outcome", ""),
            "finish": f.get("finish", ""),
            "prefill_tps": prefill_tps,
            "decode_tps": decode_tps,
            "prefill_tokens": prefill_tok,
            "gen_tokens": gen_tok,
            "prompt_tokens": num(f, "prompt_tokens", int, 0) or 0,
            "cached_tokens": num(f, "cached_tokens", int, 0) or 0,
            "ttft_ms": num(f, "ttft_ms", float),
            "queue_ms": num(f, "queue_ms", float),
            "duration_ms": num(f, "duration_ms", float),
            "accept": num(f, "acceptance_pct", float),
            "draft_accepted": num(f, "draft_accepted", int),
            "draft_proposed": num(f, "draft_proposed", int),
        }
        self.last_seen = time.monotonic()
        self.last_dt = when

    def note_time(self, when) -> None:
        """Track the span the log itself covers (matters when replaying a file)."""
        if when is None:
            return
        if self.log_first is None or when < self.log_first:
            self.log_first = when
        if self.log_last is None or when > self.log_last:
            self.log_last = when

    def add_received(self) -> None:
        self.inflight += 1

    def finish_inflight(self) -> None:
        if self.inflight > 0:
            self.inflight -= 1

    # --- derived ---------------------------------------------------------------

    @property
    def avg_prefill_tps(self):
        return self.prefill_tokens / self.prefill_seconds if self.prefill_seconds > 0 else None

    @property
    def avg_gen_tps(self):
        return self.gen_tokens / self.gen_seconds if self.gen_seconds > 0 else None

    @property
    def uptime(self) -> float:
        return max(0.0, time.monotonic() - self.started)

    @property
    def elapsed(self) -> float:
        """Wall clock since launch, or the span a replayed log covers if that is longer."""
        span = 0.0
        if self.log_first and self.log_last and self.log_last > self.log_first:
            span = (self.log_last - self.log_first).total_seconds()
        return max(self.uptime, span)

    @property
    def last_age(self) -> float:
        """How long ago the last completed request was (also correct for replays)."""
        mono = max(0.0, time.monotonic() - self.last_seen) if self.last_seen else 0.0
        span = 0.0
        if self.last_dt and self.log_last and self.log_last > self.last_dt:
            span = (self.log_last - self.last_dt).total_seconds()
        return max(mono, span)

    @property
    def cache_hit_pct(self):
        return 100.0 * self.cached_tokens / self.prompt_tokens if self.prompt_tokens else None

    @property
    def accept_pct(self):
        return 100.0 * self.draft_accepted / self.draft_proposed if self.draft_proposed else None

    def record(self, exit_code, log_path, engine_path=None, argv=None, replay=None):
        """One JSON object per run, appended to the history file for later reporting."""
        return {
            "ts": datetime.now().astimezone().replace(microsecond=0).isoformat(),
            "engine": self.engine or None,
            "family": self.family or None,
            "version": self.version or None,
            "engine_meta": self.engine_meta or None,
            "engine_path": engine_path or None,
            "profile": self.profile or None,
            "model": self.model or None,
            "context": self.context,
            "argv": argv or None,
            "elapsed_s": round(self.elapsed, 3),
            "busy_s": round(self.duration_seconds, 3),
            "requests": self.done,
            "errors": self.errors,
            "prefill_tokens": self.prefill_tokens,
            "prefill_seconds": round(self.prefill_seconds, 3),
            "avg_prefill_tps": round(self.avg_prefill_tps, 2) if self.avg_prefill_tps else None,
            "generated_tokens": self.gen_tokens,
            "gen_seconds": round(self.gen_seconds, 3),
            "avg_gen_tps": round(self.avg_gen_tps, 2) if self.avg_gen_tps else None,
            "prompt_tokens": self.prompt_tokens,
            "cached_tokens": self.cached_tokens,
            "cache_hit_pct": round(self.cache_hit_pct, 2) if self.cache_hit_pct is not None else None,
            "draft_accepted": self.draft_accepted,
            "draft_proposed": self.draft_proposed,
            "acceptance_pct": round(self.accept_pct, 2) if self.accept_pct is not None else None,
            "last_request": self.last,
            "exit": exit_code,
            "restarts": self.restarts,
            "log": log_path,
            "replay": replay,
        }


# --------------------------------------------------------------------------- formatting


def human(n, unit: str = "") -> str:
    if n is None:
        return "-"
    n = float(n)
    for suffix in ("", "k", "M", "G", "T"):
        if abs(n) < 1000 or suffix == "T":
            return f"{n:.0f}{unit}" if suffix == "" else f"{n:.1f}{suffix}{unit}"
        n /= 1000.0
    return f"{n:.1f}T{unit}"


def secs(s) -> str:
    if s is None:
        return "-"
    if s < 1:
        return f"{s * 1000:.0f}ms"
    if s < 60:
        return f"{s:.1f}s"
    m, sec = divmod(int(s), 60)
    if m < 60:
        return f"{m}m{sec:02d}s"
    h, m = divmod(m, 60)
    return f"{h}h{m:02d}m{sec:02d}s"


def ms(v) -> str:
    return "-" if v is None else secs(v / 1000.0)


def tps(v, good: float, ok: float) -> str:
    """Colour-coded tokens/second."""
    if v is None:
        return f"{C.DIM}-{C.RESET}"
    colour = C.GREEN if v >= good else (C.YELLOW if v >= ok else C.RED)
    return f"{colour}{C.BOLD}{v:6.1f}{C.RESET}{C.DIM} t/s{C.RESET}"


def pct(v) -> str:
    return "-" if v is None else f"{v:.1f}%"


def label(text: str) -> str:
    return f"{C.DIM}{text}{C.RESET}"


def summary_line(raw: str, last: dict) -> str:
    """One condensed line per completed request (--summaries), for a scrollable history."""
    return (f"{C.DIM}{raw[:19]}{C.RESET} {C.CYAN}{last.get('id', '?')}{C.RESET} "
            f"pre {tps(last.get('prefill_tps') or None, 300, 120)} "
            f"gen {tps(last.get('decode_tps') or None, 45, 25)} "
            f"{label('out')} {human(last.get('gen_tokens', 0))} "
            f"{label('ttft')} {ms(last.get('ttft_ms'))} "
            f"{label('dur')} {ms(last.get('duration_ms'))} "
            f"{label('acc')} {pct(last.get('accept'))}")


def _f(v, spec=".1f", dash="-"):
    return dash if v is None else format(v, spec)


# ----------------------------------------------------------------------------- screen


class Screen:
    """Pinned header (rows 1..HEADER_ROWS) + a scrolling log pane below it.

    The log pane is a DEC scroll region, so appending lines never touches the
    header. The cursor row is tracked in Python and every write starts from an
    absolute position, so no save/restore drift is possible.
    """

    def __init__(self, out, rows: int, cols: int):
        self.out = out
        self.rows = rows
        self.cols = cols
        self.row = HEADER_ROWS + 1
        self._header: list[str] = []

    def _w(self, text: str) -> None:
        self.out.write(text)
        self.out.flush()

    def start(self) -> None:
        self._w("\x1b[0m\x1b[2J\x1b[H")
        self._w(f"\x1b[{HEADER_ROWS + 1};{self.rows}r")  # scroll region = log pane
        self._w("\x1b[?25l")  # hide cursor
        self.row = HEADER_ROWS + 1
        self._w(f"\x1b[{self.row};1H")

    def set_header(self, lines: list[str]) -> None:
        self._header = lines
        self.draw_header()

    def draw_header(self) -> None:
        self._w("\x1b[H")
        for i in range(HEADER_ROWS):
            line = self._header[i] if i < len(self._header) else ""
            pad = self.cols - vlen(line)
            if pad < 0:  # should not happen (segments are fitted), truncate as last resort
                line = self._truncate(line)
                pad = self.cols - vlen(line)
            self._w(line + " " * max(0, pad) + "\x1b[K")
            if i < HEADER_ROWS - 1:
                self._w("\r\n")
        self._w(f"\x1b[{self.row};1H")

    def _truncate(self, line: str) -> str:
        out, used = [], 0
        for ch in strip_ansi(line):
            w = 2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1
            if used + w > self.cols - 1:
                out.append("\u2026")
                break
            out.append(ch)
            used += w
        return "".join(out)

    def print_line(self, text: str) -> None:
        self._w(f"\x1b[{self.row};1H")
        self._w(text.rstrip("\r\n"))
        self._w("\x1b[K")  # erase the tail of the previous line
        wrapped = max(1, -(-vlen(text) // max(1, self.cols)))
        self.row = min(self.row + wrapped, self.rows)

    def resize(self, rows: int, cols: int) -> None:
        if (rows, cols) == (self.rows, self.cols):
            return
        self.rows, self.cols = rows, cols
        self.row = min(self.row, rows)
        self._w(f"\x1b[{HEADER_ROWS + 1};{rows}r")
        self.draw_header()

    def begin_exit(self, needed: int) -> None:
        """Hand the terminal back without letting the exit text scroll the header away.

        `needed` is how many rows the caller is about to write. When they no longer fit
        under the last log line, the summary is anchored to the bottom of the screen and
        overwrites the last few log lines instead of scrolling the pinned header off.
        """
        self._w("\x1b[0m\x1b[r\x1b[?25h")  # reset colours, scroll region, show cursor
        row = self.row
        if row + needed - 1 > self.rows:
            row = max(HEADER_ROWS + 1, self.rows - needed + 1)
        self._w(f"\x1b[{min(row, self.rows)};1H\x1b[J")

    @staticmethod
    def row_count(text: str, cols: int) -> int:
        return sum(max(1, -(-vlen(line) // max(1, cols))) for line in text.split("\n"))


def term_size(fallback=(24, 100)):
    try:
        size = os.get_terminal_size()
        return size.lines, size.columns
    except (OSError, ValueError):
        w, h = os.environ.get("COLUMNS"), os.environ.get("LINES")
        try:
            return (int(h) if h else fallback[0]), (int(w) if w else fallback[1])
        except ValueError:
            return fallback


# --------------------------------------------------------------------------- header


def render_header(st: Stats, cols: int) -> list[str]:
    sep = f"{C.DIM} | {C.RESET}"

    # ---- line 1: engine identity and since-start averages
    ident = f"{C.BOLD}{C.CYAN}{st.engine or APP}{C.RESET}"
    if st.version:
        ident += f" {C.BOLD}{st.version}{C.RESET}"
    if st.model:
        ident += f" {C.DIM}{st.model}{C.RESET}"
    seg1 = [ident, f"{label('up')} {C.BOLD}{secs(st.elapsed)}{C.RESET}"]
    active = f" {C.MAGENTA}{st.inflight}\u25b8{C.RESET}" if st.inflight else ""
    seg1.append(f"{label('reqs')} {C.BOLD}{st.done}{C.RESET}{active}"
                + (f" {C.RED}{st.errors}err{C.RESET}" if st.errors else ""))
    if st.restarts:
        seg1.append(f"{label('restarts')} {C.YELLOW}{C.BOLD}{st.restarts}{C.RESET}")
    seg1.append(f"{label('prefill')} {tps(st.avg_prefill_tps, 300, 120)}{label(' avg')}")
    seg1.append(f"{label('generate')} {tps(st.avg_gen_tps, 45, 25)}{label(' avg')}")
    seg1.append(f"{label('tok')} {human(st.prefill_tokens)} {label('calc')} "
                f"{human(st.cached_tokens)} {label('cached')} "
                f"{human(st.gen_tokens)} {label('gen')}")
    if st.accept_pct is not None:
        seg1.append(f"{label('accept')} {C.BOLD}{pct(st.accept_pct)}{C.RESET}")

    # ---- line 2: the last completed request
    last = st.last
    if last:
        seg2 = [
            f"{C.BOLD}{C.YELLOW}LAST{C.RESET} {C.BOLD}{last['id']}{C.RESET} "
            f"{label(f'{secs(st.last_age)} ago')}",
            f"{label('pre')} {tps(last['prefill_tps'] or None, 300, 120)} "
            f"{label(human(last['prefill_tokens']) + ' tok')}",
            f"{label('gen')} {tps(last['decode_tps'] or None, 45, 25)} "
            f"{label(human(last['gen_tokens']) + ' tok')}",
            f"{label('ttft')} {C.BOLD}{ms(last['ttft_ms'])}{C.RESET}",
            f"{label('dur')} {C.BOLD}{ms(last['duration_ms'])}{C.RESET}",
        ]
        if last["prompt_tokens"]:
            hit = 100.0 * last["cached_tokens"] / last["prompt_tokens"]
            ctx = f" {label('ctx')} {pct(100.0 * last['prompt_tokens'] / st.context)}" if st.context else ""
            seg2.append(f"{label('cache')} {C.BOLD}{pct(hit)}{C.RESET}"
                        f" {label(human(last['cached_tokens']))}{ctx}")
        if last["draft_accepted"] is not None:
            seg2.append(f"{label('draft')} {C.BOLD}{last['draft_accepted']}/{last['draft_proposed']}{C.RESET}")
        if last["queue_ms"]:
            seg2.append(f"{label('queue')} {ms(last['queue_ms'])}")
    else:
        note = "waiting for first request\u2026" if not st.inflight else f"{st.inflight} request(s) in flight\u2026"
        seg2 = [f"{C.BOLD}{C.YELLOW}LAST{C.RESET} {C.DIM}{note}{C.RESET}"]

    # ---- line 3: rule for the log pane
    rule_title = " log "
    rule = f"{C.DIM}\u2500\u2500{rule_title}" + "\u2500" * max(0, cols - vlen(rule_title) - 4) + C.RESET

    return [_fit(seg1, cols, sep), _fit(seg2, cols, sep), rule]


def _fit(segments: list[str], cols: int, sep: str) -> str:
    """Join segments with a separator, dropping the tail that would wrap."""
    sep_w = vlen(sep)
    out: list[str] = []
    used = 0
    for seg in segments:
        w = vlen(seg)
        add = w if not out else sep_w + w
        if used + add > cols:
            break
        out.append(seg)
        used += add
    return sep.join(out)


# ------------------------------------------------------------------------ run history


def append_history(path: str, record: dict) -> None:
    try:
        os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(json.dumps(record, ensure_ascii=False) + "\n")
    except OSError as exc:
        print(f"{APP}: could not append to {path}: {exc}", file=sys.stderr)


def report(path: str) -> int:
    """Aggregate runs.jsonl by engine/version."""
    if not os.path.exists(path):
        print(f"{APP}: no history file at {path} yet (it is written when a run finishes)")
        return 1
    runs = []
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                runs.append(json.loads(line))
            except ValueError:
                print(f"{APP}: skipping unparsable history line", file=sys.stderr)
    if not runs:
        print(f"{APP}: {path} is empty")
        return 1

    groups = defaultdict(lambda: {"runs": 0, "requests": 0, "errors": 0, "prefill": 0,
                                  "prefill_s": 0.0, "gen": 0, "gen_s": 0.0, "prompt": 0,
                                  "cached": 0, "models": set(), "days": set()})
    for r in runs:
        key = (r.get("engine") or "?", r.get("version") or "?")
        g = groups[key]
        g["runs"] += 1
        g["requests"] += r.get("requests") or 0
        g["errors"] += r.get("errors") or 0
        g["prefill"] += r.get("prefill_tokens") or 0
        g["prefill_s"] += r.get("prefill_seconds") or 0.0
        g["gen"] += r.get("generated_tokens") or 0
        g["gen_s"] += r.get("gen_seconds") or 0.0
        g["prompt"] += r.get("prompt_tokens") or 0
        g["cached"] += r.get("cached_tokens") or 0
        if r.get("model"):
            g["models"].add(r["model"])
        if r.get("ts"):
            g["days"].add(str(r["ts"])[:10])

    cols = ("ENGINE", "VERSION", "RUNS", "REQS", "ERR", "PREFILL", "GENERATE", "CACHE",
            "GEN TOK", "MODELS")
    rows = []
    for (engine, version), g in sorted(groups.items()):
        pre = g["prefill"] / g["prefill_s"] if g["prefill_s"] else None
        gen = g["gen"] / g["gen_s"] if g["gen_s"] else None
        cache = 100.0 * g["cached"] / g["prompt"] if g["prompt"] else None
        models = ",".join(sorted(g["models"])) or "-"
        rows.append((engine, version, str(g["runs"]), str(g["requests"]), str(g["errors"]),
                     f"{pre:.1f} t/s" if pre else "-", f"{gen:.1f} t/s" if gen else "-",
                     pct(cache), human(g["gen"]), models))
    widths = [max(len(h), *(len(r[i]) for r in rows)) for i, h in enumerate(cols)]
    days = {d for g in groups.values() for d in g["days"]}
    head = "  ".join(h.ljust(widths[i]) for i, h in enumerate(cols))
    day_word = "day" if len(days) == 1 else "days"
    run_word = "run" if len(runs) == 1 else "runs"
    print(f"{C.BOLD}{head}{C.RESET}  ({len(runs)} {run_word}, {len(days)} {day_word})")
    for r in rows:
        print("  ".join(cell.ljust(widths[i]) for i, cell in enumerate(r)))
    return 0


# ------------------------------------------------------------------------------ main


def read_lines(pipe, sink: queue.Queue) -> None:
    for raw in iter(pipe.readline, ""):
        sink.put(("line", raw.rstrip("\r\n")))
    sink.put(("eof", ""))


def log_path_for(cfg: dict, log_dir_opt: str, model: str) -> str:
    """Raw-log destination: --log-file, else <log_dir>/<model>-<stamp>.log."""
    if log_dir_opt:
        path = expand(log_dir_opt)
        if os.path.isdir(path) or path.endswith(("\\", "/")):
            stamp = time.strftime("%Y%m%d-%H%M%S")
            path = os.path.join(path, f"{model or 'run'}-{stamp}.log")
    else:
        base = cfg.get("log_dir") or os.path.join(os.environ.get("TEMP", os.path.expanduser("~")), APP)
        stamp = time.strftime("%Y%m%d-%H%M%S")
        path = os.path.join(expand(base), f"{model or 'run'}-{stamp}.log")
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    return path


def print_plain_summary(st: Stats) -> str:
    lines = [
        "",
        f"{C.BOLD}=== {st.engine or APP} {st.version or ''} "
        f"{'(' + st.profile + ')' if st.profile else ''} "
        f"since start: {secs(st.elapsed)} | {st.done} requests "
        f"({st.errors} non-2xx) ==={C.RESET}",
        f"  prefill   {human(st.prefill_tokens)} tokens computed"
        f" {label('+ ' + human(st.cached_tokens) + ' from cache')}  "
        f"in {secs(st.prefill_seconds)}  ->  avg {_f(st.avg_prefill_tps)} t/s",
        f"  generate  {human(st.gen_tokens)} tokens in {secs(st.gen_seconds)}"
        f"  ->  avg {_f(st.avg_gen_tps)} t/s",
    ]
    if st.prompt_tokens:
        lines.append(f"  cache     {pct(st.cache_hit_pct)} of {human(st.prompt_tokens)} prompt "
                     f"tokens came from the prefix cache")
        lines.append(f"  busy      {secs(st.duration_seconds)} inside requests "
                     f"of {secs(st.elapsed)} wall clock")
    if st.draft_proposed:
        lines.append(f"  drafts    {st.draft_accepted}/{st.draft_proposed} accepted "
                     f"({pct(st.accept_pct)})")
    if st.last:
        l = st.last
        lines.append(f"  last      {l['id']}  prefill {_f(l['prefill_tps'])} t/s "
                     f"({_f(l['prefill_tokens'], '.0f')} tok)  gen {_f(l['decode_tps'])} t/s "
                     f"({_f(l['gen_tokens'], '.0f')} tok)  ttft {ms(l['ttft_ms'])}"
                     f"  dur {ms(l['duration_ms'])}  accept {pct(l['accept'])}")
    return "\n".join(lines)


def list_config(cfg: dict, config_path: str) -> None:
    print(f"{C.BOLD}config{C.RESET} {config_path}")
    engines = cfg.get("engines", {}) or {}
    print(f"\n{C.BOLD}engines{C.RESET}  (default: {cfg.get('default_engine', '-')})")
    for name in sorted(engines):
        eng = Engine(name, engines[name], os.path.dirname(config_path))
        found = bool(eng.path) and (os.path.exists(eng.path) or shutil.which(eng.path))
        mark = "" if found else f" {C.RED}not found{C.RESET}"
        version = eng.version or ("auto" if eng.version_args else "?")
        print(f"  {name:<12} {version:<12} {eng.family:<22} {eng.path}{mark}")
    profiles = cfg.get("profiles", {}) or {}
    print(f"\n{C.BOLD}profiles{C.RESET}  (default: {cfg.get('default_profile', '-')})")
    for name in sorted(profiles):
        prof = Profile(name, profiles[name])
        print(f"  {name:<24} engine={prof.engine or cfg.get('default_engine', '-'):<12} "
              f"model={prof.model or '-'}")
        shown = subst_home(" ".join(prof.args), os.path.dirname(config_path))
        print(f"  {'':<24} {shown[:150]}")


def self_test(keep_tmp: bool = False) -> int:
    """Run lmrun end-to-end against fake_engine.py and assert the numbers line up.

    The fake engine is deterministic, so every expectation here is exact. Nothing real
    is launched and the scratch config/history live in a temp folder.
    """
    import tempfile

    tmp = tempfile.mkdtemp(prefix="lmrun-selftest-")
    cfg_path = os.path.join(tmp, "lmrun-selftest.json")
    hist = os.path.join(tmp, "runs.jsonl")
    logs = os.path.join(tmp, "logs")
    fake = os.path.join(HERE, "fake_engine.py")
    cfg = {
        "default_engine": "fake",
        "default_profile": "fake",
        "log_dir": logs,
        "history": hist,
        "engines": {
            "fake": {"path": sys.executable, "version": None, "family": "self-test",
                     "build": "deterministic", "version_args": [fake, "--version"],
                     "log": {"style": "kv"}},
            "fake-plain": {"path": sys.executable, "version": "9.9.9", "family": "self-test",
                           "version_args": [fake, "--version"], "log": {"style": "plain"}},
        },
        "profiles": {
            "fake": {"engine": "fake", "model": "fake-model",
                     "args": [fake, "--max-requests", "3"]},
            "fake-plain": {"engine": "fake-plain", "model": "plain-model",
                           "args": [fake, "--plain", "--max-requests", "2"]},
            # crash-loop stand-ins: exit 75 is gufo's device-lost EX_TEMPFAIL
            "fake-restart": {"engine": "fake", "model": "fake-crash",
                             "args": [fake, "--max-requests", "1", "--interval", "0.05",
                                      "--exit-code", "75"],
                             "restart": {"codes": [75], "max": 2, "delay_s": 0.1}},
            "fake-norestart": {"engine": "fake", "model": "fake-crash",
                               "args": [fake, "--max-requests", "1", "--interval", "0.05",
                                        "--exit-code", "9"],
                               "restart": {"codes": [75], "max": 2, "delay_s": 0.1}},
        },
    }
    with open(cfg_path, "w", encoding="utf-8") as fh:
        json.dump(cfg, fh, indent=2)

    results = []

    def check(name, ok, detail=""):
        results.append(bool(ok))
        tag = f"{C.GREEN}PASS{C.RESET}" if ok else f"{C.RED}FAIL{C.RESET}"
        print(f"  {tag}  {name}" + (f"   {C.DIM}{detail}{C.RESET}" if detail else ""))

    def run(*args):
        try:
            proc = subprocess.run([sys.executable, os.path.join(HERE, "lmrun.py"), *args],
                                  capture_output=True, text=True, encoding="utf-8",
                                  errors="replace", timeout=120)
            return strip_ansi(proc.stdout or ""), proc.stderr or "", proc.returncode
        except subprocess.TimeoutExpired:
            return "", "timed out", 124

    print(f"{C.BOLD}lmrun self-test{C.RESET}  {C.DIM}stand-in engine: {fake}{C.RESET}")

    out, err, rc = run("--config", cfg_path, "--profile", "fake", "--size", "40x150", "--color")
    check("run exits cleanly", rc == 0, f"rc={rc} {err.strip()[:80]}")
    check("version read from --version", "fake 1.2.3" in out, "header should name engine+version")
    check("requests + errors counted", "3 requests (1 non-2xx)" in out)
    check("prefill avg = 600 tok / 1.5 s", "avg 400.0 t/s" in out)
    check("generation avg = 300 tok / 6.0 s", "avg 50.0 t/s" in out)
    check("prefix-cache hit rate", "99.8%" in out, "300.3k of 300.9k prompt tokens")
    check("draft acceptance", "240/300" in out)
    check("routine [cache] INFO hidden from the pane", "[cache]" not in out)
    check("WARN passed through", "[WARN]" in out)
    check("ERROR passed through", "[ERROR]" in out)
    check("last request line present", "LAST r3" in out)

    teed = [os.path.join(logs, n) for n in os.listdir(logs)] if os.path.isdir(logs) else []
    check("raw log tee written", bool(teed), teed[0] if teed else "no file")
    if teed:
        with open(teed[0], "r", encoding="utf-8", errors="replace") as fh:
            raw = fh.read()
        check("raw log kept the hidden lines", "event=retention" in raw
              and "event=completed" in raw)

    recs = []
    if os.path.exists(hist):
        with open(hist, "r", encoding="utf-8") as fh:
            recs = [json.loads(line) for line in fh if line.strip()]
    check("history record appended", len(recs) == 1, f"{len(recs)} records")
    if recs:
        r = recs[-1]
        check("history engine/version/meta",
              r.get("engine") == "fake" and r.get("version") == "1.2.3"
              and (r.get("engine_meta") or {}).get("build") == "deterministic")
        check("history token totals", r.get("prefill_tokens") == 600
              and r.get("generated_tokens") == 300 and r.get("requests") == 3
              and r.get("errors") == 1)
        check("history averages", r.get("avg_prefill_tps") == 400.0
              and r.get("avg_gen_tps") == 50.0)
        check("history marks a live run", r.get("replay") is None and r.get("exit") == 0)

    out, _, rc = run("--report", hist)
    check("--report runs", rc == 0)
    check("--report groups by engine/version", "fake" in out and "1.2.3" in out)
    check("--report aggregates t/s", "400.0 t/s" in out and "50.0 t/s" in out)

    out, _, rc = run("--config", cfg_path, "--profile", "fake-plain", "--size", "40x150")
    check("plain log style runs", rc == 0)
    check("plain style passes every line", "slot 1 processed 100 tokens" in out)
    check("plain style reports no parsed requests", "0 requests" in out)

    out, _, rc = run("--config", cfg_path, "--list")
    check("--list shows both engines", rc == 0 and "fake-plain" in out and "self-test" in out)

    out, _, rc = run("--config", cfg_path, "--profile", "fake-restart", "--size", "40x150",
                     "--color")
    check("device-lost exit restarts the engine", "restart 1 of 2" in out
          and "restart 2 of 2" in out, f"rc={rc}")
    check("restart budget exhausts and gives up", "giving up" in out, f"rc={rc}")
    check("engine exit code becomes lmrun's", rc == 75, f"rc={rc}")
    if os.path.exists(hist):
        with open(hist, "r", encoding="utf-8") as fh:
            last_rec = json.loads([l for l in fh if l.strip()][-1])
        check("history counts the restarts", last_rec.get("restarts") == 2
              and last_rec.get("exit") == 75, str(last_rec.get("restarts")))
    out, _, rc = run("--config", cfg_path, "--profile", "fake-norestart", "--size", "40x150",
                     "--color", "--no-history")
    check("exit code outside codes never restarts", rc == 9 and "engine exited" not in out,
          f"rc={rc}")

    shutil.rmtree(tmp, ignore_errors=not keep_tmp)
    failed = results.count(False)
    tail = f"  {C.DIM}(scratch kept in {tmp}){C.RESET}" if failed and keep_tmp else ""
    print(f"\n{C.BOLD}{len(results) - failed}/{len(results)} checks passed{C.RESET}"
          + (f"  {C.RED}{failed} failed{C.RESET}" if failed else "") + tail)
    return 1 if failed else 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(
        prog=APP,
        description="Launch a llama.cpp-compatible engine with a live prefill/generation "
                    "throughput dashboard, driven by lmrun.json.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="Everything after `--` replaces the profile's engine arguments.",
    )
    ap.add_argument("--config", default=os.environ.get("LMRUN_CONFIG", DEFAULT_CONFIG),
                    metavar="PATH", help=f"config file (default {DEFAULT_CONFIG})")
    ap.add_argument("--profile", metavar="NAME", help="profile to run (default: default_profile)")
    ap.add_argument("--engine", metavar="NAME", help="engine name from the config")
    ap.add_argument("--bin", metavar="PATH", help="engine binary, overriding the config")
    ap.add_argument("--list", action="store_true", help="list engines and profiles, then exit")
    ap.add_argument("--report", nargs="?", const="", metavar="HISTORY",
                    help="aggregate the run history by engine/version")
    ap.add_argument("--self-test", action="store_true",
                    help="drive the bundled fake_engine.py and verify counting, filtering, "
                         "version detection, raw-log tee and history/report output")
    ap.add_argument("--keep-tmp", action="store_true",
                    help="with --self-test: keep the scratch config, logs and history")
    ap.add_argument("-a", "--all", action="store_true", help="print every log line, no filtering")
    ap.add_argument("--summaries", action="store_true",
                    help="print one condensed line per completed request")
    ap.add_argument("--hide", action="append", default=[], metavar="REGEX",
                    help="also hide lines matching REGEX (repeatable)")
    ap.add_argument("--show", action="append", default=[], metavar="REGEX",
                    help="always show lines matching REGEX (repeatable, wins over --hide)")
    ap.add_argument("--no-color", action="store_true", help="disable ANSI colours")
    ap.add_argument("--color", action="store_true", help="force ANSI colours even when piped")
    ap.add_argument("--no-header", action="store_true", help="just filter, no pinned dashboard")
    ap.add_argument("--log-file", metavar="PATH", help="raw log tee destination (default: log_dir)")
    ap.add_argument("--no-log", action="store_true", help="disable the raw log tee")
    ap.add_argument("--no-history", action="store_true",
                    help="do not append this run to the JSON history")
    ap.add_argument("--replay", metavar="PATH",
                    help="parse an existing engine log instead of launching anything")
    ap.add_argument("--size", metavar="ROWSxCOLS", help="force terminal size, e.g. 120x40")
    ap.add_argument("args", nargs=argparse.REMAINDER, help="engine arguments (after --)")
    opts = ap.parse_args(argv)

    if opts.no_color or os.environ.get("NO_COLOR") or (not opts.color and not sys.stdout.isatty()):
        disable_color()
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass

    if opts.self_test:
        return self_test(keep_tmp=opts.keep_tmp)

    config_path = expand(opts.config)
    config_dir = os.path.dirname(config_path)
    try:
        cfg = load_config(config_path)
    except ConfigError as exc:
        if opts.report is not None or opts.replay:
            cfg = {}  # reporting and replays need no engine config
        else:
            print(str(exc), file=sys.stderr)
            return 2

    if opts.list:
        list_config(cfg, config_path)
        return 0

    history_path = history_target(cfg.get("history") or "runs.jsonl", config_dir)
    if opts.report is not None:
        return report(expand(opts.report) if opts.report else history_path)

    try:
        engine, profile = resolve(opts.engine, opts.bin, opts.profile, cfg, config_dir)
    except ConfigError as exc:
        print(str(exc), file=sys.stderr)
        return 2
    except KeyError as exc:
        print(f"{APP}: config is missing a key: {exc}", file=sys.stderr)
        return 2

    if opts.replay and not engine.path:
        engine.name = "replay"  # nothing is launched, so label the record honestly

    eng_args = list(opts.args)
    if eng_args and eng_args[0] == "--":
        eng_args = eng_args[1:]
    if not eng_args:
        eng_args = list(profile.args)

    if not opts.replay and not engine.path:
        print(f"{APP}: no engine binary. Set engines.<name>.path in {config_path}, "
              f"pass --bin PATH, or supply your own command after --", file=sys.stderr)
        return 2

    # llama.cpp forks spell these flags differently, and the header needs both values
    context = int_arg(eng_args, "--context") or int_arg(eng_args, "--ctx-size")
    model = (probe_arg(eng_args, "--served-model-name") or probe_arg(eng_args, "--alias")
             or profile.model or "")

    if engine.version is None and engine.path and not opts.replay:
        detected = engine.detect_version()
        if detected:
            engine.version = detected[0]

    rules = LogRules(engine, opts.hide, opts.show)

    tee = None
    log_path = None
    if not opts.no_log:
        try:
            log_path = log_path_for(cfg, opts.log_file, model or engine.name)
            tee = open(log_path, "a", encoding="utf-8", errors="replace")
        except OSError as exc:
            print(f"{APP}: cannot write log to {log_path}: {exc}", file=sys.stderr)
            tee = None

    use_tui = (sys.stdout.isatty() or bool(opts.size)) and not opts.no_header
    if use_tui:
        enable_vt_mode()  # legacy Windows consoles need this before honouring escapes
        try:  # take full control of line breaks: no \r\r\n from text-mode translation
            sys.stdout.reconfigure(newline="")
        except (AttributeError, ValueError):
            pass
    if opts.size:
        try:
            r, c = opts.size.lower().split("x")
            rows, cols = int(r), int(c)
        except ValueError:
            ap.error("--size wants ROWSxCOLS, e.g. 120x40")
    else:
        rows, cols = term_size()

    screen = Screen(sys.stdout, rows, cols) if use_tui else None

    stats = Stats(time.monotonic(), context, model, engine.name, engine.version or "",
                  profile.name or "", engine.family, engine.meta)
    policy = restart_policy(
        (cfg.get("profiles", {}) or {}).get(profile.name) if profile.name else None,
        (cfg.get("engines", {}) or {}).get(engine.name))

    proc = None
    cmd = engine.launch_argv(eng_args) if not opts.replay else None
    child_in = None
    env, creationflags = None, 0
    if opts.replay:
        child_in = open(opts.replay, "r", encoding="utf-8", errors="replace")
    else:
        env = dict(os.environ)
        env.update({str(k): str(v) for k, v in (engine.extra or {}).items()})
        creationflags = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0) if os.name == "nt" else 0

    rc = 0
    consecutive = 0  # restarts since the last attempt that lived long enough
    try:
        if screen:
            screen.start()
            screen.set_header(render_header(stats, cols))
        while True:  # engine attempts; one pass unless a restart policy is armed
            # Fresh queue per attempt: the previous reader's eof marker can never
            # end the next attempt before its own log does.
            q: queue.Queue = queue.Queue()
            if opts.replay:
                threading.Thread(target=read_lines, args=(child_in, q), daemon=True).start()
            else:
                try:
                    proc = subprocess.Popen(
                        cmd,
                        stdout=subprocess.PIPE,
                        stderr=subprocess.STDOUT,
                        stdin=subprocess.DEVNULL,
                        text=True,
                        encoding="utf-8",
                        errors="replace",
                        bufsize=1,
                        env=env,
                        creationflags=creationflags,
                    )
                except OSError as exc:
                    print(f"{APP}: cannot start {engine.path}: {exc}", file=sys.stderr)
                    rc = 2
                    break
                threading.Thread(target=read_lines, args=(proc.stdout, q), daemon=True).start()
                if not use_tui:
                    print(f"{C.DIM}{APP}: {engine.label()} pid {proc.pid}  raw log -> "
                          f"{log_path or 'off'}{C.RESET}")

            attempt_started = time.monotonic()
            eof = False
            while not eof:
                try:
                    kind, line = q.get(timeout=0.5)
                except queue.Empty:
                    if screen:
                        if proc and proc.poll() is not None and q.empty():
                            break
                        screen.set_header(render_header(stats, cols))  # tick uptime / age
                    continue
                if kind == "eof":
                    eof = True
                    continue
                if tee:
                    tee.write(line + "\n")
                    tee.flush()

                parsed = rules.parse(line)
                sev = tag = ""
                fields = {}
                when = None
                if parsed:
                    sev, tag, fields, when = parsed
                    stats.note_time(when)
                    event = fields.get("event")
                    if event in rules.received:
                        stats.add_received()
                    elif event in rules.finish:
                        stats.finish_inflight()
                        if event in rules.completed:
                            stats.add_completed(fields, when)
                        else:
                            stats.note_status(fields.get("status"), fields.get("request"))

                completed = bool(parsed) and fields.get("event") in rules.completed
                if completed and opts.summaries:
                    text, show_line = summary_line(line, stats.last or {}), True
                elif opts.all:
                    text, show_line = line, True
                elif parsed is None:
                    text, show_line = line, True  # banners, tracebacks, unknown formats
                else:
                    text, show_line = line, not rules.is_quiet(sev, tag, fields, line)

                if show_line:
                    if screen:
                        screen.print_line(text)
                    else:
                        sys.stdout.write(text + "\n")
                        sys.stdout.flush()

                if screen:
                    if completed:
                        screen.set_header(render_header(stats, cols))
                    if not opts.size:
                        r, c = term_size()
                        if (r, c) != (screen.rows, screen.cols):
                            screen.resize(r, c)

            engine_rc = 0 if opts.replay else proc.wait()
            if opts.replay:
                break
            if policy is not None and engine_rc in policy["codes"]:
                if time.monotonic() - attempt_started >= policy["reset_after_s"]:
                    consecutive = 0  # a healthy long run is not part of a crash loop
                if consecutive < policy["max"]:
                    consecutive += 1
                    stats.restarts += 1
                    banner = (f"{C.YELLOW}{APP}: engine exited with code {engine_rc}; "
                              f"restart {consecutive} of {policy['max']} in "
                              f"{policy['delay_s']:.0f}s{C.RESET}")
                    if tee:
                        tee.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} [WARN] [lmrun] "
                                  f"event=restart exit={engine_rc} restart={consecutive} "
                                  f"max={policy['max']} delay_s={policy['delay_s']:.1f}\n")
                        tee.flush()
                    if screen:
                        screen.print_line(banner)
                    else:
                        sys.stdout.write(banner + "\n")
                        sys.stdout.flush()
                    deadline = time.monotonic() + policy["delay_s"]
                    while time.monotonic() < deadline:
                        time.sleep(0.2)
                        if screen:
                            screen.set_header(render_header(stats, cols))
                    continue
                banner = (f"{C.RED}{APP}: engine exited with code {engine_rc}; restart "
                          f"budget ({policy['max']}) used up, giving up{C.RESET}")
                if tee:
                    tee.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} [ERROR] [lmrun] "
                              f"event=give_up exit={engine_rc} restarts={stats.restarts}\n")
                    tee.flush()
                if screen:
                    screen.print_line(banner)
                else:
                    sys.stdout.write(banner + "\n")
                    sys.stdout.flush()
            # The engine's own exit code becomes lmrun's, so a supervisor stacked
            # on lmrun (or a scripted caller) still sees e.g. 75 = device_lost.
            rc = engine_rc
            break
    except KeyboardInterrupt:
        if screen:
            screen.print_line(f"{C.YELLOW}^C interrupting {engine.name}\u2026{C.RESET}")
        if proc:
            try:
                if os.name == "nt":
                    # Ctrl+Break to the engine's own process group: lets it shut down tidy
                    proc.send_signal(signal.CTRL_BREAK_EVENT)
                else:
                    proc.terminate()
                proc.wait(timeout=10)
            except Exception:
                proc.kill()
        rc = 130
    finally:
        if tee:
            tee.close()
        record = stats.record(rc, log_path, engine.path,
                              None if opts.replay else cmd, replay=opts.replay)
        if not opts.no_history:
            append_history(history_path, record)
        summary = print_plain_summary(stats)
        if log_path:
            summary += f"\n{C.DIM}raw log: {log_path}{C.RESET}"
        if not opts.no_history:
            summary += f"\n{C.DIM}history: {history_path}{C.RESET}"
        if screen:
            screen.set_header(render_header(stats, cols))
            screen.begin_exit(Screen.row_count(summary, screen.cols))
        eol = "\r\n" if screen else "\n"
        sys.stdout.write(summary.replace("\n", eol) + eol)
        if proc and proc.poll() is None:
            proc.kill()
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
