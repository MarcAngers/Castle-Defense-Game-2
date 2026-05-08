import socket
import struct
import os
import sys
import csv
import subprocess
import numpy as np
import gymnasium as gym
from gymnasium import spaces
from collections import deque, defaultdict
from sb3_contrib import MaskablePPO
from stable_baselines3.common.vec_env import SubprocVecEnv
from stable_baselines3.common.callbacks import BaseCallback

def make_env(rank):
    def _init():
        unique_port = 5000 + rank
        env = CastleDefenseEnv(unique_port)
        return env
    return _init

HOST = '127.0.0.1'
PORT = 5000

# ─── Binary Protocol ───────────────────────────────────────────────────────────
# Reset msg  (C#→Py): [1392B state][14B mask][1B name_len][NB name]
# Step msg   (C#→Py): [1392B state][14B mask][4B reward][1B done][4B winner] = 1415B fixed
# Action msg (Py→C#): [1B action][4B dense_weight] = 5B fixed

_STATE_BYTES  = 348 * 4   # float32 × 348
_MASK_BYTES   = 14
_STEP_BYTES   = _STATE_BYTES + _MASK_BYTES + 4 + 1 + 4  # 1415


def _recv_exact(sock, n):
    buf = bytearray(n)
    view = memoryview(buf)
    pos = 0
    while pos < n:
        got = sock.recv_into(view[pos:], n - pos)
        if got == 0:
            raise ConnectionResetError("C# arena closed the connection")
        pos += got
    return bytes(buf)


def _recv_reset(sock):
    """Read the game-start message; returns (state, mask, opponent_name)."""
    header = _recv_exact(sock, _STATE_BYTES + _MASK_BYTES + 1)
    state = np.frombuffer(header[:_STATE_BYTES], dtype=np.float32).copy()
    mask  = np.frombuffer(header[_STATE_BYTES:_STATE_BYTES + _MASK_BYTES], dtype=np.int8).copy()
    name_len = header[_STATE_BYTES + _MASK_BYTES]
    name = _recv_exact(sock, name_len).decode("utf-8", errors="replace") if name_len else "Random Dummy"
    return state, mask, name


def _recv_step(sock):
    """Read a step-result message; returns (state, mask, reward, done, winner)."""
    data = _recv_exact(sock, _STEP_BYTES)
    state  = np.frombuffer(data[:_STATE_BYTES], dtype=np.float32).copy()
    mask   = np.frombuffer(data[_STATE_BYTES:_STATE_BYTES + _MASK_BYTES], dtype=np.int8).copy()
    off    = _STATE_BYTES + _MASK_BYTES
    reward = struct.unpack_from("<f", data, off)[0]
    done   = data[off + 4] != 0
    winner = struct.unpack_from("<i", data, off + 5)[0]
    return state, mask, reward, done, winner


def _send_action(sock, action, dense_weight):
    sock.sendall(struct.pack("<Bf", action, dense_weight))

# ─── Progress Tracking Callback ────────────────────────────────────────────────

class ProgressCallback(BaseCallback):
    """
    Logs win rate (overall and per-opponent) to CSV every `log_interval` steps,
    and launches a live-updating graph window on the first checkpoint.
    """
    def __init__(self, log_path="training_progress.csv", log_interval=50_000, verbose=0):
        super().__init__(verbose)
        self.log_path = os.path.abspath(log_path)
        self.opp_log_path = self.log_path.replace(".csv", "_opponents.csv")
        self.log_interval = log_interval
        self.last_log_step = 0
        self.recent_results = deque(maxlen=2000)
        self.recent_ep_rewards = deque(maxlen=2000)
        self.opponent_results = defaultdict(lambda: deque(maxlen=500))
        self.opponent_ep_rewards = defaultdict(lambda: deque(maxlen=500))
        self.total_games = 0
        self._watcher_proc = None
        self._ep_rewards = None  # lazily initialized to [0.0] * num_envs

    def _on_step(self):
        # Lazily init per-env reward accumulators (num_envs not known at __init__ time)
        if self._ep_rewards is None:
            self._ep_rewards = [0.0] * len(self.locals["rewards"])

        for i, (reward, done, info) in enumerate(zip(
                self.locals["rewards"], self.locals["dones"], self.locals["infos"])):
            self._ep_rewards[i] += float(reward)

            if done:
                ep_rew = self._ep_rewards[i]
                self._ep_rewards[i] = 0.0
                self.recent_ep_rewards.append(ep_rew)

                if "is_win" in info:
                    self.total_games += 1
                    result = int(info["is_win"])
                    self.recent_results.append(result)
                    opp = info.get("opponent_name", "Unknown")
                    self.opponent_results[opp].append(result)
                    self.opponent_ep_rewards[opp].append(ep_rew)

        if self.num_timesteps - self.last_log_step >= self.log_interval:
            self.last_log_step = self.num_timesteps
            self._log_checkpoint()

        return True

    def _log_checkpoint(self):
        if not self.recent_results:
            return

        overall_wr  = sum(self.recent_results)   / len(self.recent_results)
        mean_reward = sum(self.recent_ep_rewards) / len(self.recent_ep_rewards) if self.recent_ep_rewards else 0.0

        opp_data = {}
        for opp, wins in self.opponent_results.items():
            if not wins:
                continue
            rews = self.opponent_ep_rewards[opp]
            opp_data[opp] = (sum(wins), len(wins), sum(rews) / len(rews) if rews else 0.0)

        print(f"\n[{self.num_timesteps:>12,} steps] Overall WR: {overall_wr:.1%}  Avg Reward: {mean_reward:+.1f}  (last {len(self.recent_results)} games)")
        for opp in sorted(opp_data):
            w, n, r = opp_data[opp]
            print(f"  [{opp:<25}] WR: {w/n:.1%}  Reward: {r:+.1f}  ({n} games)")

        # Overall CSV
        write_header = not os.path.exists(self.log_path)
        with open(self.log_path, "a", newline="") as f:
            writer = csv.writer(f)
            if write_header:
                writer.writerow(["timestep", "overall_winrate", "mean_ep_reward", "sample_count", "total_games"])
            writer.writerow([self.num_timesteps, round(overall_wr, 4), round(mean_reward, 2), len(self.recent_results), self.total_games])

        # Per-opponent CSV
        write_header = not os.path.exists(self.opp_log_path)
        with open(self.opp_log_path, "a", newline="") as f:
            writer = csv.writer(f)
            if write_header:
                writer.writerow(["timestep", "opponent", "winrate", "mean_ep_reward", "sample_count"])
            for opp, (w, n, r) in sorted(opp_data.items()):
                writer.writerow([self.num_timesteps, opp, round(w / n, 4), round(r, 2), n])

        # Launch the live graph window on the very first checkpoint
        if self._watcher_proc is None:
            plot_script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plot_training.py")
            self._watcher_proc = subprocess.Popen(
                [sys.executable, plot_script, "--watch", self.log_path]
            )
            print("[Live graph] Launching live window...")

