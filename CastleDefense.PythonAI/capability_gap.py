"""
CAPABILITY MAP: where does the bot underperform a human with the SAME loadout?

Reads the bot-vs-bot balance sweep as a CAPABILITY map rather than a BALANCE table.
The bot number and the human number are on different scales (different opponents), so
the comparison is made on WITHIN-AGENT DEVIATION: each agent's win rate with loadout L
minus that same agent's own overall win rate. The gap is

    gap(L) = human_dev(L) - bot_dev(L)

Large POSITIVE gap = the loadout costs the bot much more than it costs Marc, i.e. a
capability the loadout demands and the bot lacks.

Run:  PYTHONIOENCODING=utf-8 python capability_gap.py

READ THE CAVEATS BEFORE QUOTING ANY NUMBER OUT OF THIS.

 1. MARC CHOSE HIS LOADOUTS; the bot's were assigned. His per-option rates carry his
    preference and practice, not just the option's strength.
 2. CEILING COMPRESSION. He is at 88.6% overall, so his deviation cannot exceed +11.4.
    Orange (12/12) and Green (9/9) are AT that ceiling — their gaps are artefacts and
    must not be read as real. The two largest gaps (speed, Blue) are driven by the BOT's
    deviation, not his, which is why they survive this.
 3. DIFFERENT OPPONENTS. The bot figure is vs the field of bots (SearchMirror, fixed
    loadout vs random opponent loadout); Marc's is vs the bot. The within-agent
    deviation removes the level difference, not any interaction.
 4. GADGET TIER. Marc's replays include upgraded gadgets (speed_2 is 2.0x, speed_3 is
    10x, vs base speed's 1.5x) because his games carry a time-machine headstart; the bot
    sweep is base-tier and headstart-free. The script pools by BASE id. For the headline
    speed result the tier-matched comparison is printed separately and is the honest one.
 5. Draws count as non-wins on BOTH sides.
"""
import sqlite3, json, math, collections, sys, os

REPO = r"C:\repos\Castle-Defense-Game-2"
DB = os.path.join(REPO, "CastleDefenseGame2", "recordings", "game_records.db")
DASH = os.path.join(REPO, "CastleDefense.BotArena", "dashboard", "results.json")
QUAR = os.path.join(REPO, "CastleDefenseGame2", "recordings",
                    "quarantine_no_p1_actions_20260805")

# ── the 11 abandoned rerolls: rows still live in the DB, replays were moved out ──
quarantined = {f.split('.')[0] for f in os.listdir(QUAR)} if os.path.isdir(QUAR) else set()


def wilson(w, n):
    if n == 0:
        return (0.0, 0.0, 0.0)
    p = w / n
    z = 1.96
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (100 * p, 100 * max(0, c - h), 100 * min(1, c + h))


def base(g):
    return g.split('_')[0].lower() if g else None


# ── HUMAN SIDE ────────────────────────────────────────────────────────────────
con = sqlite3.connect(DB)
rows = list(con.execute(
    "select id,p1_team,p1_gadget_off,p1_gadget_def,winner,opponent_type "
    "from games where opponent_type in ('search','heuristic') "
    "and game_mode in ('sp','practice')"))
rows = [r for r in rows if r[0] not in quarantined]

H = {'team': collections.defaultdict(lambda: [0, 0]),
     'off': collections.defaultdict(lambda: [0, 0]),
     'def': collections.defaultdict(lambda: [0, 0])}
h_w = h_n = 0
for gid, team, off, dfn, winner, opp in rows:
    win = 1 if winner == 1 else 0
    h_w += win; h_n += 1
    for k, v in (('team', team), ('off', base(off)), ('def', base(dfn))):
        H[k][v][0] += win; H[k][v][1] += 1

# ── BOT SIDE (SearchMirror cells of the balance sweep) ────────────────────────
d = json.load(open(DASH))
def cells(section):
    return d[section]['SearchMirror']

B = {'team': {}, 'off': {}, 'def': {}}
for t, v in cells('byTeam').items():
    B['team'][t] = (v['decisiveWins'] + v['timeoutWins'], v['total'])
for t, v in cells('byOffense').items():
    B['off'][t.lower()] = (v['decisiveWins'] + v['timeoutWins'], v['total'])
for t, v in cells('byDefense').items():
    B['def'][t.lower()] = (v['decisiveWins'] + v['timeoutWins'], v['total'])

bo = d['opponents']
sm = [o for o in bo if o['name'] == 'SearchMirror'][0]
b_w, b_n = sm['decisiveWins'] + sm['timeoutWins'], sm['total']

h_over = 100 * h_w / h_n
b_over = 100 * b_w / b_n

print(f"HUMAN baseline : {h_over:.1f}%  ({h_w}W/{h_n}, {len(quarantined)} rerolls excluded)")
print(f"BOT   baseline : {b_over:.1f}%  ({b_w}W/{b_n} mirror games)")
print()

