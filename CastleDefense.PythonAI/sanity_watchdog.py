"""
One-shot post-launch sanity watchdog for the RL training campaign.

Built after the v27 run: it trained for a full 2B steps (~12.4 hours), reached
completion cleanly with zero errors/warnings, and STILL turned out to be a
regression -- P1's actions were silently uniformly random the entire time (an
arena/model-path plumbing bug), which a human watching the log would have caught in
minutes from the telltale signature (Self-Play never appearing as an opponent,
invests/game stuck at ~0.1) but which nothing was actually checking for
automatically. This script is that automatic check, so a repeat doesn't burn another
full run before anyone notices.

Two phases, run once (not a loop):
  1. FAST (~5 min in): Self-Play's share of the opponent pool and invests/game must
     look like real, policy-driven play. If either matches the known broken-run
     signature, HALT training immediately and log why -- don't wait for phase 2.
  2. SLOW (~1 hour in): log the checkpoint-vs-HeuristicBot benchmark's trend so far
     (informational only -- one hour isn't enough readings to hard-fail on, given
     this project's established noise band at n=150).

Usage (launched detached alongside training, not by hand):
    python sanity_watchdog.py
"""

import csv
import os
import subprocess
import sys
import time

SCRIPT_DIR   = os.path.dirname(os.path.abspath(__file__))
PROGRESS_CSV = os.path.join(SCRIPT_DIR, "training_progress.csv")
OPP_CSV      = os.path.join(SCRIPT_DIR, "training_progress_opponents.csv")
BENCH_CSV    = os.path.join(SCRIPT_DIR, "checkpoint_benchmark_log.csv")
PID_FILE     = os.path.join(SCRIPT_DIR, "campaign_run.pid")
LOG_FILE     = os.path.join(SCRIPT_DIR, "watchdog.log")

FAST_DELAY_SECONDS = 300           # ~5 min: enough for several checkpoints at measured throughput
SLOW_DELAY_SECONDS = 3600          # ~1 hour total
MIN_SELFPLAY_COUNT = 20            # absolute floor, immune to the rolling-cap artifact (see latest_opponent_counts); broken run showed exactly 0, ever
MIN_INVESTS_PER_GAME = 0.5         # healthy runs show ~2+; broken run was stuck at ~0.1


def log(msg):
    line = f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {msg}"
    print(line, flush=True)
    with open(LOG_FILE, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def training_pid():
    if not os.path.exists(PID_FILE):
        return None
    try:
        return int(open(PID_FILE).read().strip())
    except (ValueError, OSError):
        return None


def training_alive():
    pid = training_pid()
    if pid is None:
        return False
    try:
        out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}"],
                              capture_output=True, text=True, timeout=15)
        return str(pid) in out.stdout
    except Exception:
        return False


def halt_training(reason):
    log(f"HALTING TRAINING: {reason}")
    pid = training_pid()
    if pid is not None:
        subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True)
    subprocess.run(["taskkill", "/IM", "CastleDefense.Simulation.exe", "/F"], capture_output=True)
    log("Training and arena processes killed. Do not relaunch until the cause is understood -- "
        "this matches the exact signature of the v27 null-training-brain bug (see TRAINING_CAMPAIGN_LOG.md).")


