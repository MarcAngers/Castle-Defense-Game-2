"""
LOGLOSS SCREEN for the t_arma / t_death evaluator features.

THIS IS A ONE-WAY SCREEN. It can REJECT a feature but it cannot ACCEPT one, and that
asymmetry is the whole point of running it:

  - A feature with ~zero incremental logloss carries no information the deployed six lack,
    so it could only help play by accident. That is a real reject signal, and it is exactly
    how the gadget-level feature was correctly killed (-0.0052, i.e. nothing).
  - Better logloss does NOT imply better play. The 2026-08-05 refit improved held-out AUC
    from 0.691 to 0.720 -- 52% more signal above chance -- and collapsed in play from 75%
    to 34.2%, because the better fit moved MONEY's share of the weight vector from 0.154 to
    0.329. Acceptance can only come from search-test win rate at matched override rate.

METHOD, copied from gadget_level_value.py because its framing was the result:
  * incremental value measured as a DELTA on the deployed six, never in isolation
  * thinned to one frame per game per THIN_TICKS, because frames within a game are
    enormously autocorrelated and raw row counts wildly overstate the sample
  * held out BY GAME, never by row -- a row-wise split leaks the game's outcome, since
    every frame in a game shares the same label

Usage:
    python time_feature_value.py <calib.csv> [--thin 300] [--folds 5] [--seed 0]
"""

import argparse
import csv
import sys
from collections import defaultdict

import numpy as np

BASE = ["hp_score", "income_score", "money_score", "army_score", "gadget_score", "repair_score"]
NEW = ["tarma_score", "tdeath_score"]
ALL = BASE + NEW


def load(path, thin):
    """Returns (X, y, gid) arrays, thinned to one frame per `thin` ticks."""
    rows, labels, gids = [], [], []
    with open(path, newline="", encoding="utf-8-sig") as f:
        rd = csv.DictReader(f)
        missing = [c for c in ALL if c not in rd.fieldnames]
        if missing:
            sys.exit(f"ERROR: csv lacks {missing}. Re-run calib-collect from a build "
                     f"that emits the new columns.")
        for r in rd:
            if int(r["tick"]) % thin != 0:
                continue
            rows.append([float(r[c]) for c in ALL])
            labels.append(1.0 if int(r["winner"]) == 1 else 0.0)
            gids.append(int(r["game_id"]))
    return np.asarray(rows), np.asarray(labels), np.asarray(gids)


def fit(X, y, iters=3000, lr=2.0, l2=1e-4):
    """
    Logistic on CENTRED features with no intercept, matching the deployed form
    z = sum(w_i * (x_i - 0.5)). Every feature is a sigmoid output equal to 0.5 in an even
    position, so centring is what makes the no-intercept form identifiable. Fitting it
    UNCENTRED is the 2026-07-28 bug: it forced sum(w) == 0, drove half the coefficients
    negative, and the script then clamped them to zero -- which is why the old
    evaluator_weights.json zeros were read as "no predictive value" when they were an
    artefact of a mis-specified fit.
    """
    Xc = X - 0.5
    w = np.zeros(Xc.shape[1])
    n = len(y)
    for _ in range(iters):
        z = np.clip(Xc @ w, -30, 30)
        p = 1.0 / (1.0 + np.exp(-z))
        g = Xc.T @ (p - y) / n + l2 * w
        w -= lr * g
    return w


def evaluate(X, y, w):
    Xc = X - 0.5
    z = np.clip(Xc @ w, -30, 30)
    p = np.clip(1.0 / (1.0 + np.exp(-z)), 1e-12, 1 - 1e-12)
    ll = float(-np.mean(y * np.log(p) + (1 - y) * np.log(1 - p)))
    acc = float(np.mean((p >= 0.5) == (y >= 0.5)))
    pos, neg = y.sum(), len(y) - y.sum()
    if pos == 0 or neg == 0:
        return ll, acc, float("nan")
    order = np.argsort(p)
    ranks = np.empty(len(p), dtype=float)
    ranks[order] = np.arange(1, len(p) + 1)
    # Average ranks within ties so the AUC is exact for a coarse score distribution.
    _, inv, counts = np.unique(p, return_inverse=True, return_counts=True)
    sums = np.bincount(inv, weights=ranks)
    ranks = (sums / counts)[inv]
    auc = float((ranks[y > 0.5].sum() - pos * (pos + 1) / 2.0) / (pos * neg))
    return ll, acc, auc


