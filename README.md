# lmrun

Start any llama.cpp-compatible inference engine with a live throughput dashboard pinned at
the top of the terminal, a log pane underneath that only shows what needs attention, and a
JSON run history so you can report performance per engine/version later.

```
C:\src\lmrun\run.cmd            start the default profile
C:\src\lmrun\run.cmd --list     what is configured
C:\src\lmrun\run.cmd --report   aggregate past runs by engine/version
C:\src\lmrun\run.cmd --self-test   verify the runner against the bundled fake engine
```

First run: `copy lmrun.example.json lmrun.json`, point `engines.local.path` at your server
binary and put your launch arguments in a profile. `lmrun.json` is git-ignored, so local
paths stay local. Even before you write one, `--self-test` and `--profile fake` work, and
`--replay some.log` / `--report path\to\runs.jsonl` need no config at all.

## What you see

```
local b4521 my-model | up 12m34s | reqs 53 | prefill  281.4 t/s avg | generate   49.7 t/s avg | tok 8.2k calc 5.9M cached 21.4k gen | accept 78.5%
LAST r53 3.1s ago | pre  465.6 t/s 334 tok | gen   50.8 t/s 287 tok | ttft 928ms | dur 6.6s | cache 99.7% 107.5k ctx 41.1% | draft 207/254 | queue 210ms
── log ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
2026-11-08 11:20:50 [WARN] [decode] draft model stalled, falling back to single-token step
2026-11-08 11:21:02 [ERROR] [http] request=r54 event=failed ... status=500 error="context overflow ..."
```

* **Line 1 – engine identity + since-start averages.** Token-weighted, i.e. `total tokens /
  total time`, not a mean of per-request numbers, so a long request counts for more.
  `calc` is prefill work really done (`prompt_tokens - cached_tokens`); `cached` is what the
  prefix cache served for free. Green / yellow / red against thresholds.
* **Line 2 – last completed request**: prefill and generation t/s, TTFT, wall duration,
  prefix-cache hit rate, context fill (`ctx`), draft acceptance, queue time.
* **Log pane** – normal scrolling terminal region, so warnings, errors and startup output
  still land in your scrollback.

Per-request time is derived from the engine's own numbers:
`prefill_seconds += prefill_tokens / prefill_tps`, `gen_seconds += generated_tokens / decode_tps`.

## Configuration: `lmrun.json`

```jsonc
{
  "default_engine": "local",
  "default_profile": "default",
  "log_dir": "%TEMP%\\lmrun",        // raw, unfiltered log of every run
  "history": "runs.jsonl",           // relative paths resolve next to the config

  "engines": {
    "local": {
      "path": "C:\\path\\to\\llama-server.exe",
      "version": null,               // omit or set null to read it from version_args
      "family": "llama.cpp",
      "build": "b4521",              // any extra keys you invent are kept as engine_meta
      "version_args": ["--version"],
      "log": { "style": "kv", "quiet_tags": ["cache", "slot", "metrics"] }
    }
  },

  "profiles": {
    "default": {
      "engine": "local",
      "model": "my-model",
      "args": ["-m", "C:\\Models\\my-model-Q4_K_M.gguf", "--alias", "my-model",
               "--port", "8080", "--ctx-size", "8192"]
    }
  }
}
```

* **engine** = an installed binary plus metadata. `path` may be absolute, `~`-relative,
  relative to the config file, or a bare command name resolved through `PATH` (e.g.
  `"python"`). `%LMRUN_HOME%` in a path, an argument or `version_args` expands to the folder
  lmrun lives in, so bundled helpers travel with the folder. Any extra keys you invent
  (`build`, `hardware`, `quant`, whatever) are copied into every history record as
  `engine_meta`, so reports can slice by them.
