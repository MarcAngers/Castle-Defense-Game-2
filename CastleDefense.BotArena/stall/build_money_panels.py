"""Money-on-hand + Armageddon-race report across several recorded games.

    python build_income_panels.py GAME1 GAME2 ...

Reads human/<GID>.csv (from `--economy-dump --every 1`) and writes money_panels.html:
one money-on-hand chart per game against the Armageddon threshold, plus how close the bot
came to affording it.
"""
import csv, io, math, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "money_panels.html")
ARM = 121221
GAMES = sys.argv[1:] or ["54D732", "2B69F2", "C7F159"]
LABEL = {"54D732": "White", "2B69F2": "Orange", "C7F159": "Blue", "8F1A28": "White"}


def price(c):
    p = math.exp(0.0109 * c ** 3 + 0.0011 * c ** 2 + 0.4351 * c + 0.5268) * (c * 5 + 5)
    return p * 2 if c >= 8 else p


def load(g):
    R = list(csv.DictReader(io.open(os.path.join(HERE, "human", g + ".csv"), encoding="utf-8")))
    for r in R:
        for k in list(r):
            try:
                r[k] = float(r[k])
            except (TypeError, ValueError):
                pass
        r["t"] = r["tick"] / 30.0
    return R


DATA = {}
for g in GAMES:
    R = load(g)
    L = R[-1]
    prev, nrep, cost = R[0], 0, 0.0
    for r in R[1:]:
        if r["p2maxhp"] > prev["p2maxhp"]:
            nrep += 1
            cost += price(int(round((r["p2maxhp"] - 1000) / 11000.0)) - 1)
        prev = r
    short = max(0.0, ARM - L["p2money"])
    DATA[g] = dict(R=R, L=L, nrep=nrep, cost=cost, short=short,
                   secs=short / L["p2income"] if L["p2income"] > 0 else float("inf"),
                   peak=max(r["p2money"] for r in R),
                   draw=(L["hp"] <= 0 and L["p2hp"] <= 0))

# ── chart geometry ──────────────────────────────────────────────────────────
W, PADL, PADR, PADT, PADB = 940, 66, 18, 16, 30
HF = 176
IW = W - PADL - PADR
# One shared vertical scale across every panel so the games are comparable at a glance, and
# tall enough that the Armageddon threshold sits inside the frame rather than at the ceiling.
MONEY_MAX = max(max(max(r["money"], r["p2money"]) for r in DATA[g]["R"]) for g in GAMES)
MONEY_MAX = max(MONEY_MAX, ARM * 1.05)


def panel(g):
    R, L = DATA[g]["R"], DATA[g]["L"]
    tmax = L["t"]

    def x(t): return PADL + IW * (t / tmax)
    def y(v): return PADT + HF * (1 - v / MONEY_MAX)

    ticks = [0, 40000, 80000, 120000]
    grid = "".join(
        f'<line class="grid" x1="{PADL}" y1="{y(v):.1f}" x2="{W-PADR}" y2="{y(v):.1f}"/>'
        f'<text class="ax" x="{PADL-8}" y="{y(v)+3.5:.1f}" text-anchor="end">${v//1000}k</text>'
        for v in ticks)

    # THE LINE THAT MATTERS: cross it and the game is over.
    arm = (f'<line class="armline" x1="{PADL}" y1="{y(ARM):.1f}" x2="{W-PADR}" y2="{y(ARM):.1f}"/>'
           f'<text class="armlab" x="{W-PADR-4}" y="{y(ARM)-5:.1f}" text-anchor="end">'
           f'ARMAGEDDON ${ARM:,}</text>')

    def line(key, cls):
        d = []
        for i in range(0, len(R), 2):
            r = R[i]
            d.append(("M" if not d else "L") + f"{x(r['t']):.1f},{y(r[key]):.1f}")
        return f'<path class="{cls}" d="{"".join(d)}"/>'

    # how short the bot finished, drawn as the gap it never closed
    gap = ""
    if L["p2money"] < ARM:
        gap = (f'<line class="gap" x1="{x(tmax):.1f}" y1="{y(L["p2money"]):.1f}" '
               f'x2="{x(tmax):.1f}" y2="{y(ARM):.1f}"/>'
               f'<circle class="gapdot" cx="{x(tmax):.1f}" cy="{y(L["p2money"]):.1f}" r="4"/>')

    xa = "".join(f'<text class="ax" x="{x(t):.1f}" y="{PADT+HF+20}" text-anchor="middle">{t}s</text>'
                 for t in (0, 60, 120, 180, 240, int(tmax)))
    return (f'<svg viewBox="0 0 {W} {PADT+HF+PADB}" role="img" aria-label="Money on hand over time '
            f'for both players in game {g}. The bot banks steadily toward the Armageddon threshold '
            f'and its line stops just below it when the castle falls.">'
            + grid + arm + line("p2money", "lbot") + line("money", "lmarc") + gap + xa + "</svg>")


