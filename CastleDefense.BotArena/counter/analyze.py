"""
Turns counter-matrix sweeps into the counter table CounterPicker plays, and answers the
prior question of whether counter-picking is worth doing at all.

THE QUESTION THAT COMES FIRST. A 128x128 matrix always has a per-column argmax, so a
counter table can always be produced -- and it will always look like it has structure,
because taking a max over 128 noisy estimates manufactures structure out of pure noise.
Before believing any of it we need evidence that the matrix has genuine INTERACTION: that
which bot loadout is best actually DEPENDS on the human's loadout, rather than one loadout
simply being best against everything. Those two worlds recommend very different things and
produce identical-looking tables.

The test is a variance decomposition. Fit the additive model

    logit(P[bot wins]) ~ mu + humanLoadout_effect + botLoadout_effect

by weighted alternating least squares, then compare the leftover residual variance against
the sampling noise a binomial with this many games per cell would produce on its own.
Whatever exceeds that is real interaction. Everything else is the dominance order.

Usage:
  analyze.py <sweep.csv> [more_sweeps.csv ...]   report structure
      [--pairs out.csv --top N]                  emit stage-2 refinement candidates
      [--emit counter_table.csv]                 write the table CounterPicker reads
"""
import sys
import numpy as np
import pandas as pd

TEAMS = ["Black", "Blue", "Green", "Orange", "Purple", "Red", "White", "Yellow"]
OFF = ["nuke", "firebomb", "snipe", "freeze"]
DEF = ["heal", "reinforcements", "speed", "wall"]
LOADOUTS = [(t, o, d) for t in TEAMS for o in OFF for d in DEF]
IDX = {l: i for i, l in enumerate(LOADOUTS)}
N = len(LOADOUTS)


def load(paths):
    """Sums several sweeps cell-by-cell. Refinement passes use --game-offset, so their
    games are disjoint from the coarse pass and the counts genuinely add."""
    wins = np.zeros((N, N))
    games = np.zeros((N, N))
    ticks = np.zeros((N, N))
    for path in paths:
        df = pd.read_csv(path)
        h = np.array([IDX[(t, o, d)] for t, o, d in zip(df.human_team, df.human_off, df.human_def)])
        b = np.array([IDX[(t, o, d)] for t, o, d in zip(df.bot_team, df.bot_off, df.bot_def)])
        # Score from the BOT's point of view. Timeout wins count as wins and draws as half,
        # because what matters for singleplayer is who is declared winner of the game the
        # human actually plays -- not this project's usual decisive-only convention, which
        # is about how convincing a bot's strength is rather than about who won.
        w = df.bot_decisive_wins + df.bot_timeout_wins + 0.5 * df.draws
        np.add.at(wins, (h, b), w.values)
        np.add.at(games, (h, b), df.games.values)
        np.add.at(ticks, (h, b), (df.avg_ticks * df.games).values)
    return wins, games, ticks


def logit(p, eps=1e-3):
    p = np.clip(p, eps, 1 - eps)
    return np.log(p / (1 - p))


def additive_fit(L, W, iters=300):
    """Weighted ALS for L[h,b] ~ mu + a[h] + c[b]."""
    mask = np.isfinite(L) & (W > 0)
    Lz = np.where(mask, L, 0.0)
    Wz = np.where(mask, W, 0.0)
    mu = (Lz * Wz).sum() / Wz.sum()
    a = np.zeros(N)
    c = np.zeros(N)
    for _ in range(iters):
        r = Lz - mu - c[None, :]
        a = (r * Wz).sum(1) / np.maximum(Wz.sum(1), 1e-9)
        a -= a.mean()
        r = Lz - mu - a[:, None]
        c = (r * Wz).sum(0) / np.maximum(Wz.sum(0), 1e-9)
        c -= c.mean()
        mu = ((Lz - a[:, None] - c[None, :]) * Wz).sum() / Wz.sum()
    return mu, a, c


def analyse(wins, games):
    """Returns (shrunk estimate matrix in probability space, diagnostics dict)."""
    obs = games > 0
    # Add-half smoothing so 24/24 cells do not become infinite in logit space.
    p_raw = np.where(obs, (wins + 0.5) / (games + 1.0), np.nan)
    L = logit(p_raw)
    mu, a, c = additive_fit(np.nan_to_num(L), games)
    fit = mu + a[:, None] + c[None, :]
    resid = np.where(obs, L - fit, 0.0)

    tot_var = np.var(L[obs])
    res_var = np.var(resid[obs])
    # Sampling variance of a logit under a binomial is ~1/(n p (1-p)).
    pc = np.clip(p_raw, 0.03, 0.97)
    cell_noise = np.where(obs, 1.0 / (np.maximum(games, 1) * pc * (1 - pc)), np.nan)
    noise_var = np.nanmean(cell_noise)
    excess = res_var - noise_var

    # JAMES-STEIN SHRINKAGE OF THE INTERACTION. The additive terms rest on ~128 cells of
    # data each and are reliable; a single cell's interaction rests on that cell's games
    # alone. Scaling each residual by its reliability ratio is what stops the per-column
    # argmax from chasing whichever cell got lucky -- without it the "counter table" is
    # mostly a map of sampling noise.
    sig2 = max(excess, 1e-9)
    w = sig2 / (sig2 + np.nan_to_num(cell_noise, nan=1e9))
    est_logit = fit + w * resid
    est = 1.0 / (1.0 + np.exp(-est_logit))

    diag = dict(tot_var=tot_var, res_var=res_var, noise_var=noise_var, excess=excess,
                explained=1 - res_var / tot_var, mean_w=float(np.mean(w[obs])),
                mu=mu, a=a, c=c, p_raw=p_raw, resid=resid)
    return est, diag


