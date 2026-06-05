"""
Trains a single GA candidate model against all 14 arenas (sequential GA architecture).
Run by ga_runner.py as a subprocess.

Usage:
    python ga_train_model.py <model_idx> <ports_csv> <onnx_path> <output_json>

Example:
    python ga_train_model.py 0 5000,5001,...,5013 /abs/path/current_model.onnx fitness_0.json
"""

import os
import sys
import json
import socket
import struct
import threading
import queue
from collections import deque

import numpy as np
import torch as th
import gymnasium as gym
from gymnasium import spaces
from sb3_contrib import MaskablePPO
from sb3_contrib.common.maskable.buffers import MaskableRolloutBuffer
from stable_baselines3.common.logger import configure
from stable_baselines3.common.vec_env import DummyVecEnv

HOST                 = '127.0.0.1'
N_OBS                = 348
N_STEPS              = 8192
TOTAL_STEPS          = 2_000_000
FITNESS_WINDOW       = 300   # last N episodes used for fitness score
ONNX_EXPORT_INTERVAL = 5     # export ONNX every N updates (reduces C# reload overhead)

STEP_SIZE  = N_OBS * 4 + 14 + 1 + 4 + 1 + 1
FINAL_SIZE = N_OBS * 4 + 14 + 1

ENT_START = 0.05
ENT_FLOOR = 0.02


# ─── Networking ───────────────────────────────────────────────────────────────

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
    _OBS_BYTES = N_OBS * 4
    n_steps = struct.unpack_from('<i', _recv_exact(sock, 4), 0)[0]
    raw     = _recv_exact(sock, n_steps * STEP_SIZE)
    steps   = []
    for i in range(n_steps):
        off      = i * STEP_SIZE
        obs      = np.frombuffer(raw, dtype=np.float32, count=N_OBS, offset=off).copy()
        mask     = np.frombuffer(raw, dtype=np.int8,    count=14,    offset=off + _OBS_BYTES).copy()
        action   = raw[off + _OBS_BYTES + 14]
        reward   = struct.unpack_from('<f', raw, off + _OBS_BYTES + 15)[0]
        ep_start = raw[off + _OBS_BYTES + 19]
        winner   = raw[off + _OBS_BYTES + 20]
        steps.append((obs, mask, action, reward, ep_start, winner))
    fin        = _recv_exact(sock, FINAL_SIZE)
    final_obs  = np.frombuffer(fin, dtype=np.float32, count=N_OBS, offset=0).copy()
    final_mask = np.frombuffer(fin, dtype=np.int8,    count=14,    offset=_OBS_BYTES).copy()
    final_done = bool(fin[_OBS_BYTES + 14])
    n_eps      = struct.unpack_from('<H', _recv_exact(sock, 2), 0)[0]
    episodes   = []
    for _ in range(n_eps):
        name_len    = _recv_exact(sock, 1)[0]
        name        = _recv_exact(sock, name_len).decode('utf-8', errors='replace') if name_len else 'Unknown'
        winner_side = _recv_exact(sock, 1)[0]
        episodes.append((name, winner_side))
    return steps, final_obs, final_mask, final_done, episodes


# ─── ONNX export ──────────────────────────────────────────────────────────────

def export_model(model, path):
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


# ─── Arena thread ─────────────────────────────────────────────────────────────

def arena_thread(env_id, port, batch_queues, version_cond, version_ref):
    print(f"[Arena {env_id}] Connecting to port {port}...")
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.connect((HOST, port))
    print(f"[Arena {env_id}] Connected.")
    last_seen_version = -1
    while True:
        try:
            batch = recv_batch(sock)
        except (ConnectionResetError, OSError):
            print(f"[Arena {env_id}] Connection closed.")
            break
        batch_queues[env_id].put(batch)
        with version_cond:
            version_cond.wait_for(lambda: version_ref[0] != last_seen_version)
            last_seen_version = version_ref[0]
            ver = last_seen_version % 256
        try:
            sock.sendall(bytes([ver]))
        except OSError:
            break
    sock.close()


