#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""
fake_engine - deterministic stand-in for a llama.cpp-compatible server.

Used by `lmrun.py --self-test` and handy for eyeballing the dashboard without loading a
real model. The numbers are fixed on purpose so the assertions stay exact:

    per request (cycle of 3):                totals over 3 requests
      r1  prefill 200 tok @ 400 t/s (0.5s)     prefill 600 tok / 1.5s -> avg 400.0 t/s
          gen     100 tok @  50 t/s (2.0s)     gen     300 tok / 6.0s -> avg  50.0 t/s
      r2  prefill 150 tok @ 300 t/s (0.5s)     cached 300300 of 300900 prompt tokens
          gen      80 tok @  40 t/s (2.0s)     drafts 240/300 accepted (80.0%)
      r3  prefill 250 tok @ 500 t/s (0.5s)     one 503 failure unless --no-errors
          gen     120 tok @  60 t/s (2.0s)

Usage
  fake_engine.py [--version] [--max-requests N] [--interval SECONDS]
                 [--plain] [--no-errors]

  --plain  emit unstructured log lines instead of `ts [LEVEL] [tag] key=value`,
           which is how lmrun treats engines configured with log.style = "plain"
"""

import sys
import time

# prefill_tokens, prefill_tps, generated_tokens, decode_tps, cached_tokens
REQUESTS = [
    (200, 400.0, 100, 50.0, 100000),
    (150, 300.0, 80, 40.0, 100100),
    (250, 500.0, 120, 60.0, 100200),
]


def stamp():
    return time.strftime("%Y-%m-%d %H:%M:%S")


def main(argv):
    if "--version" in argv:
        print("fakellm version 1.2.3 (deadbeefcafebabe)")
        return 0

    max_requests = 3
    if "--max-requests" in argv:
        max_requests = int(argv[argv.index("--max-requests") + 1])
    interval = 0.1
    if "--interval" in argv:
        interval = float(argv[argv.index("--interval") + 1])
    plain = "--plain" in argv
    errors = "--no-errors" not in argv

    def emit(text):
        print(f"{stamp()} {text}", flush=True)

    if plain:
        print(f"{stamp()} fakellm : loading model fake.gguf", flush=True)
        print(f"{stamp()} fakellm : server listening on port 9933", flush=True)
        for i in range(1, max_requests + 1):
            print(f"{stamp()} fakellm : slot {i} processed 100 tokens at 50.00 t/s", flush=True)
            time.sleep(interval)
        print(f"{stamp()} fakellm : shutting down", flush=True)
        return 0

    emit("[INFO] [gpu] event=queue_budget kind=llm observed=0 budget=8 expected_max=3 cap=2")
    emit("[INFO] [loader] event=load_started kind=text artifact=fake.gguf")
    emit("[WARN] [gpu] VRAM tight: KV cache squeezed to 262144 tokens")
    emit("[INFO] [http] event=listening port=9933 model=fake context=262144")

    for i in range(1, max_requests + 1):
        pt, ptps, gt, dtps, cached = REQUESTS[(i - 1) % len(REQUESTS)]
        accepted = int(gt * 0.8)
        duration = pt / ptps * 1000 + gt / dtps * 1000 + 210  # + queue
        emit(f"[INFO] [http] request=r{i} event=received method=POST path=/v1/chat/completions "
             f"body_bytes={480000 + i}")
        emit(f"[INFO] [cache] schema=component-cache-v1 event=acquire reuse=live "
             f"selected_tokens={cached} restored_bytes=0")
        time.sleep(interval)
        for _ in range(2):  # routine spam that the log pane should swallow
            emit(f"[INFO] [cache] schema=component-cache-v1 event=retention action=2 reason=1 "
                 f"tokens={cached} purpose=2 rank=2 unique_bytes_freed=661197 ledger_bytes=6557912150")
        emit(f"[INFO] [http] request=r{i} event=completed method=POST path=/v1/chat/completions "
             f"status=200 duration_ms={duration:.1f} outcome=completed "
             f"prompt_tokens={cached + pt} prefill_tokens={pt} generated_tokens={gt} "
             f"finish=stop cache=memory cached_tokens={cached} cache_restore_ms=0.0 "
             f"queue_depth=1 queue_ms=210.0 ttft_ms={pt / ptps * 1000 + 210:.1f} "
             f"prefill_tps={ptps:.1f} decode_tps={dtps:.1f} batch_width=1 plan=serial-c1 "
             f"draft_accepted={accepted} draft_proposed={gt} draft_rounds={gt // 3} "
             f"acceptance_pct={100.0 * accepted / gt:.1f}")
        if errors and i % 3 == 0:
            emit(f'[ERROR] [http] request=r{i + 100} event=failed method=POST '
                 f'path=/v1/chat/completions status=503 outcome=error duration_ms=88.1 '
                 f'error="upstream worker unavailable"')
        time.sleep(interval)

    emit("[INFO] [http] event=shutdown reason=requests_exhausted")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
