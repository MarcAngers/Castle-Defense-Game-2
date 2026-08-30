"""
Scores the CURRENTLY DEPLOYED board evaluator on a holdout set, without refitting it.

The question this answers: is EvaluateBoard() well calibrated on the positions
RolloutSearchBot's rollouts actually reach (HeuristicBot-driven, high-investment),
given that its weights were fit on data containing no HeuristicBot games at all?

This script deliberately does NOT fit anything. If the deployed evaluator scores well
here, the recalibration idea is dead and nothing should change.

WEIGHTS ARE PARSED FROM GameState.cs, not copied. train_evaluator.py's hand-maintained
CURRENT_W has drifted from the shipped values before (see CLEANUP_BACKLOG.md), making
its "Current vs Learned" table compare against weights that were never deployed. Reading
the source is the only version of this that cannot rot.

Usage:
    python score_evaluator.py calib_heuristic.csv [more.csv ...] [--thin N]
"""
import re
import sys
import pathlib
import numpy as np
import pandas as pd

FEATURES = ["hp_score", "income_score", "money_score",
            "army_score", "gadget_score", "repair_score"]
NAMES = ["Hp", "Income", "Money", "Army", "Gadget", "Repair"]

GAMESTATE = pathlib.Path(__file__).resolve().parents[1] / \
    "CastleDefense.Engine" / "Models" / "GameState.cs"


def deployed_weights():
    """Parse LogitWeight* and EvalWeight* straight out of the engine source."""
    src = GAMESTATE.read_text(encoding="utf-8", errors="replace")

    def grab(prefix):
        out = []
        for n in NAMES:
            m = re.search(rf"{prefix}{n}\s*=\s*(-?[\d.]+)f", src)
            if not m:
                sys.exit(f"Could not find {prefix}{n} in {GAMESTATE}")
            out.append(float(m.group(1)))
        return np.array(out)

    return grab("LogitWeight"), grab("EvalWeight")


def logistic_eval(X, w):
    """Mirrors GameState.EvaluateBoard(): logistic over centred components."""
    z = (X - 0.5) @ w
    return 1.0 / (1.0 + np.exp(-np.clip(z, -30, 30)))


def linear_eval(X, w):
    """Mirrors GameState.EvaluateBoardLinear(): normalised weighted average."""
    total = w.sum()
    if total == 0:
        return np.full(len(X), 0.5)
    return (X @ w) / total


def metrics(p, y):
    p = np.clip(p, 1e-9, 1 - 1e-9)
    logloss = -np.mean(y * np.log(p) + (1 - y) * np.log(1 - p))
    acc = np.mean((p >= 0.5) == (y == 1))
    brier = np.mean((p - y) ** 2)
    return logloss, acc, brier


def reliability(p, y, bins=10):
    """Predicted-vs-actual per decile. This is where miscalibration shows up."""
    edges = np.linspace(0, 1, bins + 1)
    rows = []
    for lo, hi in zip(edges[:-1], edges[1:]):
        m = (p >= lo) & (p < hi if hi < 1 else p <= hi)
        if m.sum() == 0:
            continue
        rows.append((lo, hi, m.sum(), p[m].mean(), y[m].mean()))
    return rows


def main():
    argv = sys.argv[1:]
    thin = 0
    if "--thin" in argv:
        i = argv.index("--thin")
        thin = int(argv[i + 1])
        del argv[i:i + 2]          # drop the flag AND its value, or "300" reads as a path
    paths = [a for a in argv if not a.startswith("--")]
    if not paths:
        sys.exit(__doc__)

    logit_w, linear_w = deployed_weights()
    print(f"Deployed weights, parsed from {GAMESTATE.name}:")
    print("  logistic:", ", ".join(f"{n}={v:g}" for n, v in zip(NAMES, logit_w)))
    print("  linear  :", ", ".join(f"{n}={v:g}" for n, v in zip(NAMES, linear_w)))

    for path in paths:
        df = pd.read_csv(path)
        n_raw = len(df)

        # Thinning matters enormously here. Consecutive samples from one game are
        # near-duplicates AND share a label, so an unthinned file reports standard
        # errors as if a few hundred games were hundreds of thousands of independent
        # observations. Dropping each game's final samples also removes the frames
        # where the outcome is already decided and any evaluator looks good.
        if thin and "tick" in df.columns:
            df = df[df["tick"] % thin == 0]
            if "game_id" in df.columns:
                last = df.groupby("game_id")["tick"].transform("max")
                df = df[df["tick"] != last]

        X = df[FEATURES].values.astype(np.float64)
        y = (df["winner"] == 1).values.astype(np.float64)
        n_games = df["game_id"].nunique() if "game_id" in df.columns else None

        print(f"\n{'=' * 66}\n{path}")
        print(f"  {n_raw:,} rows -> {len(df):,} used"
              + (f", {n_games:,} games" if n_games else "")
              + f", base rate P(P1 wins) = {y.mean():.3f}")

        for label, p in (("logistic (EvaluateBoard, DEPLOYED)", logistic_eval(X, logit_w)),
                         ("linear   (EvaluateBoardLinear)   ", linear_eval(X, linear_w))):
            ll, acc, br = metrics(p, y)
            print(f"  {label}  log-loss {ll:.4f}   acc {acc:.3f}   brier {br:.4f}")

        # An always-0.5 predictor is one floor, but the HONEST floor is always-base-rate:
        # a predictor that knows only how often P1 wins in this dataset and nothing about
        # the position. Raw log-loss is NOT comparable across datasets with different base
        # rates, so what matters is how far below its own base-rate floor each sits. That
        # gap is the only cross-dataset comparison this script supports.
        ll0, acc0, br0 = metrics(np.full(len(y), 0.5), y)
        print(f"  {'constant 0.5 (no-signal floor)     '}  log-loss {ll0:.4f}   "
              f"acc {acc0:.3f}   brier {br0:.4f}")
        base = y.mean()
        llb, accb, brb = metrics(np.full(len(y), base), y)
        print(f"  {'base rate only (the real floor)    '}  log-loss {llb:.4f}   "
              f"acc {accb:.3f}   brier {brb:.4f}")
        ll_dep, _, _ = metrics(logistic_eval(X, logit_w), y)
        print(f"  --> signal extracted by DEPLOYED evaluator: "
              f"{llb - ll_dep:.4f} nats below its own base-rate floor")

        print("\n  Reliability of the DEPLOYED logistic:")
        print(f"    {'bin':<12}{'n':>9}{'predicted':>12}{'actual':>10}{'gap':>9}")
        for lo, hi, n, pm, ym in reliability(logistic_eval(X, logit_w), y):
            print(f"    {f'{lo:.1f}-{hi:.1f}':<12}{n:>9,}{pm:>12.3f}{ym:>10.3f}{ym - pm:>+9.3f}")


if __name__ == "__main__":
    main()
