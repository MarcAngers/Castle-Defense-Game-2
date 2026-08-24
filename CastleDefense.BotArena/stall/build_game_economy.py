"""Economy report for one recorded game: money, the investment ladder, and where it diverged.

    python build_game_economy.py <GAMEID>

Reads human/<GAMEID>.csv (produced by `--economy-dump --every 1`) and writes
game_economy_<GAMEID>.html. Built to answer one question — at what moment did one side
fall behind economically — so the ladder and the divergence window are the subject and the
money chart is the evidence.
"""
import csv, io, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
GID = sys.argv[1] if len(sys.argv) > 1 else "0A7658"
SRC = os.path.join(HERE, "human", GID + ".csv")
OUT = os.path.join(HERE, f"game_economy_{GID}.html")

INV8_PRICE = 40000
ARMAGEDDON_PRICE = 121221

R = list(csv.DictReader(io.open(SRC, encoding="utf-8")))
for r in R:
    for k in list(r):
        try:
            r[k] = float(r[k])
        except (TypeError, ValueError):
            pass
    r["t"] = r["tick"] / 30.0
L = R[-1]
T_MAX = L["t"]


def inv_times(key):
    out, prev = {}, R[0][key]
    for r in R:
        if r[key] > prev:
            out[int(r[key])] = r["t"]
            prev = r[key]
    return out


M, B = inv_times("inv"), inv_times("p2inv")

# The divergence: the first rung where the lead changes hands and never comes back.
shared = sorted(set(M) & set(B))
DIVERGE = next((k for k in shared if B[k] - M[k] > 10), max(shared))
D_FROM, D_TO = B[DIVERGE - 1], B[DIVERGE]
win = [r for r in R if D_FROM <= r["t"] <= D_TO]
BOT_SPEND_WIN = win[-1]["p2spent"] - win[0]["p2spent"]
BOT_EARNED_WIN = win[0]["p2income"] * (D_TO - D_FROM)
MWIN = [r for r in R if M[DIVERGE - 1] <= r["t"] <= M[DIVERGE]]
MARC_SPEND_WIN = MWIN[-1]["p1spent"] - MWIN[0]["p1spent"]
RUNG_PRICE = win[0]["p2income"] * ((DIVERGE - 1) * 4 + 8)

# ── geometry ────────────────────────────────────────────────────────────────
W, PADL, PADR, PADT, PADB = 1000, 78, 22, 20, 34
HF = 230
IW = W - PADL - PADR
MONEY_MAX = max(max(r["money"] for r in R), max(r["p2money"] for r in R))


def x(t): return PADL + IW * (t / T_MAX)
def y(v): return PADT + HF * (1 - v / MONEY_MAX)
def fmt(v): return f"{v/1000:g}k" if v >= 1000 else str(int(v))


def path(key, step=3):
    d = []
    for i in range(0, len(R), step):
        d.append(("M" if not d else "L") + f"{x(R[i]['t']):.1f},{y(R[i][key]):.1f}")
    return "".join(d)


grid = "".join(
    f'<line class="grid" x1="{PADL}" y1="{y(v):.1f}" x2="{W-PADR}" y2="{y(v):.1f}"/>'
    f'<text class="ax" x="{PADL-10}" y="{y(v)+3.5:.1f}" text-anchor="end">${fmt(v)}</text>'
    for v in [round(MONEY_MAX / 4 * i) for i in range(5)])
xaxis = "".join(
    f'<text class="ax" x="{x(t):.1f}" y="{PADT+HF+22}" text-anchor="middle">{t}s</text>'
    for t in [0, 60, 120, 180, 240, int(T_MAX)])

band = (f'<rect class="band" x="{x(D_FROM):.1f}" y="{PADT}" '
        f'width="{x(D_TO)-x(D_FROM):.1f}" height="{HF}"/>'
        f'<text class="bandlab" x="{(x(D_FROM)+x(D_TO))/2:.1f}" y="{PADT+14}" '
        f'text-anchor="middle">the bot spends {D_TO-D_FROM:.0f}s on rung {DIVERGE}</text>')