# ─── Environment ───────────────────────────────────────────────────────────────

class CastleDefenseEnv(gym.Env):
    def __init__(self, port=5000):
        super().__init__()
        self.action_space = spaces.Discrete(14)
        self.observation_space = spaces.Box(low=-np.inf, high=np.inf, shape=(348,), dtype=np.float32)

        self.current_opp_name = "Random Dummy"

        print(f"Connecting to C# Engine at {HOST}:{port}...")
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.sock.connect((HOST, port))

        self.current_step = 0
        self.total_anneal_steps = 12_500_000

    def reset(self, seed=None, options=None):
        super().reset(seed=seed)
        ai_state, mask, opp_name = _recv_reset(self.sock)
        self.current_opp_name = opp_name
        self.current_mask = mask
        return ai_state, {}

    def step(self, action):
        self.current_step += 1
        dense_weight = max(0.0, 1.0 - (self.current_step / self.total_anneal_steps))

        _send_action(self.sock, int(action), dense_weight)

        ai_state, mask, reward, done, winner = _recv_step(self.sock)
        self.current_mask = mask

        info = {}
        if done:
            info["is_win"] = winner == 1
            info["opponent_name"] = self.current_opp_name

        return ai_state, reward, done, False, info

    def action_masks(self):
        return self.current_mask

    def close(self):
        self.sock.close()

# ─── Main Training Script ───────────────────────────────────────────────────────

if __name__ == "__main__":
    training_model_name = "castle_defense_p1_v12"
    progress_log = "training_progress.csv"

    # Clear old progress logs so each run starts fresh
    for path in [progress_log, progress_log.replace(".csv", "_opponents.csv")]:
        if os.path.exists(path):
            os.remove(path)
            print(f"Cleared old log: {path}")

    num_cpu = 14
    env = SubprocVecEnv([make_env(i) for i in range(num_cpu)])

    model_file = f"{training_model_name}.zip"

    custom_hyperparams = {
        "n_steps": 8192,
        "batch_size": 1024,
        "n_epochs": 10,
        "gamma": 0.9998,
        "learning_rate": 0.0003,
        "ent_coef": 0.02,
        "policy_kwargs": dict(net_arch=[512, 512])
    }

    if os.path.exists(model_file):
        print(f"\nResuming training for {model_file} with upgraded hyperparameters...")
        model = MaskablePPO.load(training_model_name, env=env, custom_objects=custom_hyperparams, verbose=1)
    else:
        print("\nInitializing brand new Neural Network with macro-strategy settings...")
        model = MaskablePPO("MlpPolicy", env, verbose=1, **custom_hyperparams)

    callback = ProgressCallback(log_path=progress_log)

    print("\nBeginning Training... (Press Ctrl+C to stop early and save)")
    print(f"Progress logs: {progress_log}  (run plot_training.py anytime to graph)\n")

    try:
        model.learn(total_timesteps=500_000_000, reset_num_timesteps=False, callback=callback)
    except KeyboardInterrupt:
        print("\nTraining interrupted by user! Wrapping up and saving brain...")

    model.save(training_model_name)
    print("Model saved successfully!")
    env.close()
