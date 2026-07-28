import os
import sys
import csv
import time
import socket
import struct
import subprocess
import threading
import queue
from pathlib import Path
from collections import deque, defaultdict

# Windows defaults stdout/stderr to the system codepage (cp1252) whenever it's not a
# real console -- e.g. redirected to a log file for a detached background run, which
# this campaign needs for multi-day unattended training. Several print()s below use
# non-ASCII characters (entropy schedule arrows), which then crash the whole process
# with UnicodeEncodeError the instant output isn't a live terminal. Force UTF-8
# unconditionally so file-redirected runs behave identically to interactive ones.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")

import numpy as np
import torch as th
import gymnasium as gym
from gymnasium import spaces
from sb3_contrib import MaskablePPO
from sb3_contrib.common.maskable.buffers import MaskableRolloutBuffer
from stable_baselines3.common.logger import configure

_SCRIPT_DIR = Path(__file__).parent.resolve()
NET10_DIR   = (_SCRIPT_DIR / ".." / "CastleDefense.Simulation" / "bin" / "Release" / "net10.0").resolve()
ARENA_EXE   = str(NET10_DIR / "CastleDefense.Simulation.exe")

HOST      = '127.0.0.1'
N_OBS     = 348    # observation vector size
N_STEPS   = 8192   # steps collected per arena per update (matches n_steps hyperparameter)
N_ENVS    = int(os.environ.get("TEST_N_ENVS", 14))  # number of C# arenas; override for the
                                                     # N_ENVS=10 machine-stability cap (see
                                                     # TRAINING_CAMPAIGN_LOG.md HANDOFF STATE)
STEP_SIZE = N_OBS * 4 + 14 + 1 + 4 + 4 + 1 + 1  # obs + mask + action + reward + board_eval + ep_start + winner
FINAL_SIZE = N_OBS * 4 + 14 + 1                 # obs + mask + done

# Board evaluation reward shaping — anneals linearly from START → END over the first
# ANNEAL_STEPS, then holds at END for the remainder of training.
# Tune these (or let the GA tune them via BoardShaperStart/End in reward_defaults.json).
BOARD_SHAPING_START     = 21.677585
BOARD_SHAPING_END       =   21.308428
BOARD_SHAPING_ANNEAL    = 3_000_000
BOARD_SHAPING_LOOKAHEAD = 30         # AI-steps ≈ 270 game ticks ≈ 9 s (covers ~3 income periods)

# 2026-07-27 fast-validation harness (ad-hoc copy of train_ai_cluster.py, not part of
# the real campaign): env-var overrides so this file can run short, independent
# tests without touching the real model files or train_ai_cluster.py itself.
# Unset = falls back to the real defaults.
_TEST_MODEL_NAME  = os.environ.get("TEST_MODEL_NAME")
_TEST_BASE_MODEL  = os.environ.get("TEST_BASE_MODEL")
_TEST_ONNX_NAME   = os.environ.get("TEST_ONNX_NAME")
_TEST_TOTAL_STEPS = os.environ.get("TEST_TOTAL_STEPS")

TRAINING_MODEL_NAME = _TEST_MODEL_NAME or "castle_defense_p1_v30"  # saves checkpoints here
# v28, not v27: the v27 run (2B steps, see TRAINING_CAMPAIGN_LOG.md) had every
# intended fix applied (CUDA, GA reward params, invest-explore, HeuristicBot+self-play
# league) EXCEPT one -- start_arenas() never passed the arenas their ONNX path, so
# trainingBrain was null the whole run: P1 acted uniformly randomly the entire time
# (never its own policy) and self-play (50% of the intended pool) silently fell
# through to Random Dummy every single game. Root-caused and fixed (see the
# start_arenas() fix below and the campaign log's full diagnosis) -- v27.zip is a
# confirmed REGRESSION below its own v25_bc warm-start base, not a checkpoint worth
# resuming from. Left in place as diagnosed evidence, not deleted.
TRAINING_BASE_MODEL = _TEST_BASE_MODEL or "castle_defense_p1_v25_bc"  # warm-start source if NAME.zip doesn't exist; set to None to start fresh
# Freshly regenerated 2026-07-24 from the 69 currently-available human replays (all
# singleplayer, P1-wins-only per bc_pretrain.py's convention -- no multiplayer replays
# survived the 2026-07-14 data-loss incident). No PPO .zip checkpoint for v25 itself
# survives (only its ONNX export) so this is a fresh MaskablePPO BC-trained straight
# from demonstrations, not a fine-tune of the real v25 policy. 1,270 usable examples
# after filtering -- thin, but should still beat pure-random init as a starting prior
# (69.3% action-prediction accuracy reached in BC training).
TRAINING_MODEL_ONNX = _TEST_ONNX_NAME or "current_model.onnx"

# Entropy annealing: decays linearly from start → ENT_FLOOR over the training run.
# Fresh starts need high early entropy so the invest action survives long enough to be discovered.
# Warm-starts already have the good strategy encoded; lower entropy preserves it while allowing refinement.
ENT_FRESH_START = 0.05   # starting ent_coef when no prior knowledge (new model)
ENT_WARM_START  = 0.03   # starting ent_coef when loading from a base model or resuming
ENT_FLOOR       = 0.02   # both schedules decay toward this floor


# ─── Binary receive helpers ────────────────────────────────────────────────────