def cross_val(X, y, gid, idx, folds, seed):
    """K-fold held out BY GAME. `idx` selects the feature columns."""
    ugids = np.unique(gid)
    rng = np.random.default_rng(seed)
    rng.shuffle(ugids)
    lls, accs, aucs = [], [], []
    for f in range(folds):
        test_games = set(ugids[f::folds].tolist())
        mask = np.isin(gid, list(test_games))
        if mask.all() or (~mask).all():
            continue
        w = fit(X[~mask][:, idx], y[~mask])
        ll, acc, auc = evaluate(X[mask][:, idx], y[mask], w)
        lls.append(ll)
        accs.append(acc)
        aucs.append(auc)
    return float(np.mean(lls)), float(np.mean(accs)), float(np.mean(aucs))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("csv")
    ap.add_argument("--thin", type=int, default=300,
                    help="keep one frame per this many ticks (default 300)")
    ap.add_argument("--folds", type=int, default=5)
    ap.add_argument("--seed", type=int, default=0)
    a = ap.parse_args()

    X, y, gid = load(a.csv, a.thin)
    ngames = len(np.unique(gid))
    print(f"{ngames} games, {len(y)} frames after thinning to 1 per {a.thin} ticks "
          f"({len(y) / max(1, ngames):.1f} per game)")
    # One label per game, so the base rate is checkable.
    first = {}
    for g, lab in zip(gid.tolist(), y.tolist()):
        first.setdefault(g, lab)
    p1 = int(sum(first.values()))
    print(f"P1 wins {p1}/{ngames} = {p1 / max(1, ngames):.1%}  "
          f"(HeuristicBot self-play, so ~50% is the healthy value)\n")

    col = {c: i for i, c in enumerate(ALL)}
    no_army = [c for c in BASE if c != "army_score"]
    variants = [
        ("deployed six", BASE),
        ("+ t_arma", BASE + ["tarma_score"]),
        ("+ t_death", BASE + ["tdeath_score"]),
        ("+ both", BASE + NEW),
        ("t_death REPLACES army", no_army + ["tdeath_score"]),
        ("t_death replaces army, + t_arma", no_army + ["tdeath_score", "tarma_score"]),
    ]

    print(f"{'model':<34} {'logloss':>9} {'acc':>8} {'AUC':>8}   {'d-logloss':>10} {'d-AUC':>8}")
    base_ll = base_auc = None
    results = []
    for name, cols in variants:
        idx = [col[c] for c in cols]
        ll, acc, auc = cross_val(X, y, gid, idx, a.folds, a.seed)
        if base_ll is None:
            base_ll, base_auc = ll, auc
            print(f"{name:<34} {ll:>9.4f} {acc:>7.2%} {auc:>8.4f}   {'--':>10} {'--':>8}")
        else:
            print(f"{name:<34} {ll:>9.4f} {acc:>7.2%} {auc:>8.4f}   "
                  f"{ll - base_ll:>+10.4f} {auc - base_auc:>+8.4f}")
        results.append((name, cols, idx))

    # Fitted weights on ALL data, and the money share each variant implies. Money's share is
    # the single most predictive quantity for play strength in this project's history: the
    # deployed 0.154 scores ~74-76%, 0.229 scores 67.7%, 0.329 scores 34.2%. The region
    # BELOW 0.154 has never been sampled, so dilution is unmeasured territory in both
    # directions -- which is why this is reported rather than assumed benign.
    print("\nfitted weights on all data, and the money share each implies")
    print("(deployed money share = 2.96/19.23 = 0.154; below 0.154 is UNSAMPLED)")
    for name, cols, idx in results:
        w = fit(X[:, idx], y)
        pos = np.abs(w)
        total = pos.sum()
        share = pos[cols.index("money_score")] / total if "money_score" in cols and total > 0 else 0.0
        parts = "  ".join(f"{c.replace('_score', '')}={w[i]:+.2f}" for i, c in enumerate(cols))
        print(f"  {name:<34} money share {share:.3f}   sum|w| {total:.2f}")
        print(f"      {parts}")

    print("\nREAD THIS BEFORE ACTING ON THE TABLE")
    print("  A delta near zero is a REJECT: the feature adds no information the six lack.")
    print("  A positive delta is NOT an accept. The 2026-08-05 refit improved AUC 0.691 ->")
    print("  0.720 and collapsed in play to 34.2%. Only search-test win rate at a matched")
    print("  override rate can accept a feature here.")


if __name__ == "__main__":
    main()
