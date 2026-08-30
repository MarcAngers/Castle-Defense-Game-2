"""
Per-gadget, per-LEVEL win rates -- for bot self-play and for Marc's own recordings.

Answers "is granularity worth building": the headline analyses only ever looked at AVERAGE
gadget level, and Marc's hypothesis is that the tiers differ enormously by family (he
expects poison/divine level 3 to be huge and firebomb level 3 to be minor).

TWO BASELINES, and they are not the same number:
  * self-play -- both sides are HeuristicBot, so across all (side, game) observations
    exactly half win. The baseline for ANY cell is 50%, and deviation is interpretable.
  * human -- Marc wins 88.6% and the bot 11.4%, so his gadgets and the bot's gadgets have
    to be scored against their own side's base rate, never pooled.

ONE OBSERVATION PER SIDE PER GAME, taken from the last sampled frame, so this is an
end-of-game snapshot directly comparable to what game_records.db stores. Per-FRAME counting
would inflate n by ~250x on pure autocorrelation and make every interval meaningless.

WHAT THIS CANNOT SEPARATE, and it applies to every row below: holding a high tier is
confounded with having PAID for it, and with game length (longer games permit more
upgrades on both sides). A cell below 50% does not prove the tier is bad; it is equally
consistent with the tier being fine and the route to it being expensive.

Run: PYTHONIOENCODING=utf-8 python gadget_level_table.py [calib.csv]
"""
import csv, sys, os, sqlite3, math, collections

calib = sys.argv[1] if len(sys.argv) > 1 else 'calib_gadget.csv'


def wilson(w, n):
    if n == 0:
        return (0.0, 0.0, 0.0)
    p = w / n; z = 1.96; d = 1 + z*z/n
    c = (p + z*z/(2*n)) / d
    h = z * math.sqrt(p*(1-p)/n + z*z/(4*n*n)) / d
    return (100*p, 100*max(0, c-h), 100*min(1, c+h))


def lvl_of(gid):
    if not gid:
        return 0
    return 3 if gid.endswith('_3') else 2 if gid.endswith('_2') else 1


def show(title, stat, baseline, minn=30):
    print('=' * 78)
    print(f'  {title}   (baseline {baseline:.1f}%)')
    print('=' * 78)
    print(f'  {"gadget":<16}{"lvl":>4}{"n":>8}{"win%":>8}{"95% CI":>16}{"vs base":>10}')
    for fam in sorted({f for f, _ in stat}):
        rowsf = [(l, stat[(fam, l)]) for l in (1, 2, 3) if (fam, l) in stat]
        if not rowsf:
            continue
        for l, (w, n) in rowsf:
            if n < minn:
                print(f'  {fam:<16}{l:>4}{n:>8}{"--":>8}{"(too thin)":>16}{"":>10}')
                continue
            p, lo, hi = wilson(w, n)
            print(f'  {fam:<16}{l:>4}{n:>8}{p:>7.1f}%{f"[{lo:.0f}, {hi:.0f}]":>16}{p-baseline:>+9.1f}')
        print()


# ── BOT SELF-PLAY ────────────────────────────────────────────────────────────────
if os.path.exists(calib):
    last = {}
    with open(calib, encoding='utf-8-sig') as f:
        for r in csv.DictReader(f):
            last[r['game_id']] = r          # dict order keeps the final sampled frame
    stat = collections.defaultdict(lambda: [0, 0])
    games = 0
    for r in last.values():
        games += 1
        win1 = r['winner'] == '1'
        for side, won in ((1, win1), (2, not win1)):
            for slot in ('off', 'def', 'sig'):
                fam = r[f'p{side}_{slot}']
                l = int(r[f'p{side}_{slot}_lvl'])
                if not fam or l == 0:
                    continue
                stat[(fam, l)][0] += 1 if won else 0
                stat[(fam, l)][1] += 1
    print(f'SELF-PLAY: {games} games -> {games*2} side-observations, '
          f'{sum(n for _, n in stat.values())} gadget-slot observations\n')
    show('BOT SELF-PLAY (HeuristicBot both sides)', stat, 50.0)
else:
    print(f'(no calib file at {calib}; skipping self-play table)\n')


# ── HUMAN RECORDINGS ─────────────────────────────────────────────────────────────
REC = os.path.join(os.path.dirname(__file__), '..', 'CastleDefenseGame2', 'recordings')
DB = os.path.abspath(os.path.join(REC, 'game_records.db'))
QUAR = os.path.abspath(os.path.join(REC, 'quarantine_no_p1_actions_20260805'))
quar = {f.split('.')[0] for f in os.listdir(QUAR)} if os.path.isdir(QUAR) else set()

con = sqlite3.connect(DB)
rows = [r for r in con.execute(
    "select id,p1_gadget_off,p1_gadget_def,p1_gadget_sig,"
    "p2_gadget_off,p2_gadget_def,p2_gadget_sig,winner "
    "from games where opponent_type in ('search','heuristic') and game_mode in ('sp','practice')")
    if r[0] not in quar]

marc = collections.defaultdict(lambda: [0, 0])
bot = collections.defaultdict(lambda: [0, 0])
mw = 0
for gid, o1, d1, s1, o2, d2, s2, winner in rows:
    won = winner == 1
    mw += 1 if won else 0
    for g in (o1, d1, s1):
        if g:
            k = (g.split('_')[0].lower(), lvl_of(g))
            marc[k][0] += 1 if won else 0; marc[k][1] += 1
    for g in (o2, d2, s2):
        if g:
            k = (g.split('_')[0].lower(), lvl_of(g))
            bot[k][0] += 1 if not won else 0; bot[k][1] += 1

print(f'HUMAN: {len(rows)} games, Marc won {mw} ({100*mw/len(rows):.1f}%)\n')
show("MARC'S OWN GADGETS", marc, 100.0*mw/len(rows), minn=8)
show("THE BOT'S GADGETS (its win rate holding them)", bot, 100.0*(len(rows)-mw)/len(rows), minn=8)

print('  READ WITH CARE. Every cell confounds "holds this tier" with "paid for this tier"')
print('  and with game length. The human tables are additionally tiny -- Marc has 114 games')
print('  total, so a family/level cell of 8-25 is normal and its CI spans tens of points.')
print('  Use these to spot where a granular model MIGHT pay, not as effect sizes.')
