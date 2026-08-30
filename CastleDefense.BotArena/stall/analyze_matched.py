"""Does a matched-tier defender, arriving after the chump line has stacked the attackers,
earn its price?"""
import csv, os, statistics as st

BASE = os.path.dirname(os.path.abspath(__file__))
INF = float("inf")

rows = []
for t in (5, 6, 7, 8):
    for r in csv.DictReader(open(os.path.join(BASE, "matched_t%d.csv" % t))):
        r["tier"] = int(r["tier"]); r["f"] = int(r["force_size"]); r["e"] = int(r["escort_tier"])
        r["a"] = int(r["anchor_tier"]); r["iv"] = int(r["interval_ticks"]); r["sec"] = float(r["seconds"])
        r["chumpS"] = float(r["blocker_spend"]); r["ancS"] = float(r["anchor_spend"])
        r["tot"] = r["chumpS"] + r["ancS"]
        r["surv"] = r["sec"] if r["outcome"] == "castle_destroyed" else INF
        rows.append(r)

TEAMS = sorted({r["attacker_team"] for r in rows})
IVS = sorted({r["iv"] for r in rows if r["iv"] > 0}, reverse=True)
IX = {(r["attacker_team"], r["tier"], r["f"], r["e"], r["a"], r["iv"]): r for r in rows}

def med(v):
    v = sorted(v, key=lambda x: (x == INF, x)); n = len(v)
    if n % 2: return v[n // 2]
    a, b = v[n // 2 - 1], v[n // 2]
    return INF if (a == INF or b == INF) else (a + b) / 2

def f(m): return "inf" if m == INF else "%.0f" % m

for e in (0, 4):
    print("=" * 112)
    print("MATCHED DEFENDER (one same-tier unit at 5s) vs CHUMPS ALONE   --  %s"
          % ("no escort" if e == 0 else "with T4 escort"))
    print("survival seconds, median of 8 teams (mirror teams, so the 1v1 is a mutual kill)")
    print("=" * 112)
    for t in (5, 6, 7, 8):
        print("\n  tier %d" % t)
        print("  %-6s%-10s%9s" % ("force", "defence", "no chumps")
              + "".join("%8.3g" % (30.0 / i) for i in IVS) + "   chumps/sec")
        for fo in (1, 3, 5):
            for a in (0, t):
                lbl = "chumps" if a == 0 else "+ matched"
                base = med([IX[(x, t, fo, e, a, 0)]["surv"] for x in TEAMS])
                line = "  x%-5d%-10s%9s" % (fo, lbl, f(base))
                for i in IVS:
                    line += "%8s" % f(med([IX[(x, t, fo, e, a, i)]["surv"] for x in TEAMS]))
                print(line)
            print()

print("=" * 112)
print("IS IT WORTH THE MONEY?  cheapest total $/s that holds all 8 teams")
print("=" * 112)
print("%-5s%-6s%-8s%22s%24s%10s" % ("tier", "force", "escort", "chumps only", "with matched defender", "winner"))
for t in (5, 6, 7, 8):
    for fo in (1, 3, 5):
        for e in (0, 4):
            out = []
            for a in (0, t):
                best = None
                for i in [0] + IVS:
                    cs = [IX[(x, t, fo, e, a, i)] for x in TEAMS]
                    if any(c["surv"] != INF for c in cs): continue
                    rt = med([c["tot"] / max(c["sec"], 1e-9) for c in cs])
                    if best is None or rt < best: best = rt
                out.append(best)
            w = ("matched" if (out[1] is not None and (out[0] is None or out[1] < out[0] * 0.98))
                 else ("chumps" if out[0] is not None else "neither"))
            print("%-5s%-6s%-8s%22s%24s%10s" % (
                "T%d" % t, "x%d" % fo, "-" if e == 0 else "T4",
                ("$%.1f/s" % out[0]) if out[0] else "never",
                ("$%.1f/s" % out[1]) if out[1] else "never", w))

print()
print("=" * 112)
print("WHAT THE MATCHED DEFENDER COSTS, and how many attackers one swing reaches")
print("=" * 112)
print("%-5s%-6s%16s%16s%18s" % ("tier", "force", "unit price", "force price", "defender as % of force"))
for t in (5, 6, 7, 8):
    for fo in (1, 3, 5):
        cs = [IX[(x, t, fo, 0, t, 3)] for x in TEAMS]
        anc = med([c["ancS"] for c in cs])
        atk = med([float(c["force_spend"]) for c in cs])
        print("%-5s%-6s%16s%16s%17.0f%%" % ("T%d" % t, "x%d" % fo, "$%.0f" % anc, "$%.0f" % atk,
                                            100.0 * anc / atk if atk else 0))
