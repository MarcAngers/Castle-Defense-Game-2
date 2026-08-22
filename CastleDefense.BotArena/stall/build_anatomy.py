"""Builds the game-anatomy report: castle HP over time, enemy pressure below it on a shared
x-axis (never a second y-scale), and the army that produced each collapse."""
import io, json, os

SRC = r"C:/Users/marcf/AppData/Local/Temp/claude/C--repos-Castle-Defense-Game-2-CastleDefenseGame2/eb08b672-8094-4486-92e3-5a87ed2f5670/scratchpad"
OUT = r"C:/repos/Castle-Defense-Game-2/CastleDefense.BotArena/stall/mirror_anatomy.html"
D = json.load(io.open(os.path.join(SRC, "mirror_chart.json"), encoding="utf-8"))

series, snaps, final = D["series"], D["snaps"], D["final"]
T_MAX = max(p[0] for p in series)
HP_MAX = max(max(p[2] for p in series), max(p[1] for p in series))
DPS_MAX = max(p[3] for p in series)

# ---- geometry (one shared time axis; two stacked plots, never a dual y-scale)
W, PADL, PADR = 1000, 68, 22
H_HP, H_DPS, PADT, PADB, GAP = 240, 150, 16, 30, 44
IW = W - PADL - PADR

def x(t): return PADL + IW * (t / T_MAX)
def yhp(v): return PADT + H_HP * (1 - v / HP_MAX)
def ydps(v): return PADT + H_DPS * (1 - v / DPS_MAX)

def path(pts, fy, idx):
    d = []
    for i, p in enumerate(pts):
        d.append(("M" if i == 0 else "L") + f"{x(p[0]):.1f},{fy(p[idx]):.1f}")
    return "".join(d)

hp_path   = path(series, yhp, 1)
max_path  = path(series, yhp, 2)
dps_path  = path(series, ydps, 3)
dps_area  = dps_path + f"L{x(series[-1][0]):.1f},{ydps(0):.1f}L{x(series[0][0]):.1f},{ydps(0):.1f}Z"

def ticks(maxv, n=4):
    step = maxv / n
    return [round(step * i) for i in range(n + 1)]

hp_ticks  = ticks(HP_MAX)
dps_ticks = ticks(DPS_MAX, 3)
t_ticks   = [0, 40, 80, 120, 160, int(T_MAX)]

def fmt(v):
    return f"{v/1000:.0f}k" if v >= 1000 else str(v)

# ---- marker rails
markers_hp, markers_dps, snapcards = "", "", ""
for i, s in enumerate(snaps):
    px = x(s["t"])
    markers_hp += (f'<line class="mark" x1="{px:.1f}" y1="{PADT}" x2="{px:.1f}" y2="{PADT+H_HP}"/>'
                   f'<circle class="markdot" cx="{px:.1f}" cy="{yhp(s["hp"]):.1f}" r="4.5"/>'
                   f'<text class="marklab" x="{px:.1f}" y="{PADT-4}" text-anchor="middle">{chr(65+i)}</text>')
    markers_dps += (f'<line class="mark" x1="{px:.1f}" y1="{PADT}" x2="{px:.1f}" y2="{PADT+H_DPS}"/>'
                    f'<circle class="markdot dps" cx="{px:.1f}" cy="{ydps(s["dps"]):.1f}" r="4.5"/>')

    total = sum(s["tiers"].values())
    bar, acc = "", 0.0
    ramp = {"T4": "var(--ord-1)", "T5": "var(--ord-2)", "T7": "var(--ord-3)",
            "T6": "var(--ord-2)", "T8": "var(--ord-3)", "T3": "var(--ord-1)"}
    for tier, cnt in sorted(s["tiers"].items()):
        wpc = 100.0 * cnt / total
        bar += (f'<span class="seg" style="width:{wpc:.2f}%;background:{ramp.get(tier,"var(--ord-2)")}" '
                f'title="{tier} x{cnt}"></span>')
        acc += wpc
    chips = "".join(f'<span class="chip"><i style="background:{ramp.get(t,"var(--ord-2)")}"></i>{t}&thinsp;&times;&thinsp;{c}</span>'
                    for t, c in sorted(s["tiers"].items()))
    snapcards += f"""
    <div class="snap">
      <div class="snap-h"><span class="badge">{chr(65+i)}</span><span class="snap-t">t = {s['t']:.0f}s</span>
        <span class="snap-rate">{s['rate']:,} HP/s</span></div>
      <div class="bar">{bar}</div>
      <div class="chips">{chips}</div>
      <dl>
        <dt>castle HP</dt><dd>{s['hp']:,} <span class="dim">/ {s['maxhp']:,}</span></dd>
        <dt>enemy units</dt><dd>{s['enemy']}</dd>
        <dt>incoming DPS</dt><dd>{s['dps']:,}</dd>
        <dt>swings / sec</dt><dd>{s['swings']}</dd>
        <dt>army value</dt><dd>${s['value']:,}</dd>
        <dt>our blockers</dt><dd>{s['own']}</dd>
        <dt>bot chose</dt><dd class="choice">{s['choice'] or '&mdash;'}</dd>
      </dl>
    </div>"""

