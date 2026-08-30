"""Builds the human-play report from a rebuilt singleplayer replay.

Reads human_9A9A41.csv (Marc vs the HeuristicBot, --economy-dump --every 1) and
mirror_dump_cur.csv (the defence-only bot in the same pinned mirror against the same
bot) and puts castle health and economy for both on one page.

GAME 9A9A41 IS THE COMPARABLE ONE. The earlier version of this report used 34BA36,
which was against the SEARCH bot, and its headline -- "Marc won on $356 of units" --
turned out to be a property of that opponent, not of how Marc plays. Against the
HeuristicBot he spends $24,569. The correction is carried on the page rather than
quietly dropped.
"""
import csv, io, os, collections

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "human_economy.html")


def load(name):
    rows = list(csv.DictReader(io.open(os.path.join(HERE, name), encoding="utf-8")))
    for r in rows:
        for k in list(r):
            try:
                r[k] = float(r[k])
            except (TypeError, ValueError):
                pass
        r["t"] = r["tick"] / 30.0
    return rows


H = load("human_9A9A41.csv")
B = load("mirror_dump_cur.csv")
HL, BL = H[-1], B[-1]

# ── what he did ─────────────────────────────────────────────────────────────
def census(rows, key):
    c = collections.Counter(int(r[key]) for r in rows if r[key])
    return (c,
            sum(v for k, v in c.items() if 1 <= k <= 8),
            sum(v for k, v in c.items() if 11 <= k <= 13),
            sum(c.values()))


MC, M_UNITS, M_GAD, M_ALL = census(H, "p1act")
OC, O_UNITS, O_GAD, O_ALL = census(H, "p2act")

FIRST_UNIT = next(r["t"] for r in H if 1 <= int(r["p1act"]) <= 8)
LOW = min(H, key=lambda r: r["hp"])


def inv_times(rows, key):
    out, prev = {}, rows[0][key]
    for r in rows:
        if r[key] > prev:
            out[int(r[key])] = r["t"]
            prev = r[key]
    return out


HI, HB = inv_times(H, "inv"), inv_times(H, "p2inv")
BI, BB = inv_times(B, "inv"), inv_times(B, "p2inv")

# ── geometry ────────────────────────────────────────────────────────────────
W, PADL, PADR, PADT, PADB = 1000, 76, 22, 18, 34
H_FIG = 220
IW = W - PADL - PADR
T_MAX = HL["t"]
HP_MAX = max(max(r["maxhp"] for r in H), max(r["p2maxhp"] for r in H))
MONEY_MAX = max(max(r["money"] for r in H), max(r["p2money"] for r in H))


def x(t): return PADL + IW * (t / T_MAX)
def yv(v, mx): return PADT + H_FIG * (1 - v / mx)
def fmt(v): return f"{v/1000:g}k" if v >= 1000 else str(int(v))


def path(rows, key, mx, step=3):
    d = []
    for i in range(0, len(rows), step):
        d.append(("M" if not d else "L") + f"{x(rows[i]['t']):.1f},{yv(rows[i][key], mx):.1f}")
    return "".join(d)


def grid(mx, n=4):
    return "".join(
        f'<line class="grid" x1="{PADL}" y1="{yv(v,mx):.1f}" x2="{W-PADR}" y2="{yv(v,mx):.1f}"/>'
        f'<text class="ax" x="{PADL-10}" y="{yv(v,mx)+3.5:.1f}" text-anchor="end">{fmt(v)}</text>'
        for v in [round(mx / n * i) for i in range(n + 1)])


XT = [0, 60, 120, 150, 180, 240, int(T_MAX)]
xaxis = "".join(f'<text class="ax" x="{x(t):.1f}" y="{PADT+H_FIG+22}" text-anchor="middle">{t}s</text>'
                for t in XT)

# the moment he starts buying, marked on both charts
phase = (f'<line class="phase" x1="{x(FIRST_UNIT):.1f}" y1="{PADT}" '
         f'x2="{x(FIRST_UNIT):.1f}" y2="{PADT+H_FIG}"/>'
         f'<text class="phaselab" x="{x(FIRST_UNIT)+6:.1f}" y="{PADT+12}">'
         f'{FIRST_UNIT:.0f}s &#183; first unit bought</text>')