* **profile** = a named command line: which engine plus `args`. `model` is only a label; if
  the arguments carry `--served-model-name` (or llama-server's `--alias`) that wins. The
  header's `ctx` fill percentage reads `--context` or `--ctx-size`.
* Add another engine by copying the block in `"$engine_template"`, or skip the config
  entirely with `run.cmd --bin C:\path\to\llama-server.exe -- <args>`.

### Restarting a dead engine (`restart`)

Engines that exit with a defined "only a new process can recover" code — gufo exits `75`
(`EX_TEMPFAIL`) when it detects a lost GPU context — can relaunch themselves:

```jsonc
"restart": {
  "codes": [75],        // engine exit codes eligible for a restart (default: [75])
  "max": 3,             // consecutive restarts before lmrun gives up
  "delay_s": 15,        // pause before relaunch (let a flaky driver settle)
  "reset_after_s": 1800 // an attempt that lived this long clears the consecutive counter
}
```

Put it on an engine (all its profiles) or on a profile (wins). `"restart": true` means the
defaults; `"restart": [75, 1]` is shorthand for `codes`. A restart banner goes to the log
pane and the raw log (`event=restart` / `event=give_up`), the header counts restarts, and
the history record gains a `restarts` field. Ctrl+C and `--replay` never restart. When the
engine finally exits, lmrun exits with the engine's code, so a supervisor stacked on lmrun
still sees `75`.

### Log shapes

`log.style: "kv"` (default) expects `2026-11-08 11:15:48 [INFO] [tag] key=value ...`, which
is what engines with structured logging emit; the fields lmrun reads are `prefill_tokens`,
`prefill_tps`, `generated_tokens`, `decode_tps`, `prompt_tokens`, `cached_tokens`,
`ttft_ms`, `duration_ms`, `queue_ms`, `acceptance_pct`, `draft_accepted`, `draft_proposed`,
`status`, `request`, `event`. Set `log.pattern` to your own timestamp/level regex, and
`log.fields` to rename keys, e.g. `"fields": {"decode_tps": "predicted_toks_per_sec"}`.

`log.style: "plain"` means "no structured log": everything passes through, filtering by
`hide`/`show` still works, and only the wall clock is tracked.

## Filtering

Routine `INFO` lines are dropped from the log pane (still counted, still written to the raw
log): every tag in `log.quiet_tags` plus the events in `log.quiet_events_by_tag`. `WARN`,
`ERROR`, load output and anything unrecognised always pass through.

`--all` disables filtering · `--summaries` prints one condensed line per completed request ·
`--hide REGEX` / `--show REGEX` add rules (`--show` wins).

## Run history and reporting

Every finished run appends one JSON line to `history`: engine, version, `engine_meta`,
profile, model, context, argv, elapsed/busy seconds, request and error counts, prefill and
generation tokens with their seconds and averages, prompt/cached tokens with cache hit
rate, draft acceptance, the last request, exit code and the raw-log path.

```
ENGINE  VERSION   RUNS  REQS  ERR  PREFILL    GENERATE  CACHE  GEN TOK  MODELS
local   b4521       12   318    3  341.2 t/s   46.7 t/s  99.4%  412.5k  my-model
```

`--report` (or `--report path\to\runs.jsonl`) prints that table; aggregate the JSONL itself
however you like afterwards. `--no-history` skips the write for throwaway runs.

`runs.jsonl` is git-ignored because it grows with every run and each record embeds your temp
log path. To publish measured numbers, commit a `--report` dump instead:

```
run.cmd --report > bench.md      # aggregate table only, no local paths
```

## Local data stays local

`lmrun.json`, `runs.jsonl` and `*.log` are git-ignored, so ordinary editing and pushing of the
code never touches them: `git add -A`, `git commit`, `git push` and `git pull` skip ignored
files, and `git status` does not even list them (`git status --ignored` does). Three things to
keep in mind:

* **Don't force-add.** `git add -f lmrun.json` is the one command that would publish your paths
  and model names, so a `pre-commit` guard is worth installing locally (see below).
* **`git clean -fdx` deletes them.** That command throws away ignored files, i.e. your config
  and your measured history. Use `git clean -fd` (without `x`) if you ever need to tidy up.
* **Nothing backs them up.** Since they are not in the repo, a fresh clone elsewhere needs a copy
  of `lmrun.json` — keep it somewhere private, or point `LMRUN_CONFIG` at a file outside the repo
  and set `history` there to a path of your own, leaving the working copy free of personal data.

```sh
# .git/hooks/pre-commit  (hooks are local; they are never pushed)
#!/bin/sh
staged=$(git diff --cached --name-only --diff-filter=ACMR | tr '\n' ' ')
case " $staged " in
  *" lmrun.json "*|*" runs.jsonl "*|*.log*) echo "local data staged - commit refused"; exit 1 ;;
esac
exit 0
```

## Verifying without a model

`fake_engine.py` is a deterministic stand-in server: no GPU, no port, no model file, just
fixed log lines. The shipped config registers it as `fake` (kv-style) and `fake-plain`:

```
C:\src\lmrun\run.cmd --profile fake        watch the dashboard move (~20 s, Ctrl+C anytime)
C:\src\lmrun\run.cmd --profile fake-plain  the same engine with log.style = plain
C:\src\lmrun\run.cmd --self-test           30 automated checks, exit code 0 when all pass
```

`--self-test` writes its own scratch config in a temp folder, drives the fake engine through
lmrun with `--max-requests 3`, and asserts the whole chain: version read from `--version`,
request/error counting, token-weighted averages (600 tok / 1.5 s = 400.0 t/s and
300 tok / 6.0 s = 50.0 t/s), cache hit rate, draft acceptance, `[cache]` INFO swallowed
while WARN/ERROR pass through, the raw-log tee keeping the swallowed lines, exactly one
history record with matching engine/version/meta/averages, `--report` aggregating it, and a
plain-style engine running with every line passed through. It uses its own temp history, so
your `runs.jsonl` is untouched; add `--keep-tmp` to inspect the scratch folder after a failure.

## Options

| flag | meaning |
| --- | --- |
| `--config PATH` | config file (default `lmrun.json` next to the script, or `$LMRUN_CONFIG`) |
| `--profile NAME` / `--engine NAME` / `--bin PATH` | choose profile, configured engine, or raw binary |
| `-- ARGS…` | replace the profile's engine arguments entirely |
| `--list` | show engines and profiles |
| `--report [PATH]` | aggregate run history by engine/version |
| `--self-test` | drive the bundled fake engine and verify counting, filtering, history |
| `--keep-tmp` | with `--self-test`: keep its scratch config/logs/history |
| `-a`, `--all` | disable filtering |
| `--summaries` | condensed per-request lines in the log pane |
| `--no-header` | plain filtered stream, no pinned dashboard (good for pipes) |
| `--log-file PATH` / `--no-log` | raw tee destination (default `log_dir`) |
| `--no-history` | do not append to `history` |
| `--replay FILE` | parse an existing engine log instead of launching anything |
| `--color` / `--no-color` | force or disable ANSI colours |
| `--size ROWSxCOLS` | force the terminal size (mostly for testing/pipes) |

## Notes

* Ctrl+C sends Ctrl+Break to the engine's process group so it shuts down tidy, then prints
  the totals: uptime, requests, computed vs cached prefill, average t/s, cache hit rate,
  busy-vs-wall-clock time, draft acceptance and the last request. Engines flush log lines to
  pipes, so the dashboard keeps up in real time.
* When the engine exits (and no `restart` policy takes over, see Configuration), lmrun exits
  with the engine's own exit code; `130` stays reserved for Ctrl+C.
* With `--replay`, "up" and "… ago" follow the timestamps inside the log rather than the
  clock on the wall, so old logs report what actually happened.
* The pinned header uses a DEC scroll region; Windows Terminal and the Windows console both
  handle it. Non-tty output falls back to plain text automatically.

## License

MIT — see [LICENSE](LICENSE).
