"""
Behavioral Cloning (BC) pre-training for Castle Defense.

Pipeline:
  1. Exports (obs, mask, action) pairs from two replay directories:
       - recordings/multiplayer/  — human vs human, both perspectives, high weight
       - recordings/singleplayer/ — human vs AI, P1 perspective only, P1-wins only, low weight
  2. Trains a MaskablePPO policy with weighted cross-entropy loss.
  3. Saves the BC-trained model as <base_model>_bc.zip

Usage (from CastleDefense.PythonAI/ with ai_env active):
    python bc_pretrain.py
    python bc_pretrain.py --model castle_defense_p1_v25 --epochs 30
    python bc_pretrain.py --model none   # fresh start
    python bc_pretrain.py --replay-dir <path>  # explicit recordings root

Weights:
    MULTIPLAYER_WEIGHT  = 10  (human vs human — gold standard)
    SINGLEPLAYER_WEIGHT =  1  (human beats AI — useful but lower quality)

After BC pre-training, use the _bc model as the starting point for GA / RL.
"""

import os
import sys
import struct
import subprocess
import numpy as np
import torch as th
import torch.nn.functional as F
from pathlib import Path
from collections import Counter
from sb3_contrib import MaskablePPO
from stable_baselines3 import PPO
from stable_baselines3.common.vec_env import DummyVecEnv
import gymnasium as gym
from gymnasium import spaces

# ─── Constants ────────────────────────────────────────────────────────────────

N_OBS     = 348
N_ACTIONS = 14

# Relative importance of each data source.
# Multiplayer (human vs human) is the gold standard — both perspectives used.
# Singleplayer (human beats AI) is useful context — P1 perspective only, P1 wins only.
MULTIPLAYER_WEIGHT  = 10.0
SINGLEPLAYER_WEIGHT =  1.0

_SCRIPT_DIR = Path(__file__).parent.resolve()
_NET10_DIR  = (_SCRIPT_DIR / ".." / "CastleDefense.Simulation" / "bin" / "Release" / "net10.0").resolve()
ARENA_EXE   = str(_NET10_DIR / "CastleDefense.Simulation.exe")

BC_MP_FILE = str(_SCRIPT_DIR / "bc_mp.bin")   # multiplayer export
BC_SP_FILE = str(_SCRIPT_DIR / "bc_sp.bin")   # singleplayer export

ACTION_NAMES = {
    0: "Wait", 1: "Tier 1", 2: "Tier 2", 3: "Tier 3", 4: "Tier 4",
    5: "Tier 5", 6: "Tier 6", 7: "Tier 7", 8: "Tier 8",
    9: "Invest", 10: "Repair", 11: "Off Gadget", 12: "Def Gadget", 13: "Sig Gadget",
}


# ─── Directory discovery ───────────────────────────────────────────────────────

def find_recordings_root(explicit: str = None):
    """Return the recordings root directory (parent of multiplayer/ and singleplayer/)."""
    if explicit:
        return Path(explicit)
    candidates = [
        _SCRIPT_DIR / ".." / "CastleDefenseGame2" / "bin" / "Debug"   / "net10.0" / "recordings",
        _SCRIPT_DIR / ".." / "CastleDefenseGame2" / "bin" / "Release" / "net10.0" / "recordings",
    ]
    for c in candidates:
        if c.exists():
            return c.resolve()
    return None


# ─── Export helpers ────────────────────────────────────────────────────────────

def _call_export(replay_dir: str, output_file: str, p1_only: bool, p1_wins_only: bool) -> bool:
    """Run the C# exporter. Returns True on success."""
    if not os.path.exists(ARENA_EXE):
        print(f"[BC] ERROR: {ARENA_EXE} not found — build CastleDefense.Simulation -c Release")
        return False
    if not os.path.isdir(replay_dir) or not any(Path(replay_dir).glob("*.replay")):
        print(f"[BC] No .replay files in {replay_dir} — skipping.")
        return False

    flags = []
    if p1_only:      flags.append("--p1-only")
    if p1_wins_only: flags.append("--p1-wins-only")

    print(f"[BC] Exporting from {replay_dir}" +
          (" [P1 only]"      if p1_only      else "") +
          (" [P1 wins only]" if p1_wins_only else "") + " ...")
    r = subprocess.run([ARENA_EXE, "--export-bc", replay_dir, output_file] + flags,
                       cwd=str(_NET10_DIR))
    return r.returncode == 0


def export_all(recordings_root: Path):
    """Export multiplayer and singleplayer directories, return (mp_ok, sp_ok)."""
    mp_dir = recordings_root / "multiplayer"
    sp_dir = recordings_root / "singleplayer"

    mp_ok = _call_export(str(mp_dir), BC_MP_FILE,
                         p1_only=False, p1_wins_only=False)
    sp_ok = _call_export(str(sp_dir), BC_SP_FILE,
                         p1_only=True,  p1_wins_only=True)
    return mp_ok, sp_ok


# ─── Data loading ─────────────────────────────────────────────────────────────