def report(wins, games, est, diag):
    obs = games > 0
    print(f"cells observed = {obs.sum()} / {N*N}   games = {int(games.sum()):,}   "
          f"median games/cell = {int(np.median(games[obs]))}")
    print(f"\nVARIANCE DECOMPOSITION (logit win rate)")
    print(f"  total variance                 : {diag['tot_var']:.4f}")
    print(f"  explained by additive model    : {100*diag['explained']:5.1f}%")
    print(f"  residual                       : {diag['res_var']:.4f}")
    print(f"  expected from sampling noise   : {diag['noise_var']:.4f}")
    print(f"  => REAL INTERACTION variance   : {diag['excess']:.4f} "
          f"({100*max(diag['excess'],0)/diag['tot_var']:.1f}% of total)")
    print(f"  shrinkage kept on interaction  : {diag['mean_w']:.3f} "
          f"(0 = discard as noise, 1 = trust fully)")

    p = diag["p_raw"]
    colmean = np.nanmean(np.where(obs, p, np.nan), axis=0)
    best_fixed = int(np.nanargmax(colmean))
    pick = np.argmax(np.where(obs, est, -np.inf), axis=1)

    print(f"\nBest SINGLE loadout vs all humans : {LOADOUTS[best_fixed]}  "
          f"{100*colmean[best_fixed]:.1f}%")
    print(f"Mean over all bot loadouts        : {100*np.nanmean(p):.1f}%")
    n_distinct = len(set(pick.tolist()))
    print(f"Distinct answers chosen           : {n_distinct} / {N} human loadouts")
    print(f"Counter-pick (in-sample)          : {100*np.nanmean(p[np.arange(N), pick]):.1f}%")
    print(f"  ^ optimistic: picked and scored on the same games. The honest number is the "
          f"held-out `counter-eval` run.")

    print("\nTop 12 bot loadouts by mean win rate across all human loadouts:")
    for i in np.argsort(-colmean)[:12]:
        print(f"  {str(LOADOUTS[i]):40s} {100*colmean[i]:5.1f}%")

    print("\nMarginal bot-seat win rate by team / offense / defense:")
    for name, keyfn, vals in [("team", lambda l: l[0], TEAMS),
                              ("offense", lambda l: l[1], OFF),
                              ("defense", lambda l: l[2], DEF)]:
        print(f"  by {name}:")
        for v in vals:
            cols = [i for i, l in enumerate(LOADOUTS) if keyfn(l) == v]
            print(f"    {v:16s} {100*np.nanmean(colmean[cols]):5.1f}%")
    return pick, best_fixed


def write_pairs(path, est, top):
    """Stage-2 candidate list: the top-N answers per human loadout, to be re-measured at
    high n. Refining only the plausible winners is what makes a precise final table
    affordable -- resolving all 16,384 cells to the same precision is not."""
    rows = []
    for h in range(N):
        for b in np.argsort(-est[h])[:top]:
            rows.append(LOADOUTS[h] + LOADOUTS[b])
    with open(path, "w") as f:
        f.write("human_team,human_off,human_def,bot_team,bot_off,bot_def\n")
        for r in rows:
            f.write(",".join(map(str, r)) + "\n")
    print(f"\nWrote {len(rows)} refinement pairs to {path}")


def write_table(path, est, games, top=8):
    """The file CounterPicker reads: per human loadout, its answers in rank order."""
    with open(path, "w", encoding="utf-8") as f:
        f.write("# Loadout best-response table for the singleplayer bot.\n")
        f.write("# row = loadout the HUMAN picked (seat P1); the ranked entries are what the\n")
        f.write("# BOT (seat P2) should answer with. DIRECTIONAL AND SEAT-SPECIFIC: fitted from\n")
        f.write("# HeuristicBot-vs-HeuristicBot games with fixed seats, so est_winrate is the\n")
        f.write("# bot's win rate against HeuristicBot playing that loadout, NOT against Marc.\n")
        f.write("# Generated by CastleDefense.BotArena counter-matrix + counter/analyze.py.\n")
        f.write("human_team,human_off,human_def,rank,bot_team,bot_off,bot_def,est_winrate,games\n")
        for h in range(N):
            for rank, b in enumerate(np.argsort(-est[h])[:top]):
                ht, ho, hd = LOADOUTS[h]
                bt, bo, bd = LOADOUTS[b]
                f.write(f"{ht},{ho},{hd},{rank},{bt},{bo},{bd},"
                        f"{est[h, b]:.4f},{int(games[h, b])}\n")
    print(f"Wrote counter table to {path}")


def main():
    args = sys.argv[1:]
    paths, pairs_out, table_out, top = [], None, None, 8
    i = 0
    while i < len(args):
        if args[i] == "--pairs":
            pairs_out = args[i + 1]; i += 2
        elif args[i] == "--emit":
            table_out = args[i + 1]; i += 2
        elif args[i] == "--top":
            top = int(args[i + 1]); i += 2
        else:
            paths.append(args[i]); i += 1

    wins, games, _ = load(paths)
    print("=== " + " + ".join(paths) + " ===")
    est, diag = analyse(wins, games)
    report(wins, games, est, diag)

    if pairs_out:
        write_pairs(pairs_out, np.where(games > 0, est, -np.inf), top)
    if table_out:
        write_table(table_out, est, games, top)


if __name__ == "__main__":
    main()