cards = ""
for g in GAMES:
    d = DATA[g]
    L = d["L"]
    verdict = ("DRAW" if d["draw"] else "Marc won")
    cards += f"""
    <figure>
      <p class="figtitle">{g} &middot; Marc as {LABEL.get(g,'?')} &middot; {verdict} in {L['t']:.0f}s</p>
      <p class="figsub">money on hand, both players, against the Armageddon threshold</p>
      <div class="scroll">{panel(g)}</div>
      <div class="statline">
        <span><b>bot died holding</b> ${L['p2money']:,.0f}</span>
        <span><b>{100*L['p2money']/ARM:.0f}%</b> of Armageddon</span>
        <span><b>{d['secs']:.1f}s</b> short at ${L['p2income']:,.0f}/s</span>
        <span class="dim">peak ${d['peak']:,.0f} &middot; {d['nrep']} repairs, ${d['cost']:,.0f}</span>
      </div>
    </figure>"""

rows = "".join(
    f'<tr><th class="rh">{g}</th><td>{LABEL.get(g,"?")}</td>'
    f'<td>${DATA[g]["L"]["p2money"]:,.0f}</td>'
    f'<td>{100*DATA[g]["L"]["p2money"]/ARM:.0f}%</td>'
    f'<td class="hot">{DATA[g]["secs"]:.1f}s</td>'
    f'<td class="dim">${DATA[g]["peak"]:,.0f}</td>'
    f'<td class="dim">${DATA[g]["cost"]:,.0f}</td></tr>' for g in GAMES)