# ─── Rollout buffer ───────────────────────────────────────────────────────────

def make_rollout_buffer(model, n_envs):
    return MaskableRolloutBuffer(
        N_STEPS,
        spaces.Box(low=-np.inf, high=np.inf, shape=(N_OBS,), dtype=np.float32),
        spaces.Discrete(14),
        device=model.device,
        gamma=model.gamma,
        gae_lambda=model.gae_lambda,
        n_envs=n_envs,
    )


def fill_rollout_buffer(model, all_batches):
    n_envs  = len(all_batches)
    n_steps = len(all_batches[0][0])
    obs_arr     = np.empty((n_envs, n_steps, N_OBS), dtype=np.float32)
    mask_arr    = np.empty((n_envs, n_steps, 14),   dtype=np.float32)
    action_arr  = np.empty((n_envs, n_steps),        dtype=np.int64)
    reward_arr  = np.empty((n_envs, n_steps),        dtype=np.float32)
    epstart_arr = np.empty((n_envs, n_steps),        dtype=np.float32)
    final_obs_arr  = np.empty((n_envs, N_OBS), dtype=np.float32)
    final_done_arr = np.empty((n_envs,),       dtype=bool)
    all_episodes   = []

    for e, batch_data in enumerate(all_batches):
        steps, final_obs, final_done, episodes = batch_data[0], batch_data[1], batch_data[3], batch_data[4]
        for t, step in enumerate(steps):
            obs_arr[e, t]     = step[0]
            mask_arr[e, t]    = step[1].astype(np.float32)
            action_arr[e, t]  = step[2]
            reward_arr[e, t]  = step[3]
            epstart_arr[e, t] = float(step[4])
        final_obs_arr[e]  = final_obs
        final_done_arr[e] = final_done
        all_episodes.extend(episodes)

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
        last_values = model.policy.predict_values(
            th.FloatTensor(final_obs_arr).to(model.device)).flatten()

    values_arr   = values_flat.cpu().numpy().reshape(n_envs, n_steps)
    logprobs_arr = logprobs_flat.cpu().numpy().reshape(n_envs, n_steps)

    buf = model.rollout_buffer
    buf.reset()
    buf.observations[:] = obs_arr.transpose(1, 0, 2)
    buf.actions[:, :, 0] = action_arr.T.astype(np.float32)
    buf.rewards[:]        = reward_arr.T
    buf.episode_starts[:] = epstart_arr.T
    buf.values[:]         = values_arr.T
    buf.log_probs[:]      = logprobs_arr.T
    buf.action_masks[:]   = mask_arr.transpose(1, 0, 2)
    buf.pos  = n_steps
    buf.full = True
    buf.compute_returns_and_advantage(last_values=last_values, dones=final_done_arr)

    return all_episodes


# ─── Main ─────────────────────────────────────────────────────────────────────