def load_bc_file(path: str):
    """Load a BCTR binary file. Returns (obs, mask, actions) or None if missing."""
    if not os.path.exists(path):
        return None
    with open(path, "rb") as f:
        raw = f.read()
    if raw[:4] != b'BCTR':
        print(f"[BC] Bad magic in {path}")
        return None
    n = struct.unpack_from('<i', raw, 4)[0]
    if n == 0:
        return None
    record_size = N_OBS * 4 + N_ACTIONS + 1

    obs_arr    = np.empty((n, N_OBS),     dtype=np.float32)
    mask_arr   = np.empty((n, N_ACTIONS), dtype=bool)
    action_arr = np.empty(n,              dtype=np.int64)

    off = 8
    for i in range(n):
        obs_arr[i]    = np.frombuffer(raw, dtype=np.float32, count=N_OBS,    offset=off)
        off += N_OBS * 4
        mask_arr[i]   = np.frombuffer(raw, dtype=np.int8,    count=N_ACTIONS, offset=off).astype(bool)
        off += N_ACTIONS
        action_arr[i] = raw[off]
        off += 1

    kb = os.path.getsize(path) // 1024
    print(f"[BC]   {Path(path).name}: {n} examples ({kb} KB)")
    return obs_arr, mask_arr, action_arr


def filter_valid(obs, mask, act, label):
    """
    Drop samples where the recorded action is masked out (mask[action] == 0).

    Root cause: Training League replays use a time-machine starting state, but the
    C# exporter re-simulates from tick 0 to compute observations and masks.  The
    re-simulated economy diverges from the actual game, so expensive actions (tier
    4-8, invest, gadgets) that were valid in the real game are flagged as invalid in
    the re-simulation.  Those samples corrupt the loss (log(0) = -inf) and must be
    removed.  Multiplayer games never use the time machine, so they are unaffected.
    """
    valid = np.array([mask[i, act[i]] for i in range(len(act))], dtype=bool)
    n_bad = (~valid).sum()
    if n_bad:
        pct = 100 * n_bad / len(act)
        print(f"[BC]   {label}: dropped {n_bad} bad samples ({pct:.1f}%) where mask[action]==0")
    return obs[valid], mask[valid], act[valid]


def load_and_weight(mp_ok: bool, sp_ok: bool):
    """Load both files, filter bad samples, combine with per-sample weights."""
    sources = []

    if mp_ok:
        data = load_bc_file(BC_MP_FILE)
        if data:
            obs, mask, act = filter_valid(*data, "multiplayer")
            if len(obs): sources.append(((obs, mask, act), MULTIPLAYER_WEIGHT, "multiplayer"))

    if sp_ok:
        data = load_bc_file(BC_SP_FILE)
        if data:
            obs, mask, act = filter_valid(*data, "singleplayer")
            if len(obs): sources.append(((obs, mask, act), SINGLEPLAYER_WEIGHT, "singleplayer (P1-wins, P1-only)"))

    if not sources:
        print("[BC] ERROR: no training data found.")
        sys.exit(1)

    obs_list, mask_list, act_list, w_list = [], [], [], []
    for (obs, mask, act), weight, label in sources:
        n = len(obs)
        obs_list.append(obs);  mask_list.append(mask);  act_list.append(act)
        w_list.append(np.full(n, weight, dtype=np.float32))
        print(f"[BC]   {label}: {n} examples, weight={weight}")

    obs_all    = np.concatenate(obs_list)
    mask_all   = np.concatenate(mask_list)
    act_all    = np.concatenate(act_list)
    weights    = np.concatenate(w_list)

    # Normalise weights so they average to 1.0 (keeps loss scale stable)
    weights   /= weights.mean()

    return obs_all, mask_all, act_all, weights


# ─── Model loading ────────────────────────────────────────────────────────────

def load_or_create_model(model_name: str) -> MaskablePPO:
    if model_name and model_name.lower() != "none":
        for cls in (MaskablePPO, PPO):
            try:
                model = cls.load(model_name, device="cuda")
                print(f"[BC] Loaded {cls.__name__} from {model_name}.zip")
                return model
            except Exception:
                pass
        print(f"[BC] Could not load {model_name} — starting from scratch.")

    print("[BC] Creating fresh MaskablePPO model...")

    class _Env(gym.Env):
        observation_space = spaces.Box(-np.inf, np.inf, (N_OBS,), np.float32)
        action_space      = spaces.Discrete(N_ACTIONS)
        def reset(self, **_): return self.observation_space.sample(), {}
        def step(self, _):    return self.observation_space.sample(), 0.0, True, False, {}
        def action_masks(self): return np.ones(N_ACTIONS, dtype=bool)

    return MaskablePPO("MlpPolicy", DummyVecEnv([_Env]), verbose=0, device="cuda",
                       policy_kwargs=dict(net_arch=[512, 512]))


# ─── BC training ──────────────────────────────────────────────────────────────

