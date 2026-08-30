"""Tests the mechanistic survival model against the dense chump-rate sweep.

Model: every chump in contact absorbs one enemy swing, so with the force delivering S
swings/sec and chumps arriving at r/sec, castle-bound swings leak at (S - r). The castle
needs K of them. Hence

    t(r) = T_walk + K / (S - r)          for r < S,   and survives forever for r >= S

which linearises as  1/(t - T_walk) = S/K - r/K.  Fit that and check whether the recovered
S and K match the roster values they are supposed to be.
"""
import csv, os, math, statistics as st

BASE = os.path.dirname(os.path.abspath(__file__))
ROSTER = os.path.join(BASE, "master_roster_copy.csv")

# --- roster + the engine's derived attack speed (GameDataManager.LoadTeamsFromCsv) ---
UNIT = {}
for r in csv.DictReader(open(ROSTER, encoding="utf-8-sig")):
    tier = int(r["Tier"]); dmg = int(r["Damage"]); spd = float(r["Speed"])
    if tier < 6:   aps = tier * 2 * spd / dmg
    elif tier < 7: aps = tier * tier * spd / dmg
    else:          aps = tier ** 3 * spd / dmg
    aps = min(max(aps, 0.2), 5.0)
    UNIT[(r["Team"].capitalize(), tier)] = dict(
        id=r["ID"], hp=int(r["Health"]), dmg=dmg, aps=aps, speed=spd,
        width=int(r["Width"]), cost=int(r["Price"]))

CASTLE_HP = 23000
LANE = 1700.0
TPS = 30.0

def walk_seconds(team, tier):
    """Time for the first force member to reach the wall, in seconds."""
    return LANE / UNIT[(team, tier)]["speed"] / TPS

def swings_to_kill(team, tier):
    """Castle swings needed, honouring siege doubling and the one-shot floor at 1 HP."""
    d = UNIT[(team, tier)]
    per = d["dmg"] * (2 if tier == 8 else 1)   # tier 8 is the only Siege tier by default
    if per >= CASTLE_HP:
        return 2                                # first hit floors at 1 HP, second kills
    return math.ceil(CASTLE_HP / per)

rows = []
for r in csv.DictReader(open(os.path.join(BASE, "curve_full.csv"))):
    r["tier"] = int(r["tier"]); r["f"] = int(r["force_size"]); r["e"] = int(r["escort_tier"])
    r["a"] = int(r["anchor_tier"])
    r["iv"] = int(r["interval_ticks"]); r["sec"] = float(r["seconds"])
    r["defS"] = float(r["blocker_spend"])
    r["rate"] = TPS / r["iv"] if r["iv"] else 0.0
    rows.append(r)

TEAMS = sorted({r["attacker_team"] for r in rows})

def died(r):
    return r["outcome"] == "castle_destroyed"

def fit(pts):
    """OLS of y = a + b x. Returns (a, b, r2)."""
    n = len(pts)
    if n < 3: return None
    xs = [p[0] for p in pts]; ys = [p[1] for p in pts]
    mx = sum(xs) / n; my = sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs)
    if sxx == 0: return None
    b = sum((x - mx) * (y - my) for x, y in pts) / sxx
    a = my - b * mx
    ss_res = sum((y - (a + b * x)) ** 2 for x, y in pts)
    ss_tot = sum((y - my) ** 2 for y in ys)
    return a, b, (1 - ss_res / ss_tot if ss_tot > 0 else float("nan"))

print("MODEL CHECK -- unescorted forces, chumps only")
print("Fitting 1/(t - T_walk) = S/K - r/K over every rate where the castle actually died.\n")
print(f"{'team':<8}{'T':<3}{'F':<3}{'n':<4}{'R2':>7}{'S fit':>8}{'S true':>8}{'K fit':>8}{'K true':>8}")
agg = []
for tier in (5, 6, 7, 8):
    for f in (1, 3, 5):
        for team in TEAMS:
            tw = walk_seconds(team, tier)
            pts = [(r["rate"], 1.0 / (r["sec"] - tw))
                   for r in rows
                   if r["attacker_team"] == team and r["tier"] == tier and r["f"] == f
                   and r["e"] == 0 and r["a"] == 0 and died(r) and r["sec"] - tw > 0.5]
            res = fit(pts)
            if not res: continue
            a, b, r2 = res
            if b >= 0: continue
            K = -1.0 / b
            S = a * K
            Strue = UNIT[(team, tier)]["aps"] * f
            Ktrue = swings_to_kill(team, tier)
            agg.append((r2, S / Strue if Strue else float("nan"), K / Ktrue if Ktrue else float("nan")))
            if team in ("White", "Red", "Green"):
                print(f"{team:<8}{tier:<3}{f:<3}{len(pts):<4}{r2:>7.3f}{S:>8.2f}{Strue:>8.2f}{K:>8.1f}{Ktrue:>8d}")
print()
if agg:
    print(f"pooled over {len(agg)} (team,tier,force) cells:")
    print(f"  median R^2            {st.median([a[0] for a in agg]):.3f}")
    print(f"  median S_fit / S_true {st.median([a[1] for a in agg]):.2f}")
    print(f"  median K_fit / K_true {st.median([a[2] for a in agg]):.2f}")
    good = sum(1 for a in agg if a[0] > 0.95)
    print(f"  cells with R^2 > 0.95: {good}/{len(agg)}")
