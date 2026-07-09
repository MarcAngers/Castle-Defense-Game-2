"""
Trains the board evaluator weights using logistic regression on game outcomes.

The six sigmoid component scores (hp, income, money, army, gadget, repair) computed
by GameState.GetEvalComponents() are used as features.  The target label is whether P1
won (1) or P2 won (0).  Perspective mirroring doubles the dataset and guarantees
perfect 50/50 class balance without discarding any data.

Usage (from CastleDefense.PythonAI/ with ai_env active):
    # One-shot: export from human replays, collect 2000 self-play games, train once
    python train_evaluator.py --n-calib 2000

    # Continuous loop: collect and retrain in batches until stopped with Ctrl+C
    python train_evaluator.py --loop --n-calib 1000

    # Skip C# export / use existing CSV files
    python train_evaluator.py --no-export --no-calib

    # Point at a specific replay directory
    python train_evaluator.py --replay-dir <path>

Output:
  - Prints current vs learned weights side by side
  - Prints a ready-to-paste C# snippet for EvalWeight* fields in GameState.cs
  - Saves eval_calibration.png  (calibration curve + weight bar chart)
  - Saves evaluator_weights.json (for reference / future loading)
"""

import os
import sys
import json
import time
import subprocess
import numpy as np
import pandas as pd
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from pathlib import Path

try:
    from sklearn.linear_model import LogisticRegression
    HAS_SKLEARN = True
except ImportError:
    HAS_SKLEARN = False

# ─── Paths ────────────────────────────────────────────────────────────────────

_SCRIPT_DIR     = Path(__file__).parent.resolve()
_NET10_DIR      = (_SCRIPT_DIR / ".." / "CastleDefense.Simulation" / "bin" / "Release" / "net10.0").resolve()
_WEBAPP_DEBUG   = (_SCRIPT_DIR / ".." / "CastleDefenseGame2" / "bin" / "Debug"   / "net10.0").resolve()
_WEBAPP_RELEASE = (_SCRIPT_DIR / ".." / "CastleDefenseGame2" / "bin" / "Release" / "net10.0").resolve()

ARENA_EXE    = str(_NET10_DIR / "CastleDefense.Simulation.exe")
EVAL_CSV     = str(_SCRIPT_DIR / "eval_trajectories.csv")
CALIB_CSV    = str(_SCRIPT_DIR / "calib_data.csv")
WEIGHTS_JSON = str(_SCRIPT_DIR / "evaluator_weights.json")
CALIB_PNG    = str(_SCRIPT_DIR / "eval_calibration.png")

FEATURES = ["hp_score", "income_score", "money_score", "army_score", "gadget_score", "repair_score"]
CURRENT_W = [0.35, 0.15, 0.05, 0.30, 0.10, 0.05]

FEATURE_LABELS = {
    "hp_score":     "Castle HP",
    "income_score": "Income",
    "money_score":  "Money",
    "army_score":   "Army Threat",
    "gadget_score": "Gadget Readiness",
    "repair_score": "Repair Access",
}


# ─── Data helpers ─────────────────────────────────────────────────────────────

def find_replay_dir():
    for c in [
        _WEBAPP_DEBUG   / "recordings" / "multiplayer",
        _WEBAPP_RELEASE / "recordings" / "multiplayer",
        _WEBAPP_DEBUG   / "recordings",
        _WEBAPP_RELEASE / "recordings",
    ]:
        if c.exists() and any(c.glob("*.replay")):
            return str(c)
    return None


def export_eval_data(replay_dir):
    if not os.path.exists(ARENA_EXE):
        print(f"[Train] ERROR: {ARENA_EXE} not found — build CastleDefense.Simulation -c Release")
        sys.exit(1)
    print(f"[Train] Exporting eval data from {replay_dir} ...")
    r = subprocess.run([ARENA_EXE, "--export-eval", replay_dir, EVAL_CSV], cwd=str(_NET10_DIR))
    if r.returncode != 0:
        print("[Train] C# eval exporter failed.")
        sys.exit(1)


def collect_calib_data(n_games, onnx_path):
    if not os.path.exists(ARENA_EXE):
        print(f"[Train] ERROR: {ARENA_EXE} not found")
        sys.exit(1)
    print(f"[Train] Collecting {n_games} calibration games ...")
    r = subprocess.run(
        [ARENA_EXE, "--collect-calibration", str(n_games), onnx_path, CALIB_CSV],
        cwd=str(_NET10_DIR),
    )
    if r.returncode != 0:
        print("[Train] C# calibration runner failed.")
        sys.exit(1)


def load_features_labels(csv_path, subsample_ticks=30):
    """Load (X, y) from either the full eval CSV or the lightweight calib CSV."""
    df = pd.read_csv(csv_path)
    missing = [c for c in FEATURES + ["winner"] if c not in df.columns]
    if missing:
        print(f"[Train] ERROR: {csv_path} missing columns: {missing}")
        print("  Re-export with the latest binary.")
        sys.exit(1)

    # Sub-sample by tick if the column exists (reduces autocorrelation within games)
    if "tick" in df.columns:
        df = df[df["tick"] % subsample_ticks == 0].copy()
        # Drop last frame per game (trivially extreme eval)
        if "game_id" in df.columns:
            last_ticks = df.groupby("game_id")["tick"].transform("max")
            df = df[df["tick"] != last_ticks]

    X = df[FEATURES].values.astype(np.float64)
    y = (df["winner"] == 1).values.astype(np.float64)
    return X, y


