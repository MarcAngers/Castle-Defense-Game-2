"""Builds the game-anatomy report: castle health over time for both builds on one shared
clock, incoming pressure below it (never a second y-scale), and the army behind each collapse.

Run from this directory. Reads mirror_dump.csv (the original defence-only build) and
mirror_dump_cur.csv (the current build: priced repair, seconds-valued options,
value-selected wiper on a 1s cooldown).
"""
import csv, io, math, os

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "mirror_anatomy.html")


def load(name):
    rows = list(csv.DictReader(io.open(os.path.join(HERE, name), encoding="utf-8")))
    for r in rows:
        for k in ("tick", "hp", "maxhp", "money", "inv", "own", "enemy",
                  "enemy_dps", "enemy_swings", "enemy_value",
                  "t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8",
                  "p2money", "p2inv", "p1income", "p2income", "p1spent", "p2spent"):
            r[k] = float(r[k])
        r["t"] = round(r["tick"] / 30.0, 2)
    return rows


def steep(rows, W=20, n=5):
    """Steepest non-overlapping 2-second health-loss windows."""
    cand = []
    for i in range(W, len(rows)):
        a, b = rows[i - W], rows[i]
        dt = b["t"] - a["t"]
        if dt > 0:
            cand.append(((b["hp"] - a["hp"]) / dt, b))
    cand.sort(key=lambda x: x[0])
    out, seen = [], []
    for rate, b in cand:
        if rate > -50 or any(abs(b["t"] - s) < 8 for s in seen):
            continue
        seen.append(b["t"])
        out.append((rate, b))
        if len(out) >= n:
            break
    return sorted(out, key=lambda x: x[1]["t"])


def repairs(rows):
    """Repair events and their real cost, from the steps in max health."""
    def price(c):
        p = math.exp(0.0109 * c ** 3 + 0.0011 * c ** 2 + 0.4351 * c + 0.5268) * (c * 5 + 5)
        return p * 2 if c >= 8 else p
    prev, ev, tot = rows[0], [], 0.0
    for r in rows[1:]:
        if r["maxhp"] > prev["maxhp"]:
            c = int(round((r["maxhp"] - 1000) / 11000.0)) - 1
            tot += price(c)
            ev.append((r["t"], c, price(c)))
        prev = r
    return ev, tot


OLD, NEW = load("mirror_dump.csv"), load("mirror_dump_cur.csv")
old_ev, old_cost = repairs(OLD)
new_ev, new_cost = repairs(NEW)
snaps = steep(NEW)
old_worst = min(steep(OLD), key=lambda x: x[0])

# ── prose numbers, derived rather than typed ────────────────────────────────
# Every figure quoted in the copy below comes from here. Hand-typed numbers in a
# generated report are exactly the "no feedback loop" failure the project keeps hitting.
new_worst = min(steep(NEW), key=lambda x: x[0])
OLD_END, NEW_END = OLD[-1]["t"], NEW[-1]["t"]
OLD_PEAK_HP, NEW_PEAK_HP = max(r["hp"] for r in OLD), max(r["hp"] for r in NEW)
OLD_PEAK_DPS, NEW_PEAK_DPS = max(r["enemy_dps"] for r in OLD), max(r["enemy_dps"] for r in NEW)
SHALLOWER = old_worst[0] / new_worst[0]
LONGER = (NEW_END - OLD_END) / OLD_END
CASH_PCT = 100 * sum(1 for r in NEW if r["money"] > 5000) / len(NEW)


T_MAX = max(OLD[-1]["t"], NEW[-1]["t"])
HP_MAX = max(max(r["maxhp"] for r in OLD), max(r["maxhp"] for r in NEW))
DPS_MAX = max(max(r["enemy_dps"] for r in OLD), max(r["enemy_dps"] for r in NEW))

W, PADL, PADR = 1000, 68, 22
H_HP, H_DPS, PADT, PADB = 250, 155, 18, 30
IW = W - PADL - PADR


def x(t): return PADL + IW * (t / T_MAX)
def yhp(v): return PADT + H_HP * (1 - v / HP_MAX)
def ydps(v): return PADT + H_DPS * (1 - v / DPS_MAX)


def path(rows, fy, key, step=2):
    d = []
    for i in range(0, len(rows), step):
        p = rows[i]
        d.append(("M" if not d else "L") + f"{x(p['t']):.1f},{fy(p[key]):.1f}")
    return "".join(d)


def ticks(maxv, n=4): return [round(maxv / n * i) for i in range(n + 1)]
def fmt(v): return f"{v/1000:.0f}k" if v >= 1000 else str(int(v))


