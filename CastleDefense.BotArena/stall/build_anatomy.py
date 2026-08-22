"""Builds the game-anatomy report: castle health over time for both builds on one shared
clock, incoming pressure below it (never a second y-scale), and the army behind each collapse.

Run from this directory. Reads mirror_dump.csv (the original defence-only build) and
mirror_dump_new.csv (with the priced repair and the seconds-valued option comparison).
"""
import csv, io, math, os

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "mirror_anatomy.html")


def load(name):
    rows = list(csv.DictReader(io.open(os.path.join(HERE, name), encoding="utf-8")))
    for r in rows:
        for k in ("tick", "hp", "maxhp", "money", "inv", "own", "enemy",
                  "enemy_dps", "enemy_swings", "enemy_value",
                  "t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8"):
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


OLD, NEW = load("mirror_dump.csv"), load("mirror_dump_new.csv")
old_ev, old_cost = repairs(OLD)
new_ev, new_cost = repairs(NEW)
snaps = steep(NEW)
old_worst = min(steep(OLD), key=lambda x: x[0])

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
--new:#2a78d6;--old:#eb6834;--ord-1:#86b6ef;--ord-2:#2a78d6;--ord-3:#104281}}
@media (prefers-color-scheme:dark){{:root:not([data-theme="light"]){{--ground:#131210;--surface:#1c1a16;
--sunken:#242119;--ink:#efebe1;--ink-soft:#b3ab99;--ink-dim:#837b69;--rule:#343026;--rule-soft:#282419;
--accent:#d6a741;--accent-w:#3a2e14;--new:#3987e5;--old:#d95926;--ord-1:#b7d3f6;--ord-2:#5598e7;--ord-3:#184f95}}}}
:root[data-theme="dark"]{{--ground:#131210;--surface:#1c1a16;--sunken:#242119;--ink:#efebe1;
--ink-soft:#b3ab99;--ink-dim:#837b69;--rule:#343026;--rule-soft:#282419;--accent:#d6a741;
--accent-w:#3a2e14;--new:#3987e5;--old:#d95926;--ord-1:#b7d3f6;--ord-2:#5598e7;--ord-3:#184f95}}
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
.lold{{fill:none;stroke:var(--old);stroke-width:2;stroke-linejoin:round;opacity:.85}}
.anew{{fill:var(--new);opacity:.12}} .aold{{fill:var(--old);opacity:.10}}
.mark{{stroke:var(--accent);stroke-width:1;stroke-dasharray:2 3;opacity:.65}}
.markdot{{fill:var(--new);stroke:var(--surface);stroke-width:2}}
.marklab{{fill:var(--accent);font-size:11px;font-weight:600}}
.legend{{display:flex;flex-wrap:wrap;gap:18px;margin:10px 0 4px 4px;font-family:"Archivo",sans-serif;font-size:.78rem;color:var(--ink-soft)}}
.legend i{{display:inline-block;width:16px;height:3px;border-radius:2px;margin-right:7px;vertical-align:middle}}
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
  loadout, before and after pricing repairs by the seconds they buy. Nothing gameplay-affecting
  varies in this matchup, so each line is the whole story of its build rather than a sample.</p>
  <div class="stats">
    <div class="stat"><span class="stat-n">+29<span class="u">s</span></span>
      <span class="stat-t">Longer before the castle falls &mdash; {OLD[-1]['t']:.0f}s to {NEW[-1]['t']:.0f}s.</span></div>
    <div class="stat"><span class="stat-n">3<span class="u">&times;</span></span>
      <span class="stat-t">Shallower worst moment: the steepest loss goes from
      {old_worst[0]:,.0f} to {snaps[0][0] if False else min(s[0] for s in snaps):,.0f} HP/s.</span></div>
    <div class="stat"><span class="stat-n">&minus;78<span class="u">%</span></span>
      <span class="stat-t">Spent on repairs: ${old_cost:,.0f} down to ${new_cost:,.0f}.</span></div>
    <div class="stat"><span class="stat-n">&minus;34<span class="u">%</span></span>
      <span class="stat-t">Peak incoming damage, {max(r['enemy_dps'] for r in OLD):,.0f} to
      {max(r['enemy_dps'] for r in NEW):,.0f} &mdash; the wipes are landing.</span></div>
  </div>
</header>

<section>
  <div class="shead"><span class="n">01</span><h2>Castle health, both builds</h2></div>
  <figure>
    <p class="figtitle">Our castle health</p>
    <p class="figsub">one shared clock; markers A&ndash;E are the new build's steepest loss windows</p>
    <div class="scroll"><svg viewBox="0 0 {W} {PADT+H_HP+16}" role="img"
      aria-label="Castle health for both builds. The old build spikes to 78000 then collapses at 193 seconds; the new build peaks lower and declines gradually to 222 seconds.">
      {hp_grid}{markers}
      <path class="lold" d="{path(OLD, yhp, 'hp')}"/>
      <path class="lnew" d="{path(NEW, yhp, 'hp')}"/>
    </svg></div>
    <div class="legend">
      <span><i style="background:var(--new)"></i>after &mdash; priced repair, seconds-valued options</span>
      <span><i style="background:var(--old)"></i>before</span>
      <span><i style="background:var(--accent)"></i>A&ndash;E steepest windows</span>
    </div>
  </figure>

  <figure style="margin-top:18px">
    <p class="figtitle">Incoming damage per second</p>
    <p class="figsub">what the enemy army on the field could do to the castle, unblocked</p>
    <div class="scroll"><svg viewBox="0 0 {W} {PADT+H_DPS+30}" role="img"
      aria-label="Incoming damage per second for both builds. The old build peaks near 30000; the new build peaks near 19750.">
      {dps_grid}
      <path class="lold" d="{path(OLD, ydps, 'enemy_dps')}"/>
      <path class="lnew" d="{path(NEW, ydps, 'enemy_dps')}"/>
      {x_axis}
    </svg></div>
    <div class="legend"><span><i style="background:var(--new)"></i>after</span>
      <span><i style="background:var(--old)"></i>before</span></div>
  </figure>
  <p class="cap">Two charts rather than one with two vertical scales: health peaks at 78,000 and
  incoming damage at 29,990, so a shared axis would flatten one and invent a crossing point that
  means nothing.</p>

  <div class="callout">
    <h3>The shape changed, not just the numbers</h3>
    <p>The old line is a cliff &mdash; a tall repair-funded spike to 78,000 and then 24,045 health
    gone in a single second. The new line never gets that high and never falls that fast: its
    worst moment is <strong>three times shallower</strong>, and it grinds down through three
    similar windows instead of being deleted in one.</p>
    <p>Peak incoming damage also dropped by a third, from 29,990 to 19,750. That is the wipes
    landing &mdash; the enemy army is being cut down rather than accumulating unopposed.</p>
  </div>
</section>

<section>
  <div class="shead"><span class="n">02</span><h2>The army at each collapse, now</h2></div>
  <div class="prose"><p>Five steepest two-second windows of the new build.</p></div>
  <div class="snaps">{cards}</div>
  <p class="cap">Still overwhelmingly tier 4. The composition of the threat has not changed &mdash;
  what changed is that the bot now trades against it instead of banking health it cannot keep.</p>
</section>

<section>
  <div class="shead"><span class="n">03</span><h2>Where the money went</h2></div>
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
  before: {OLD[-1]['t']:.0f}s, {OLD[-1]['inv']:.0f} investments &middot;
  after: {NEW[-1]['t']:.0f}s, {NEW[-1]['inv']:.0f} investments
</footer>
</div>
"""

io.open(OUT, "w", encoding="utf-8").write(HTML)
print(f"wrote {OUT} ({len(HTML)} bytes)")
print(f"  before {OLD[-1]['t']:.0f}s worst {old_worst[0]:,.0f} HP/s repairs ${old_cost:,.0f}")
print(f"  after  {NEW[-1]['t']:.0f}s worst {min(s[0] for s in snaps):,.0f} HP/s repairs ${new_cost:,.0f}")