def main():
    if len(sys.argv) < 5:
        print("Usage: ga_train_model.py <model_idx> <ports_csv> <onnx_path> <output_json>")
        sys.exit(1)

    model_idx   = int(sys.argv[1])
    ports       = [int(p) for p in sys.argv[2].split(",")]
    onnx_path   = sys.argv[3]
    output_file = sys.argv[4]
    n_envs      = len(ports)

    print(f"[GA Model {model_idx}] {n_envs} arenas | ONNX: {onnx_path}")

    custom_hyperparams = {
        "n_steps":       N_STEPS,
        "batch_size":    1024,
        "n_epochs":      6,
        "gamma":         0.9998,
        "gae_lambda":    0.98,
        "learning_rate": 0.0001,
        "ent_coef":      ENT_START,
        "target_kl":     0.08,
        "policy_kwargs": dict(net_arch=[512, 512]),
    }

    class _InitEnv(gym.Env):
        observation_space = spaces.Box(low=-np.inf, high=np.inf, shape=(N_OBS,), dtype=np.float32)
        action_space      = spaces.Discrete(14)
        def reset(self, **_): return self.observation_space.sample(), {}
        def step(self, _):    return self.observation_space.sample(), 0.0, True, False, {}
        def action_masks(self): return np.ones(14, dtype=bool)

    model = MaskablePPO("MlpPolicy", DummyVecEnv([_InitEnv]), verbose=0,
                        device="cuda", **custom_hyperparams)
    model.set_logger(configure(None, ["stdout"]))
    model._current_progress_remaining = 1.0
    model.num_timesteps = 0
    model._n_updates    = 0
    model.rollout_buffer = make_rollout_buffer(model, n_envs)

    # Export initial ONNX before arenas connect so they have something to load
    export_model(model, onnx_path)
    print(f"[GA Model {model_idx}] Initial ONNX exported.")

    batch_queues = [queue.Queue(maxsize=2) for _ in range(n_envs)]
    version_cond = threading.Condition()
    version_ref  = [0]

    for i, port in enumerate(ports):
        t = threading.Thread(target=arena_thread,
                             args=(i, port, batch_queues, version_cond, version_ref),
                             daemon=True)
        t.start()

    recent_results = deque(maxlen=FITNESS_WINDOW)
    update_n = 0

    print(f"[GA Model {model_idx}] Training for {TOTAL_STEPS:,} steps "
          f"(~{TOTAL_STEPS // (n_envs * N_STEPS)} updates)...")

    while model.num_timesteps < TOTAL_STEPS:
        all_batches = [batch_queues[i].get() for i in range(n_envs)]

        model.policy.set_training_mode(True)
        all_episodes = fill_rollout_buffer(model, all_batches)

        buf = model.rollout_buffer
        if not (np.all(np.isfinite(buf.rewards)) and np.all(np.isfinite(buf.observations))):
            print(f"[GA Model {model_idx}] Non-finite data at update {update_n}, skipping.")
            with version_cond:
                version_ref[0] = update_n
                version_cond.notify_all()
            update_n += 1
            continue

        progress = max(0.0, 1.0 - model.num_timesteps / TOTAL_STEPS)
        model._current_progress_remaining = progress
        model.ent_coef = ENT_FLOOR + (ENT_START - ENT_FLOOR) * progress

        try:
            model.train()
        except Exception as e:
            print(f"[GA Model {model_idx}] model.train() crashed: {e}")
            break

        if any(not th.all(th.isfinite(p)).item() for p in model.policy.parameters()):
            print(f"[GA Model {model_idx}] NaN weights at update {update_n}, stopping early.")
            break

        model.num_timesteps += n_envs * N_STEPS
        update_n += 1

        # Export ONNX periodically so arenas track the improving policy
        is_last_update = model.num_timesteps >= TOTAL_STEPS
        if update_n % ONNX_EXPORT_INTERVAL == 0 or is_last_update:
            export_model(model, onnx_path)

        with version_cond:
            version_ref[0] = update_n
            version_cond.notify_all()

        for name, winner_side in all_episodes:
            recent_results.append(1 if winner_side == 1 else 0)

        if update_n % 5 == 0:
            wr = sum(recent_results) / len(recent_results) if recent_results else 0.0
            print(f"[GA Model {model_idx}] Update {update_n} | "
                  f"{model.num_timesteps:,}/{TOTAL_STEPS:,} steps | WR: {wr:.1%}")

    fitness = sum(recent_results) / len(recent_results) if recent_results else 0.0
    print(f"[GA Model {model_idx}] Done. Fitness: {fitness:.4f} "
          f"({len(recent_results)} episodes in window)")

    with open(output_file, "w") as f:
        json.dump({"model_idx": model_idx, "fitness": fitness,
                   "games_in_window": len(recent_results)}, f)

    os._exit(0)


if __name__ == "__main__":
    main()
