"""
Fits candidate weight sets for GameState.EvaluateBoard() and scores them on holdouts.

Answers one question: does refitting the six logistic weights on games the CURRENT
bots play beat the deployed weights, on data none of them were fit on?

THREE DESIGN DECISIONS THAT MATTER

1. MIRROR AUGMENTATION. EvaluateBoard is sigmoid(w.(x-0.5)) with NO intercept, which
   is only identifiable on a symmetric dataset. calib_search.csv is not symmetric --
   P1 is always the search bot and wins 76% -- so a no-intercept fit would try to
   explain that asymmetry through the feature weights and inflate them. Every
   component is Sig(k*(p1-p2)), so swapping the seats maps x -> 1-x and y -> 1-y
   EXACTLY. Adding each row's mirror forces the base rate to 0.5 and makes the
   deployed functional form correctly specified. This is exact, not an approximation.

2. SPLIT BY GAME, NEVER BY ROW. Consecutive frames of one game are near-duplicates
   sharing a label. A row-wise split leaks the label across train and holdout and
   would report a fantastic score for a useless fit.

3. HUMAN GAMES ARE HOLDOUT ONLY. Their labels mean "P(win | Marc plays on)", while
   the search rollouts continue with HeuristicBot. Training on them would make the
   leaf evaluator and the rollout policy disagree about who is playing. They are
   scored, never fitted.

Usage:
    python fit_evaluator.py [--thin 300] [--holdout 0.3] [--l2 1.0]
"""
import sys
import numpy as np
import pandas as pd
import pathlib
import re

FEATURES = ["hp_score", "income_score", "money_score",
            "army_score", "gadget_score", "repair_score"]
NAMES = ["Hp", "Income", "Money", "Army", "Gadget", "Repair"]

HERE = pathlib.Path(__file__).resolve().parent
GAMESTATE = HERE.parents[0] / "CastleDefense.Engine" / "Models" / "GameState.cs"


def deployed():
    src = GAMESTATE.read_text(encoding="utf-8", errors="replace")
    return np.array([float(re.search(rf"LogitWeight{n}\s*=\s*(-?[\d.]+)f", src).group(1))
                     for n in NAMES])


def load(path, thin, need_games=True):
    df = pd.read_csv(path)
    if "tick" in df.columns and thin:
        df = df[df["tick"] % thin == 0]
        if "game_id" in df.columns:
            last = df.groupby("game_id")["tick"].transform("max")
            df = df[df["tick"] != last]
    X = df[FEATURES].values.astype(np.float64)
    y = (df["winner"] == 1).values.astype(np.float64)
    g = df["game_id"].values if ("game_id" in df.columns and need_games) else \
        np.full(len(df), -1)
    return X, y, g


def mirror(X, y):
    """Seat swap: every component is Sig(k*(p1-p2)), so x -> 1-x and y -> 1-y."""
    return np.vstack([X, 1.0 - X]), np.concatenate([y, 1.0 - y])


def fit(X, y, l2=1.0, iters=60):
    """IRLS for a no-intercept logistic on centred features, matching EvaluateBoard."""
    Z = X - 0.5
    w = np.zeros(Z.shape[1])
    for _ in range(iters):
        p = 1.0 / (1.0 + np.exp(-np.clip(Z @ w, -30, 30)))
        W = np.clip(p * (1 - p), 1e-6, None)
        grad = Z.T @ (y - p) - l2 * w
        H = -(Z.T * W) @ Z - l2 * np.eye(Z.shape[1])
        step = np.linalg.solve(H, grad)
        w_new = w - step
        if not np.all(np.isfinite(w_new)) or np.max(np.abs(w_new - w)) < 1e-9:
            w = w_new if np.all(np.isfinite(w_new)) else w
            break
        w = w_new
    return w


def auc(p, y):
    """Rank-based AUC via the Mann-Whitney identity, with ties averaged.

    This is the metric that matters most for search. Search takes an argmax, so it
    cares about ORDERING, not absolute probabilities -- and AUC is invariant to both
    the base rate and any monotone transform. Log-loss against a base-rate floor
    penalises the deployed form unfairly on human games, because a no-intercept
    logistic cannot express "P1 is simply the stronger player" no matter the weights.
    """
    pos, neg = p[y == 1], p[y == 0]
    if len(pos) == 0 or len(neg) == 0:
        return float("nan")
    r = np.argsort(np.argsort(np.concatenate([pos, neg]), kind="mergesort")) + 1.0
    # average ranks within ties so a constant predictor scores exactly 0.5
    order = np.argsort(np.concatenate([pos, neg]), kind="mergesort")
    vals = np.concatenate([pos, neg])[order]
    i = 0
    while i < len(vals):
        j = i
        while j + 1 < len(vals) and vals[j + 1] == vals[i]:
            j += 1
        if j > i:
            r[order[i:j + 1]] = np.mean(r[order[i:j + 1]])
        i = j + 1
    return (r[:len(pos)].sum() - len(pos) * (len(pos) + 1) / 2) / (len(pos) * len(neg))