LABEL = {'team': 'TEAM', 'off': 'OFFENSE GADGET', 'def': 'DEFENSE GADGET'}
allgaps = []
for key in ('team', 'off', 'def'):
    print(f"── {LABEL[key]} " + "─" * 62)
    print(f"{'option':<16}{'BOT':>18}{'MARC':>22}{'bot dev':>9}{'marc dev':>10}{'GAP':>8}")
    out = []
    for opt in sorted(set(B[key]) | set(H[key])):
        bw, bn = B[key].get(opt, (0, 0))
        hw, hn = H[key].get(opt, [0, 0])
        if bn == 0 or hn == 0:
            continue
        bp, blo, bhi = wilson(bw, bn)
        hp, hlo, hhi = wilson(hw, hn)
        bdev, hdev = bp - b_over, hp - h_over
        out.append((hdev - bdev, opt, bp, bn, hp, hw, hn, bdev, hdev))
    for gap, opt, bp, bn, hp, hw, hn, bdev, hdev in sorted(out, reverse=True):
        print(f"{opt:<16}{bp:>6.1f}% (n={bn:>4}){hp:>10.1f}% ({hw:>2}/{hn:<3})"
              f"{bdev:>+9.1f}{hdev:>+10.1f}{gap:>+8.1f}")
        allgaps.append((gap, key, opt, hn))
    print()

print("── LARGEST GAPS OVERALL " + "─" * 47)
for gap, key, opt, hn in sorted(allgaps, reverse=True)[:6]:
    print(f"  {gap:+6.1f}  {LABEL[key]:<15} {opt:<15} (human n={hn})")
print()

# ── the specific hypothesis: Blue / snipe / speed ─────────────────────────────
print("── MARC'S HYPOTHESIS: the stall-loop cell " + "─" * 30)
combo = d['byCombo']['SearchMirror']
worst = []
for team, cs in combo.items():
    for k, v in cs.items():
        w = v['decisiveWins'] + v['timeoutWins']
        worst.append((100 * w / v['total'], team, k, w, v['total']))
worst.sort()
print("  bot's 8 worst cells of 128 (12 games each):")
for p, team, k, w, n in worst[:8]:
    print(f"    {p:>5.1f}%   {team:<7} {k}")
print()
byteam_speed = collections.defaultdict(lambda: [0, 0])
for team, cs in combo.items():
    for k, v in cs.items():
        off, dfn = k.split('|')
        byteam_speed[(team, dfn)][0] += v['decisiveWins'] + v['timeoutWins']
        byteam_speed[(team, dfn)][1] += v['total']
print("  speed-defence deficit per team (bot, vs that team's other 3 defences):")
for team in sorted({t for t, _ in byteam_speed}):
    sw, sn = byteam_speed[(team, 'speed')]
    ow = sum(byteam_speed[(team, dd)][0] for dd in ('heal', 'wall', 'reinforcements'))
    on = sum(byteam_speed[(team, dd)][1] for dd in ('heal', 'wall', 'reinforcements'))
    print(f"    {team:<8} speed {100*sw/sn:>5.1f}%   others {100*ow/on:>5.1f}%   "
          f"delta {100*sw/sn - 100*ow/on:>+6.1f}")
print()

# Marc's own record with the cells he actually played that the bot is worst at
print("  Marc's record on speed defence, by team:")
hs = collections.defaultdict(lambda: [0, 0])
for gid, team, off, dfn, winner, opp in rows:
    if base(dfn) == 'speed':
        hs[team][0] += 1 if winner == 1 else 0
        hs[team][1] += 1
tot = [sum(v[0] for v in hs.values()), sum(v[1] for v in hs.values())]
for team in sorted(hs):
    w, n = hs[team]
    print(f"    {team:<8} {w}/{n}")
print(f"    {'TOTAL':<8} {tot[0]}/{tot[1]} = {100*tot[0]/tot[1]:.1f}%  "
      f"(Marc overall {h_over:.1f}%)")
print()

# ── caveat 4 made explicit: tier-match the headline result ────────────────────
print("── TIER-MATCHED speed (the honest comparison) " + "─" * 26)
tiers = collections.defaultdict(lambda: [0, 0])
for gid, team, off, dfn, winner, opp in rows:
    if base(dfn) == 'speed':
        tiers[dfn][0] += 1 if winner == 1 else 0
        tiers[dfn][1] += 1
for k in sorted(tiers):
    w, n = tiers[k]
    p, lo, hi = wilson(w, n)
    print(f"    Marc {k:<10} {w:>2}/{n:<3} = {p:>5.1f}%  [{lo:.0f}, {hi:.0f}]")
bw, bn = B['def']['speed']
bp, _, _ = wilson(bw, bn)
hw2, hn2 = tiers.get('speed', [0, 0])
if hn2:
    hp2, hlo2, hhi2 = wilson(hw2, hn2)
    print(f"\n    BASE speed only — bot {bp:.1f}% (dev {bp-b_over:+.1f}) "
          f"vs Marc {hp2:.1f}% (dev {hp2-h_over:+.1f}), gap "
          f"{(hp2-h_over)-(bp-b_over):+.1f}")
    print(f"    Marc's base-speed n is only {hn2} — CI [{hlo2:.0f}, {hhi2:.0f}]. "
          "The gap is large either way, but this is the noisy end of it.")