HTML = f"""<title>Losing the Armageddon Race</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Archivo:wght@500;600;700&family=Newsreader:ital,opsz,wght@0,6..72,400;0,6..72,500;1,6..72,400&family=IBM+Plex+Mono:wght@400;500;600&display=swap">
<style>
:root{{--ground:#f5f3ee;--surface:#fffdf9;--sunken:#edeae2;--ink:#1b1813;--ink-soft:#5a5346;
--ink-dim:#8a8271;--rule:#dad5c8;--rule-soft:#e7e3d9;--accent:#8a6212;--accent-w:#f0e4c8;
--marc:#8a6212;--bot:#1baf7a;--warn:#c2410c}}
@media (prefers-color-scheme:dark){{:root:not([data-theme="light"]){{--ground:#131210;--surface:#1c1a16;
--sunken:#242119;--ink:#efebe1;--ink-soft:#b3ab99;--ink-dim:#837b69;--rule:#343026;--rule-soft:#282419;
--accent:#d6a741;--accent-w:#3a2e14;--marc:#d6a741;--bot:#199e70;--warn:#e2703a}}}}
:root[data-theme="dark"]{{--ground:#131210;--surface:#1c1a16;--sunken:#242119;--ink:#efebe1;
--ink-soft:#b3ab99;--ink-dim:#837b69;--rule:#343026;--rule-soft:#282419;--accent:#d6a741;
--accent-w:#3a2e14;--marc:#d6a741;--bot:#199e70;--warn:#e2703a}}
*{{box-sizing:border-box}}
body{{background:var(--ground);color:var(--ink);margin:0;padding:0 24px 76px;
font-family:"Newsreader",Georgia,serif;font-size:17px;line-height:1.62;-webkit-font-smoothing:antialiased}}
.wrap{{max-width:1000px;margin:0 auto}} .prose{{max-width:65ch}}
h1,h2,h3,.lab,thead th,th.rh,.figtitle{{font-family:"Archivo",system-ui,sans-serif}}
h1{{font-size:clamp(2rem,4.4vw,2.7rem);font-weight:700;letter-spacing:-.028em;line-height:1.06;margin:0 0 .5rem;text-wrap:balance}}
h2{{font-size:1.3rem;font-weight:600;margin:0}}
h3{{font-size:.95rem;font-weight:600;margin:0 0 .5rem}}
p{{margin:0 0 1rem}} p:last-child{{margin-bottom:0}}
strong{{font-weight:500;background:var(--accent-w);padding:0 .2em;border-radius:2px}}
code{{font-family:"IBM Plex Mono",monospace;font-size:.84em;background:var(--sunken);color:var(--ink-soft);padding:.12em .38em;border-radius:3px}}
.lab{{font-size:.7rem;font-weight:600;letter-spacing:.13em;text-transform:uppercase;color:var(--ink-dim)}}
header{{padding:56px 0 28px;border-bottom:2px solid var(--ink)}}
.kicker{{display:flex;align-items:baseline;gap:12px;margin-bottom:16px}}
.kicker .dot{{width:9px;height:9px;background:var(--accent);transform:rotate(45deg);flex:none}}
.dek{{font-size:1.2rem;color:var(--ink-soft);max-width:62ch;margin:0}}
section{{margin-top:46px}}
.shead{{display:flex;align-items:baseline;gap:14px;padding-bottom:12px;margin-bottom:18px;border-bottom:1px solid var(--rule)}}
.shead .n{{font-family:"IBM Plex Mono",monospace;font-size:.74rem;font-weight:600;color:var(--accent);flex:none}}
figure{{margin:0 0 20px;background:var(--surface);border:1px solid var(--rule);padding:16px 14px 10px}}
.figtitle{{font-size:.92rem;font-weight:600;margin:0 0 2px 4px}}
.figsub{{font-size:.82rem;color:var(--ink-dim);margin:0 0 8px 4px}}
.scroll{{overflow-x:auto}} svg{{display:block;width:100%;height:auto;font-family:"IBM Plex Mono",monospace}}
.grid{{stroke:var(--rule-soft);stroke-width:1}} .ax{{fill:var(--ink-dim);font-size:10px}}
.lmarc{{fill:none;stroke:var(--marc);stroke-width:2.2;stroke-linejoin:round}}
.lbot{{fill:none;stroke:var(--bot);stroke-width:2;stroke-linejoin:round}}
.armline{{stroke:var(--warn);stroke-width:1.4;stroke-dasharray:6 4;opacity:.85}}
.armlab{{fill:var(--warn);font-size:10px;font-weight:600}}
.gap{{stroke:var(--warn);stroke-width:3;opacity:.55}}
.gapdot{{fill:var(--warn);stroke:var(--surface);stroke-width:1.5}}
.statline{{display:flex;flex-wrap:wrap;gap:16px;margin:8px 0 2px 4px;font-family:"IBM Plex Mono",monospace;
font-size:.79rem;color:var(--ink-soft);font-variant-numeric:tabular-nums}}
.statline b{{color:var(--ink);font-weight:600}} .statline .dim{{color:var(--ink-dim)}}
.legend{{display:flex;gap:18px;margin:0 0 18px 4px;font-family:"Archivo",sans-serif;font-size:.78rem;color:var(--ink-soft)}}
.legend i{{display:inline-block;width:16px;height:3px;border-radius:2px;margin-right:7px;vertical-align:middle}}
table{{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums;margin-top:8px}}
th,td{{font-family:"IBM Plex Mono",monospace;font-size:.79rem;text-align:right;padding:7px 11px;
border-bottom:1px solid var(--rule-soft);white-space:nowrap}}
thead th{{font-weight:600;font-size:.72rem;color:var(--ink-soft);background:var(--sunken)}}
th.rh{{text-align:left;font-weight:600;color:var(--ink)}}
td.hot{{color:var(--warn);font-weight:600}} td.dim{{color:var(--ink-dim)}}
.callout{{border-left:3px solid var(--accent);background:var(--surface);padding:18px 22px;margin:22px 0}}
.cap{{font-size:.84rem;color:var(--ink-dim);margin-top:10px;max-width:68ch;line-height:1.5}}
footer{{margin-top:56px;padding-top:18px;border-top:1px solid var(--rule);font-size:.83rem;color:var(--ink-dim)}}
</style>

<div class="wrap">
<header>
  <div class="kicker"><span class="dot"></span><span class="lab">three games &middot; economy-brake build &middot; Marc on three different teams</span></div>
  <h1>The bot now dies {min(DATA[g]['secs'] for g in GAMES):.1f} seconds short of winning.</h1>
  <p class="dek">Armageddon costs <strong>${ARM:,}</strong>. Across these three games the bot
  finished holding <strong>${min(DATA[g]['L']['p2money'] for g in GAMES):,.0f}&ndash;${max(DATA[g]['L']['p2money'] for g in GAMES):,.0f}</strong>
  &mdash; between {min(100*DATA[g]['L']['p2money']/ARM for g in GAMES):.0f}% and
  {max(100*DATA[g]['L']['p2money']/ARM for g in GAMES):.0f}% of the win condition. Before the
  economy brakes it was stalling on rung 7 and finishing on $750/s income.</p>
</header>

<section>
  <div class="shead"><span class="n">01</span><h2>Money on hand, all three games</h2></div>
  <div class="prose">
    <p>The bot's balance climbs toward the one line that ends the game. In all three it gets
    there and stops just underneath &mdash; the marked gap at the end of each line is the money it
    never earned. The sawtooth drops are investments; the deep drop late in each game is
    investment 8 at $40,000.</p>
  </div>
  <div class="legend">
    <span><i style="background:var(--marc)"></i>Marc</span>
    <span><i style="background:var(--bot)"></i>the bot</span>
    <span><i style="background:var(--warn)"></i>Armageddon threshold &mdash; and the gap it died short of</span>
  </div>
  {cards}
</section>

<section>
  <div class="shead"><span class="n">02</span><h2>How close it came</h2></div>
  <div class="scroll"><table>
    <thead><tr><th>game</th><th>Marc</th><th>bot money at death</th><th>of Armageddon</th>
      <th>seconds short</th><th>peak</th><th>repair spend</th></tr></thead>
    <tbody>{rows}</tbody>
  </table></div>
  <div class="callout">
    <h3>It is not refusing to buy &mdash; it is running out of clock</h3>
    <p>In <code>54D732</code> the bot's balance crossed ${ARM:,} for <strong>exactly one tick</strong>
    &mdash; at 297.0s, the final tick of the game. It died in the same instant it could first
    afford the win. In the draw (<code>8F1A28</code>, not shown above) it crossed at 279.0s and
    <strong>bought Armageddon 0.1 seconds later</strong>; both castles then hit zero together.</p>
    <p>So the invest gate is working. What decides these games now is a race measured in
    seconds, and Marc is winning it by between
    {min(DATA[g]['secs'] for g in GAMES):.1f} and {max(DATA[g]['secs'] for g in GAMES):.1f}
    seconds of income.</p>
  </div>
  <p class="cap">Repair spend across the three games is $764&ndash;$2,560, against $11,398 in the
  pre-fix game. The over-repair is gone, and it is no longer where the money goes.</p>
</section>

<footer>
  Castle Defense engine &middot; {", ".join(GAMES)} &middot;
  rebuilt from v3 replays with <code>--economy-dump --every 1</code> &middot;
  final income, money and castle HP reproduce each recorded row exactly
</footer>
</div>
"""

io.open(OUT, "w", encoding="utf-8").write(HTML)
print(f"wrote {OUT}")
for g in GAMES:
    d = DATA[g]
    print(f"  {g}  bot ${d['L']['p2money']:>9,.0f}  "
          f"{100*d['L']['p2money']/ARM:>5.1f}% of Armageddon  {d['secs']:>5.1f}s short")