def train_bc(model, obs_arr, mask_arr, action_arr, sample_weights,
             n_epochs=20, batch_size=512, lr=3e-4):

    n      = len(obs_arr)
    device = model.device
    optimizer = th.optim.Adam(model.policy.parameters(), lr=lr)

    print(f"\n[BC] Training: {n} examples | {n_epochs} epochs | batch={batch_size} | lr={lr}")
    print(f"[BC] Device: {device}\n")

    for epoch in range(n_epochs):
        perm       = np.random.permutation(n)
        epoch_loss = 0.0
        n_batches  = 0
        n_correct  = 0

        model.policy.set_training_mode(True)
        for start in range(0, n, batch_size):
            idx = perm[start:start + batch_size]
            obs_t    = th.FloatTensor(obs_arr[idx]).to(device)
            mask_t   = th.BoolTensor(mask_arr[idx]).to(device)
            action_t = th.LongTensor(action_arr[idx]).to(device)
            weight_t = th.FloatTensor(sample_weights[idx]).to(device)

            features          = model.policy.extract_features(obs_t)
            latent_pi, _      = model.policy.mlp_extractor(features)
            logits            = model.policy.action_net(latent_pi)
            logits            = logits.masked_fill(~mask_t, float('-inf'))

            # Weighted cross-entropy: multiplayer samples pull harder on the gradient
            losses = F.cross_entropy(logits, action_t, reduction='none')
            loss   = (losses * weight_t).mean()

            optimizer.zero_grad()
            loss.backward()
            optimizer.step()

            epoch_loss += loss.item()
            n_batches  += 1
            n_correct  += (logits.argmax(1) == action_t).sum().item()

        avg_loss = epoch_loss / n_batches
        accuracy = n_correct / n * 100
        print(f"  Epoch {epoch + 1:2d}/{n_epochs} | loss={avg_loss:.4f} | acc={accuracy:.1f}%")

    model.policy.set_training_mode(False)
    return model


# ─── Entry point ──────────────────────────────────────────────────────────────

def main():
    model_name   = "castle_defense_p1_v25"
    n_epochs     = 20
    batch_size   = 512
    lr           = 3e-4
    replay_root  = None
    skip_export  = False

    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if   args[i] == "--model"      and i + 1 < len(args): model_name  = args[i+1]; i += 2
        elif args[i] == "--epochs"     and i + 1 < len(args): n_epochs    = int(args[i+1]); i += 2
        elif args[i] == "--batch-size" and i + 1 < len(args): batch_size  = int(args[i+1]); i += 2
        elif args[i] == "--lr"         and i + 1 < len(args): lr          = float(args[i+1]); i += 2
        elif args[i] == "--replay-dir" and i + 1 < len(args): replay_root = args[i+1]; i += 2
        elif args[i] == "--no-export":                          skip_export = True; i += 1
        else: i += 1

    # ── Find recordings root ──────────────────────────────────────────────────
    root = find_recordings_root(replay_root)
    if root is None and not skip_export:
        print("[BC] ERROR: Could not find recordings directory.")
        print("  Pass --replay-dir <path> to the recordings root (parent of multiplayer/ and singleplayer/).")
        sys.exit(1)

    print(f"[BC] Recordings root: {root}")
    print(f"[BC] Base model: {model_name}")
    print(f"[BC] Weights — multiplayer: {MULTIPLAYER_WEIGHT}x  |  singleplayer (P1-wins): {SINGLEPLAYER_WEIGHT}x")

    # ── Export ───────────────────────────────────────────────────────────────
    mp_ok = sp_ok = False
    if not skip_export:
        mp_ok, sp_ok = export_all(root)
    else:
        mp_ok = os.path.exists(BC_MP_FILE)
        sp_ok = os.path.exists(BC_SP_FILE)
        print(f"[BC] Skipping export — using existing files (mp={mp_ok}, sp={sp_ok})")

    if not mp_ok and not sp_ok:
        print("[BC] ERROR: no data exported. Check that replays exist in multiplayer/ and/or singleplayer/ subdirectories.")
        sys.exit(1)

    # ── Load & combine ───────────────────────────────────────────────────────
    print("\n[BC] Loading training data:")
    obs_arr, mask_arr, action_arr, sample_weights = load_and_weight(mp_ok, sp_ok)
    print(f"[BC] Total: {len(obs_arr)} examples")

    # Print action distribution
    counts = Counter(action_arr.tolist())
    total  = len(action_arr)
    print("\n[BC] Action distribution:")
    for a in sorted(counts):
        print(f"  {ACTION_NAMES.get(a, f'Action {a}'):<14}: {counts[a]:5d}  ({counts[a]/total*100:.1f}%)")

    # ── Train ────────────────────────────────────────────────────────────────
    model = load_or_create_model(model_name)
    model = train_bc(model, obs_arr, mask_arr, action_arr, sample_weights,
                     n_epochs=n_epochs, batch_size=batch_size, lr=lr)

    # ── Save ─────────────────────────────────────────────────────────────────
    out_name = (model_name if model_name.lower() != "none" else "castle_defense_p1") + "_bc"
    model.save(str(_SCRIPT_DIR / out_name))
    print(f"\n[BC] Saved: {out_name}.zip")
    print(f"[BC] Use this as the starting point for GA / RL fine-tuning.")


if __name__ == "__main__":
    main()
