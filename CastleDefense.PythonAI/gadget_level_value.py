"""
Does GADGET LEVEL belong in the board evaluator?

Motivated by the 2026-08-12 upgrade-macro result: search could buy a real +1.17 points
from gadget upgrades, but only barely, and the structural reason is that gadget level is
not one of EvaluateBoard's six features. An upgrade needs 4-15 casts through their own
cooldowns -- speed alone is 7 x 5s = 1050 ticks -- so it lands far outside the 300-tick
rollout horizon and is invisible at the leaf.

THE QUESTION IS INCREMENTAL VALUE, NOT CORRELATION. Gadget level rises with income, game
length and board control, all of which the existing features already carry. A raw
"higher level correlates with winning" would be almost guaranteed and would tell us
nothing -- adding a redundant feature is precisely how the 2026-08-05 refit went wrong:
it shifted MONEY's share of the weight vector and the win rate collapsed from 75% to 34%.
So everything below is measured as a delta on top of the deployed six.

Input: calib-collect output with the gadget-level columns (2026-08-12 onward).
Run:   PYTHONIOENCODING=utf-8 python gadget_level_value.py <calib.csv>
"""
import csv, sys, math, collections, random

path = sys.argv[1] if len(sys.argv) > 1 else 'calib_gadget.csv'
SIX = ['hp_score', 'income_score', 'money_score', 'army_score', 'gadget_score', 'repair_score']

rows, per_game = [], collections.defaultdict(list)
with open(path, encoding='utf-8-sig') as f:
    for r in csv.DictReader(f):
        p1 = (int(r['p1_off_lvl']) + int(r['p1_def_lvl']) + int(r['p1_sig_lvl'])) / 3.0
        p2 = (int(r['p2_off_lvl']) + int(r['p2_def_lvl']) + int(r['p2_sig_lvl'])) / 3.0
        rec = {
            'g': r['game_id'], 'tick': int(r['tick']),
            'x': [float(r[k]) for k in SIX],
            'lvl_diff': p1 - p2,            # P1 minus P2, the differential that matters
            'p1_avg': p1, 'p2_avg': p2,
            'y': 1 if r['winner'] == '1' else 0,
            'fams': [(r['p1_off'], int(r['p1_off_lvl']), r['p2_off'], int(r['p2_off_lvl'])),
                     (r['p1_def'], int(r['p1_def_lvl']), r['p2_def'], int(r['p2_def_lvl'])),
                     (r['p1_sig'], int(r['p1_sig_lvl']), r['p2_sig'], int(r['p2_sig_lvl']))],
        }
        rows.append(rec); per_game[rec['g']].append(rec)

print(f"{len(rows):,} rows, {len(per_game)} games, "
      f"P1 wins {sum(1 for g in per_game if per_game[g][0]['y']):,}/{len(per_game)}")

# ── THINNING. Frames inside one game are enormously autocorrelated; without this the
# effective sample size is the GAME count, not the row count, and every standard error
# below would be ~500x too small. Take one frame per game per 300-tick block.
thin = []
for g, rs in per_game.items():
    seen = set()
    for r in sorted(rs, key=lambda r: r['tick']):
        b = r['tick'] // 300
        if b not in seen:
            seen.add(b); thin.append(r)
print(f"after thinning: {len(thin):,} rows\n")

# ── HOLD OUT BY GAME, never by row: two frames from the same game share an outcome, so a
# row-wise split leaks the label straight across the boundary.
games = sorted(per_game)
random.Random(12345).shuffle(games)
cut = int(0.7 * len(games))
tr_g, te_g = set(games[:cut]), set(games[cut:])
tr = [r for r in thin if r['g'] in tr_g]
te = [r for r in thin if r['g'] in te_g]
print(f"train {len(tr):,} rows / {len(tr_g)} games   test {len(te):,} rows / {len(te_g)} games\n")


def fit(data, feats, iters=4000, lr=0.5, l2=1e-4):
    """Plain logistic with intercept, centered features. Gradient descent is ample here."""
    n = len(feats)
    w = [0.0] * n; b = 0.0
    for _ in range(iters):
        gw = [0.0] * n; gb = 0.0
        for r in data:
            x = feats_of(r, feats)
            z = b + sum(w[i] * x[i] for i in range(n))
            p = 1.0 / (1.0 + math.exp(-max(-30, min(30, z))))
            e = p - r['y']
            for i in range(n): gw[i] += e * x[i]
            gb += e
        m = len(data)
        for i in range(n): w[i] -= lr * (gw[i] / m + l2 * w[i])
        b -= lr * gb / m
    return w, b