def _recv_exact(sock, n):
    buf = bytearray(n)
    view = memoryview(buf)
    pos = 0
    while pos < n:
        got = sock.recv_into(view[pos:], n - pos)
        if got == 0:
            raise ConnectionResetError("C# arena closed connection")
        pos += got
    return bytes(buf)


def recv_batch(sock):
    """Receive one batch from a C# arena.
    Returns (steps, final_obs, final_mask, final_done, episodes).
      steps: list of (obs, mask, action, reward, ep_start, winner) tuples
      final_obs: np.float32 (N_OBS,)
      final_mask: np.int8 (14,)
      final_done: bool
      episodes: list of (name, winner_side) tuples
    """
    _OBS_BYTES = N_OBS * 4

    # Header
    hdr = _recv_exact(sock, 4)
    n_steps = struct.unpack_from('<i', hdr, 0)[0]

    # Step records
    raw = _recv_exact(sock, n_steps * STEP_SIZE)
    steps = []
    for i in range(n_steps):
        off = i * STEP_SIZE
        obs        = np.frombuffer(raw, dtype=np.float32, count=N_OBS, offset=off).copy()
        mask       = np.frombuffer(raw, dtype=np.int8,    count=14,    offset=off + _OBS_BYTES).copy()
        action     = raw[off + _OBS_BYTES + 14]
        reward     = struct.unpack_from('<f', raw, off + _OBS_BYTES + 15)[0]
        board_eval = struct.unpack_from('<f', raw, off + _OBS_BYTES + 19)[0]
        ep_start   = raw[off + _OBS_BYTES + 23]
        winner     = raw[off + _OBS_BYTES + 24]
        steps.append((obs, mask, action, reward, board_eval, ep_start, winner))

    # Final state
    fin = _recv_exact(sock, FINAL_SIZE)
    final_obs  = np.frombuffer(fin, dtype=np.float32, count=N_OBS, offset=0).copy()
    final_mask = np.frombuffer(fin, dtype=np.int8,    count=14,    offset=_OBS_BYTES).copy()
    final_done = bool(fin[_OBS_BYTES + 14])

    # Episode summaries
    ep_hdr = _recv_exact(sock, 2)
    n_eps  = struct.unpack_from('<H', ep_hdr, 0)[0]
    episodes = []
    for _ in range(n_eps):
        name_len   = _recv_exact(sock, 1)[0]
        name       = _recv_exact(sock, name_len).decode('utf-8', errors='replace') if name_len else 'Unknown'
        winner_side = _recv_exact(sock, 1)[0]
        episodes.append((name, winner_side))

    return steps, final_obs, final_mask, final_done, episodes


# ─── Policy health check ──────────────────────────────────────────────────────

def check_policy_health(model, n_samples=20):
    """
    Returns (logit_range, action0_rate) across n_samples random observations.

    Two distinct failure modes produce a frozen 'always-wait' model in greedy inference:

      1. Logit collapse — all logits near zero; argmax ties on action 0.
         Signature: low logit_range relative to peak.

      2. Action-0 dominance — logit[0] >> all others due to a strong gradient
         that penalised every non-wait action in early training.
         Signature: high logit_range (looks healthy!) but action0_rate ~1.0.
         Our old peak-based check missed this entirely.

    action0_rate is the fraction of random observations where greedy argmax
    picks action 0. A healthy model with real strategy diversity scores < 0.6;
    a frozen model scores > 0.9.
    """
    model.policy.set_training_mode(False)
    with th.no_grad():
        obs = th.randn(n_samples, N_OBS).to(model.device)
        features = model.policy.extract_features(obs)
        latent_pi, _ = model.policy.mlp_extractor(features)
        logits = model.policy.action_net(latent_pi)          # (n_samples, 14)
        logit_range   = (logits.max() - logits.min()).item()
        greedy_actions = logits.argmax(dim=-1)
        action0_rate   = (greedy_actions == 0).float().mean().item()
    return logit_range, action0_rate


def is_degenerate_drop(logit_range, action0_rate, peak_range,
                       update_n, warmup=50,
                       activate_threshold=2.0, drop_ratio=0.3,
                       action0_threshold=0.9):
    """
    Returns True if either failure mode is detected:
      - Logit collapse: peak developed real preferences (>= activate_threshold)
        and current range dropped to < drop_ratio of peak.
      - Action-0 dominance: after warmup period, greedy picks action 0 for
        > action0_threshold of random observations.
    """
    if update_n < warmup:
        return False
    collapse  = peak_range >= activate_threshold and logit_range < peak_range * drop_ratio
    dominated = action0_rate > action0_threshold
    return collapse or dominated


