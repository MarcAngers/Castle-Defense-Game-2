"""Out-of-sample checks on the survival law, then the practical spend tables.

Law (fitted only on unescorted chumps-only runs):
    t(r) = T_walk + K / (S - r)        r < S
with S = force_size * attack_rate and K = castle swings to kill.

Two predictions it makes that were never fitted:
  1. An ANCHOR is just a body arriving on its own cadence, so it should act through
     r_eff = r_chump + r_anchor and nothing else.
  2. An ESCORT adds attackers, so it should raise S -- but escorts accumulate over time,
     so the law should degrade in a specific direction rather than simply fail.
"""
import csv, os, math, statistics as st

BASE = os.path.dirname(os.path.abspath(__file__))
TPS, LANE, CASTLE_HP = 30.0, 1700.0, 23000

UNIT = {}
for r in csv.DictReader(open(os.path.join(BASE, "master_roster_copy.csv"), encoding="utf-8-sig")):
    tier = int(r["Tier"]); dmg = int(r["Damage"]); spd = float(r["Speed"])
    if tier < 6:   aps = tier * 2 * spd / dmg
    elif tier < 7: aps = tier * tier * spd / dmg
    else:          aps = tier ** 3 * spd / dmg
    UNIT[(r["Team"].capitalize(), tier)] = dict(
        hp=int(r["Health"]), dmg=dmg, aps=min(max(aps, 0.2), 5.0), speed=spd, cost=int(r["Price"]))

def walk(team, tier): return LANE / UNIT[(team, tier)]["speed"] / TPS
def K_of(team, tier):
    d = UNIT[(team, tier)]; per = d["dmg"] * (2 if tier == 8 else 1)
    return 2 if per >= CASTLE_HP else math.ceil(CASTLE_HP / per)
def S_of(team, tier, f): return UNIT[(team, tier)]["aps"] * f

def predict(team, tier, f, r_eff):
    S, K = S_of(team, tier, f), K_of(team, tier)
    if r_eff >= S: return float("inf")
    return walk(team, tier) + K / (S - r_eff)

def load(name):
    out = []
    for r in csv.DictReader(open(os.path.join(BASE, name))):
        r["tier"] = int(r["tier"]); r["f"] = int(r["force_size"]); r["e"] = int(r["escort_tier"])
        r["a"] = int(r.get("anchor_tier", 0)); r["iv"] = int(r["interval_ticks"])
        r["sec"] = float(r["seconds"]); r["defS"] = float(r["blocker_spend"])
        r["ancS"] = float(r.get("anchor_spend", 0) or 0)
        r["rate"] = TPS / r["iv"] if r["iv"] else 0.0
        out.append(r)
    return out

curve = load("curve_full.csv")
anch = load("anchor_isolated.csv")
TEAMS = sorted({r["attacker_team"] for r in curve})
ANCHOR_GAP_S = 5.0   # anchor_isolated.csv was run at --anchor-gap 150

def err(pred, obs): return (pred - obs) / obs

print("=" * 78)
print("CHECK 1 -- unescorted, chumps only. In-sample sanity for the closed form.")
print("=" * 78)
e = [err(predict(r["attacker_team"], r["tier"], r["f"], r["rate"]), r["sec"])
     for r in curve if r["e"] == 0 and r["outcome"] == "castle_destroyed"
     and math.isfinite(predict(r["attacker_team"], r["tier"], r["f"], r["rate"]))]
print(f"  n={len(e)}  median error {st.median(e):+.1%}   "
      f"|err|<20%: {sum(1 for x in e if abs(x) < .2)/len(e):.0%}   "
      f"|err|<40%: {sum(1 for x in e if abs(x) < .4)/len(e):.0%}\n")

print("=" * 78)
print("CHECK 2 -- ANCHORS, never fitted. Prediction: a T5 every 5s is just r_eff = r + 0.2")
print("=" * 78)
for label, use_anchor_as_body in (("ignoring the anchor", False), ("counting it as a body", True)):
    e = []
    for r in anch:
        if r["e"] != 0 or r["a"] != 5 or r["outcome"] != "castle_destroyed": continue
        r_eff = r["rate"] + (1.0 / ANCHOR_GAP_S if use_anchor_as_body else 0.0)
        p = predict(r["attacker_team"], r["tier"], r["f"], r_eff)
        if math.isfinite(p): e.append(err(p, r["sec"]))
    if e:
        print(f"  {label:<24} n={len(e):<4} median error {st.median(e):+.1%}   "
              f"|err|<40%: {sum(1 for x in e if abs(x) < .4)/len(e):.0%}")
print("  (if the anchor were only a killer and not a blocker, counting it as a body would")
print("   OVER-predict survival; if it were only a body, the two rows would differ by exactly")
print("   the blocking term)\n")

print("=" * 78)
print("CHECK 3 -- ESCORTED forces. The law should UNDER-predict survival, because escorts")
print("           add attackers (raising S) that the unescorted S does not know about.")
print("=" * 78)
e = [err(predict(r["attacker_team"], r["tier"], r["f"], r["rate"]), r["sec"])
     for r in curve if r["e"] == 4 and r["outcome"] == "castle_destroyed"
     and math.isfinite(predict(r["attacker_team"], r["tier"], r["f"], r["rate"]))]
print(f"  n={len(e)}  median error {st.median(e):+.1%}  "
      f"-> the escort makes the real attack {1/(1+st.median(e)):.1f}x faster than the force alone")
print("  So the law is only valid for the force you can SEE swinging; escorts must be added to S.\n")

print("=" * 78)
print("CHECK 4 -- does the law predict the CRITICAL RATE (where survival becomes infinite)?")
print("=" * 78)
print(f"{'tier':<6}{'force':<7}{'S = predicted r_crit':>22}{'observed r_crit':>18}{'ratio':>8}")
for tier in (5, 6, 7, 8):
    for f in (1, 3, 5):
        preds, obs = [], []
        for team in TEAMS:
            rs = sorted([r for r in curve if r["attacker_team"] == team and r["tier"] == tier
                         and r["f"] == f and r["e"] == 0], key=lambda r: r["rate"])
            died = [r["rate"] for r in rs if r["outcome"] == "castle_destroyed"]
            lived = [r["rate"] for r in rs if r["outcome"] != "castle_destroyed" and r["rate"] > 0]
            if not died or not lived: continue
            obs.append((max(died) + min([l for l in lived if l > max(died)] or [max(died)])) / 2)
            preds.append(S_of(team, tier, f))
        if obs:
            print(f"T{tier:<5}x{f:<6}{st.median(preds):>22.2f}{st.median(obs):>18.2f}"
                  f"{st.median(obs)/st.median(preds):>8.2f}")