hp_grid = "".join(f'<line class="grid" x1="{PADL}" y1="{yhp(v):.1f}" x2="{W-PADR}" y2="{yhp(v):.1f}"/>'
                  f'<text class="ax" x="{PADL-9}" y="{yhp(v)+3.5:.1f}" text-anchor="end">{fmt(v)}</text>'
                  for v in hp_ticks)
dps_grid = "".join(f'<line class="grid" x1="{PADL}" y1="{ydps(v):.1f}" x2="{W-PADR}" y2="{ydps(v):.1f}"/>'
                   f'<text class="ax" x="{PADL-9}" y="{ydps(v)+3.5:.1f}" text-anchor="end">{fmt(v)}</text>'
                   for v in dps_ticks)
x_axis = "".join(f'<text class="ax" x="{x(t):.1f}" y="{PADT+H_DPS+22}" text-anchor="middle">{t}s</text>'
                 for t in t_ticks)

rows_tbl = "".join(
    f"<tr><td>{chr(65+i)}</td><td>{s['t']:.0f}s</td><td>{s['rate']:,}</td><td>{s['hp']:,}</td>"
    f"<td>{s['enemy']}</td><td>{s['dps']:,}</td><td>{s['value']:,}</td>"
    f"<td>{', '.join(f'{t}×{c}' for t,c in sorted(s['tiers'].items()))}</td></tr>"
    for i, s in enumerate(snaps))