# ─── Invest action-specific probability floor ─────────────────────────────────
#
# 2026-07-27: validated cheaply (TRAINING_CAMPAIGN_LOG.md, "Validation methodology
# + cheap probes") that investing has a large, real, positive causal effect AND
# the trained critic already agrees -- the entire blocker to v30 ever learning to
# invest past its ~2.5-invest ceiling was that P(invest|legal) collapsed to
# ~1e-187, far beyond what PPO's clipped surrogate objective can ever recover from
# (recovering needs ~log(1e187)/log(1.2) ~= 2360 consecutive positive updates,
# not realistic). This wraps `action_net` (the final Linear(512,14) layer that
# produces raw action logits, shared by rollout log_prob computation, model.train(),
# check_policy_health, AND export_current_model's ONNX export -- wrapping it here
# is the single injection point that keeps all of those consistent) so the invest
# action's logit can never fall more than INVEST_FLOOR_MAX_GAP below the best
# legal action's logit, regardless of what the rest of the network learns.
#
# Applied to the RAW logit, before any legality masking -- when invest is actually
# illegal (not enough money), the environment's action mask still zeroes its
# probability downstream regardless of the floor, so this has no effect on illegal
# ticks. It only guarantees a floor when investing is a real, legal choice.
#
# INVEST_FLOOR_MAX_GAP=5.0 was chosen from the natural early-training logit
# spread this run's own history showed (logit `range` was ~10-19 across the first
# ~70M steps of the previous run, before any collapse) -- a 5.0 gap corresponds to
# roughly exp(-5)~=0.0067 (~0.7%) of the top action's relative weight in a simple
# two-way approximation, comparable to ordinary early-training uncertainty, not a
# large forced push. High enough that PPO sees genuine, frequent invest samples to
# learn from; low enough that it doesn't dominate decisions or force constant
# investing the way the old 90%-curriculum forcing did.
INVEST_ACTION_IDX     = 9
INVEST_FLOOR_MAX_GAP  = 5.0


class FloorInvestActionNet(th.nn.Module):
    """Wraps the policy's action_net; clamps the invest logit to at most
    INVEST_FLOOR_MAX_GAP below the best legal-or-not logit this forward pass."""
    def __init__(self, base_action_net, invest_idx=INVEST_ACTION_IDX, max_gap=INVEST_FLOOR_MAX_GAP):
        super().__init__()
        self.base = base_action_net
        self.invest_idx = invest_idx
        self.max_gap = max_gap

    def forward(self, x):
        logits = self.base(x)
        floor = logits.max(dim=-1, keepdim=True).values.squeeze(-1) - self.max_gap
        floored = logits.clone()
        floored[..., self.invest_idx] = th.maximum(logits[..., self.invest_idx], floor)
        return floored


def apply_invest_floor(model):
    """Idempotent -- safe to call after every load/create, including the
    NaN-rollback and degenerate-policy-rollback reload paths, all of which
    construct a fresh action_net that needs the wrap applied again."""
    if not isinstance(model.policy.action_net, FloorInvestActionNet):
        model.policy.action_net = FloorInvestActionNet(model.policy.action_net).to(model.device)
    return model


def save_model_unwrapped(model, path):
    """MaskablePPO.load() reconstructs a fresh (unwrapped) policy from the saved
    constructor args, then loads the saved state_dict onto it -- so a checkpoint
    saved with action_net still wrapped as FloorInvestActionNet fails to reload
    ("Missing key(s): action_net.weight ... Unexpected key(s): action_net.base.weight").
    Temporarily unwrap immediately before saving so the on-disk state_dict always
    has the plain action_net.{weight,bias} shape MaskablePPO.load() expects, then
    re-wrap right after so training continues under the floor uninterrupted."""
    wrapped = isinstance(model.policy.action_net, FloorInvestActionNet)
    if wrapped:
        base = model.policy.action_net.base
        model.policy.action_net = base
    try:
        model.save(path)
    finally:
        if wrapped:
            apply_invest_floor(model)


# ─── ONNX export ──────────────────────────────────────────────────────────────

def export_current_model(model, path=TRAINING_MODEL_ONNX):
    """Atomically export the policy to ONNX so C# arenas can reload it."""
    class _Net(th.nn.Module):
        def __init__(self, policy):
            super().__init__()
            self.policy = policy
        def forward(self, obs):
            features = self.policy.extract_features(obs)
            latent_pi, _ = self.policy.mlp_extractor(features)
            return self.policy.action_net(latent_pi)

    device = model.device
    model.policy.cpu()
    try:
        net = _Net(model.policy).eval()
        tmp = path + ".tmp"
        th.onnx.export(net, th.randn(1, N_OBS), tmp, opset_version=17,
                       input_names=["observation"], output_names=["action_logits"],
                       dynamo=False)
    finally:
        model.policy.to(device)
    os.replace(tmp, path)


# ─── Arena management ─────────────────────────────────────────────────────────