def score(X, y, w):
    p = 1.0 / (1.0 + np.exp(-np.clip((X - 0.5) @ w, -30, 30)))
    p = np.clip(p, 1e-9, 1 - 1e-9)
    ll = -np.mean(y * np.log(p) + (1 - y) * np.log(1 - p))
    base = np.clip(y.mean(), 1e-9, 1 - 1e-9)
    llb = -(base * np.log(base) + (1 - base) * np.log(1 - base))
    return ll, np.mean((p >= 0.5) == (y == 1)), llb - ll, auc(p, y)


def main():
    thin = int(sys.argv[sys.argv.index("--thin") + 1]) if "--thin" in sys.argv else 300
    frac = float(sys.argv[sys.argv.index("--holdout") + 1]) if "--holdout" in sys.argv else 0.3
    l2 = float(sys.argv[sys.argv.index("--l2") + 1]) if "--l2" in sys.argv else 1.0

    rng = np.random.default_rng(20260805)
    parts = {}
    for key, path in (("heur", "calib_heuristic.csv"), ("search", "calib_search.csv")):
        X, y, g = load(HERE / path, thin)
        games = np.unique(g)
        rng.shuffle(games)
        cut = int(len(games) * (1 - frac))
        tr = np.isin(g, games[:cut])
        parts[key] = ((X[tr], y[tr]), (X[~tr], y[~tr]), len(games))
        print(f"{path:<22} {len(X):>7,} rows  {len(games):>4} games  "
              f"-> {tr.sum():,} train / {(~tr).sum():,} holdout")

    Xo, yo, _ = load(HERE / "calib_data.csv", thin, need_games=False)
    print(f"{'calib_data.csv':<22} {len(Xo):>7,} rows  (old pool, train only)")
    Xh, yh, gh = load(HERE / "human_eval.csv", thin)
    print(f"{'human_eval.csv':<22} {len(Xh):>7,} rows  {len(np.unique(gh)):>4} games  "
          f"-> HOLDOUT ONLY\n")

    (Xht, yht), (Xhh, yhh), _ = parts["heur"]
    (Xst, yst), (Xsh, ysh), _ = parts["search"]

    mixes = {
        "A  HeuristicBot only": (Xht, yht),
        "B  Heuristic+Search": (np.vstack([Xht, Xst]), np.concatenate([yht, yst])),
        "C  B + old pool": (np.vstack([Xht, Xst, Xo]), np.concatenate([yht, yst, yo])),
    }
    # D downweights the old pool rather than dropping it: keeps tail coverage for the
    # off-manifold leaves search evaluates, without letting 1.7M off-policy rows
    # outvote the on-policy data ~40:1.
    idx = rng.choice(len(Xo), size=min(len(Xo), len(Xht) + len(Xst)), replace=False)
    mixes["D  B + old pool (downweighted)"] = (
        np.vstack([Xht, Xst, Xo[idx]]), np.concatenate([yht, yst, yo[idx]]))

    hold = {
        "on-policy holdout (heur)": (Xhh, yhh),
        "on-policy holdout (search)": (Xsh, ysh),
        "HUMAN games (never trained)": (Xh, yh),
    }

    w_dep = deployed()
    results = {"DEPLOYED (current)": w_dep}
    for name, (Xt, yt) in mixes.items():
        Xm, ym = mirror(Xt, yt)
        results[name] = fit(Xm, ym, l2=l2)

    print(f"{'weights':<32}" + "".join(f"{n:>9}" for n in NAMES) + f"{'|w|':>8}{'cos':>7}")
    for name, w in results.items():
        cos = float(w @ w_dep / (np.linalg.norm(w) * np.linalg.norm(w_dep)))
        print(f"{name:<32}" + "".join(f"{v:>9.2f}" for v in w)
              + f"{np.linalg.norm(w):>8.2f}{cos:>7.3f}")

    print("\nC#-ready (paste into GameState.cs):")
    for name, w in results.items():
        if name.startswith("DEPLOYED"):
            continue
        print(f"  // {name}")
        print("  " + "  ".join(f"{n}={v:.4f}f" for n, v in zip(NAMES, w)))

    print("\ncos = cosine similarity with the deployed weights. Near 1.00 means the refit")
    print("is a rescaling, which leaves the search argmax UNCHANGED and can only move the")
    print("margin test. Real reordering requires the direction to move.\n")

    for hname, (Xv, yv) in hold.items():
        print(f"--- {hname}  ({len(Xv):,} rows, base rate {yv.mean():.3f})")
        print(f"    {'weights':<32}{'log-loss':>10}{'acc':>8}{'signal':>9}{'AUC':>8}")
        for name, w in results.items():
            ll, acc, sig, a = score(Xv, yv, w)
            print(f"    {name:<32}{ll:>10.4f}{acc:>8.3f}{sig:>9.4f}{a:>8.3f}")
        print()
    print("signal = nats below that holdout's own base-rate floor; higher is better.")
    print("AUC    = P(a won position scores above a lost one). 0.5 = no ranking ability.")
    print("         This is the one search actually depends on, since it takes an argmax.")


if __name__ == "__main__":
    main()