def marks(times, cls):
    return "".join(f'<line class="{cls}" x1="{x(t):.1f}" y1="{PADT+HF-9}" '
                   f'x2="{x(t):.1f}" y2="{PADT+HF}"/>' for t in times.values())


fig = (grid + band + marks(B, "mbot") + marks(M, "mmarc")
       + f'<path class="lbot" d="{path("p2money")}"/>'
       + f'<path class="lmarc" d="{path("money")}"/>' + xaxis)

rows = ""
for k in range(1, 9):
    m, b = M.get(k), B.get(k)
    if m is None and b is None:
        continue
    gap = f"{b-m:+.0f}s" if (m and b) else "&mdash;"
    if m and b:
        lead, cls = ("MARC", "marc") if m < b else ("bot", "bot")
    else:
        lead, cls = ("MARC", "marc") if m else ("bot", "bot")
    hot = ' class="hot"' if k == DIVERGE else ""
    rows += (f'<tr{hot}><th class="rh">{k}</th>'
             f'<td>{f"{m:.0f}s" if m else "&mdash;"}</td>'
             f'<td>{f"{b:.0f}s" if b else "&mdash;"}</td>'
             f'<td class="{cls}">{gap}</td><td class="{cls}">{lead}</td></tr>')

HTML = f"""<title>Where the Bot Fell Behind</title>
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
body{{background:var(--ground);color:var(--ink);margin:0;padding:0 24px 80px;
font-family:"Newsreader",Georgia,serif;font-size:17px;line-height:1.62;-webkit-font-smoothing:antialiased}}
.wrap{{max-width:1060px;margin:0 auto}} .prose{{max-width:65ch}}
h1,h2,h3,.lab,thead th,th.rh{{font-family:"Archivo",system-ui,sans-serif}}
h1{{font-size:clamp(2rem,4.4vw,2.8rem);font-weight:700;letter-spacing:-.028em;line-height:1.06;margin:0 0 .5rem;text-wrap:balance}}
h2{{font-size:1.3rem;font-weight:600;letter-spacing:-.012em;margin:0}}
h3{{font-size:.95rem;font-weight:600;margin:0 0 .5rem}}
p{{margin:0 0 1rem}} p:last-child{{margin-bottom:0}}
strong{{font-weight:500;background:var(--accent-w);padding:0 .2em;border-radius:2px}}
code{{font-family:"IBM Plex Mono",monospace;font-size:.84em;background:var(--sunken);color:var(--ink-soft);padding:.12em .38em;border-radius:3px}}
.lab{{font-size:.7rem;font-weight:600;letter-spacing:.13em;text-transform:uppercase;color:var(--ink-dim)}}
header{{padding:58px 0 30px;border-bottom:2px solid var(--ink)}}
.kicker{{display:flex;align-items:baseline;gap:12px;margin-bottom:18px}}
.kicker .dot{{width:9px;height:9px;background:var(--accent);transform:rotate(45deg);flex:none}}
.dek{{font-size:1.2rem;color:var(--ink-soft);max-width:62ch;margin:0}}
.stats{{display:grid;gap:1px;background:var(--rule);border:1px solid var(--rule);margin-top:34px;
grid-template-columns:repeat(auto-fit,minmax(190px,1fr))}}
.stat{{background:var(--surface);padding:20px 18px}}
.stat-n{{font-family:"Archivo",sans-serif;font-size:clamp(1.5rem,3.2vw,2rem);font-weight:700;
letter-spacing:-.03em;line-height:1;color:var(--warn);display:block;margin-bottom:8px;font-variant-numeric:tabular-nums}}
.stat-t{{font-size:.87rem;color:var(--ink-soft);line-height:1.42}}
section{{margin-top:52px}}
.shead{{display:flex;align-items:baseline;gap:14px;padding-bottom:12px;margin-bottom:20px;border-bottom:1px solid var(--rule)}}
figure{{margin:0;background:var(--surface);border:1px solid var(--rule);padding:18px 16px 8px}}
.figtitle{{font-family:"Archivo",sans-serif;font-size:.95rem;font-weight:600;margin:0 0 2px 4px}}
.figsub{{font-size:.83rem;color:var(--ink-dim);margin:0 0 10px 4px}}
.scroll{{overflow-x:auto}} svg{{display:block;width:100%;height:auto;font-family:"IBM Plex Mono",monospace}}
.grid{{stroke:var(--rule-soft);stroke-width:1}} .ax{{fill:var(--ink-dim);font-size:10.5px}}
.lmarc{{fill:none;stroke:var(--marc);stroke-width:2.3;stroke-linejoin:round}}
.lbot{{fill:none;stroke:var(--bot);stroke-width:2;stroke-linejoin:round}}
.mmarc{{stroke:var(--marc);stroke-width:2.5}} .mbot{{stroke:var(--bot);stroke-width:2.5}}
.band{{fill:var(--warn);opacity:.10}}
.bandlab{{fill:var(--warn);font-size:11px;font-weight:600;font-family:"Archivo",sans-serif}}
.legend{{display:flex;flex-wrap:wrap;gap:18px;margin:10px 0 4px 4px;font-family:"Archivo",sans-serif;font-size:.78rem;color:var(--ink-soft)}}
.legend i{{display:inline-block;width:16px;height:11px;border-radius:2px;margin-right:7px;vertical-align:middle}}
table{{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums;margin-top:10px}}
th,td{{font-family:"IBM Plex Mono",monospace;font-size:.79rem;text-align:right;padding:7px 11px;
border-bottom:1px solid var(--rule-soft);white-space:nowrap}}
thead th{{font-weight:600;font-size:.72rem;color:var(--ink-soft);background:var(--sunken)}}
th.rh{{text-align:left;font-weight:600;color:var(--ink)}}
td.marc{{color:var(--marc);font-weight:600}} td.bot{{color:var(--bot);font-weight:600}}
tr.hot{{background:var(--accent-w)}}
tr.hot th.rh{{color:var(--warn)}}
.callout{{border-left:3px solid var(--warn);background:var(--surface);padding:18px 22px;margin:24px 0}}
.cap{{font-size:.84rem;color:var(--ink-dim);margin-top:10px;max-width:68ch;line-height:1.5}}
footer{{margin-top:64px;padding-top:18px;border-top:1px solid var(--rule);font-size:.83rem;color:var(--ink-dim)}}
</style>

<div class="wrap">
<header>
  <div class="kicker"><span class="dot"></span><span class="lab">game {GID} &middot; White mirror &middot; Marc won in {T_MAX:.0f}s</span></div>
  <h1>The bot led every rung until the seventh.</h1>
  <p class="dek">It was ahead on the investment ladder for the first six rungs &mdash; by as much
  as six seconds &mdash; then took <strong>{D_TO-D_FROM:.0f} seconds</strong> to buy rung
  {DIVERGE} against Marc's {M[DIVERGE]-M[DIVERGE-1]:.0f}, and never bought another.</p>
  <div class="stats">
    <div class="stat"><span class="stat-n">{D_TO-D_FROM:.0f}<span style="font-size:.5em">s</span></span>
      <span class="stat-t">On rung {DIVERGE}, against a pure-saving time of
      {RUNG_PRICE/win[0]['p2income']:.0f}s at its income.</span></div>
    <div class="stat"><span class="stat-n">{100*BOT_SPEND_WIN/BOT_EARNED_WIN:.0f}<span style="font-size:.5em">%</span></span>
      <span class="stat-t">Of everything it earned in that window went on units
      (${BOT_SPEND_WIN:,.0f} of ${BOT_EARNED_WIN:,.0f}).</span></div>
    <div class="stat"><span class="stat-n">${MARC_SPEND_WIN:,.0f}</span>
      <span class="stat-t">What Marc spent on units over his own rung-{DIVERGE} climb.</span></div>
    <div class="stat"><span class="stat-n">${L['p2money']:,.0f}</span>
      <span class="stat-t">The bot died holding this, having peaked at
      ${max(r['p2money'] for r in R):,.0f}.</span></div>
  </div>
</header>

<section>
  <div class="shead"><span style="font-family:'IBM Plex Mono',monospace;font-size:.74rem;font-weight:600;color:var(--accent)">01</span><h2>Money on hand</h2></div>
  <div class="prose">
    <p>Ticks below the axis mark investments in each player's colour. The shaded band is the
    bot's rung-{DIVERGE} climb &mdash; the window where the game was decided.</p>
  </div>
  <figure>
    <p class="figtitle">Money on hand</p>
    <p class="figsub">Marc against the bot, game {GID}</p>
    <div class="scroll"><svg viewBox="0 0 {W} {PADT+HF+PADB}" role="img"
      aria-label="Money on hand for both players. Both sawtooth upward in step through the first six investments; the bot's line then flattens into a long shaded window while Marc's continues to ladder up and pulls away.">
      {fig}
    </svg></div>
    <div class="legend">
      <span><i style="background:var(--marc)"></i>Marc</span>
      <span><i style="background:var(--bot)"></i>the bot</span>
      <span><i style="background:var(--warn);opacity:.35"></i>the bot's rung-{DIVERGE} climb</span>
    </div>
  </figure>
</section>

<section>
  <div class="shead"><span style="font-family:'IBM Plex Mono',monospace;font-size:.74rem;font-weight:600;color:var(--accent)">02</span><h2>The ladder</h2></div>
  <div class="scroll"><table>
    <thead><tr><th>rung</th><th>MARC</th><th>bot</th><th>gap</th><th>leader</th></tr></thead>
    <tbody>{rows}</tbody>
  </table></div>
  <div class="callout">
    <h3>Rung {DIVERGE} is the whole game</h3>
    <p>Through rung {DIVERGE-1} the bot is <em>ahead every single time</em> &mdash; 9s, 21s, 38s,
    53s, 78s, 114s against Marc's 9s, 21s, 39s, 59s, 81s, 117s. Nothing in the first two minutes
    suggests it is losing.</p>
    <p>Rung {DIVERGE} costs <strong>${RUNG_PRICE:,.0f}</strong>, which is
    {RUNG_PRICE/win[0]['p2income']:.0f} seconds of pure saving at its income of
    ${win[0]['p2income']:,.0f}/s. It took <strong>{D_TO-D_FROM:.0f} seconds</strong>, because it
    spent <strong>${BOT_SPEND_WIN:,.0f}</strong> of the ${BOT_EARNED_WIN:,.0f} it earned in that
    window on units &mdash; {100*BOT_SPEND_WIN/BOT_EARNED_WIN:.0f}% of its income. Marc climbed
    the same rung in {M[DIVERGE]-M[DIVERGE-1]:.0f}s on <strong>${MARC_SPEND_WIN:,.0f}</strong> of
    units.</p>
    <p>By the time the bot finally bought rung {DIVERGE} at {D_TO:.0f}s, Marc had already had
    rung 8 for {D_TO-M[8]:.0f} seconds and was banking toward Armageddon at
    ${ARMAGEDDON_PRICE:,}. The bot never held more than
    ${max(r['p2money'] for r in R):,.0f} &mdash; it was never once in a position to buy rung 8 at
    ${INV8_PRICE:,}.</p>
  </div>
  <p class="cap">The bot's balance never reaches $40,000 at any point in the game, so rung 8 was
  not a decision it declined &mdash; it was never affordable. The decision it actually made, over
  and over, was to convert income into units during the one window where saving would have kept
  it in the race.</p>
</section>

<footer>
  Castle Defense engine &middot; game {GID}, {T_MAX:.0f}s &middot;
  rebuilt from a v3 replay with <code>--economy-dump --every 1</code> &middot;
  final income, money and castle HP reproduce the recorded row exactly
</footer>
</div>
"""

io.open(OUT, "w", encoding="utf-8").write(HTML)
print(f"wrote {OUT}")
print(f"  divergence at rung {DIVERGE}: bot {D_FROM:.0f}s -> {D_TO:.0f}s ({D_TO-D_FROM:.0f}s), "
      f"Marc {M[DIVERGE-1]:.0f}s -> {M[DIVERGE]:.0f}s ({M[DIVERGE]-M[DIVERGE-1]:.0f}s)")