def feats_of(r, feats):
    out = []
    for f in feats:
        if f == 'lvl_diff': out.append(r['lvl_diff'])
        else: out.append(r['x'][SIX.index(f)] - 0.5)   # centre the [0,1] components
    return out


def score(data, feats, w, b):
    ll = 0.0; correct = 0; pairs = []
    for r in data:
        x = feats_of(r, feats)
        z = b + sum(w[i] * x[i] for i in range(len(feats)))
        p = 1.0 / (1.0 + math.exp(-max(-30, min(30, z))))
        ll += -(r['y'] * math.log(max(p, 1e-12)) + (1 - r['y']) * math.log(max(1 - p, 1e-12)))
        if (p >= 0.5) == (r['y'] == 1): correct += 1
        pairs.append((p, r['y']))
    pos = [p for p, y in pairs if y == 1]; neg = [p for p, y in pairs if y == 0]
    # AUC via rank sum
    allp = sorted(pairs); ranks = {}
    auc = 0.0
    if pos and neg:
        s = sorted(range(len(pairs)), key=lambda i: pairs[i][0])
        rank = [0] * len(pairs)
        for i, idx in enumerate(s): rank[idx] = i + 1
        rsum = sum(rank[i] for i in range(len(pairs)) if pairs[i][1] == 1)
        auc = (rsum - len(pos) * (len(pos) + 1) / 2) / (len(pos) * len(neg))
    return ll / len(data), correct / len(data), auc


print('=' * 74)
print('  HEADLINE: does average gadget level add anything to the deployed six?')
print('=' * 74)
base_w, base_b = fit(tr, SIX)
add_w, add_b = fit(tr, SIX + ['lvl_diff'])
bll, bacc, bauc = score(te, SIX, base_w, base_b)
all_, aacc, aauc = score(te, SIX + ['lvl_diff'], add_w, add_b)
print(f"  six features        logloss {bll:.4f}  acc {bacc:.2%}  AUC {bauc:.4f}")
print(f"  + avg level diff    logloss {all_:.4f}  acc {aacc:.2%}  AUC {aauc:.4f}")
print(f"  DELTA               logloss {all_-bll:+.4f}  acc {aacc-bacc:+.2%}  AUC {aauc-bauc:+.4f}")
print(f"\n  fitted coefficient on lvl_diff: {add_w[-1]:+.4f}")
print("  (for scale, the six: " + ", ".join(f"{f.split('_')[0]} {add_w[i]:+.3f}"
                                            for i, f in enumerate(SIX)) + ")")

# Raw correlation, reported ONLY to show how misleading it is next to the delta above.
n1 = sum(1 for r in thin if r['lvl_diff'] > 0 and r['y'] == 1)
d1 = sum(1 for r in thin if r['lvl_diff'] > 0)
n2 = sum(1 for r in thin if r['lvl_diff'] < 0 and r['y'] == 1)
d2 = sum(1 for r in thin if r['lvl_diff'] < 0)
print(f"\n  raw (uncontrolled): P1 wins {n1/max(d1,1):.1%} when ahead on level (n={d1:,}), "
      f"{n2/max(d2,1):.1%} when behind (n={d2:,})")
print("  ^ this gap is what a naive read would quote; the DELTA above is the honest number.")

print()
print('=' * 74)
print('  GRANULARITY: is per-gadget level worth modelling separately?')
print('=' * 74)
print('  Win rate for the side holding the HIGHER level of a given family,')
print('  counted only on frames where the two sides differ in that family.')
print(f'  {"family":<16}{"n frames":>10}{"win% ahead":>12}{"lift vs 50":>12}')
fam_stat = collections.defaultdict(lambda: [0, 0])
for r in thin:
    for (fa, la, fb, lb) in r['fams']:
        if fa == fb and la != lb:          # same family both sides, different level
            ahead_is_p1 = la > lb
            won = (r['y'] == 1) == ahead_is_p1
            fam_stat[fa][0] += 1 if won else 0
            fam_stat[fa][1] += 1
for fam, (w_, n_) in sorted(fam_stat.items(), key=lambda kv: -kv[1][1]):
    if n_ < 200: continue
    print(f'  {fam:<16}{n_:>10,}{w_/n_:>11.1%}{w_/n_-0.5:>+12.1%}')
print('\n  Same-family comparison only, so team and loadout are held fixed -- but this is')
print('  still UNCONTROLLED for the other five features, so read it as "where is the')
print('  signal concentrated", not as an effect size.')