hp_fig = (grid(HP_MAX) + phase
          + f'<path class="lthem" d="{path(H,"p2hp",HP_MAX)}"/>'
          + f'<path class="lus" d="{path(H,"hp",HP_MAX)}"/>'
          + f'<circle class="dot" cx="{x(LOW["t"]):.1f}" cy="{yv(LOW["hp"],HP_MAX):.1f}" r="4.5"/>'
          + f'<text class="dotlab" x="{x(LOW["t"]):.1f}" y="{yv(LOW["hp"],HP_MAX)+18:.1f}" '
            f'text-anchor="middle">{LOW["hp"]:,.0f} HP</text>' + xaxis)


def marks(times, cls):
    return "".join(f'<line class="{cls}" x1="{x(t):.1f}" y1="{PADT+H_FIG-8}" '
                   f'x2="{x(t):.1f}" y2="{PADT+H_FIG}"/>' for t in times.values())


econ_fig = (grid(MONEY_MAX) + phase + marks(HB, "mthem") + marks(HI, "mus")
            + f'<path class="lthem" d="{path(H,"p2money",MONEY_MAX)}"/>'
            + f'<path class="lus" d="{path(H,"money",MONEY_MAX)}"/>' + xaxis)

# ── the ladder ──────────────────────────────────────────────────────────────
ladder = ""
for k in range(1, 9):
    def cell(d, hot=False):
        if k not in d:
            return '<td class="none">&mdash;</td>'
        return f'<td class="{"hot" if hot else ""}">{d[k]:.0f}s</td>'
    lead = k in HI and (k not in HB or HB[k] - HI[k] >= 10)
    ladder += f'<tr><th class="rh">{k}</th>{cell(HI, lead)}{cell(HB)}{cell(BI)}{cell(BB)}</tr>'

# ── spend by window ─────────────────────────────────────────────────────────
win_rows = ""
for a in range(0, int(T_MAX) + 1, 30):
    w = [r for r in H if a <= r["t"] < a + 30]
    if len(w) < 30:
        continue
    buys = sum(1 for r in w if 1 <= int(r["p1act"]) <= 8)
    ds = w[-1]["p1spent"] - w[0]["p1spent"]
    db = w[-1]["p2spent"] - w[0]["p2spent"]
    cls = "quiet" if buys == 0 else ""
    win_rows += (f'<tr class="{cls}"><th class="rh">{a}&ndash;{a+30}s</th>'
                 f'<td>{buys or "&mdash;"}</td><td>${ds:,.0f}</td>'
                 f'<td class="dim">${db:,.0f}</td>'
                 f'<td class="dim">{w[-1]["hp"]:,.0f}</td>'
                 f'<td class="dim">{w[-1]["p2inv"]:.0f}</td></tr>')

