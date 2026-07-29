"""
audit_evaluator.py -- 2026-07-28 audit of the board-evaluator calibration.

Investigates why train_evaluator.py produced evaluator_weights.json with
hp=0.0, army=0.0, repair=0.0 (i.e. "castle HP does not affect win probability"),
which is not a believable statement about this game.

Runs four fits on the same data and compares them:

  A) CURRENT      -- exactly what train_evaluator.py does today:
                     no-intercept logistic on raw [0,1] features, then clamp
                     negative coefficients to zero, then normalise.
  B) UNCLAMPED    -- same fit, but showing the coefficients BEFORE the clamp.
  C) CENTERED     -- no-intercept logistic on (X - 0.5). This is the correctly
                     specified version of the same model.
  D) DEPLOYED     -- fits the functional form GameState.EvaluateBoard() actually
                     uses at runtime: p = (w . x) / sum(w), w >= 0. Minimises
                     log-loss by projected gradient. These are the weights that
                     mean what GameState.cs thinks they mean.

Pure numpy -- no sklearn/scipy needed.

Usage (from CastleDefense.PythonAI/):
    python audit_evaluator.py
    python audit_evaluator.py --subsample 30   # thin autocorrelated frames
"""

import sys
import json
import numpy as np
import pandas as pd

FEATURES = ["hp_score", "income_score", "money_score",
            "army_score", "gadget_score", "repair_score"]
LABELS = ["Castle HP", "Income", "Money", "Army Threat", "Gadget Ready", "Repair Acc"]

# What GameState.cs actually ships right now (lines 190-195).
INCODE_W = np.array([0.2853, 0.7000, 0.2406, 0.0500, 0.1500, 0.0000])


def sigmoid(z):
    return 1.0 / (1.0 + np.exp(-np.clip(z, -30, 30)))


def load(subsample=0):
    frames = []
    for path in ("eval_trajectories.csv", "calib_data.csv"):
        try:
            df = pd.read_csv(path)
        except FileNotFoundError:
            print(f"  (skipping {path} -- not found)")
            continue
        n_raw = len(df)
        if subsample and "tick" in df.columns:
            df = df[df["tick"] % subsample == 0]
            if "game_id" in df.columns:
                last = df.groupby("game_id")["tick"].transform("max")
                df = df[df["tick"] != last]
        has_tick = "tick" in df.columns
        print(f"  {path:<24} {n_raw:>9,} rows -> {len(df):>9,} used"
              f"   (tick column: {'yes' if has_tick else 'NO -- cannot thin'})")
        frames.append((df[FEATURES].values.astype(np.float64),
                       (df["winner"] == 1).values.astype(np.float64)))
    if not frames:
        sys.exit("No data found. Run from CastleDefense.PythonAI/.")
    X = np.vstack([f[0] for f in frames])
    y = np.concatenate([f[1] for f in frames])
    return X, y


def mirror(X, y):
    """P2's view of every component is (1 - component); the label flips too."""
    return np.vstack([X, 1.0 - X]), np.concatenate([y, 1.0 - y])


def fit_logistic(X, y, lr=2.0, iters=4000, l2=1e-6):
    """Plain no-intercept logistic regression, signed coefficients allowed."""
    w = np.zeros(X.shape[1])
    n = len(y)
    for _ in range(iters):
        g = X.T @ (sigmoid(X @ w) - y) / n + l2 * w
        w -= lr * g
    return w


def fit_deployed(X, y, iters=6000, lr=3.0):
    """
    Fits p = (w . x) / sum(w) with w >= 0 -- the form EvaluateBoard() uses.
    Projected gradient on log-loss. Scale-invariant, so we renormalise each step.
    """
    w = np.ones(X.shape[1]) / X.shape[1]
    n = len(y)
    eps = 1e-9
    for _ in range(iters):
        s = w.sum()
        p = np.clip(X @ w / s, eps, 1 - eps)
        # d(logloss)/dp  *  dp/dw   with the quotient rule on (w.x)/sum(w)
        dp = (p - y) / (p * (1 - p)) / n
        grad = X.T @ dp / s - (dp @ p).sum() / s * np.ones(X.shape[1])
        w -= lr * grad
        w = np.maximum(w, 0.0)
        if w.sum() <= 0:
            w = np.ones(X.shape[1]) / X.shape[1]
        w /= w.sum()
    return w


def logloss(p, y):
    p = np.clip(p, 1e-9, 1 - 1e-9)
    return float(-(y * np.log(p) + (1 - y) * np.log(1 - p)).mean())