def latest_opponent_counts():
    """
    Returns {opponent: sample_count} at the most recent timestep.

    NOTE: sample_count is a rolling deque capped at maxlen=500 (see ProgressTracker
    in train_ai_cluster.py) -- once an opponent has ever been selected 500+ times,
    every future row shows exactly 500 regardless of how much MORE it's been played
    since. This means a naive "share of the sum across all opponents at this
    timestep" comparison is only valid before ANY opponent has saturated -- once
    high-frequency opponents (Self-Play, HeuristicBot) hit the cap while low-
    frequency ones (individual old league models, spam tiers) haven't yet, the
    capped ones' computed "share" is artificially deflated (their true count could
    be arbitrarily higher than 500, but the sum treats it as exactly 500) while the
    uncapped ones' share is inflated by comparison. Learned this the hard way: the
    very first version of this check false-alarmed and killed a genuinely healthy
    v28 run at ~3.5M steps because Self-Play and HeuristicBot had BOTH already hit
    the 500 cap (confirming real, frequent selection) while every other opponent was
    still well under it -- the ratio looked wrong even though the raw evidence
    (500, the maximum trackable value) was exactly what a working self-play
    mechanism should show. Fixed: check the RAW count against an absolute floor
    instead of a share of a cap-corrupted sum. A working self-play mechanism will
    clear a small absolute floor (a few dozen selections) quickly regardless of how
    many OTHER opponents have or haven't saturated; the real bug's signature was
    ZERO occurrences ever, not "a smaller share than intended."
    """
    if not os.path.exists(OPP_CSV):
        return None
    with open(OPP_CSV, newline="") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        return None
    last_ts = rows[-1]["timestep"]
    latest = [r for r in rows if r["timestep"] == last_ts]
    return {r["opponent"]: int(r["sample_count"]) for r in latest}


def latest_invests_per_game():
    if not os.path.exists(PROGRESS_CSV):
        return None
    with open(PROGRESS_CSV, newline="") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        return None
    return float(rows[-1]["avg_invests_per_game"])


def main():
    log("Sanity watchdog started.")
    log(f"Waiting {FAST_DELAY_SECONDS}s for the fast checks (self-play share, invests/game)...")
    time.sleep(FAST_DELAY_SECONDS)

    if not training_alive():
        log("Training process is not running at the fast-check mark -- nothing to gate, exiting.")
        sys.exit(1)

    counts = latest_opponent_counts()
    invests = latest_invests_per_game()
    selfplay_count = (counts or {}).get("Self-Play", 0)

    log(f"Latest opponent sample counts: {counts}")
    log(f"Latest invests/game: {invests}")

    fail_reasons = []
    if selfplay_count < MIN_SELFPLAY_COUNT:
        fail_reasons.append(
            f"Self-Play sample count is {selfplay_count} (expected to clear {MIN_SELFPLAY_COUNT}+ quickly if "
            f"self-play is running at all) -- matches the null-training-brain bug signature (zero, ever)")
    if invests is None or invests < MIN_INVESTS_PER_GAME:
        fail_reasons.append(
            f"invests/game is {invests} (expected ~2+) -- matches the null-training-brain bug signature")

    if fail_reasons:
        halt_training("; ".join(fail_reasons))
        sys.exit(1)

    log(f"PASS (fast checks): Self-Play sample count={selfplay_count}, invests/game={invests:.2f} -- "
        f"the model is genuinely driving its own actions this run.")

    remaining = SLOW_DELAY_SECONDS - FAST_DELAY_SECONDS
    log(f"Waiting {remaining}s more for the slow check (checkpoint-vs-heuristic trend)...")
    time.sleep(remaining)

    if not training_alive():
        log("Training process is no longer running at the slow-check mark "
            "(could be a deliberate stop -- not treated as a failure here).")

    if os.path.exists(BENCH_CSV):
        with open(BENCH_CSV, newline="") as f:
            rows = list(csv.DictReader(f))
        if len(rows) >= 2:
            log(f"Checkpoint-vs-heuristic trend so far: first={rows[0]['model_winrate_approx_pct']}%  "
                f"latest={rows[-1]['model_winrate_approx_pct']}%  ({len(rows)} readings)")
        else:
            log(f"Only {len(rows)} checkpoint benchmark reading(s) so far -- too early to judge a trend.")
    else:
        log("No checkpoint_benchmark_log.csv found yet.")

    log("Sanity watchdog finished all checks. No further automated action -- "
        "continue monitoring training_progress.csv / checkpoint_benchmark_log.csv normally.")


if __name__ == "__main__":
    main()