def mirror_observations(X, y):
    """
    Add a flipped copy of every sample with the label inverted.
    From P2's perspective each component is (1 - component) and the winner label flips.
    This guarantees perfect 50/50 class balance regardless of the actual win rates in
    the dataset, and doubles the effective training set size.
    """
    X_flip = 1.0 - X
    y_flip = 1.0 - y
    return np.vstack([X, X_flip]), np.concatenate([y, y_flip])


# ─── Logistic regression (numpy fallback) ────────────────────────────────────

def sigmoid(x):
    return 1.0 / (1.0 + np.exp(-np.clip(x, -30, 30)))


def fit_logistic_numpy(X, y, lr=0.05, n_iter=3000, l2=1e-3):
    w = np.ones(len(FEATURES)) / len(FEATURES)
    for _ in range(n_iter):
        pred = sigmoid(X @ w)
        grad = X.T @ (pred - y) / len(y) + l2 * w
        w -= lr * grad
        w = np.maximum(w, 0)
    return w


# ─── Core training ────────────────────────────────────────────────────────────

def train(eval_csv=None, calib_csv=None):
    """
    Loads available data, applies perspective mirroring, fits logistic regression.
    Returns (X_raw, y_raw, probs_learned, learned_weights).
    """
    frames = []

    if eval_csv and os.path.exists(eval_csv):
        X_e, y_e = load_features_labels(eval_csv)
        frames.append((X_e, y_e, "human replays"))

    if calib_csv and os.path.exists(calib_csv):
        X_c, y_c = load_features_labels(calib_csv)
        frames.append((X_c, y_c, "self-play calib"))

    if not frames:
        print("[Train] No data loaded — nothing to train on.")
        sys.exit(1)

    X_all = np.vstack([f[0] for f in frames])
    y_all = np.concatenate([f[1] for f in frames])

    print(f"\n[Train] Raw samples: {len(X_all)}")
    for X_f, y_f, label in frames:
        p1_rate = y_f.mean()
        print(f"  {label:<22}: {len(X_f):>6} samples  P1 win rate: {p1_rate:.0%}")

    # Mirror to guarantee perfect 50/50 balance
    X_all, y_all = mirror_observations(X_all, y_all)
    print(f"[Train] After mirroring: {len(X_all)} samples, {y_all.mean():.0%} P1 (perfectly balanced)")

    if HAS_SKLEARN:
        clf = LogisticRegression(fit_intercept=False, C=5.0, max_iter=2000, solver='lbfgs')
        clf.fit(X_all, y_all)
        raw_w = clf.coef_[0]
        probs = clf.predict_proba(X_all)[:, 1]
    else:
        print("[Train] sklearn not found — using numpy gradient solver")
        raw_w = fit_logistic_numpy(X_all, y_all)
        probs = sigmoid(X_all @ raw_w)

    raw_w = np.maximum(raw_w, 0.0)
    learned_w = raw_w / raw_w.sum() if raw_w.sum() > 0 else np.ones(len(FEATURES)) / len(FEATURES)

    return X_all, y_all, probs, learned_w


def print_results(learned_w, X, y, probs):
    acc = ((probs >= 0.5) == y).mean()
    print(f"\n[Train] Training accuracy: {acc:.1%}  (balanced dataset, baseline = 50%)")

    print(f"\n  {'Component':<20} {'Current':>9} {'Learned':>9}  {'Change':>9}")
    print("  " + "-" * 52)
    for feat, cw, lw in zip(FEATURES, CURRENT_W, learned_w):
        delta = lw - cw
        arrow = "  ^" if delta > 0.01 else ("  v" if delta < -0.01 else "   ")
        print(f"  {FEATURE_LABELS[feat]:<20} {cw:>9.4f} {lw:>9.4f}  {delta:+.4f}{arrow}")
    print("  " + "-" * 52)

    print("\n[Train] C# snippet (paste into GameState.cs EvalWeight* fields):")
    print("  " + "-" * 60)
    for feat, lw in zip(FEATURES, learned_w):
        fname    = feat.replace("_score", "").capitalize()
        cs_field = f"EvalWeight{fname}"
        print(f"  public static float {cs_field:<22} = {lw:.4f}f;")
    print("  " + "-" * 60)

    with open(WEIGHTS_JSON, "w") as fh:
        json.dump({f: float(lw) for f, lw in zip(FEATURES, learned_w)}, fh, indent=2)
    print(f"\n[Train] Weights saved: {WEIGHTS_JSON}")

    return learned_w


