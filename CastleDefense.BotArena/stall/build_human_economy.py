"""Builds the human-economy report from a rebuilt singleplayer replay.

Reads human_34BA36.csv (Marc's game, via --economy-dump) and mirror_dump_cur.csv (the
defence-only bot in the same pinned mirror) and puts the two economies on one page.
Shares the visual system of mirror_anatomy.html deliberately: same subject, same
investigation, so it should read as the companion piece it is.
"""
import csv, io, os, collections

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "human_economy.html")
NUM = ("tick", "hp", "maxhp", "money", "inv", "own", "enemy", "p2hp", "p2maxhp",
       "p2money", "p2inv", "p1income", "p2income", "p1spent", "p2spent", "p1act", "p2act")


def load(name, cols=NUM):
    rows = list(csv.DictReader(io.open(os.path.join(HERE, name), encoding="utf-8")))
    for r in rows:
        for k in cols:
            if k in r:
                r[k] = float(r[k])
        r["t"] = r["tick"] / 30.0
    return rows


H = load("human_34BA36.csv")
B = load("mirror_dump_cur.csv", ("tick", "money", "inv", "p2money", "p2inv",
                                "p1income", "p2income", "p1spent", "p2spent", "hp"))
HL = H[-1]


def census(rows, key):
    c = collections.Counter(int(r[key]) for r in rows if r[key])
    units = sum(v for k, v in c.items() if 1 <= k <= 8)
    gadgets = sum(v for k, v in c.items() if 11 <= k <= 13)
    return c, units, gadgets, sum(c.values())


MC, M_UNITS, M_GAD, M_ALL = census(H, "p1act")
OC, O_UNITS, O_GAD, O_ALL = census(H, "p2act")


def inv_times(rows, key):
    out, prev = {}, rows[0][key]
    for r in rows:
        if r[key] > prev:
            out[int(r[key])] = r["t"]
            prev = r[key]
    return out


HI, HB = inv_times(H, "inv"), inv_times(H, "p2inv")
BI, BB = inv_times(B, "inv"), inv_times(B, "p2inv")

# geometry
W, PADL, PADR, PADT = 1000, 74, 22, 18
H_ECON, PADB = 240, 34
IW = W - PADL - PADR
T_MAX = H[-1]["t"]
MONEY_MAX = max(max(r["money"] for r in H), max(r["p2money"] for r in H))


def x(t): return PADL + IW * (t / T_MAX)
def y(v): return PADT + H_ECON * (1 - v / MONEY_MAX)
def fmt(v): return f"{v/1000:.0f}k" if v >= 1000 else str(int(v))


def path(rows, key, step=2):
    d = []
    for i in range(0, len(rows), step):
        d.append(("M" if not d else "L") + f"{x(rows[i]['t']):.1f},{y(rows[i][key]):.1f}")
    return "".join(d)


grid = "".join(
    f'<line class="grid" x1="{PADL}" y1="{y(v):.1f}" x2="{W-PADR}" y2="{y(v):.1f}"/>'
    f'<text class="ax" x="{PADL-10}" y="{y(v)+3.5:.1f}" text-anchor="end">${fmt(v)}</text>'
    for v in [round(MONEY_MAX / 4 * i) for i in range(5)])
xaxis = "".join(
    f'<text class="ax" x="{x(t):.1f}" y="{PADT+H_ECON+22}" text-anchor="middle">{t}s</text>'
    for t in [0, 30, 60, 90, 120, 150, int(T_MAX)])


def marks(times, cls):
    return "".join(f'<line class="{cls}" x1="{x(t):.1f}" y1="{PADT+H_ECON-8}" '
                   f'x2="{x(t):.1f}" y2="{PADT+H_ECON}"/>' for t in times.values())


econ = (grid + marks(HB, "mthem") + marks(HI, "mus")
        + f'<path class="lthem" d="{path(H, "p2money")}"/>'
        + f'<path class="lus" d="{path(H, "money")}"/>' + xaxis)

# the investment ladder
ladder = ""
for k in range(1, 9):
    def cell(d, hot=False):
        if k not in d:
            return '<td class="none">&mdash;</td>'
        return f'<td class="{"hot" if hot else ""}">{d[k]:.0f}s</td>'
    lead = k in HI and k in HB and HB[k] - HI[k] >= 10
    ladder += f'<tr><th class="rh">{k}</th>{cell(HI, lead)}{cell(HB)}{cell(BI)}{cell(BB)}</tr>'