def report(name, w, p, y, note=""):
    acc = float(((p >= 0.5) == y).mean())
    print(f"\n  {name}   acc {acc:6.2%}   logloss {logloss(p, y):.4f}   {note}")
    print("    " + "  ".join(f"{l:>12}" for l in LABELS))
    print("    " + "  ".join(f"{v:>12.4f}" for v in w))


def regularisation_sweep(X, y):
    """
    The decisive test. Same data, same model spec -- vary only the L2 strength,
    then apply train_evaluator.py's >=0 clamp and see which components survive.
    If the surviving set moves, the fit is not identified and the zeros in
    evaluator_weights.json carry no information about the game.
    """
    print("\n" + "=" * 78)
    print("\nREGULARISATION SWEEP -- which components survive the >=0 clamp?\n")
    print(f"  {'l2':>8} {'sum(w)':>8}   " + " ".join(f"{a:>11}" for a in LABELS))
    for l2 in (0.0, 1e-4, 1e-3, 1e-2):
        w = fit_logistic(X, y, iters=2500, l2=l2)
        c = np.maximum(w, 0.0)
        c = c / c.sum() if c.sum() > 0 else c
        print(f"  {l2:>8} {w.sum():>+8.3f}   " + " ".join(f"{v:>11.3f}" for v in c))
    print("\n  sum(w) drifting away from 0 as l2 grows is the tell: the penalty,")
    print("  not the data, is picking which components get zeroed.")


def main():
    sub = 0
    if "--subsample" in sys.argv:
        sub = int(sys.argv[sys.argv.index("--subsample") + 1])

    print("Loading:")
    X, y = load(sub)
    X, y = mirror(X, y)
    print(f"\n  after mirroring: {len(X):,} samples, {y.mean():.1%} positive\n")
    print("=" * 78)

    # --- A/B: what train_evaluator.py does now -------------------------------
    w_raw = fit_logistic(X, y)
    report("B) UNCLAMPED (no-intercept logistic on raw X)", w_raw,
           sigmoid(X @ w_raw), y, f"sum(w) = {w_raw.sum():+.4f}")
    print("       ^ a no-intercept logistic on features centered at 0.5 can only")
    print("         call an even game 50/50 if sum(w) == 0, so the fit is forced")
    print("         into a zero-sum split: some coefficients MUST come out negative.")

    w_clamped = np.maximum(w_raw, 0.0)
    w_clamped = w_clamped / w_clamped.sum()
    report("A) CURRENT (above, clamped to >=0 then normalised)", w_clamped,
           X @ w_clamped, y, "<-- this is evaluator_weights.json")
    print("       ^ the clamp deletes the negative half of a zero-sum solution.")
    print("         Zeros here mean 'negative coefficient', NOT 'no effect'.")

    # --- C: correctly specified logistic ------------------------------------
    Xc = X - 0.5
    w_cent = fit_logistic(Xc, y)
    report("C) CENTERED (no-intercept logistic on X - 0.5) -- correctly specified",
           w_cent, sigmoid(Xc @ w_cent), y)

    # --- D: the form the engine actually evaluates ---------------------------
    w_dep = fit_deployed(X, y)
    report("D) DEPLOYED FORM  p = (w.x)/sum(w),  w >= 0  -- matches EvaluateBoard()",
           w_dep, X @ w_dep, y, "<-- use these in GameState.cs")

    # --- baseline: what ships today -----------------------------------------
    w_in = INCODE_W / INCODE_W.sum()
    report("   in-code GameState.cs weights today (hand-floored)", w_in,
           X @ w_in, y)

    regularisation_sweep(X, y)

    print("\n" + "=" * 78)
    print("\nC# snippet for GameState.cs (from fit D):")
    print("  " + "-" * 62)
    for feat, v in zip(FEATURES, w_dep):
        fname = feat.replace("_score", "").capitalize()
        print(f"  public static float {'EvalWeight' + fname:<22} = {v:.4f}f;")
    print("  " + "-" * 62)

    with open("evaluator_weights_audited.json", "w") as fh:
        json.dump({
            "deployed_form_weights": {f: float(v) for f, v in zip(FEATURES, w_dep)},
            "centered_logistic_weights": {f: float(v) for f, v in zip(FEATURES, w_cent)},
            "current_broken_weights": {f: float(v) for f, v in zip(FEATURES, w_clamped)},
            "note": "deployed_form_weights match GameState.EvaluateBoard()'s actual "
                    "weighted-average formula; the other two are diagnostic.",
        }, fh, indent=2)
    print("\nWrote evaluator_weights_audited.json")


if __name__ == "__main__":
    main()