def plot_calibration(X, y, probs_learned, learned_w, output_path):
    fig, axes = plt.subplots(1, 2, figsize=(13, 5))
    fig.suptitle("Board Evaluator - Weight Calibration", fontsize=13, fontweight='bold')

    ax = axes[0]
    current_w_arr = np.array(CURRENT_W) / np.sum(CURRENT_W)
    probs_current = X @ current_w_arr

    n_bins = 10
    for probs, label, color, ls in [
        (probs_current, "Current heuristic", "#6b7280", "--"),
        (probs_learned, "Learned weights",   "#3b82f6", "-"),
    ]:
        bins    = np.linspace(0, 1, n_bins + 1)
        bin_idx = np.clip(np.digitize(probs, bins) - 1, 0, n_bins - 1)
        cx, cy  = [], []
        for b in range(n_bins):
            mask = bin_idx == b
            if mask.sum() >= 5:
                cx.append(probs[mask].mean())
                cy.append(y[mask].mean())
        ax.plot(cx, cy, marker='o', markersize=5, label=label, color=color, linestyle=ls, linewidth=2)

    ax.plot([0, 1], [0, 1], color='#9ca3af', linewidth=1, linestyle=':', label='Perfect calibration')
    ax.set_xlabel("Predicted P1 win probability")
    ax.set_ylabel("Actual P1 win rate")
    ax.set_title("Calibration Curve\n(closer to diagonal = better)")
    ax.legend(fontsize=9); ax.set_xlim(0, 1); ax.set_ylim(0, 1); ax.grid(True, alpha=0.3)

    ax = axes[1]
    x = np.arange(len(FEATURES)); w = 0.35
    b1 = ax.bar(x - w/2, CURRENT_W,  w, label='Current', color='#9ca3af', alpha=0.85)
    b2 = ax.bar(x + w/2, learned_w,  w, label='Learned', color='#3b82f6', alpha=0.85)
    ax.set_xticks(x)
    ax.set_xticklabels([FEATURE_LABELS[f] for f in FEATURES], rotation=20, ha='right', fontsize=9)
    ax.set_ylabel("Weight"); ax.set_title("Component Weights: Current vs Learned")
    ax.legend(fontsize=9); ax.grid(True, alpha=0.3, axis='y')
    for bar in list(b1) + list(b2):
        h = bar.get_height()
        ax.text(bar.get_x() + bar.get_width()/2, h + 0.005, f"{h:.3f}",
                ha='center', va='bottom', fontsize=7.5)

    plt.tight_layout()
    plt.savefig(output_path, dpi=150, bbox_inches='tight')
    print(f"[Train] Calibration plot saved: {output_path}")


# ─── Entry point ──────────────────────────────────────────────────────────────

def main():
    replay_dir  = None
    skip_export = False
    skip_calib  = False
    loop_mode   = False
    n_calib     = 2000   # default games per calibration batch

    onnx_path = str(_NET10_DIR / "current_model.onnx")

    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if   args[i] == "--replay-dir" and i + 1 < len(args): replay_dir  = args[i+1]; i += 2
        elif args[i] == "--onnx"       and i + 1 < len(args): onnx_path   = args[i+1]; i += 2
        elif args[i] == "--n-calib"    and i + 1 < len(args): n_calib     = int(args[i+1]); i += 2
        elif args[i] == "--no-export":                          skip_export = True; i += 1
        elif args[i] == "--no-calib":                           skip_calib  = True; i += 1
        elif args[i] == "--loop":                               loop_mode   = True; i += 1
        else: i += 1

    # ── One-time human replay export ─────────────────────────────────────────
    if not skip_export:
        if replay_dir is None:
            replay_dir = find_replay_dir()
            if replay_dir is None:
                print("[Train] No multiplayer recordings found — skipping human replay export.")
                print("  (Human games are optional; calibration data alone is sufficient)")
                skip_export = True
        if not skip_export:
            export_eval_data(replay_dir)

    iteration = 0
    while True:
        iteration += 1
        if loop_mode:
            print(f"\n{'='*60}")
            print(f"[Train] === Calibration loop iteration {iteration} ===")
            print(f"{'='*60}")

        # ── Collect self-play calibration data ───────────────────────────────
        if not skip_calib:
            collect_calib_data(n_calib, onnx_path)
        elif not os.path.exists(CALIB_CSV) and not os.path.exists(EVAL_CSV):
            print("[Train] ERROR: No data available. Run without --no-calib / --no-export.")
            sys.exit(1)

        # ── Train ─────────────────────────────────────────────────────────────
        eval_src  = EVAL_CSV  if os.path.exists(EVAL_CSV)  else None
        calib_src = CALIB_CSV if (not skip_calib and os.path.exists(CALIB_CSV)) else None
        X, y, probs, learned_w = train(eval_src, calib_src)
        print_results(learned_w, X, y, probs)
        plot_calibration(X, y, probs, learned_w, CALIB_PNG)

        if not loop_mode:
            break

        # After first loop iteration, only collect new data (don't re-export replays)
        skip_export = True
        # Append to existing calib CSV (C# uses append=true flag)
        print("\n[Train] Waiting 5 seconds before next batch...")
        time.sleep(5)


if __name__ == "__main__":
    main()
