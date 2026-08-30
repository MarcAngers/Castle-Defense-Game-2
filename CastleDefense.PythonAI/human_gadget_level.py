"""
Does Marc win with LOWER-level gadgets than the bot?

The self-play analysis (gadget_level_value.py, 800 games) found gadget level carries no
incremental signal over the deployed six features and its coefficient is NEGATIVE: the
side ahead on level tends to lose. The proposed mechanism is that gadget XP is earned by
CASTING, so a high level marks a player who has been spending on gadgets rather than on
units and investments -- a consequence variable, like money.

That mechanism makes a sharp prediction about the human record: HeuristicBot casts 6 of
its 16 gadgets on cooldown, so it should out-LEVEL Marc while losing to him. If instead
Marc out-levels the bot, the negative sign is about the value of tiers rather than about
how this bot acquires them, and the self-play conclusion does not transfer.

Data: game_records.db. Gadget ids are captured from the live PlayerState at GAME OVER
(GameRecorder.Save), so they are post-upgrade finals, not starting loadouts.

Run: PYTHONIOENCODING=utf-8 python human_gadget_level.py
"""
import sqlite3, os, collections, math

REC = os.path.join(os.path.dirname(__file__), '..', 'CastleDefenseGame2', 'recordings')
DB = os.path.abspath(os.path.join(REC, 'game_records.db'))
QUAR = os.path.abspath(os.path.join(REC, 'quarantine_no_p1_actions_20260805'))
quarantined = {f.split('.')[0] for f in os.listdir(QUAR)} if os.path.isdir(QUAR) else set()


def lvl(gid):
    if not gid:
        return 0
    return 3 if gid.endswith('_3') else 2 if gid.endswith('_2') else 1


con = sqlite3.connect(DB)
rows = [r for r in con.execute(
    "select id,p1_gadget_off,p1_gadget_def,p1_gadget_sig,"
    "p2_gadget_off,p2_gadget_def,p2_gadget_sig,winner,opponent_type,game_mode,duration_ticks "
    "from games where opponent_type in ('search','heuristic') and game_mode in ('sp','practice')")
    if r[0] not in quarantined]

print(f"{len(rows)} human games ({len(quarantined)} quarantined rerolls excluded)\n")

recs = []
for gid, o1, d1, s1, o2, d2, s2, winner, opp, mode, ticks in rows:
    m = (lvl(o1) + lvl(d1) + lvl(s1)) / 3.0      # Marc is always P1 in these
    b = (lvl(o2) + lvl(d2) + lvl(s2)) / 3.0
    recs.append({'marc': m, 'bot': b, 'win': winner == 1, 'opp': opp, 'ticks': ticks})

n = len(recs)
mw = sum(1 for r in recs if r['win'])
print(f"Marc's record: {mw}/{n} = {100*mw/n:.1f}%\n")

print('=' * 70)
print('  FINAL GADGET LEVEL, Marc vs the bot')
print('=' * 70)
print(f"  {'opponent':<12}{'n':>5}{'Marc avg lvl':>14}{'bot avg lvl':>13}{'diff':>8}{'Marc win%':>11}")
for opp in ('heuristic', 'search', 'ALL'):
    sub = recs if opp == 'ALL' else [r for r in recs if r['opp'] == opp]
    if not sub:
        continue
    am = sum(r['marc'] for r in sub) / len(sub)
    ab = sum(r['bot'] for r in sub) / len(sub)
    w = sum(1 for r in sub if r['win'])
    print(f"  {opp:<12}{len(sub):>5}{am:>14.2f}{ab:>13.2f}{am-ab:>+8.2f}{100*w/len(sub):>10.1f}%")

print()
print('=' * 70)
print('  Does Marc win MORE when he is behind on gadget level?')
print('=' * 70)
buckets = collections.defaultdict(lambda: [0, 0])
for r in recs:
    d = r['marc'] - r['bot']
    k = 'Marc AHEAD' if d > 0.01 else 'Marc BEHIND' if d < -0.01 else 'level'
    buckets[k][0] += 1 if r['win'] else 0
    buckets[k][1] += 1
for k in ('Marc AHEAD', 'level', 'Marc BEHIND'):
    w, t = buckets[k]
    if t:
        # Wilson interval, because several of these cells are small
        p = w / t; z = 1.96; den = 1 + z*z/t
        c = (p + z*z/(2*t)) / den
        h = z * math.sqrt(p*(1-p)/t + z*z/(4*t*t)) / den
        print(f"  {k:<14}{w:>4}/{t:<4} = {100*p:>5.1f}%  [{100*max(0,c-h):.0f}, {100*min(1,c+h):.0f}]")

print()
print('=' * 70)
print('  How Marc LOSES: level in his wins vs his losses')
print('=' * 70)
for label, sel in (('wins', True), ('losses', False)):
    sub = [r for r in recs if r['win'] == sel]
    if not sub:
        continue
    am = sum(r['marc'] for r in sub) / len(sub)
    ab = sum(r['bot'] for r in sub) / len(sub)
    at = sum(r['ticks'] for r in sub) / len(sub) / 30.0
    print(f"  {label:<8}n={len(sub):<4} Marc lvl {am:.2f}   bot lvl {ab:.2f}   "
          f"diff {am-ab:+.2f}   avg length {at:.0f}s")

print()
print("  POWER: Marc wins ~89% of these games, so the loss cell is tiny and every")
print("  conditional above is thin. Read the descriptive Marc-vs-bot level gap as the")
print("  reliable part; the win-rate splits are directional at best.")
print("  Also: these are END-of-game levels, and a longer game allows more upgrades on")
print("  BOTH sides, so level and duration are entangled here in a way the per-frame")
print("  self-play analysis was not.")