hp_grid = "".join(
    f'<line class="grid" x1="{PADL}" y1="{yhp(v):.1f}" x2="{W-PADR}" y2="{yhp(v):.1f}"/>'
    f'<text class="ax" x="{PADL-9}" y="{yhp(v)+3.5:.1f}" text-anchor="end">{fmt(v)}</text>'
    for v in ticks(HP_MAX))
dps_grid = "".join(
    f'<line class="grid" x1="{PADL}" y1="{ydps(v):.1f}" x2="{W-PADR}" y2="{ydps(v):.1f}"/>'
    f'<text class="ax" x="{PADL-9}" y="{ydps(v)+3.5:.1f}" text-anchor="end">{fmt(v)}</text>'
    for v in ticks(DPS_MAX, 3))
x_axis = "".join(
    f'<text class="ax" x="{x(t):.1f}" y="{PADT+H_DPS+22}" text-anchor="middle">{t}s</text>'
    for t in [0, 40, 80, 120, 160, 200, int(T_MAX)])

# ── economy figure ──────────────────────────────────────────────────────────
H_ECON = 190
MONEY_MAX = max(max(r["money"] for r in NEW), max(r["p2money"] for r in NEW))


def yecon(v): return PADT + H_ECON * (1 - v / MONEY_MAX)


econ_grid = "".join(
    f'<line class="grid" x1="{PADL}" y1="{yecon(v):.1f}" x2="{W-PADR}" y2="{yecon(v):.1f}"/>'
    f'<text class="ax" x="{PADL-9}" y="{yecon(v)+3.5:.1f}" text-anchor="end">${fmt(v)}</text>'
    for v in ticks(MONEY_MAX, 4))
econ_x = "".join(
    f'<text class="ax" x="{x(t):.1f}" y="{PADT+H_ECON+22}" text-anchor="middle">{t}s</text>'
    for t in [0, 40, 80, 120, 160, 200, int(T_MAX)])


def invest_marks(rows, key, cls):
    """A tick wherever an investment lands -- the money sawtooth is this, not spending."""
    out, prev = "", rows[0][key]
    for r in rows:
        if r[key] > prev:
            out += (f'<line class="{cls}" x1="{x(r["t"]):.1f}" y1="{PADT+H_ECON-7}" '
                    f'x2="{x(r["t"]):.1f}" y2="{PADT+H_ECON}"/>')
            prev = r[key]
    return out


# The investment race, which is what this game is actually about. Investment 8 is
# Armageddon and a guaranteed win; its price is set when the 7th lands.
INV8_PRICE = 40000   # PlayerState.ApplyInvestmentStep, the InvestmentCount == 7 branch


def inv_times(rows, key):
    out, prev = {}, rows[0][key]
    for r in rows:
        if r[key] > prev:
            out[int(r[key])] = r["t"]
            prev = r[key]
    return out


US_INV, THEM_INV = inv_times(NEW, "inv"), inv_times(NEW, "p2inv")
OUR_LAST, THEIR_LAST = max(US_INV), max(THEM_INV)
RACE_FROM = US_INV[OUR_LAST]
late = [r for r in NEW if r["t"] >= RACE_FROM]
PEAK_CASH = max(r["money"] for r in late)
SPEND_LATE = late[-1]["p1spent"] - late[0]["p1spent"]
THEIR_SPEND_LATE = late[-1]["p2spent"] - late[0]["p2spent"]
EARNED_LATE = late[-1]["p1income"] * (late[-1]["t"] - RACE_FROM)
LOCKSTEP = sum(1 for k in US_INV if k in THEM_INV and abs(US_INV[k] - THEM_INV[k]) < 5)

econ_us = path(NEW, yecon, "money")
econ_them = path(NEW, yecon, "p2money")
econ_marks = invest_marks(NEW, "inv", "invus") + invest_marks(NEW, "p2inv", "invthem")
LAST = NEW[-1]


# ── decision ribbon ─────────────────────────────────────────────────────────
# Four groups rather than seven raw arms. A ribbon puts arbitrary pairs of segments
# side by side, so the all-pairs colour floor applies -- and the reference palette
# only clears that for its first three slots. Three hues plus a recessive neutral is
# what fits, and the grouping is the one that answers "is the bot acting, and why".
GROUP = {
    "": "idle", "(none)": "idle", "watch": "idle", "free": "idle",
    "wait": "holding", "outmatched": "holding",
    "block": "spending", "wipe": "spending", "repair": "spending", "repair-idle": "spending",
    "critical": "critical",
}
GCOLOR = {"idle": "var(--sunken)", "holding": "var(--them)",
          "spending": "var(--new)", "critical": "var(--old)"}
H_RIB = 34