HTML = f"""<title>Anatomy of a Lost Mirror</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Archivo:wght@500;600;700&family=Newsreader:ital,opsz,wght@0,6..72,400;0,6..72,500;1,6..72,400&family=IBM+Plex+Mono:wght@400;500;600&display=swap">
<style>
:root {{
  --ground:#f5f3ee; --surface:#fffdf9; --sunken:#edeae2;
  --ink:#1b1813; --ink-soft:#5a5346; --ink-dim:#8a8271;
  --rule:#dad5c8; --rule-soft:#e7e3d9; --accent:#8a6212; --accent-w:#f0e4c8;
  --hp:#2a78d6; --dps:#eb6834;
  --ord-1:#86b6ef; --ord-2:#2a78d6; --ord-3:#104281;
}}
@media (prefers-color-scheme: dark) {{
  :root:not([data-theme="light"]) {{
    --ground:#131210; --surface:#1c1a16; --sunken:#242119;
    --ink:#efebe1; --ink-soft:#b3ab99; --ink-dim:#837b69;
    --rule:#343026; --rule-soft:#282419; --accent:#d6a741; --accent-w:#3a2e14;
    --hp:#3987e5; --dps:#d95926;
    --ord-1:#b7d3f6; --ord-2:#5598e7; --ord-3:#184f95;
  }}
}}
:root[data-theme="dark"] {{
  --ground:#131210; --surface:#1c1a16; --sunken:#242119;
  --ink:#efebe1; --ink-soft:#b3ab99; --ink-dim:#837b69;
  --rule:#343026; --rule-soft:#282419; --accent:#d6a741; --accent-w:#3a2e14;
  --hp:#3987e5; --dps:#d95926;
  --ord-1:#b7d3f6; --ord-2:#5598e7; --ord-3:#184f95;
}}
*{{box-sizing:border-box}}
body{{background:var(--ground);color:var(--ink);margin:0;padding:0 24px 88px;
     font-family:"Newsreader",Georgia,serif;font-size:17px;line-height:1.62;-webkit-font-smoothing:antialiased}}
.wrap{{max-width:1060px;margin:0 auto}}
.prose{{max-width:65ch}}
h1,h2,h3,.lab,th,.stat-n,.badge{{font-family:"Archivo",system-ui,sans-serif}}
h1{{font-size:clamp(2rem,4.6vw,2.9rem);font-weight:700;letter-spacing:-.028em;line-height:1.05;margin:0 0 .5rem;text-wrap:balance}}
h2{{font-size:1.3rem;font-weight:600;letter-spacing:-.012em;margin:0;text-wrap:balance}}
h3{{font-size:.95rem;font-weight:600;margin:0 0 .5rem}}
p{{margin:0 0 1rem}} p:last-child{{margin-bottom:0}}
strong{{font-weight:500;background:var(--accent-w);padding:0 .2em;border-radius:2px}}
code{{font-family:"IBM Plex Mono",monospace;font-size:.84em;background:var(--sunken);color:var(--ink-soft);padding:.12em .38em;border-radius:3px}}
.lab{{font-size:.7rem;font-weight:600;letter-spacing:.13em;text-transform:uppercase;color:var(--ink-dim)}}
header{{padding:64px 0 32px;border-bottom:2px solid var(--ink)}}
.kicker{{display:flex;align-items:baseline;gap:12px;margin-bottom:18px}}
.kicker .dot{{width:9px;height:9px;background:var(--accent);transform:rotate(45deg);flex:none}}
.dek{{font-size:1.2rem;color:var(--ink-soft);max-width:62ch;margin:0}}
section{{margin-top:56px}}
.shead{{display:flex;align-items:baseline;gap:14px;padding-bottom:12px;margin-bottom:20px;border-bottom:1px solid var(--rule)}}
.shead .n{{font-family:"IBM Plex Mono",monospace;font-size:.74rem;font-weight:600;color:var(--accent);flex:none}}
figure{{margin:0;background:var(--surface);border:1px solid var(--rule);padding:18px 16px 8px}}
.figtitle{{font-family:"Archivo",sans-serif;font-size:.95rem;font-weight:600;margin:0 0 2px 4px}}
.figsub{{font-size:.83rem;color:var(--ink-dim);margin:0 0 10px 4px}}
.scroll{{overflow-x:auto}}
svg{{display:block;width:100%;height:auto;font-family:"IBM Plex Mono",monospace}}
.grid{{stroke:var(--rule-soft);stroke-width:1}}
.ax{{fill:var(--ink-dim);font-size:10.5px}}
.hpline{{fill:none;stroke:var(--hp);stroke-width:2;stroke-linejoin:round}}
.maxline{{fill:none;stroke:var(--ink-dim);stroke-width:1.5;stroke-dasharray:4 3;opacity:.75}}
.dpsline{{fill:none;stroke:var(--dps);stroke-width:2;stroke-linejoin:round}}
.dpsarea{{fill:var(--dps);opacity:.13}}
.mark{{stroke:var(--accent);stroke-width:1;stroke-dasharray:2 3;opacity:.7}}
.markdot{{fill:var(--hp);stroke:var(--surface);stroke-width:2}}
.markdot.dps{{fill:var(--dps)}}
.marklab{{fill:var(--accent);font-size:11px;font-weight:600}}
.legend{{display:flex;flex-wrap:wrap;gap:18px;margin:10px 0 4px 4px;font-family:"Archivo",sans-serif;font-size:.78rem;color:var(--ink-soft)}}
.legend i{{display:inline-block;width:16px;height:3px;border-radius:2px;margin-right:7px;vertical-align:middle}}
.snaps{{display:grid;gap:1px;background:var(--rule);border:1px solid var(--rule);grid-template-columns:repeat(auto-fit,minmax(215px,1fr));margin-top:22px}}
.snap{{background:var(--surface);padding:16px 15px}}
.snap-h{{display:flex;align-items:center;gap:8px;margin-bottom:10px}}
.badge{{display:inline-flex;align-items:center;justify-content:center;width:20px;height:20px;background:var(--accent);color:var(--ground);font-size:.72rem;font-weight:700;border-radius:2px}}
.snap-t{{font-family:"IBM Plex Mono",monospace;font-size:.82rem;color:var(--ink-soft)}}
.snap-rate{{margin-left:auto;font-family:"IBM Plex Mono",monospace;font-size:.82rem;font-weight:600;color:var(--dps)}}
.bar{{display:flex;height:9px;border-radius:2px;overflow:hidden;background:var(--sunken);gap:2px}}
.seg{{display:block;height:100%}}
.chips{{display:flex;flex-wrap:wrap;gap:8px;margin:9px 0 12px;font-family:"Archivo",sans-serif;font-size:.73rem;color:var(--ink-soft)}}
.chips i{{display:inline-block;width:8px;height:8px;border-radius:2px;margin-right:5px;vertical-align:baseline}}
dl{{display:grid;grid-template-columns:auto auto;gap:3px 10px;margin:0;font-family:"IBM Plex Mono",monospace;font-size:.76rem}}
dt{{color:var(--ink-dim)}} dd{{margin:0;text-align:right;font-variant-numeric:tabular-nums}}
.dim{{color:var(--ink-dim)}}
.choice{{color:var(--dps);font-weight:600}}
table{{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums;margin-top:10px}}
th,td{{font-family:"IBM Plex Mono",monospace;font-size:.79rem;text-align:right;padding:7px 10px;border-bottom:1px solid var(--rule-soft);white-space:nowrap}}
th{{font-family:"Archivo",sans-serif;font-weight:600;font-size:.72rem;color:var(--ink-soft);background:var(--sunken)}}
td:first-child,th:first-child,td:last-child,th:last-child{{text-align:left}}
.callout{{border-left:3px solid var(--accent);background:var(--surface);padding:18px 22px;margin:24px 0}}
.cap{{font-size:.84rem;color:var(--ink-dim);margin-top:10px;max-width:68ch;line-height:1.5}}
footer{{margin-top:70px;padding-top:18px;border-top:1px solid var(--rule);font-size:.83rem;color:var(--ink-dim)}}
</style>

<div class="wrap">
<header>
  <div class="kicker"><span class="dot"></span><span class="lab">Single game &middot; White / nuke / reinforcements mirror</span></div>
  <h1>The bot is fine for 160 seconds, then dies in 30.</h1>
  <p class="dek">Our defence-only bot against the shipped attacking bot, same team and same
  loadout. Nothing gameplay-affecting varies in this matchup, so this is the whole story of it
  &mdash; not a sample of one, but the only game there is.</p>
</header>

<section>
  <div class="shead"><span class="n">01</span><h2>Castle health, and the pressure that took it</h2></div>
  <div class="prose">
    <p>Two measures on one shared clock. The dashed line is the health <em>ceiling</em> &mdash;
    it steps up every time the bot repairs, which is why health can climb.</p>
  </div>

  <figure>
    <p class="figtitle">Our castle health</p>
    <p class="figsub">health and its repaired ceiling, over the whole game</p>
    <div class="scroll"><svg viewBox="0 0 {W} {PADT+H_HP+16}" role="img"
         aria-label="Castle health over time, flat until 160 seconds then collapsing to zero at 193 seconds">
      {hp_grid}{markers_hp}
      <path class="maxline" d="{max_path}"/>
      <path class="hpline" d="{hp_path}"/>
    </svg></div>
    <div class="legend">
      <span><i style="background:var(--hp)"></i>castle health</span>
      <span><i style="background:var(--ink-dim)"></i>health ceiling (raised by repairs)</span>
      <span><i style="background:var(--accent)"></i>A&ndash;E: steepest loss windows</span>
    </div>
  </figure>

  <figure style="margin-top:18px">
    <p class="figtitle">Incoming damage per second</p>
    <p class="figsub">what the enemy army on the field could do to the castle, unblocked</p>
    <div class="scroll"><svg viewBox="0 0 {W} {PADT+H_DPS+30}" role="img"
         aria-label="Enemy damage per second over time, near zero until 160 seconds then spiking above 27000">
      {dps_grid}{markers_dps}
      <path class="dpsarea" d="{dps_area}"/>
      <path class="dpsline" d="{dps_path}"/>
      {x_axis}
    </svg></div>
    <div class="legend"><span><i style="background:var(--dps)"></i>incoming DPS</span></div>
  </figure>

  <p class="cap">Deliberately two charts rather than one with two vertical scales. Health peaks at
  78,000 and incoming damage at 27,435; drawing them against a shared axis would make one of them
  a flat line and invent a crossing point that means nothing.</p>
</section>

<section>
  <div class="shead"><span class="n">02</span><h2>The army at each collapse</h2></div>
  <div class="prose">
    <p>Five steepest two-second windows, and what was standing on the field at each.</p>
  </div>
  <div class="snaps">{snapcards}</div>

  <div class="callout">
    <h3>It is a tier-4 swarm, not the big units</h3>
    <p>At the worst moment the field holds <strong>43 tier-4 units</strong> against 7 tier 7s
    &mdash; 52 attackers throwing <strong>222 swings a second</strong> for 27,435 damage, and the
    castle loses 24,045 health in a second. The whole army cost the attacker $15,398.</p>
    <p>That is the same pattern the balance sweeps turned up independently: a tier-4 stream razes a
    castle in comparable time to a tier 8 for roughly 28&times; less money. The shipped bot has
    found it on its own, and it is what kills us.</p>
  </div>
</section>

<section>
  <div class="shead"><span class="n">03</span><h2>What the bot was doing about it</h2></div>
  <div class="prose">
    <p>The <em>bot chose</em> row on each card is the branch that won that decision. At window A,
    with 5 tier-5 units doing 100 damage a second, it is idle &mdash; correctly, that attack would
    take three minutes to matter. From window B onward it is in <code>critical</code>, meaning the
    survival law has already put it inside its own death window and the economics are switched off.</p>
    <p>So the bot never gets a middle game. It goes from "this is not worth answering" to "we are
    dying" with almost nothing in between, because the attack that kills it assembles in under
    thirty seconds.</p>
  </div>
  <div class="scroll"><table>
    <thead><tr><th></th><th>time</th><th>HP/s</th><th>health</th><th>units</th><th>DPS</th><th>army $</th><th>composition</th></tr></thead>
    <tbody>{rows_tbl}</tbody>
  </table></div>
  <p class="cap">Table view of the same five windows, for reading without relying on the marks.</p>
</section>

<footer>
  Castle Defense engine &middot; one deterministic game, dumped every 3 ticks &middot;
  defence-only bot in seat 1 against the shipped bot in seat 2 &middot; final: health 0 of
  {final['maxhp']:,} at {final['t']:.0f}s, {final['inv']} investments earned
</footer>
</div>
"""

io.open(OUT, "w", encoding="utf-8").write(HTML)
print(f"wrote {OUT} ({len(HTML)} bytes)")