HTML = f"""<title>How Marc Spends a Game</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Archivo:wght@500;600;700&family=Newsreader:ital,opsz,wght@0,6..72,400;0,6..72,500;1,6..72,400&family=IBM+Plex+Mono:wght@400;500;600&display=swap">
<style>
:root{{--ground:#f5f3ee;--surface:#fffdf9;--sunken:#edeae2;--ink:#1b1813;--ink-soft:#5a5346;
--ink-dim:#8a8271;--rule:#dad5c8;--rule-soft:#e7e3d9;--accent:#8a6212;--accent-w:#f0e4c8;
--us:#8a6212;--them:#1baf7a;--bot:#2a78d6;--warn:#c2410c}}
@media (prefers-color-scheme:dark){{:root:not([data-theme="light"]){{--ground:#131210;--surface:#1c1a16;
--sunken:#242119;--ink:#efebe1;--ink-soft:#b3ab99;--ink-dim:#837b69;--rule:#343026;--rule-soft:#282419;
--accent:#d6a741;--accent-w:#3a2e14;--us:#d6a741;--them:#199e70;--bot:#3987e5;--warn:#e2703a}}}}
:root[data-theme="dark"]{{--ground:#131210;--surface:#1c1a16;--sunken:#242119;--ink:#efebe1;
--ink-soft:#b3ab99;--ink-dim:#837b69;--rule:#343026;--rule-soft:#282419;--accent:#d6a741;
--accent-w:#3a2e14;--us:#d6a741;--them:#199e70;--bot:#3987e5;--warn:#e2703a}}
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
letter-spacing:-.03em;line-height:1;color:var(--us);display:block;margin-bottom:8px;font-variant-numeric:tabular-nums}}
.stat-t{{font-size:.87rem;color:var(--ink-soft);line-height:1.42}}
section{{margin-top:52px}}
.shead{{display:flex;align-items:baseline;gap:14px;padding-bottom:12px;margin-bottom:20px;border-bottom:1px solid var(--rule)}}
.shead .n{{font-family:"IBM Plex Mono",monospace;font-size:.74rem;font-weight:600;color:var(--accent);flex:none}}
figure{{margin:0 0 18px;background:var(--surface);border:1px solid var(--rule);padding:18px 16px 8px}}
.figtitle{{font-family:"Archivo",sans-serif;font-size:.95rem;font-weight:600;margin:0 0 2px 4px}}
.figsub{{font-size:.83rem;color:var(--ink-dim);margin:0 0 10px 4px}}
.scroll{{overflow-x:auto}} svg{{display:block;width:100%;height:auto;font-family:"IBM Plex Mono",monospace}}
.grid{{stroke:var(--rule-soft);stroke-width:1}} .ax{{fill:var(--ink-dim);font-size:10.5px}}
.lus{{fill:none;stroke:var(--us);stroke-width:2.3;stroke-linejoin:round}}
.lthem{{fill:none;stroke:var(--them);stroke-width:2;stroke-linejoin:round}}
.mus{{stroke:var(--us);stroke-width:2.5}} .mthem{{stroke:var(--them);stroke-width:2.5}}
.phase{{stroke:var(--accent);stroke-width:1.2;stroke-dasharray:3 3;opacity:.8}}
.phaselab{{fill:var(--accent);font-size:10.5px}}
.dot{{fill:var(--warn);stroke:var(--surface);stroke-width:2}}
.dotlab{{fill:var(--warn);font-size:11px;font-weight:600}}
.legend{{display:flex;flex-wrap:wrap;gap:18px;margin:10px 0 4px 4px;font-family:"Archivo",sans-serif;font-size:.78rem;color:var(--ink-soft)}}
.legend i{{display:inline-block;width:16px;height:11px;border-radius:2px;margin-right:7px;vertical-align:middle}}
table{{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums;margin-top:10px}}
th,td{{font-family:"IBM Plex Mono",monospace;font-size:.79rem;text-align:right;padding:7px 11px;
border-bottom:1px solid var(--rule-soft);white-space:nowrap}}
thead th{{font-weight:600;font-size:.72rem;color:var(--ink-soft);background:var(--sunken)}}
th.rh{{text-align:left;font-weight:600;color:var(--ink)}}
td.none{{color:var(--rule)}} td.dim{{color:var(--ink-dim)}}
td.hot{{color:var(--us);font-weight:600;background:var(--accent-w)}}
tr.quiet th.rh,tr.quiet td{{color:var(--ink-dim)}}
tr.quiet{{background:var(--sunken)}}
.callout{{border-left:3px solid var(--accent);background:var(--surface);padding:18px 22px;margin:24px 0}}
.callout.fix{{border-left-color:var(--warn)}}
.cap{{font-size:.84rem;color:var(--ink-dim);margin-top:10px;max-width:68ch;line-height:1.5}}
footer{{margin-top:64px;padding-top:18px;border-top:1px solid var(--rule);font-size:.83rem;color:var(--ink-dim)}}
</style>

<div class="wrap">
<header>
  <div class="kicker"><span class="dot"></span><span class="lab">game 9A9A41 &middot; Marc vs HeuristicBot &middot; White / nuke / reinforcements</span></div>
  <h1>He buys nothing for {FIRST_UNIT:.0f} seconds and takes the game to 999 HP.</h1>
  <p class="dek">Marc against the same HeuristicBot the defence-only bot is measured against, in
  the same pinned mirror. He spends the first two and a half minutes buying no units at all,
  survives on gadgets and repairs down to <strong>{LOW['hp']:,.0f} HP</strong>, and converts the
  economy lead that buys into a win.</p>
  <div class="stats">
    <div class="stat"><span class="stat-n">{FIRST_UNIT:.0f}<span style="font-size:.5em">s</span></span>
      <span class="stat-t">Before his first unit purchase. Zero units bought before that.</span></div>
    <div class="stat"><span class="stat-n">{LOW['hp']:,.0f}</span>
      <span class="stat-t">Lowest castle HP, at {LOW['t']:.0f}s &mdash; one hit from losing.</span></div>
    <div class="stat"><span class="stat-n">{max(HI)}</span>
      <span class="stat-t">Investments earned, to the bot's {max(HB)}. Income
      ${HL['p1income']:,.0f}/s against ${HL['p2income']:,.0f}/s.</span></div>
    <div class="stat"><span class="stat-n">{HB[7]-HI[7]:.0f}<span style="font-size:.5em">s</span></span>
      <span class="stat-t">His lead on rung 7 &mdash; {HI[7]:.0f}s against the bot's
      {HB[7]:.0f}s.</span></div>
  </div>
</header>

<section>
  <div class="shead"><span class="n">01</span><h2>Castle health</h2></div>
  <div class="prose">
    <p>Both castles across the game. The step changes are repairs, which raise maximum health as
    well as restoring it. The dashed line is the moment Marc buys his first unit.</p>
  </div>
  <figure>
    <p class="figtitle">Castle health</p>
    <p class="figsub">Marc against HeuristicBot, game 9A9A41</p>
    <div class="scroll"><svg viewBox="0 0 {W} {PADT+H_FIG+PADB}" role="img"
      aria-label="Castle health for both players. Marc's health falls to 999 at 94 seconds, recovers through repairs, and dips again in the middle of the game while the bot's climbs steadily; the bot's health then collapses to zero at the end.">
      {hp_fig}
    </svg></div>
    <div class="legend">
      <span><i style="background:var(--us)"></i>Marc</span>
      <span><i style="background:var(--them)"></i>HeuristicBot</span>
      <span><i style="background:var(--warn)"></i>his lowest point</span>
    </div>
  </figure>
  <div class="callout">
    <h3>The near-death at {LOW['t']:.0f}s is the strategy, not a mistake</h3>
    <p>Marc bottoms out at <strong>{LOW['hp']:,.0f} HP</strong> while holding money he could have
    spent on defence. He does not spend it. He is buying rungs instead, and taking the damage is
    what pays for them &mdash; the castle is a resource he is willing to spend down to almost
    nothing.</p>
    <p>The defence-only bot never does this. Its whole option comparison prices a purchase against
    <em>dying</em>, so as its survival estimate falls it spends harder, which is the opposite
    response.</p>
  </div>
</section>

<section>
  <div class="shead"><span class="n">02</span><h2>The economy</h2></div>
  <div class="prose">
    <p>Money on hand for both players. Ticks along the bottom mark investments in each player's own
    colour &mdash; the sawtooth is the ladder being climbed, not spending.</p>
  </div>
  <figure>
    <p class="figtitle">Money on hand</p>
    <p class="figsub">the same clock as the health chart above</p>
    <div class="scroll"><svg viewBox="0 0 {W} {PADT+H_FIG+PADB}" role="img"
      aria-label="Money on hand for both players. Marc's balance is repeatedly drained to near zero by investments through the first half, then rises as he out-earns the bot; the bot accumulates a large unspent balance late.">
      {econ_fig}
    </svg></div>
    <div class="legend">
      <span><i style="background:var(--us)"></i>Marc</span>
      <span><i style="background:var(--them)"></i>HeuristicBot</span>
      <span><i style="background:var(--ink-dim)"></i>ticks below the axis mark investments</span>
    </div>
  </figure>
  <div class="scroll"><table>
    <thead><tr><th>window</th><th>his unit buys</th><th>his spend</th><th>bot spend</th>
      <th>his HP</th><th>bot rung</th></tr></thead>
    <tbody>{win_rows}</tbody>
  </table></div>
  <p class="cap">The shaded rows are the ones where he buys nothing at all &mdash; the first
  {FIRST_UNIT:.0f} seconds of the game, five straight windows, while the bot is already spending.</p>
</section>

<section>
  <div class="shead"><span class="n">03</span><h2>The investment ladder</h2></div>
  <div class="prose">
    <p>Both players in seat 1 against the same HeuristicBot. The rungs are a real sequence, so they
    are numbered as one.</p>
  </div>
  <div class="scroll"><table>
    <thead><tr><th>rung</th><th>MARC</th><th>his bot</th><th>defence-only bot</th><th>its bot</th></tr></thead>
    <tbody>{ladder}</tbody>
  </table></div>
  <div class="callout">
    <h3>He is BEHIND for six rungs and then wins the two that matter</h3>
    <p>Through rung 6 Marc is consistently a few seconds <em>slower</em> than his opponent
    ({HI[6]:.0f}s against {HB[6]:.0f}s), and slower than the defence-only bot too. Nothing in the
    first two minutes looks like winning.</p>
    <p>Then he takes rung 7 at <strong>{HI[7]:.0f}s</strong> while the bot does not reach it until
    <strong>{HB[7]:.0f}s</strong>, and rung 8 at <strong>{HI[8]:.0f}s</strong> while the bot never
    does. The defence-only bot's mirror is the exact inverse: it reaches rung 7 first
    ({BI[7]:.0f}s to {BB[7]:.0f}s) and then its opponent takes rung 8 at {BB[8]:.0f}s and it never
    does.</p>
  </div>
</section>

<section>
  <div class="shead"><span class="n">04</span><h2>What this changes</h2></div>
  <div class="prose">
    <p>Marc's total unit spend here is <strong>${HL['p1spent']:,.0f}</strong>, against the
    defence-only bot's <strong>${BL['p1spent']:,.0f}</strong> in its mirror. He is not spending
    less overall &mdash; he is spending it <em>later</em>. Every dollar before
    {FIRST_UNIT:.0f}s goes into the ladder, and the army only appears once the income is there to
    pay for it.</p>
    <p>So the defence-only premise &mdash; defend cheaply, out-invest, win late &mdash; is
    vindicated, and the bot is failing at the part it was built for. It spends
    ${BL['p1spent']:,.0f} defending and finishes a rung behind. Marc spends nothing for
    {FIRST_UNIT:.0f} seconds and finishes a rung ahead.</p>
    <p>The gap is not which unit to buy or when to wipe. It is that Marc treats castle health as
    something to spend, and the bot treats it as something to protect.</p>
  </div>
  <div class="callout fix">
    <h3>Correction to the earlier version of this page</h3>
    <p>This report previously used game <code>34BA36</code> and led with "Marc won on $356 of
    units" &mdash; two unit purchases in the whole game. That game was against the <em>search</em>
    bot, not the HeuristicBot, and the finding does not generalise: here he buys
    {M_UNITS} units for ${HL['p1spent']:,.0f}. How much he buys is a property of the opponent. What
    survives from that game is the shape shown above &mdash; buy nothing early, put everything on
    the ladder, convert late.</p>
  </div>
</section>

<footer>
  Castle Defense engine &middot; game 9A9A41, {T_MAX:.0f}s, winner P1 &middot;
  rebuilt from a v3 replay with <code>--economy-dump --every 1</code> &middot;
  final income, money and castle HP all reproduce the recorded row exactly &middot;
  Marc {M_ALL} actions ({M_UNITS} units / {M_GAD} gadgets), bot {O_ALL} ({O_UNITS} / {O_GAD})
</footer>
</div>
"""

io.open(OUT, "w", encoding="utf-8").write(HTML)
print(f"wrote {OUT} ({len(HTML):,} bytes)")
print(f"  first unit at {FIRST_UNIT:.0f}s   low HP {LOW['hp']:,.0f} at {LOW['t']:.0f}s")
print(f"  Marc rung 7 {HI[7]:.0f}s vs bot {HB[7]:.0f}s   rung 8 {HI[8]:.0f}s vs never")
print(f"  spend Marc ${HL['p1spent']:,.0f}   defence-only bot ${BL['p1spent']:,.0f}")