def ribbon(rows):
    """Contiguous runs of the grouped decision, as one row of segments."""
    segs, start, cur = [], rows[0]["t"], GROUP.get(rows[0]["choice"], "idle")
    for r in rows[1:]:
        g = GROUP.get(r["choice"], "idle")
        if g != cur:
            segs.append((start, r["t"], cur))
            start, cur = r["t"], g
    segs.append((start, rows[-1]["t"], cur))
    out = ""
    for a, b, g in segs:
        w = max(0.6, x(b) - x(a))
        out += (f'<rect x="{x(a):.1f}" y="{PADT}" width="{w:.1f}" height="{H_RIB}" '
                f'fill="{GCOLOR[g]}"><title>{g} {a:.0f}-{b:.0f}s</title></rect>')
    return out


rib = ribbon(NEW)
rib_x = "".join(
    f'<text class="ax" x="{x(t):.1f}" y="{PADT+H_RIB+18}" text-anchor="middle">{t}s</text>'
    for t in [0, 40, 80, 120, 160, 200, int(T_MAX)])

# share of time per group, and the full arm breakdown per investment level
import collections
gshare = collections.Counter(GROUP.get(r["choice"], "idle") for r in NEW)
gtotal = sum(gshare.values())
rib_legend = "".join(
    f'<span><i style="background:{GCOLOR[g]}"></i>{g} &mdash; {100*gshare[g]/gtotal:.0f}% of the game</span>'
    for g in ("idle", "holding", "spending", "critical"))

ARMS = ["watch", "wait", "outmatched", "block", "wipe", "critical"]
inv_rows = ""
for L in sorted({int(r["inv"]) for r in NEW}):
    sub = [r for r in NEW if int(r["inv"]) == L]
    if len(sub) < 5:
        continue
    cc = collections.Counter(r["choice"] or "idle" for r in sub)
    span = max(r["t"] for r in sub) - min(r["t"] for r in sub)
    cells = ""
    for a in ARMS:
        pct = 100 * cc[a] / len(sub)
        k = "hot" if (a == "critical" and pct >= 50) else ("dim" if pct < 1 else "")
        cells += f'<td class="{k}">{pct:.0f}%</td>'
    inv_rows += (f'<tr><th class="rh">{L}</th><td class="num dim">{span:.0f}s</td>{cells}</tr>')
    LAST_L, LAST_SPAN, LAST_CC, LAST_N = L, span, cc, len(sub)

# the level the game is actually lost on, as shares
LP = {a: 100 * LAST_CC[a] / LAST_N for a in ARMS}

markers, cards = "", ""
RAMP = {"T3": "var(--ord-1)", "T4": "var(--ord-1)", "T5": "var(--ord-2)",
        "T6": "var(--ord-2)", "T7": "var(--ord-3)", "T8": "var(--ord-3)"}
for i, (rate, b) in enumerate(snaps):
    px = x(b["t"])
    markers += (f'<line class="mark" x1="{px:.1f}" y1="{PADT}" x2="{px:.1f}" y2="{PADT+H_HP}"/>'
                f'<circle class="markdot" cx="{px:.1f}" cy="{yhp(b["hp"]):.1f}" r="4.5"/>'
                f'<text class="marklab" x="{px:.1f}" y="{PADT-5}" text-anchor="middle">{chr(65+i)}</text>')
    tiers = {f"T{k}": int(b["t" + str(k)]) for k in range(1, 9) if b["t" + str(k)] > 0}
    total = max(1, sum(tiers.values()))
    bar = "".join(f'<span class="seg" style="width:{100*c/total:.2f}%;background:{RAMP.get(t,"var(--ord-2)")}"></span>'
                  for t, c in sorted(tiers.items()))
    chips = "".join(f'<span class="chip"><i style="background:{RAMP.get(t,"var(--ord-2)")}"></i>{t}&thinsp;&times;&thinsp;{c}</span>'
                    for t, c in sorted(tiers.items()))
    cards += f"""
    <div class="snap"><div class="snap-h"><span class="badge">{chr(65+i)}</span>
      <span class="snap-t">t = {b['t']:.0f}s</span><span class="snap-rate">{rate:,.0f} HP/s</span></div>
      <div class="bar">{bar}</div><div class="chips">{chips}</div>
      <dl><dt>castle HP</dt><dd>{b['hp']:,.0f}</dd>
          <dt>enemy units</dt><dd>{b['enemy']:.0f}</dd>
          <dt>incoming DPS</dt><dd>{b['enemy_dps']:,.0f}</dd>
          <dt>swings / sec</dt><dd>{b['enemy_swings']:.0f}</dd>
          <dt>army value</dt><dd>${b['enemy_value']:,.0f}</dd>
          <dt>our blockers</dt><dd>{b['own']:.0f}</dd>
          <dt>bot chose</dt><dd class="choice">{b['choice'] or '&mdash;'}</dd></dl></div>"""