# action census
ROWS = [(1, "tier-1 unit"), (2, "tier-2 unit"), (3, "tier-3 unit"), (4, "tier-4 unit"),
        (5, "tier-5 unit"), (6, "tier-6 unit"), (9, "invest"), (10, "repair"),
        (11, "offensive gadget"), (12, "reinforcements"), (13, "signature gadget")]
cen = ""
for k, label in ROWS:
    if not MC[k] and not OC[k]:
        continue
    kind = "unit" if 1 <= k <= 8 else ("econ" if k in (9, 10) else "gadget")
    cen += (f'<tr><th class="rh">{label}</th><td class="k {kind}">{kind}</td>'
            f'<td>{MC[k] or "&mdash;"}</td><td class="dim">{OC[k] or "&mdash;"}</td></tr>')

HTML = f"""<title>How Marc Spends a Game</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Archivo:wght@500;600;700&family=Newsreader:ital,opsz,wght@0,6..72,400;0,6..72,500;1,6..72,400&family=IBM+Plex+Mono:wght@400;500;600&display=swap">
<style>
:root{{--ground:#f5f3ee;--surface:#fffdf9;--sunken:#edeae2;--ink:#1b1813;--ink-soft:#5a5346;
--ink-dim:#8a8271;--rule:#dad5c8;--rule-soft:#e7e3d9;--accent:#8a6212;--accent-w:#f0e4c8;
--us:#8a6212;--them:#1baf7a;--bot:#2a78d6}}
@media (prefers-color-scheme:dark){{:root:not([data-theme="light"]){{--ground:#131210;--surface:#1c1a16;
--sunken:#242119;--ink:#efebe1;--ink-soft:#b3ab99;--ink-dim:#837b69;--rule:#343026;--rule-soft:#282419;
--accent:#d6a741;--accent-w:#3a2e14;--us:#d6a741;--them:#199e70;--bot:#3987e5}}}}
:root[data-theme="dark"]{{--ground:#131210;--surface:#1c1a16;--sunken:#242119;--ink:#efebe1;
--ink-soft:#b3ab99;--ink-dim:#837b69;--rule:#343026;--rule-soft:#282419;--accent:#d6a741;
--accent-w:#3a2e14;--us:#d6a741;--them:#199e70;--bot:#3987e5}}
*{{box-sizing:border-box}}
body{{background:var(--ground);color:var(--ink);margin:0;padding:0 24px 80px;
font-family:"Newsreader",Georgia,serif;font-size:17px;line-height:1.62;-webkit-font-smoothing:antialiased}}
.wrap{{max-width:1060px;margin:0 auto}} .prose{{max-width:65ch}}
h1,h2,h3,.lab,thead th,th.rh{{font-family:"Archivo",system-ui,sans-serif}}
h1{{font-size:clamp(2rem,4.6vw,2.9rem);font-weight:700;letter-spacing:-.028em;line-height:1.05;margin:0 0 .5rem;text-wrap:balance}}
h2{{font-size:1.3rem;font-weight:600;letter-spacing:-.012em;margin:0}}
h3{{font-size:.95rem;font-weight:600;margin:0 0 .5rem}}
p{{margin:0 0 1rem}} p:last-child{{margin-bottom:0}}
strong{{font-weight:500;background:var(--accent-w);padding:0 .2em;border-radius:2px}}
code{{font-family:"IBM Plex Mono",monospace;font-size:.84em;background:var(--sunken);color:var(--ink-soft);padding:.12em .38em;border-radius:3px}}
.lab{{font-size:.7rem;font-weight:600;letter-spacing:.13em;text-transform:uppercase;color:var(--ink-dim)}}
header{{padding:60px 0 30px;border-bottom:2px solid var(--ink)}}
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
figure{{margin:0;background:var(--surface);border:1px solid var(--rule);padding:18px 16px 8px}}
.figtitle{{font-family:"Archivo",sans-serif;font-size:.95rem;font-weight:600;margin:0 0 2px 4px}}
.figsub{{font-size:.83rem;color:var(--ink-dim);margin:0 0 10px 4px}}
.scroll{{overflow-x:auto}} svg{{display:block;width:100%;height:auto;font-family:"IBM Plex Mono",monospace}}
.grid{{stroke:var(--rule-soft);stroke-width:1}} .ax{{fill:var(--ink-dim);font-size:10.5px}}
.lus{{fill:none;stroke:var(--us);stroke-width:2.2;stroke-linejoin:round}}
.lthem{{fill:none;stroke:var(--them);stroke-width:2;stroke-linejoin:round}}
.mus{{stroke:var(--us);stroke-width:2.5}} .mthem{{stroke:var(--them);stroke-width:2.5}}
.legend{{display:flex;flex-wrap:wrap;gap:18px;margin:10px 0 4px 4px;font-family:"Archivo",sans-serif;font-size:.78rem;color:var(--ink-soft)}}
.legend i{{display:inline-block;width:16px;height:11px;border-radius:2px;margin-right:7px;vertical-align:middle}}
table{{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums;margin-top:10px}}
th,td{{font-family:"IBM Plex Mono",monospace;font-size:.79rem;text-align:right;padding:7px 11px;
border-bottom:1px solid var(--rule-soft);white-space:nowrap}}
thead th{{font-weight:600;font-size:.72rem;color:var(--ink-soft);background:var(--sunken)}}
th.rh{{text-align:left;font-weight:600;color:var(--ink)}}
td.none{{color:var(--rule)}} td.dim{{color:var(--ink-dim)}}
td.hot{{color:var(--us);font-weight:600;background:var(--accent-w)}}
td.k{{text-align:left;font-size:.68rem;letter-spacing:.06em;text-transform:uppercase;color:var(--ink-dim)}}
td.k.unit{{color:var(--bot)}} td.k.gadget{{color:var(--them)}} td.k.econ{{color:var(--accent)}}
.callout{{border-left:3px solid var(--accent);background:var(--surface);padding:18px 22px;margin:24px 0}}
.cap{{font-size:.84rem;color:var(--ink-dim);margin-top:10px;max-width:68ch;line-height:1.5}}
footer{{margin-top:64px;padding-top:18px;border-top:1px solid var(--rule);font-size:.83rem;color:var(--ink-dim)}}
</style>

<div class="wrap">
<header>
  <div class="kicker"><span class="dot"></span><span class="lab">game 34BA36 &middot; White / nuke / reinforcements &middot; rebuilt from the replay</span></div>
  <h1>Marc won this game on ${HL['p1spent']:,.0f} of units.</h1>
  <p class="dek">His most recent singleplayer game in the pinned mirror &mdash; the same matchup and
  the same loadout on both sides as every bot measurement in this investigation. He bought
  {M_UNITS} units in {T_MAX:.0f} seconds and spent the rest of the game buying the economy.</p>
  <div class="stats">
    <div class="stat"><span class="stat-n">${HL['p1spent']:,.0f}</span>
      <span class="stat-t">Total spent on units &mdash; {M_UNITS} purchases in the whole game.</span></div>
    <div class="stat"><span class="stat-n">{M_GAD}</span>
      <span class="stat-t">Gadget casts, against {M_UNITS} unit buys. His army is almost entirely
      gadget-spawned.</span></div>
    <div class="stat"><span class="stat-n">{max(HI)}</span>
      <span class="stat-t">Investments earned, to the bot's {max(HB)} &mdash; and he still finished
      holding ${HL['money']:,.0f}.</span></div>
    <div class="stat"><span class="stat-n">{M_ALL}</span>
      <span class="stat-t">Actions in total, against the bot's {O_ALL}. He acts less than half as
      often and wins.</span></div>
  </div>
</header>

<section>
  <div class="shead"><span class="n">01</span><h2>The economy</h2></div>
  <div class="prose">
    <p>Money on hand for both players. Ticks along the bottom axis mark investments in each
    player's own colour &mdash; the sawtooth is the ladder being climbed, not spending.</p>
  </div>
  <figure>
    <p class="figtitle">Money on hand</p>
    <p class="figsub">Marc against the singleplayer bot, game 34BA36</p>
    <div class="scroll"><svg viewBox="0 0 {W} {PADT+H_ECON+PADB}" role="img"
      aria-label="Money on hand for both players. Marc's line stays near zero for the first two and a half minutes as every dollar goes into investments, then climbs steeply once he runs out of affordable rungs, while the bot's line holds a working balance throughout.">
      {econ}
    </svg></div>
    <div class="legend">
      <span><i style="background:var(--us)"></i>Marc</span>
      <span><i style="background:var(--them)"></i>the bot</span>
      <span><i style="background:var(--ink-dim)"></i>ticks below the axis mark investments</span>
    </div>
  </figure>
  <div class="callout">
    <h3>He runs at nearly zero for two and a half minutes, on purpose</h3>
    <p>Marc's line sits on the floor until {HI[max(HI)]:.0f}s. Every dollar that arrives is
    converted into the next rung as soon as it can be &mdash; the balance climbs at the end only
    because he has run out of rungs he can afford, not because he started saving. He finishes
    holding <strong>${HL['money']:,.0f}</strong>, most of a rung he never got to buy.</p>
    <p>The bot's line is the opposite shape: it keeps a working balance and spends it on units as
    it goes, which is why it is still on investment {max(HB)} when the game ends.</p>
  </div>
</section>

<section>
  <div class="shead"><span class="n">02</span><h2>The investment ladder</h2></div>
  <div class="prose">
    <p>The rungs are a real sequence, so they are numbered as one. Marc's game on the left, the
    defence-only bot's mirror game on the right &mdash; both of them in seat 1 against a bot.</p>
  </div>
  <div class="scroll"><table>
    <thead><tr><th>rung</th><th>MARC</th><th>his opponent</th><th>our bot</th><th>its opponent</th></tr></thead>
    <tbody>{ladder}</tbody>
  </table></div>
  <div class="callout">
    <h3>Marc pulls ahead. Our bot runs in lock-step and loses by one.</h3>
    <p>By rung 6 Marc is <strong>{HB[6]-HI[6]:.0f} seconds ahead</strong> of his opponent
    ({HI[6]:.0f}s against {HB[6]:.0f}s), and he takes rung 7 while it never does. Our defence-only
    bot reaches the same rungs at almost exactly the same times as <em>its</em> opponent &mdash;
    rung 6 at {BI[6]:.0f}s against {BB[6]:.0f}s &mdash; and then the opponent takes rung 8 and it
    does not.</p>
    <p>Marc is not faster in absolute terms. He reaches rung 6 at {HI[6]:.0f}s where our bot gets
    there at {BI[6]:.0f}s. <strong>He is faster relative to the player he is fighting</strong>,
    which is the only comparison that decides a game.</p>
  </div>
</section>

<section>
  <div class="shead"><span class="n">03</span><h2>Every action he took</h2></div>
  <div class="prose">
    <p>{M_ALL} actions in {T_MAX:.0f} seconds. The whole game fits in one table, which is itself
    part of the finding.</p>
  </div>
  <div class="scroll"><table>
    <thead><tr><th>action</th><th>kind</th><th>MARC</th><th>the bot</th></tr></thead>
    <tbody>{cen}</tbody>
  </table></div>
  <p class="cap">Marc: <strong>{M_UNITS} unit buys against {M_GAD} gadget casts</strong>. The bot:
  {O_UNITS} unit buys against {O_GAD} gadget casts. Both players hold the same
  <code>reinforcements</code>, and Marc casts it {MC[12]} times &mdash; every cast is five units he
  did not pay for. The army on his side of the field is bought almost entirely with gadget
  cooldowns rather than with money.</p>
</section>

<section>
  <div class="shead"><span class="n">04</span><h2>What this says about our bot</h2></div>
  <div class="prose">
    <p>The defence-only bot spent <strong>${B[-1]['p1spent']:,.0f}</strong> on units in its mirror
    game. Marc spent <strong>${HL['p1spent']:,.0f}</strong> in his &mdash; a factor of
    {B[-1]['p1spent']/max(1, HL['p1spent']):,.0f}. Both were handed the same gadget, on the same
    cooldown, spawning the same five tier-appropriate units per cast.</p>
    <p>Earlier in this investigation the bot's tier-7 buying looked like a wiper-selection problem,
    and the fix for it &mdash; pricing a purchase against what is already deployed &mdash; came out
    win-rate neutral. This is a plausible reason why. The question it was answering was which unit
    to buy, in a matchup where the strongest player in the dataset buys twice and puts everything
    else on the ladder.</p>
    <p>One caveat on how far to read this: it is a single game, and Marc's opponent here is the
    search bot rather than the HeuristicBot the defence-only measurements use. What transfers is
    the shape &mdash; the spend ratio and the relative ladder speed &mdash; not the exact numbers.</p>
  </div>
</section>

<footer>
  Castle Defense engine &middot; game 34BA36, {T_MAX:.0f}s, winner P1 &middot;
  rebuilt from a v3 replay with <code>--economy-dump</code> &middot;
  final income, money and castle HP all reproduce the recorded row exactly
</footer>
</div>
"""

io.open(OUT, "w", encoding="utf-8").write(HTML)
print(f"wrote {OUT} ({len(HTML):,} bytes)")
print(f"  Marc  {M_UNITS} units / {M_GAD} gadgets / {M_ALL} actions / ${HL['p1spent']:,.0f}")
print(f"  bot   {O_UNITS} units / {O_GAD} gadgets / {O_ALL} actions / ${HL['p2spent']:,.0f}")