def start_arenas():
    # CRITICAL FIX (found 2026-07-25, after the v27 campaign completed and its final
    # model turned out WORSE than its own BC-only warm-start base): each arena is
    # spawned with cwd=NET10_DIR, but was previously launched with only the port arg,
    # so CastleDefense.Simulation/Program.cs's modelPath defaulted to the bare
    # relative filename "current_model.onnx" -- resolved against NET10_DIR, NOT
    # against _SCRIPT_DIR where export_current_model() actually writes it. The file
    # NEVER existed at the path the arena checked (confirmed: zero "[Model] Reloaded"
    # or "[Model] Reload failed" lines in the entire v27 run's log -- TryLoadTrainingBrain
    # silently no-ops on File.Exists()==false), so `trainingBrain` was null for the
    # ENTIRE 2B-step run. Two catastrophic consequences: (1) P1's own actions during
    # every game fell back to GetRandomValidAction(p1Mask) -- i.e. PURELY RANDOM, never
    # the model's own learned policy -- so PPO trained on (state, random-action) pairs
    # completely disconnected from the policy being updated; (2) self-play (P2 using
    # this same trainingBrain, 50% of the intended opponent mix) silently fell through
    # to Random Dummy every single time (confirmed: "Self-Play" appears ZERO times in
    # training_progress_opponents.csv; Random Dummy's true share was ~56% of games at
    # the first checkpoint, not the intended 3%). This alone explains the whole
    # campaign's result: win rate/reward/invest-rate were completely flat across all
    # 2B steps (no real learning signal), and the final model regressed measurably
    # below its own BC starting point (a plausible mechanism: 2B steps of policy-
    # gradient updates weighted toward uniformly-random actions slowly eroding the
    # initially-good BC-cloned weights, rather than any coherent improve-then-plateau
    # trajectory). Fixed by passing the ONNX path as an explicit, absolute second
    # argument -- eliminates the cwd-relative ambiguity entirely regardless of either
    # side's working directory.
    onnx_abs_path = str((_SCRIPT_DIR / TRAINING_MODEL_ONNX).resolve())
    procs = []
    for i in range(N_ENVS):
        port = 5000 + i
        proc = subprocess.Popen(
            [ARENA_EXE, str(port), onnx_abs_path],
            cwd=str(NET10_DIR),
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        procs.append(proc)
        print(f"  [Arena {i}] Started port {port}, PID={proc.pid}")
    return procs


def stop_arenas(procs):
    print("\nStopping arenas...")
    for p in procs:
        try:
            p.terminate()
        except Exception:
            pass
    for p in procs:
        try:
            p.wait(timeout=5)
        except Exception:
            pass
    print("Arenas stopped.")


# ─── Progress tracking ─────────────────────────────────────────────────────────

class ProgressTracker:
    def __init__(self, log_path="training_progress.csv", log_interval=50_000):
        self.log_path      = os.path.abspath(log_path)
        self.opp_log_path  = log_path.replace(".csv", "_opponents.csv")
        self.log_interval  = log_interval
        self.last_log_step = 0
        self.total_games   = 0
        self.total_steps   = 0
        self.recent_results    = deque(maxlen=2000)
        self.recent_rewards    = deque(maxlen=2000)
        self.opp_results       = defaultdict(lambda: deque(maxlen=500))
        self.opp_rewards       = defaultdict(lambda: deque(maxlen=500))
        self.recent_invests    = deque(maxlen=500)  # avg invests-per-game per update
        self._watcher_proc = None

    def record_episodes(self, episodes, batch_reward_sum, batch_n_steps, batch_invest_count=0):
        """Called after each update with the episode summaries from all arenas."""
        self.total_steps += batch_n_steps
        # Per-episode win/loss from episode summaries
        for (name, winner_side) in episodes:
            self.total_games += 1
            is_win = int(winner_side == 1)
            self.recent_results.append(is_win)
            self.opp_results[name].append(is_win)

        # Approximate episode reward and invest rate from batch totals / number of episodes
        if episodes:
            approx_ep_reward = batch_reward_sum / len(episodes)
            self.recent_rewards.append(approx_ep_reward)
            for name, _ in episodes:
                self.opp_rewards[name].append(approx_ep_reward)
            self.recent_invests.append(batch_invest_count / len(episodes))

        if self.total_steps - self.last_log_step >= self.log_interval:
            self.last_log_step = self.total_steps
            self._checkpoint()

    def _checkpoint(self):
        if not self.recent_results:
            return
        wr           = sum(self.recent_results) / len(self.recent_results)
        mean_reward  = sum(self.recent_rewards) / len(self.recent_rewards) if self.recent_rewards else 0.0
        avg_invests  = sum(self.recent_invests) / len(self.recent_invests) if self.recent_invests else 0.0

        print(f"\n[{self.total_steps:>12,} steps | {self.total_games:,} games]  "
              f"WR: {wr:.1%}  Avg Reward: {mean_reward:+.1f}  Invests/Game: {avg_invests:.2f}"
              f"  (last {len(self.recent_results)} games)")

        opp_data = {}
        for opp, wins in self.opp_results.items():
            if not wins: continue
            rews = self.opp_rewards[opp]
            opp_data[opp] = (sum(wins), len(wins), sum(rews) / len(rews) if rews else 0.0)
        for opp in sorted(opp_data):
            w, n, r = opp_data[opp]
            print(f"  [{opp:<25}] WR: {w/n:.1%}  Reward: {r:+.1f}  ({n} games)")

        write_header = not os.path.exists(self.log_path)
        with open(self.log_path, "a", newline="") as f:
            writer = csv.writer(f)
            if write_header:
                writer.writerow(["timestep", "overall_winrate", "mean_ep_reward", "avg_invests_per_game", "sample_count", "total_games"])
            writer.writerow([self.total_steps, round(wr, 4), round(mean_reward, 2), round(avg_invests, 3), len(self.recent_results), self.total_games])

        write_header = not os.path.exists(self.opp_log_path)
        with open(self.opp_log_path, "a", newline="") as f:
            writer = csv.writer(f)
            if write_header:
                writer.writerow(["timestep", "opponent", "winrate", "mean_ep_reward", "sample_count"])
            for opp, (w, n, r) in sorted(opp_data.items()):
                writer.writerow([self.total_steps, opp, round(w / n, 4), round(r, 2), n])

        if self._watcher_proc is None:
            plot_script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plot_training.py")
            if os.path.exists(plot_script):
                self._watcher_proc = subprocess.Popen(
                    [sys.executable, plot_script, "--watch", self.log_path])
                print("[Live graph] Launched.")


# ─── Arena receiver thread ─────────────────────────────────────────────────────

def arena_thread(env_id, port, batch_queues, version_cond, version_ref):
    """One thread per C# arena. Receives batches and sends version ACKs."""
    print(f"[Arena {env_id}] Connecting to port {port}...")
    sock = None
    for attempt in range(30):
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            sock.connect((HOST, port))
            break
        except ConnectionRefusedError:
            sock.close()
            if attempt == 29:
                print(f"[Arena {env_id}] Could not connect to port {port} after 30s.")
                batch_queues[env_id].put(None)  # sentinel so main loop doesn't hang
                return
            time.sleep(1)
    print(f"[Arena {env_id}] Connected.")

    last_seen_version = -1
    while True:
        try:
            batch = recv_batch(sock)
        except (ConnectionResetError, OSError):
            print(f"[Arena {env_id}] Connection closed.")
            break

        batch_queues[env_id].put(batch)

        # Wait for the main thread to finish its PPO update
        with version_cond:
            version_cond.wait_for(lambda: version_ref[0] != last_seen_version)
            last_seen_version = version_ref[0]
            ver = last_seen_version % 256

        try:
            sock.sendall(bytes([ver]))
        except OSError:
            break

    sock.close()


# ─── Rollout buffer filler ─────────────────────────────────────────────────────

def compute_nstep_shaping(board_evals, ep_starts, lookahead):
    """
    N-step board eval return: shaping[t] = eval[t+N] - eval[t]
    where N = min(lookahead, steps remaining before episode boundary).
    Returns 0 for steps with no valid future within the same episode.

    board_evals: (n_steps,) float32 — absolute eval at each observation
    ep_starts:   (n_steps,) — 1 if this step begins a new episode
    """
    n = len(board_evals)
    shaping = np.zeros(n, dtype=np.float32)
    for t in range(n):
        target = t
        for k in range(1, lookahead + 1):
            future = t + k
            if future >= n or ep_starts[future]:
                break       # buffer edge or episode boundary — stop here
            target = future
        if target > t:
            shaping[t] = board_evals[target] - board_evals[t]
    return shaping


def fill_rollout_buffer(model, all_batches):
    """
    Receives 14 arena batches, re-infers log_probs/values on GPU,
    and directly writes the SB3 rollout buffer arrays (fast vectorized path).
    Returns (last_values_tensor, final_done_array, all_episodes, total_reward).

    Step tuple layout: (obs, mask, action, reward, board_eval, ep_start, winner)
    Shaping: N-step eval return eval[t+N] - eval[t], annealed by shaping_weight.
    """
    n_envs  = len(all_batches)
    n_steps = len(all_batches[0][0])  # should be N_STEPS

    # Annealed shaping weight for this update
    progress       = min(1.0, model.num_timesteps / BOARD_SHAPING_ANNEAL)
    shaping_weight = BOARD_SHAPING_START + (BOARD_SHAPING_END - BOARD_SHAPING_START) * progress

    # Stack into (n_envs, n_steps, ...) arrays
    obs_arr     = np.empty((n_envs, n_steps, N_OBS), dtype=np.float32)
    mask_arr    = np.empty((n_envs, n_steps, 14),   dtype=np.float32)
    action_arr  = np.empty((n_envs, n_steps),        dtype=np.int64)
    reward_arr  = np.empty((n_envs, n_steps),        dtype=np.float32)
    epstart_arr = np.empty((n_envs, n_steps),        dtype=np.float32)
    final_obs_arr  = np.empty((n_envs, 348), dtype=np.float32)
    final_done_arr = np.empty((n_envs,),     dtype=bool)

    all_episodes  = []
    total_reward  = 0.0

    for e, batch_data in enumerate(all_batches):
        steps, final_obs, final_done, episodes = batch_data[0], batch_data[1], batch_data[3], batch_data[4]

        board_evals = np.array([step[4] for step in steps], dtype=np.float32)
        ep_starts   = np.array([step[5] for step in steps], dtype=np.float32)
        nstep       = compute_nstep_shaping(board_evals, ep_starts, BOARD_SHAPING_LOOKAHEAD)

        for t, step in enumerate(steps):
            obs_arr[e, t]     = step[0]
            mask_arr[e, t]    = step[1].astype(np.float32)
            action_arr[e, t]  = step[2]
            reward_arr[e, t]  = step[3] + shaping_weight * nstep[t]  # base + N-step shaping
            epstart_arr[e, t] = float(step[5])
            total_reward     += step[3]                               # log base reward only
        final_obs_arr[e]  = final_obs
        final_done_arr[e] = final_done
        all_episodes.extend(episodes)

    # Batch GPU inference for log_probs and values
    model.policy.set_training_mode(False)
    obs_flat    = obs_arr.reshape(-1, N_OBS)
    mask_flat   = mask_arr.reshape(-1, 14).astype(bool)
    action_flat = action_arr.reshape(-1)

    with th.no_grad():
        obs_t    = th.FloatTensor(obs_flat).to(model.device)
        mask_t   = th.BoolTensor(mask_flat).to(model.device)
        action_t = th.LongTensor(action_flat).to(model.device)
        values_flat, logprobs_flat, _ = model.policy.evaluate_actions(
            obs_t, action_t, action_masks=mask_t)

        final_obs_t  = th.FloatTensor(final_obs_arr).to(model.device)
        last_values  = model.policy.predict_values(final_obs_t).flatten()  # (n_envs,)

    # Reshape back to (n_envs, n_steps)
    values_arr  = values_flat.cpu().numpy().reshape(n_envs, n_steps)
    logprobs_arr = logprobs_flat.cpu().numpy().reshape(n_envs, n_steps)

    # Directly write rollout buffer arrays (vectorized, avoids 8192 individual add() calls)
    buf = model.rollout_buffer
    buf.reset()

    # SB3 buffer shapes: (n_steps, n_envs, ...) — so transpose axis 0 and 1
    buf.observations[:] = obs_arr.transpose(1, 0, 2)           # (n_steps, n_envs, 348)
    buf.actions[:, :, 0] = action_arr.T.astype(np.float32)    # (n_steps, n_envs, 1)
    buf.rewards[:]       = reward_arr.T                        # (n_steps, n_envs)
    buf.episode_starts[:] = epstart_arr.T                      # (n_steps, n_envs)
    buf.values[:]        = values_arr.T                        # (n_steps, n_envs)
    buf.log_probs[:]     = logprobs_arr.T                      # (n_steps, n_envs)
    buf.action_masks[:]  = mask_arr.transpose(1, 0, 2)        # (n_steps, n_envs, 14)
    buf.pos  = n_steps
    buf.full = True

    model.rollout_buffer.compute_returns_and_advantage(
        last_values=last_values, dones=final_done_arr)

    invest_count = int(np.sum(action_arr == 9))
    return last_values, final_done_arr, all_episodes, total_reward, invest_count


def make_rollout_buffer(model):
    return MaskableRolloutBuffer(
        N_STEPS,
        spaces.Box(low=-np.inf, high=np.inf, shape=(N_OBS,), dtype=np.float32),
        spaces.Discrete(14),
        device=model.device,
        gamma=model.gamma,
        gae_lambda=model.gae_lambda,
        n_envs=N_ENVS,
    )


# ─── Main ──────────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    # Default matches the real campaign's log filename -- override for any ad-hoc
    # test run using this harness, since the unconditional os.remove() below would
    # otherwise silently delete the live campaign's real training_progress.csv
    # (this bit us almost for real: this file's default was never meant to be run
    # concurrently with or between real campaign sessions).
    progress_log = os.environ.get("TEST_PROGRESS_LOG", "training_progress.csv")
    for path in [progress_log, progress_log.replace(".csv", "_opponents.csv")]:
        if os.path.exists(path):
            os.remove(path)
            print(f"Cleared old log: {path}")

    custom_hyperparams = {
        "n_steps":      N_STEPS,
        # Raised from 1024 (was ~0.9% of the 114,688-sample rollout per minibatch,
        # 112 minibatches/epoch) to 4096 (28 minibatches/epoch, still an even divisor
        # of N_ENVS*N_STEPS) per Marc's explicit ask to increase batch size
        # substantially — his prior runs plateauing around a ~20k-game mark is
        # consistent with noisy small-batch gradient estimates compounding with a
        # 16-way-random (now weighted, see CastleDefense.Simulation/Program.cs)
        # opponent pool. learning_rate is already reduced to 0.0001 below specifically
        # "for large-batch training" per that comment's own history — kept as-is.
        "batch_size":   4096,
        "n_epochs":     6,       # reduced from 10 — fewer passes = less policy drift per update
        "gamma":        0.9998,
        "gae_lambda":   0.98,
        "learning_rate": 0.0001, # reduced from 0.0003 — key stability fix for large-batch training
        "ent_coef":     0.02,   # decayed manually each update — see training loop
        "target_kl":    0.08,   # stop epoch early if KL divergence exceeds threshold
        "policy_kwargs": dict(net_arch=[512, 512]),
    }

    # Minimal env with the correct spaces — only used for model initialisation,
    # never called during training (rollout buffer is filled manually).
    class _InitEnv(gym.Env):
        observation_space = spaces.Box(low=-np.inf, high=np.inf, shape=(N_OBS,), dtype=np.float32)
        action_space      = spaces.Discrete(14)
        def reset(self, **_): return self.observation_space.sample(), {}
        def step(self, _):    return self.observation_space.sample(), 0.0, True, False, {}
        def action_masks(self): return np.ones(14, dtype=bool)

    from stable_baselines3.common.vec_env import DummyVecEnv
    init_env = DummyVecEnv([_InitEnv])

    model_file = TRAINING_MODEL_NAME + ".zip"
    base_file  = (TRAINING_BASE_MODEL + ".zip") if TRAINING_BASE_MODEL else None
    if os.path.exists(model_file):
        print(f"\nResuming training: {model_file}")
        model = MaskablePPO.load(TRAINING_MODEL_NAME, env=init_env,
                                  custom_objects=custom_hyperparams,
                                  verbose=1, device="cuda")
        is_fresh_start = False
    elif base_file and os.path.exists(base_file):
        print(f"\nWarm-starting from base model: {base_file}")
        model = MaskablePPO.load(TRAINING_BASE_MODEL, env=init_env,
                                  custom_objects=custom_hyperparams,
                                  verbose=1, device="cuda")
        model.num_timesteps = 0  # reset so entropy annealing and progress start fresh
        is_fresh_start = False
    else:
        print("\nInitializing new model...")
        model = MaskablePPO("MlpPolicy", init_env, verbose=1, device="cuda", **custom_hyperparams)
        is_fresh_start = True

    apply_invest_floor(model)
    print(f"[Invest floor] Active -- invest logit clamped to at most {INVEST_FLOOR_MAX_GAP} below the best logit whenever legal.")

    # ── SB3 internal setup (bypasses model.learn()) ──
    model.set_logger(configure(None, ["stdout"]))
    model._current_progress_remaining = 1.0
    # Raised from 10M for this campaign: at the measured ~25-28k steps/sec, 10M steps
    # completes in under 7 minutes -- the entropy-annealing schedule (which is driven
    # by this same total_timesteps, not just the stop condition) would finish almost
    # immediately and then the run would just HALT, defeating an unattended multi-day
    # campaign. 2B gives a realistic multi-day annealing horizon even if real
    # throughput ends up well below the measured spam/simple-opponent-heavy benchmark
    # rate (self-play/HeuristicBot games are likely longer and more contested).
    total_timesteps = int(_TEST_TOTAL_STEPS) if _TEST_TOTAL_STEPS else 2_000_000_000
    if not hasattr(model, 'num_timesteps') or model.num_timesteps is None:
        model.num_timesteps = 0
    if not hasattr(model, '_n_updates') or model._n_updates is None:
        model._n_updates = 0

    model.rollout_buffer = make_rollout_buffer(model)

    ent_label = f"fresh ({ENT_FRESH_START} → {ENT_FLOOR})" if is_fresh_start else f"warm-start ({ENT_WARM_START} → {ENT_FLOOR})"
    print(f"Entropy schedule: {ent_label}")

    # Export initial ONNX before arenas connect so they have something to load
    print("\nExporting initial model to ONNX...")
    export_current_model(model)
    print(f"Exported to {TRAINING_MODEL_ONNX}")

    print(f"\nStarting {N_ENVS} arenas...")
    arena_procs = start_arenas()
    print("Waiting for arenas to load league models...")
    time.sleep(15)
    print("Arenas ready.\n")

    # ── Start arena threads ──
    batch_queues = [queue.Queue(maxsize=2) for _ in range(N_ENVS)]
    version_cond = threading.Condition()
    version_ref  = [0]  # shared mutable version counter

    threads = []
    for i in range(N_ENVS):
        t = threading.Thread(
            target=arena_thread,
            args=(i, 5000 + i, batch_queues, version_cond, version_ref),
            daemon=True)
        t.start()
        threads.append(t)

    tracker         = ProgressTracker(log_path=progress_log)
    update_n        = 0
    peak_logit_range = 0.0  # tracks the highest logit range seen; used to detect corruption drops

    print(f"\nAll {N_ENVS} arena threads started. Waiting for first batches...")
    print("(Press Ctrl+C to stop and save)\n")

    try:
        while True:
            # Collect one batch from each arena (blocks until all 14 are ready)
            all_batches = [batch_queues[i].get() for i in range(N_ENVS)]
            if any(b is None for b in all_batches):
                failed = [i for i, b in enumerate(all_batches) if b is None]
                print(f"[ERROR] Arena thread(s) {failed} failed to connect. Stopping.")
                break

            # Re-infer + fill buffer + train
            model.policy.set_training_mode(True)
            _, _, all_episodes, total_reward, invest_count = fill_rollout_buffer(model, all_batches)

            # Guard against NaN/Inf — check all buffer arrays written by fill_rollout_buffer
            buf = model.rollout_buffer
            bad_rewards  = int(np.sum(~np.isfinite(buf.rewards)))
            bad_obs      = int(np.sum(~np.isfinite(buf.observations)))
            bad_logprobs = int(np.sum(~np.isfinite(buf.log_probs)))
            bad_values   = int(np.sum(~np.isfinite(buf.values)))

            if bad_rewards > 0 or bad_obs > 0:
                # Bad data from C# — skip the update, arenas continue
                print(f"[WARN] Update {update_n}: non-finite rewards/obs in buffer "
                      f"(rewards={bad_rewards}, obs={bad_obs}) — skipping update.")
                with version_cond:
                    version_ref[0] = update_n
                    version_cond.notify_all()
                update_n += 1
                continue

            if bad_logprobs > 0 or bad_values > 0:
                # log_probs/values are NaN iff the policy weights are already corrupted.
                # Roll back to the last periodic checkpoint and re-export.
                print(f"[WARN] Update {update_n}: NaN log_probs/values detected "
                      f"({bad_logprobs}/{bad_values}) — rolling back to last checkpoint...")
                model = MaskablePPO.load(TRAINING_MODEL_NAME, device="cuda",
                                         custom_objects=custom_hyperparams, verbose=1)
                apply_invest_floor(model)
                model.rollout_buffer = make_rollout_buffer(model)
                model.set_logger(configure(None, ["stdout"]))
                export_current_model(model)
                with version_cond:
                    version_ref[0] = update_n
                    version_cond.notify_all()
                update_n += 1
                continue

            progress = max(0.0, 1.0 - model.num_timesteps / total_timesteps)
            model._current_progress_remaining = progress
            ent_start = ENT_FRESH_START if is_fresh_start else ENT_WARM_START
            model.ent_coef = ENT_FLOOR + (ent_start - ENT_FLOOR) * progress

            try:
                model.train()
            except Exception as e:
                print(f"[CRITICAL] model.train() crashed at update {update_n}: {e}")
                emergency_path = TRAINING_MODEL_NAME + "_emergency"
                save_model_unwrapped(model, emergency_path)
                print(f"[CRITICAL] Emergency checkpoint saved to {emergency_path}.zip")
                raise

            # KL logging only — rollback guard removed.
            # target_kl already limits damage to one mini-batch per rollout.
            # The degenerate check below catches actual weight corruption.
            # A rollback guard that triggers at 1.5× target_kl causes an infinite loop:
            # the checkpoint is in the same volatile phase, so every subsequent rollout
            # produces the same spike, and training never makes forward progress.
            approx_kl = float(model.logger.name_to_value.get("train/approx_kl", 0.0))
            if approx_kl > (model.target_kl or 0.02):
                print(f"[INFO] Update {update_n}: KL={approx_kl:.4f} (target_kl early-stopped at {model.target_kl:.3f})")

            # Detect silent NaN weights (can appear after a "successful" update and crash the next one)
            nan_params = sum(1 for p in model.policy.parameters()
                             if not th.all(th.isfinite(p)).item())
            if nan_params > 0 and os.path.exists(TRAINING_MODEL_NAME + ".zip"):
                print(f"[WARN] Update {update_n}: {nan_params} NaN weight tensors after training "
                      f"— rolling back to last checkpoint...")
                model = MaskablePPO.load(TRAINING_MODEL_NAME, device="cuda",
                                         custom_objects=custom_hyperparams, verbose=1)
                apply_invest_floor(model)
                model.rollout_buffer = make_rollout_buffer(model)
                model.set_logger(configure(None, ["stdout"]))
                export_current_model(model)
                with version_cond:
                    version_ref[0] = update_n
                    version_cond.notify_all()
                update_n += 1
                continue

            # Degenerate policy check — catches the 'always-wait' corruption where
            # logits collapse toward uniform. KL and NaN checks both miss this because
            # the corrupted weights are finite and the resulting uniform rollouts have
            # low variance (consistent low KL). Uses a relative drop from peak rather
            # than an absolute threshold, since early-training and corrupted models
            # overlap in the low logit-range region.
            logit_range, action0_rate = check_policy_health(model)
            peak_logit_range = max(peak_logit_range, logit_range)
            if is_degenerate_drop(logit_range, action0_rate, peak_logit_range, update_n):
                checkpoint_exists = os.path.exists(TRAINING_MODEL_NAME + ".zip")
                print(f"[WARN] Update {update_n}: degenerate policy detected "
                      f"(logit range={logit_range:.3f}, action0 rate={action0_rate:.0%}, peak={peak_logit_range:.3f})"
                      f"{' — rolling back.' if checkpoint_exists else ' — no checkpoint to roll back to, skipping.'}")
                if checkpoint_exists:
                    model = MaskablePPO.load(TRAINING_MODEL_NAME, device="cuda",
                                             custom_objects=custom_hyperparams, verbose=1)
                    apply_invest_floor(model)
                    model.rollout_buffer = make_rollout_buffer(model)
                    model.set_logger(configure(None, ["stdout"]))
                    logit_range, action0_rate = check_policy_health(model)
                    peak_logit_range = max(peak_logit_range, logit_range)
                    export_current_model(model)
                    with version_cond:
                        version_ref[0] = update_n
                        version_cond.notify_all()
                update_n += 1
                continue

            model.num_timesteps += N_ENVS * N_STEPS
            update_n += 1

            # Export updated model and notify arenas
            export_current_model(model)
            with version_cond:
                version_ref[0] = update_n
                version_cond.notify_all()

            # Progress tracking
            tracker.record_episodes(all_episodes, total_reward, N_ENVS * N_STEPS, invest_count)

            # Tightened from every 10 updates to every 3 (2026-07-26, for Marc's
            # pause/resume workflow): at the measured ~9-10k steps/sec, 10 updates
            # was ~2 minutes of at-risk progress on a pause; 3 updates is ~35
            # seconds. model.save() already writes the full resumable PPO state
            # (weights + optimizer + num_timesteps), not just an inference export --
            # confirmed by reading the load path below, which only resets
            # num_timesteps when warm-starting from TRAINING_BASE_MODEL, not when
            # resuming TRAINING_MODEL_NAME itself.
            if update_n % 3 == 0:
                logit_range, action0_rate = check_policy_health(model)
                if not is_degenerate_drop(logit_range, action0_rate, peak_logit_range, update_n):
                    print(f"[Update {update_n}] {model.num_timesteps:,} steps | "
                          f"saving checkpoint (range={logit_range:.2f}, action0={action0_rate:.0%})...")
                    save_model_unwrapped(model, TRAINING_MODEL_NAME)
                else:
                    print(f"[Update {update_n}] Skipping checkpoint — degenerate policy "
                          f"(range={logit_range:.3f}, action0={action0_rate:.0%}), preserving last clean save.")

            if model.num_timesteps >= total_timesteps:
                print(f"\nReached {total_timesteps:,} timesteps. Training complete.")
                break

    except KeyboardInterrupt:
        print("\nStopping training...")

    # Save final state to a separate file so the last clean periodic checkpoint
    # (TRAINING_MODEL_NAME.zip, saved every 10 updates) is never overwritten by
    # a potentially-corrupted end-of-run save. Resume next run from TRAINING_MODEL_NAME.
    last_path = TRAINING_MODEL_NAME + "_last"
    print(f"Saving final model to {last_path}.zip (this may take a moment)...")
    save_model_unwrapped(model, last_path)
    print(f"Done.")
    print(f"Last clean checkpoint remains at {TRAINING_MODEL_NAME}.zip")
    stop_arenas(arena_procs)
    # Force-exit so Windows doesn't hang on arena threads blocked in socket reads.
    # All data is saved at this point; Python's normal cleanup isn't needed.
    os._exit(0)