# what the threat is actually made of, across the snapshot windows
SNAP_TIERS = {k: sum(int(b["t" + str(k)]) for _, b in snaps) for k in range(1, 9)}
SNAP_TOT = max(1, sum(SNAP_TIERS.values()))
TOP_TIERS = sorted(SNAP_TIERS, key=SNAP_TIERS.get, reverse=True)[:2]
TOP_SHARE = 100 * sum(SNAP_TIERS[k] for k in TOP_TIERS) / SNAP_TOT

rep_rows = ""
for label, ev, tot in (("before", old_ev, old_cost), ("after", new_ev, new_cost)):
    for t, c, p in ev:
        rep_rows += (f'<tr class="{label}"><td>{label}</td><td>{t:.0f}s</td><td>#{c}</td>'
                     f'<td>${p:,.0f}</td></tr>')

HTML = f"""<title>Anatomy of a Lost Mirror</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Archivo:wght@500;600;700&family=Newsreader:ital,opsz,wght@0,6..72,400;0,6..72,500;1,6..72,400&family=IBM+Plex+Mono:wght@400;500;600&display=swap">
<style>
:root{{--ground:#f5f3ee;--surface:#fffdf9;--sunken:#edeae2;--ink:#1b1813;--ink-soft:#5a5346;
--ink-dim:#8a8271;--rule:#dad5c8;--rule-soft:#e7e3d9;--accent:#8a6212;--accent-w:#f0e4c8;
--new:#2a78d6;--old:#eb6834;--them:#1baf7a;--ord-1:#86b6ef;--ord-2:#2a78d6;--ord-3:#104281}}
@media (prefers-color-scheme:dark){{:root:not([data-theme="light"]){{--ground:#131210;--surface:#1c1a16;
--sunken:#242119;--ink:#efebe1;--ink-soft:#b3ab99;--ink-dim:#837b69;--rule:#343026;--rule-soft:#282419;
--accent:#d6a741;--accent-w:#3a2e14;--new:#3987e5;--old:#d95926;--them:#199e70;--ord-1:#b7d3f6;--ord-2:#5598e7;--ord-3:#184f95}}}}
:root[data-theme="dark"]{{--ground:#131210;--surface:#1c1a16;--sunken:#242119;--ink:#efebe1;
--ink-soft:#b3ab99;--ink-dim:#837b69;--rule:#343026;--rule-soft:#282419;--accent:#d6a741;
--accent-w:#3a2e14;--new:#3987e5;--old:#d95926;--them:#199e70;--ord-1:#b7d3f6;--ord-2:#5598e7;--ord-3:#184f95}}
*{{box-sizing:border-box}}
body{{background:var(--ground);color:var(--ink);margin:0;padding:0 24px 88px;
font-family:"Newsreader",Georgia,serif;font-size:17px;line-height:1.62;-webkit-font-smoothing:antialiased}}
.wrap{{max-width:1060px;margin:0 auto}} .prose{{max-width:65ch}}
h1,h2,h3,.lab,th,.badge{{font-family:"Archivo",system-ui,sans-serif}}
h1{{font-size:clamp(2rem,4.6vw,2.9rem);font-weight:700;letter-spacing:-.028em;line-height:1.05;margin:0 0 .5rem;text-wrap:balance}}
h2{{font-size:1.3rem;font-weight:600;letter-spacing:-.012em;margin:0}}
h3{{font-size:.95rem;font-weight:600;margin:0 0 .5rem}}
p{{margin:0 0 1rem}} p:last-child{{margin-bottom:0}}
strong{{font-weight:500;background:var(--accent-w);padding:0 .2em;border-radius:2px}}
code{{font-family:"IBM Plex Mono",monospace;font-size:.84em;background:var(--sunken);color:var(--ink-soft);padding:.12em .38em;border-radius:3px}}
.lab{{font-size:.7rem;font-weight:600;letter-spacing:.13em;text-transform:uppercase;color:var(--ink-dim)}}
header{{padding:64px 0 32px;border-bottom:2px solid var(--ink)}}
.kicker{{display:flex;align-items:baseline;gap:12px;margin-bottom:18px}}
.kicker .dot{{width:9px;height:9px;background:var(--accent);transform:rotate(45deg);flex:none}}
.dek{{font-size:1.2rem;color:var(--ink-soft);max-width:62ch;margin:0}}
.stats{{display:grid;gap:1px;background:var(--rule);border:1px solid var(--rule);margin:34px 0 0;
grid-template-columns:repeat(auto-fit,minmax(190px,1fr))}}
.stat{{background:var(--surface);padding:20px 18px}}
.stat-n{{font-family:"Archivo",sans-serif;font-size:clamp(1.5rem,3.2vw,2rem);font-weight:700;
letter-spacing:-.03em;line-height:1;color:var(--new);display:block;margin-bottom:8px;font-variant-numeric:tabular-nums}}
.stat-n .u{{font-size:.5em;font-weight:600}} .stat-t{{font-size:.87rem;color:var(--ink-soft);line-height:1.42}}
section{{margin-top:56px}}
.shead{{display:flex;align-items:baseline;gap:14px;padding-bottom:12px;margin-bottom:20px;border-bottom:1px solid var(--rule)}}
.shead .n{{font-family:"IBM Plex Mono",monospace;font-size:.74rem;font-weight:600;color:var(--accent);flex:none}}
figure{{margin:0;background:var(--surface);border:1px solid var(--rule);padding:18px 16px 8px}}
.figtitle{{font-family:"Archivo",sans-serif;font-size:.95rem;font-weight:600;margin:0 0 2px 4px}}
.figsub{{font-size:.83rem;color:var(--ink-dim);margin:0 0 10px 4px}}
.scroll{{overflow-x:auto}} svg{{display:block;width:100%;height:auto;font-family:"IBM Plex Mono",monospace}}
.grid{{stroke:var(--rule-soft);stroke-width:1}} .ax{{fill:var(--ink-dim);font-size:10.5px}}
.lnew{{fill:none;stroke:var(--new);stroke-width:2;stroke-linejoin:round}}
.lthem{{fill:none;stroke:var(--them);stroke-width:2;stroke-linejoin:round}}
.invus{{stroke:var(--new);stroke-width:2;opacity:.9}}
.invthem{{stroke:var(--them);stroke-width:2;opacity:.9}}
.lold{{fill:none;stroke:var(--old);stroke-width:2;stroke-linejoin:round;opacity:.85}}
.anew{{fill:var(--new);opacity:.12}} .aold{{fill:var(--old);opacity:.10}}
.mark{{stroke:var(--accent);stroke-width:1;stroke-dasharray:2 3;opacity:.65}}
.markdot{{fill:var(--new);stroke:var(--surface);stroke-width:2}}
.marklab{{fill:var(--accent);font-size:11px;font-weight:600}}
.legend{{display:flex;flex-wrap:wrap;gap:18px;margin:10px 0 4px 4px;font-family:"Archivo",sans-serif;font-size:.78rem;color:var(--ink-soft)}}
.legend i{{display:inline-block;width:16px;height:12px;border-radius:2px;margin-right:7px;vertical-align:middle}}
.legend .note{{color:var(--ink-dim);font-style:italic}}
.legend i.sq{{width:12px;height:12px;border-radius:2px}}
td.hot{{color:var(--old);font-weight:600;background:var(--accent-w)}}
th.rh{{text-align:left;font-family:"Archivo",sans-serif;font-weight:600;color:var(--ink)}}
.snaps{{display:grid;gap:1px;background:var(--rule);border:1px solid var(--rule);
grid-template-columns:repeat(auto-fit,minmax(215px,1fr));margin-top:22px}}
.snap{{background:var(--surface);padding:16px 15px}}
.snap-h{{display:flex;align-items:center;gap:8px;margin-bottom:10px}}
.badge{{display:inline-flex;align-items:center;justify-content:center;width:20px;height:20px;
background:var(--accent);color:var(--ground);font-size:.72rem;font-weight:700;border-radius:2px}}
.snap-t{{font-family:"IBM Plex Mono",monospace;font-size:.82rem;color:var(--ink-soft)}}
.snap-rate{{margin-left:auto;font-family:"IBM Plex Mono",monospace;font-size:.82rem;font-weight:600;color:var(--old)}}
.bar{{display:flex;height:9px;border-radius:2px;overflow:hidden;background:var(--sunken);gap:2px}}
.seg{{display:block;height:100%}}
.chips{{display:flex;flex-wrap:wrap;gap:8px;margin:9px 0 12px;font-family:"Archivo",sans-serif;font-size:.73rem;color:var(--ink-soft)}}
.chips i{{display:inline-block;width:8px;height:8px;border-radius:2px;margin-right:5px}}
dl{{display:grid;grid-template-columns:auto auto;gap:3px 10px;margin:0;font-family:"IBM Plex Mono",monospace;font-size:.76rem}}
dt{{color:var(--ink-dim)}} dd{{margin:0;text-align:right;font-variant-numeric:tabular-nums}}
.choice{{color:var(--old);font-weight:600}}
table{{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums;margin-top:10px}}
th,td{{font-family:"IBM Plex Mono",monospace;font-size:.79rem;text-align:right;padding:7px 10px;
border-bottom:1px solid var(--rule-soft);white-space:nowrap}}
th{{font-family:"Archivo",sans-serif;font-weight:600;font-size:.72rem;color:var(--ink-soft);background:var(--sunken)}}
td:first-child,th:first-child{{text-align:left}}
tr.before td:first-child{{color:var(--old)}} tr.after td:first-child{{color:var(--new)}}
.callout{{border-left:3px solid var(--accent);background:var(--surface);padding:18px 22px;margin:24px 0}}
.cap{{font-size:.84rem;color:var(--ink-dim);margin-top:10px;max-width:68ch;line-height:1.5}}
footer{{margin-top:70px;padding-top:18px;border-top:1px solid var(--rule);font-size:.83rem;color:var(--ink-dim)}}
</style>

<div class="wrap">
<header>
  <div class="kicker"><span class="dot"></span><span class="lab">White / nuke / reinforcements mirror &middot; before and after</span></div>
  <h1>Still a loss &mdash; but it stopped being a deletion.</h1>
  <p class="dek">The defence-only bot against the shipped attacking bot, same team and same
  loadout &mdash; the first defence-only build against the current one. Nothing gameplay-affecting
  varies in this matchup, so each line is the whole story of its build rather than a sample.</p>
  <div class="stats">
    <div class="stat"><span class="stat-n">+{100*LONGER:.0f}<span class="u">%</span></span>
      <span class="stat-t">Longer before the castle falls &mdash; {OLD_END:.0f}s to {NEW_END:.0f}s.</span></div>
    <div class="stat"><span class="stat-n">{SHALLOWER:.1f}<span class="u">&times;</span></span>
      <span class="stat-t">Shallower worst moment: the steepest loss goes from
      {old_worst[0]:,.0f} to {new_worst[0]:,.0f} HP/s.</span></div>
    <div class="stat"><span class="stat-n">&minus;{100*(1-new_cost/old_cost):.0f}<span class="u">%</span></span>
      <span class="stat-t">Spent on repairs: ${old_cost:,.0f} down to ${new_cost:,.0f}.</span></div>
    <div class="stat"><span class="stat-n">{NEW_PEAK_DPS/OLD_PEAK_DPS:.1f}<span class="u">&times;</span></span>
      <span class="stat-t">Peak incoming damage, {OLD_PEAK_DPS:,.0f} to {NEW_PEAK_DPS:,.0f} &mdash;
      it now survives to meet a far worse army.</span></div>
  </div>
</header>

<section>
  <div class="shead"><span class="n">01</span><h2>Castle health, both builds</h2></div>
  <figure>
    <p class="figtitle">Our castle health</p>
    <p class="figsub">one shared clock; markers A&ndash;E are the new build's steepest loss windows</p>
    <div class="scroll"><svg viewBox="0 0 {W} {PADT+H_HP+16}" role="img"
      aria-label="Castle health for both builds on one clock. The original build spikes high on repair spending then collapses early; the current build peaks lower and grinds down over a substantially longer game.">
      {hp_grid}{markers}
      <path class="lold" d="{path(OLD, yhp, 'hp')}"/>
      <path class="lnew" d="{path(NEW, yhp, 'hp')}"/>
    </svg></div>
    <div class="legend">
      <span><i style="background:var(--new)"></i>current build</span>
      <span><i style="background:var(--old)"></i>first defence-only build</span>
      <span><i style="background:var(--accent)"></i>A&ndash;E steepest windows</span>
    </div>
  </figure>

  <figure style="margin-top:18px">
    <p class="figtitle">Incoming damage per second</p>
    <p class="figsub">what the enemy army on the field could do to the castle, unblocked</p>
    <div class="scroll"><svg viewBox="0 0 {W} {PADT+H_DPS+30}" role="img"
      aria-label="Incoming damage per second for both builds. The original build ends before the enemy army compounds; the current build survives into a period of far higher incoming damage.">
      {dps_grid}
      <path class="lold" d="{path(OLD, ydps, 'enemy_dps')}"/>
      <path class="lnew" d="{path(NEW, ydps, 'enemy_dps')}"/>
      {x_axis}
    </svg></div>
    <div class="legend"><span><i style="background:var(--new)"></i>current build</span>
      <span><i style="background:var(--old)"></i>first defence-only build</span></div>
  </figure>
  <p class="cap">Two charts rather than one with two vertical scales: health peaks at
  {max(OLD_PEAK_HP, NEW_PEAK_HP):,.0f} and incoming damage at {max(OLD_PEAK_DPS, NEW_PEAK_DPS):,.0f},
  so a shared axis would flatten one and invent a crossing point that means nothing.</p>

  <div class="callout">
    <h3>The shape changed, not just the numbers</h3>
    <p>The old line is a cliff &mdash; a repair-funded spike to {OLD_PEAK_HP:,.0f} and then
    {abs(old_worst[0]):,.0f} health gone per second. The current line never gets that high and never
    falls that fast: its worst moment is <strong>{SHALLOWER:.1f} times shallower</strong>
    ({abs(new_worst[0]):,.0f} HP/s), and it grinds down through several similar windows instead of
    being deleted in one.</p>
    <p><strong>Peak incoming damage went the other way</strong> &mdash; {OLD_PEAK_DPS:,.0f} to
    {NEW_PEAK_DPS:,.0f}, {NEW_PEAK_DPS/OLD_PEAK_DPS:.1f} times worse. An earlier draft of this page
    read that as the wipes landing, back when the number fell. It rose because the bot now lives
    {NEW_END-OLD_END:.0f} seconds longer, and the opponent's economy compounds through every one of
    them. Surviving longer means meeting a bigger army, so peak DPS is a clock reading, not a
    scoreboard &mdash; the honest measure is the slope of our health, and that is
    {SHALLOWER:.1f} times shallower.</p>
  </div>
</section>

<section>
  <div class="shead"><span class="n">02</span><h2>The army at each collapse, now</h2></div>
  <div class="prose"><p>Five steepest two-second windows of the new build.</p></div>
  <div class="snaps">{cards}</div>
  <p class="cap">Tiers {TOP_TIERS[0]} and {TOP_TIERS[1]} are {TOP_SHARE:.0f}% of every body in
  these windows &mdash; the threat is still a cheap swarm, not a few expensive units, which is why
  one wiper can be worth so much more than its price. Note that the worst window is now the
  <em>last</em> one: the earlier build's collapse came first and everything after it was falling.</p>
</section>

<section>
  <div class="shead"><span class="n">03</span><h2>The economy, both players</h2></div>
  <div class="prose">
    <p>Money on hand through the current build\'s game. The ticks along the bottom mark
    investments &mdash; the sawtooth is the economy laddering up, not spending. Investment 8 is
    Armageddon, and Armageddon is a guaranteed win, so this chart is the game.</p>
  </div>
  <figure>
    <p class="figtitle">Money on hand</p>
    <p class="figsub">us against the shipped attacking bot, same team, same loadout</p>
    <div class="scroll"><svg viewBox="0 0 {W} {PADT+H_ECON+30}" role="img"
      aria-label="Money on hand for both players. The two ladders track each other closely through seven investments, then the opponent takes an eighth and pulls away.">
      {econ_grid}{econ_marks}
      <path class="lthem" d="{econ_them}"/>
      <path class="lnew" d="{econ_us}"/>
      {econ_x}
    </svg></div>
    <div class="legend">
      <span><i style="background:var(--new)"></i>us (defence only)</span>
      <span><i style="background:var(--them)"></i>them (shipped attacking bot)</span>
      <span class="note">ticks along the axis mark each player&rsquo;s investments, in their own colour</span>
    </div>
  </figure>

  <div class="callout">
    <h3>It wins the economic race for {LOCKSTEP} rungs and loses it on the eighth</h3>
    <p>The two ladders are <strong>lock-step identical</strong> through investment {OUR_LAST}
    &mdash; the same {LOCKSTEP} rungs at the same seconds, and we reach the last shared one
    <strong>{THEM_INV[OUR_LAST]-US_INV[OUR_LAST]:.0f} seconds ahead</strong> of them
    ({US_INV[OUR_LAST]:.0f}s against {THEM_INV[OUR_LAST]:.0f}s). The premise of the whole
    defence-only design &mdash; defend cheaply, keep pace, reach Armageddon &mdash; is working
    exactly as intended, right up to the rung that decides the game.</p>
    <p>Then they buy investment <strong>{THEIR_LAST}</strong> at {THEM_INV[THEIR_LAST]:.0f}s, their
    income triples to ${LAST['p2income']:,.0f}/s, and we never buy it at all. We die
    {NEW_END-THEM_INV[THEIR_LAST]:.0f} seconds later.</p>
    <p>The eighth investment costs <strong>${INV8_PRICE:,}</strong>. In the
    {NEW_END-RACE_FROM:.0f} seconds we had, we earned ${EARNED_LATE:,.0f} and spent
    <strong>${SPEND_LATE:,.0f} of it on defence</strong> &mdash; against the
    ${THEIR_SPEND_LATE:,.0f} they spent attacking. Our cash peaked at ${PEAK_CASH:,.0f}:
    <strong>{100*PEAK_CASH/INV8_PRICE:.0f}% of the price</strong>. The bot never chose to lose
    this race; it was never once asked whether to buy the win condition instead of the next
    blocker.</p>
  </div>
  <p class="cap">Our line sits above $5,000 for {CASH_PCT:.0f}% of the game, up from 16% before the
  repair fix. That is money not being burned on $8,837 repairs &mdash; but the defensive comparison
  prices a blocker against <em>dying</em> and never against the ${INV8_PRICE:,} that ends the game,
  so a rung it could reach stays invisible to it.</p>
</section>

<section>
  <div class="shead"><span class="n">04</span><h2>Which arm the bot is in</h2></div>
  <div class="prose">
    <p>The same clock again, as a band. Four groups: <em>idle</em> is not engaging at all,
    <em>holding</em> is engaged but deliberately not spending, <em>spending</em> is buying
    blockers or a wipe, and <em>critical</em> is inside its own death window with the economics
    switched off.</p>
  </div>
  <figure>
    <p class="figtitle">Decision arm through the game</p>
    <p class="figsub">what the defensive comparison chose, moment to moment</p>
    <div class="scroll"><svg viewBox="0 0 {W} {PADT+H_RIB+26}" role="img"
      aria-label="Decision arm over time: idle through the early economy, then alternating between holding, spending and critical once the first real wave arrives.">
      {rib}{rib_x}
    </svg></div>
    <div class="legend">{rib_legend}</div>
  </figure>

  <div class="prose" style="margin-top:26px">
    <p>Broken out by investment level, which is where the story is:</p>
  </div>
  <div class="scroll"><table>
    <thead><tr><th>inv</th><th>lasted</th><th>watch</th><th>wait</th><th>outmatched</th>
      <th>block</th><th>wipe</th><th>critical</th></tr></thead>
    <tbody>{inv_rows}</tbody>
  </table></div>

  <div class="callout">
    <h3>Investment {LAST_L} is where the game is decided &mdash; and it is choosing again</h3>
    <p>Through investments 3 to 5 the bot is in <em>watch</em> almost the whole time &mdash;
    correctly, nothing is threatening it. At investment 6 it fights its first real engagement and
    spends most of it <em>holding</em>, which is the branch working as designed: it lets the wave
    form and answers it.</p>
    <p>Investment {LAST_L} is then <strong>{LAST_SPAN:.0f} seconds long</strong>, half the game, and
    the whole outcome sits inside it. The previous build spent 68% of that level in
    <em>critical</em> &mdash; not a decision but the absence of one, the survival law holding it
    permanently inside its own death window so the dollar comparison never ran. It is now
    <strong>{LP['critical']:.0f}%</strong>, with {LP['block']:.0f}% <em>block</em>,
    {LP['outmatched']:.0f}% <em>outmatched</em> and {LP['wipe']:.0f}% <em>wipe</em> &mdash; wipes
    firing {LP['wipe']/2:.0f} times as often as before, at exactly the point where one unit can
    clear the biggest army it will ever face.</p>
    <p>That is the intended machinery finally running under load. It is also still a loss, which
    is the point worth keeping: the comparison now works and picks the best available option, and
    the best available option is not good enough.</p>
  </div>
  <p class="cap">Across the whole game: {100*gshare['idle']/gtotal:.0f}% idle,
  {100*gshare['holding']/gtotal:.0f}% holding, {100*gshare['spending']/gtotal:.0f}% spending,
  {100*gshare['critical']/gtotal:.0f}% critical &mdash; so only
  {100*gshare['spending']/gtotal:.0f}% of the game is spent actually buying anything, and more
  than a quarter of it is spent past the point where the bot still has a choice.</p>
</section>

<section>
  <div class="shead"><span class="n">05</span><h2>Where the money went</h2></div>
  <div class="prose">
    <p>The old build bought six repairs in seven seconds, each one raising max health, which drops
    time-to-death back under the threshold a second later. The last cost <strong>$8,837 for 0.89
    seconds of life</strong>. The price check refuses it.</p>
  </div>
  <div class="scroll"><table>
    <thead><tr><th>build</th><th>time</th><th>repair</th><th>price</th></tr></thead>
    <tbody>{rep_rows}</tbody>
  </table></div>
  <p class="cap">Repair spend ${old_cost:,.0f} &rarr; ${new_cost:,.0f}. The bot still repairs the
  cheap ones, which are worth many seconds each; it stops when a repair costs more than the time
  it returns.</p>
</section>

<footer>
  Castle Defense engine &middot; two deterministic games, dumped every 3 ticks &middot;
  defence-only bot in seat 1 against the shipped bot in seat 2 &middot;
  first build: {OLD_END:.0f}s &middot;
  current build: {NEW_END:.0f}s, {OUR_LAST} investments to their {THEIR_LAST}
</footer>
</div>
"""

io.open(OUT, "w", encoding="utf-8").write(HTML)
print(f"wrote {OUT} ({len(HTML)} bytes)")
print(f"  before {OLD[-1]['t']:.0f}s worst {old_worst[0]:,.0f} HP/s repairs ${old_cost:,.0f}")
print(f"  after  {NEW[-1]['t']:.0f}s worst {min(s[0] for s in snaps):,.0f} HP/s repairs ${new_cost:,.0f}")
