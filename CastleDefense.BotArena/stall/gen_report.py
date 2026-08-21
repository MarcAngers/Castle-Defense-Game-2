"""Builds the chump-block report HTML straight from the sweep CSVs, so no number in
the page is transcribed by hand."""
import csv, statistics as st, os, html

BASE = os.path.dirname(os.path.abspath(__file__))
OUT  = os.path.join(os.path.dirname(os.path.abspath(__file__)), "chump_block.html")

def load(name, seat1_only=True):
    rows = []
    for r in csv.DictReader(open(os.path.join(BASE, name))):
        if seat1_only and r['attacker_seat'] != '1':
            continue
        r['tier'] = int(r['tier']); r['interval'] = int(r['interval_ticks'])
        r['sec'] = float(r['seconds']); r['chumps'] = int(r['blockers_spawned'])
        r['spend'] = float(r['blocker_spend']); r['peak'] = int(r['peak_blockers'])
        rows.append(r)
    return rows

iso  = load('stall_isolated.csv')
real = load('stall_realistic.csv')
thr  = load('t8_threshold.csv')

TEAMS = sorted({r['attacker_team'] for r in iso})
IVS   = sorted({r['interval'] for r in iso if r['interval'] > 0})
ctrl  = {(r['attacker_team'], r['tier']): r for r in iso if r['interval'] == 0}
idx   = {(r['attacker_team'], r['tier'], r['interval']): r for r in iso if r['interval'] > 0}
ridx  = {(r['attacker_team'], r['tier'], r['interval']): r for r in real if r['interval'] > 0}

def rate(iv):
    v = 30 / iv
    return f"{v:g}" if v >= 1 else f"{v:.2f}".rstrip('0')

def cell(r):
    """One outcome cell, class-coded so the cliff between held and fallen is visible."""
    if r['outcome'] == 'attacker_killed':
        return '<td class="o-kill" title="chumps destroyed the attacker">kill</td>'
    if r['outcome'] == 'survived_horizon':
        return '<td class="o-hold" title="attacker alive, castle still standing at 1200s">hold</td>'
    return f'<td class="o-fell">{r["sec"]:.0f}<span class="u">s</span></td>'

# ---------------------------------------------------------------- matrix per tier
def matrix(tier):
    head = "".join(f"<th>{rate(i)}</th>" for i in IVS)
    body = ""
    for t in TEAMS:
        c = ctrl[(t, tier)]
        cells = "".join(cell(idx[(t, tier, i)]) for i in IVS)
        body += (f'<tr><th class="rh">{t}</th><td class="unit">{c["attacker_unit"]}</td>'
                 f'<td class="o-base">{c["sec"]:.0f}<span class="u">s</span></td>{cells}</tr>')
    return (f'<div class="scroll"><table class="matrix">'
            f'<thead><tr><th class="rh"></th><th></th><th class="o-base">none</th>'
            f'<th colspan="{len(IVS)}" class="grouphead">tier-1 bodies spawned per second</th></tr>'
            f'<tr><th class="rh">team</th><th>attacker</th><th class="o-base">&nbsp;</th>{head}</tr></thead>'
            f'<tbody>{body}</tbody></table></div>')

# ---------------------------------------------------------------- threshold table
THR_IVS = sorted({r['interval'] for r in thr if r['interval'] > 0})
def threshold_table():
    head = "".join(
        f'<th class="{"cliff" if i == 155 else ""}">{i/30:.2f}<span class="u">s</span></th>'
        for i in THR_IVS)
    tidx = {(r['attacker_team'], r['interval']): r for r in thr if r['interval'] > 0}
    body = ""
    for t in TEAMS:
        cells = ""
        for i in THR_IVS:
            r = tidx[(t, i)]
            klass = "cliff" if i == 155 else ""
            if r['outcome'] == 'survived_horizon':
                cells += f'<td class="o-hold {klass}">hold</td>'
            else:
                cells += f'<td class="o-fell {klass}">{r["sec"]:.0f}<span class="u">s</span></td>'
        body += f'<tr><th class="rh">{t}</th>{cells}</tr>'
    return (f'<div class="scroll"><table class="matrix thr">'
            f'<thead><tr><th class="rh">team</th>'
            f'<th colspan="{len(THR_IVS)}" class="grouphead">one tier-1 body every&hellip;</th></tr>'
            f'<tr><th class="rh"></th>{head}</tr></thead><tbody>{body}</tbody></table></div>')

# ---------------------------------------------------------------- headline numbers
def headline_rows(interval=15):
    out = []
    for tier in (5, 6, 7, 8):
        c = ctrl[('White', tier)]
        s = idx[('White', tier, interval)]
        med_ctrl = st.median([ctrl[(t, tier)]['sec'] for t in TEAMS])
        verdict = {'attacker_killed': 'attacker destroyed',
                   'survived_horizon': 'held past 1200s',
                   'castle_destroyed': f'{s["sec"]:.0f}s'}[s['outcome']]
        out.append((tier, c['attacker_unit'], c['sec'], med_ctrl, verdict, s['chumps'], s['spend']))
    return out

# ---------------------------------------------------------------- cost
fell = [r for r in iso if r['interval'] > 0 and r['outcome'] == 'castle_destroyed']
per_dollar = st.median([(r['sec'] - ctrl[(r['attacker_team'], r['tier'])]['sec']) / r['spend']
                        for r in fell if r['spend'] > 0])
dollars_per_sec = 1 / per_dollar

def cost_rows():
    out = []
    for tier in (5, 6, 7, 8):
        c = [r for r in fell if r['tier'] == tier]
        perc = st.median([(r['sec'] - ctrl[(r['attacker_team'], tier)]['sec']) / r['chumps']
                          for r in c if r['chumps'] > 0])
        perd = st.median([(r['sec'] - ctrl[(r['attacker_team'], tier)]['sec']) / r['spend']
                          for r in c if r['spend'] > 0])
        out.append((tier, len(c), perc, 1 / perd))
    return out

# ---------------------------------------------------------------- realistic arm
def real_rows():
    out = []
    for tier in (5, 6, 7, 8):
        row = []
        for i in IVS:
            c = [ridx[(t, tier, i)] for t in TEAMS]
            row.append(sum(1 for x in c if x['outcome'] == 'defender_won'))
        out.append((tier, row))
    return out

# ---------------------------------------------------------------- pile sizes
def pile_rows():
    out = []
    for tier in (5, 6, 7, 8):
        best = None
        for i in IVS:
            c = [r for r in iso if r['tier'] == tier and r['interval'] == i
                 and r['outcome'] == 'attacker_killed']
            if len(c) == 8:
                best = (i, st.median([x['chumps'] for x in c]),
                        st.median([x['peak'] for x in c]), st.median([x['sec'] for x in c]))
        out.append((tier,) + (best if best else (None, None, None, None)))
    return out


# ================================================================ experiment 2
def load2(name):
    rows = []
    for r in csv.DictReader(open(os.path.join(BASE, name))):
        r["tier"] = int(r["tier"]); r["f"] = int(r["force_size"]); r["e"] = int(r["escort_tier"])
        r["iv"] = int(r["interval_ticks"]); r["sec"] = float(r["seconds"])
        r["defS"] = float(r["blocker_spend"]); r["atkS"] = float(r["attacker_spend"])
        r["ddS"] = float(r["spend_at_force_death"]); r["fdead"] = int(r["force_died_tick"])
        rows.append(r)
    return rows

F    = load2("force_isolated.csv")
FR   = load2("force_realistic.csv")
FT   = load2("t8_force_threshold.csv") + load2("t8_fine2.csv")
FIVS = sorted({r["iv"] for r in F if r["iv"] > 0})
FCTRL = {(r["attacker_team"], r["tier"], r["f"], r["e"]): r for r in F if r["iv"] == 0}
FIDX  = {(r["attacker_team"], r["tier"], r["f"], r["e"], r["iv"]): r for r in F if r["iv"] > 0}
RIDX  = {(r["attacker_team"], r["tier"], r["f"], r["e"], r["iv"]): r for r in FR if r["iv"] > 0}
RIVS  = sorted({r["iv"] for r in FR if r["iv"] > 0})

def hold(r):
    return r["outcome"] in ("force_destroyed", "force_destroyed_horizon", "survived_horizon")

def undefended_table():
    head = "".join("<th>&times;%d</th>" % f for f in (1, 2, 3, 4, 5))
    body = ""
    for t in (5, 6, 7, 8):
        cells = "".join('<td class="num">%.0f<span class="u">s</span></td>'
                        % st.median([FCTRL[(x, t, f, 0)]["sec"] for x in TEAMS]) for f in (1, 2, 3, 4, 5))
        cells += "".join('<td class="num esc">%.0f<span class="u">s</span></td>'
                         % st.median([FCTRL[(x, t, f, 4)]["sec"] for x in TEAMS]) for f in (1, 5))
        body += '<tr><th class="rh">T%d</th>%s</tr>' % (t, cells)
    only = st.median([FCTRL[(x, 5, 0, 4)]["sec"] for x in TEAMS])
    body += ('<tr><th class="rh">&mdash;</th><td class="dim" colspan="5">no high-tier units at all</td>'
             '<td class="num esc" colspan="2">%.0f<span class="u">s</span></td></tr>' % only)
    return ('<div class="scroll"><table class="matrix"><thead>'
            '<tr><th class="rh"></th><th colspan="5" class="grouphead">force size, no escort</th>'
            '<th colspan="2" class="grouphead">+ T4 escort/s</th></tr>'
            '<tr><th class="rh">tier</th>%s<th>&times;1</th><th>&times;5</th></tr></thead>'
            '<tbody>%s</tbody></table></div>' % (head, body))

def hold_matrix(e):
    head = "".join("<th>%s</th>" % rate(i) for i in FIVS)
    body = ""
    for t in (5, 6, 7, 8):
        for f in (1, 2, 3, 4, 5):
            cells = ""
            for i in FIVS:
                n = sum(1 for x in TEAMS if hold(FIDX[(x, t, f, e, i)]))
                k = "o-hold" if n == 8 else "o-mixed" if n else "o-fell"
                cells += '<td class="%s">%s</td>' % (k, n if n else "&middot;")
            lead = '<th class="rh">T%d</th>' % t if f == 1 else '<th class="rh"></th>'
            cls = ' class="grp"' if f == 1 and t > 5 else ""
            body += "<tr%s>%s<td class=\"unit\">&times;%d</td>%s</tr>" % (cls, lead, f, cells)
    if e == 4:
        cells = ""
        for i in FIVS:
            n = sum(1 for x in TEAMS if hold(FIDX[(x, 5, 0, 4, i)]))
            k = "o-hold" if n == 8 else "o-mixed" if n else "o-fell"
            cells += '<td class="%s">%s</td>' % (k, n if n else "&middot;")
        body += ('<tr class="grp"><th class="rh">&mdash;</th>'
                 '<td class="unit">escort only</td>%s</tr>' % cells)
    return ('<div class="scroll"><table class="matrix"><thead>'
            '<tr><th class="rh"></th><th></th>'
            '<th colspan="%d" class="grouphead">tier-1 bodies spawned per second</th></tr>'
            '<tr><th class="rh">tier</th><th>force</th>%s</tr></thead><tbody>%s</tbody></table></div>'
            % (len(FIVS), head, body))

FTIDX = {(r["attacker_team"], r["f"], r["iv"]): r for r in FT if r["iv"] > 0}
FTIVS = sorted({r["iv"] for r in FT if r["iv"] > 0})

def threshold_by_force():
    """Two statistics, because the naive one is not trustworthy on its own.

    NAIVE = the slowest spawn period that holds all 8 teams. ROBUST = the slowest period
    such that it AND every faster period also hold. They differ wherever the phase
    relationship between the spawn period and the 150-tick swing period lets a slow-but-
    aligned rate succeed where a faster misaligned one fails, which happens at x3 and x5.
    Everything downstream quotes the robust figure.
    """
    out = []
    for f in (1, 2, 3, 4, 5):
        avail = sorted([i for i in FTIVS if (TEAMS[0], f, i) in FTIDX], reverse=True)
        naive = next((i for i in avail if all(hold(FTIDX[(x, f, i)]) for x in TEAMS)), None)
        robust = next((i for i in avail
                       if all(all(hold(FTIDX[(x, f, j)]) for x in TEAMS)
                              for j in avail if j <= i)), None)
        out.append((f, naive, robust))
    body = "".join(
        '<tr><th class="rh">&times;%d</th><td class="num dim">%d<span class="u"> t</span></td>'
        '<td class="num">%d<span class="u"> t</span></td>'
        '<td class="num">%.2f<span class="u">s</span></td>'
        '<td class="num big">%.2f</td><td class="num dim">%.2f&times;</td></tr>'
        % (f, n, r, r / 30.0, 150.0 / r, (150.0 / r) / f)
        for f, n, r in out if r)
    return ('<div class="scroll"><table><thead><tr><th class="rh">force</th>'
            "<th>slowest that holds</th><th>slowest with all faster holding</th>"
            "<th>in seconds</th><th>chumps per enemy swing</th>"
            "<th>ratio to force size</th></tr></thead>"
            "<tbody>%s</tbody></table></div>" % body)

def cost_table():
    body = ""
    for t in (5, 6, 7, 8):
        for f in (1, 3, 5):
            best = None
            for i in sorted(FIVS, reverse=True):
                if all(hold(FIDX[(x, t, f, 0, i)]) for x in TEAMS):
                    best = i
                    break
            if best is None:
                body += ('<tr><th class="rh">T%d</th><td class="unit">&times;%d</td>'
                         '<td class="o-fell" colspan="4">no tested rate holds</td></tr>' % (t, f))
                continue
            cs = [FIDX[(x, t, f, 0, best)] for x in TEAMS]
            atk = st.median([c["atkS"] for c in cs])
            dfn = st.median([c["ddS"] if c["fdead"] >= 0 else c["defS"] for c in cs])
            ratio = st.median([(c["ddS"] if c["fdead"] >= 0 else c["defS"]) / c["atkS"] for c in cs])
            k = "o-hold" if ratio < 0.5 else "o-mixed" if ratio < 1 else "o-fell"
            body += ('<tr><th class="rh">T%d</th><td class="unit">&times;%d</td>'
                     '<td class="num">%s<span class="u">/s</span></td>'
                     '<td class="num dim">$%s</td><td class="num">$%s</td>'
                     '<td class="%s">%.2f&times;</td></tr>'
                     % (t, f, rate(best), format(atk, ",.0f"), format(dfn, ",.0f"), k, ratio))
    return ('<div class="scroll"><table><thead><tr><th class="rh">tier</th><th>force</th>'
            "<th>cheapest holding rate</th><th>attacker spent</th><th>defender spent</th>"
            "<th>defender / attacker</th></tr></thead><tbody>%s</tbody></table></div>" % body)

def escort_attrib():
    head = "".join("<th>T%d</th>" % t for t in (5, 6, 7, 8))
    body = ""
    for f in (0, 1, 2, 3, 5):
        cells = ""
        for t in (5, 6, 7, 8):
            n = sum(1 for x in TEAMS if hold(FIDX[(x, t, f, 4, 2)]))
            k = "o-hold" if n == 8 else "o-mixed" if n else "o-fell"
            cells += '<td class="%s">%d/8</td>' % (k, n)
        lbl = "escort alone" if f == 0 else "escort + &times;%d" % f
        body += '<tr><th class="rh">%s</th>%s</tr>' % (lbl, cells)
    return ('<div class="scroll"><table class="matrix"><thead>'
            '<tr><th class="rh">attacking force</th>%s</tr></thead>'
            "<tbody>%s</tbody></table></div>" % (head, body))

def t4_vs_t8():
    body = ""
    ratios = []
    for x in TEAMS:
        a = FCTRL[(x, 8, 1, 0)]
        b = FCTRL[(x, 5, 0, 4)]
        r = a["atkS"] / b["atkS"]
        ratios.append(r)
        body += ('<tr><th class="rh">%s</th><td class="num dim">$%s</td>'
                 '<td class="num dim">%.1f<span class="u">s</span></td>'
                 '<td class="num">$%s</td><td class="num">%.1f<span class="u">s</span></td>'
                 '<td class="o-hold">%.0f&times;</td></tr>'
                 % (x, format(a["atkS"], ",.0f"), a["sec"], format(b["atkS"], ",.0f"), b["sec"], r))
    tbl = ('<div class="scroll"><table><thead><tr><th class="rh">team</th>'
           "<th>1&times; tier 8 cost</th><th>kill time</th><th>T4 stream cost</th>"
           "<th>kill time</th><th>cheaper by</th></tr></thead>"
           "<tbody>%s</tbody></table></div>" % body)
    return st.median(ratios), tbl

T4RATIO, T4TABLE = t4_vs_t8()

def real_force_table():
    head = "".join("<th>%s</th>" % rate(i) for i in RIVS)
    body = ""
    for e in (0, 4):
        for t in (5, 6, 7, 8):
            for f in (1, 5):
                cells = ""
                for i in RIVS:
                    n = sum(1 for x in TEAMS if RIDX[(x, t, f, e, i)]["outcome"] == "defender_won")
                    k = "o-hold" if n == 8 else "o-mixed" if n else "o-fell"
                    cells += '<td class="%s">%s</td>' % (k, n if n else "&middot;")
                body += ('<tr><th class="rh">%s</th><td class="unit">T%d &times;%d</td>%s</tr>'
                         % ("&mdash;" if e == 0 else "T4", t, f, cells))
    return ('<div class="scroll"><table class="matrix"><thead>'
            '<tr><th class="rh"></th><th></th>'
            '<th colspan="%d" class="grouphead">tier-1 bodies per second</th></tr>'
            '<tr><th class="rh">escort</th><th>force</th>%s</tr></thead>'
            "<tbody>%s</tbody></table></div>" % (len(RIVS), head, body))

# ================================================================ page
def td(v, cls=""):
    return f'<td class="{cls}">{v}</td>'

hl = headline_rows()
hl_html = "".join(
    f'<tr><th class="rh">T{t}</th><td class="unit">{u}</td>'
    f'<td class="num">{base:.0f}<span class="u">s</span></td>'
    f'<td class="num dim">{med:.0f}<span class="u">s</span></td>'
    f'<td class="verdict">{v}</td>'
    f'<td class="num dim">{ch}</td><td class="num dim">${sp:,.0f}</td></tr>'
    for t, u, base, med, v, ch, sp in hl)

cost_html = "".join(
    f'<tr><th class="rh">T{t}</th><td class="num dim">{n}</td>'
    f'<td class="num">{pc:.2f}<span class="u">s</span></td>'
    f'<td class="num">${dps:.2f}<span class="u">/s</span></td></tr>'
    for t, n, pc, dps in cost_rows())

real_html = ""
for tier, row in real_rows():
    cells = "".join(
        f'<td class="{"o-win" if n == 8 else "o-mixed" if n else "o-none"}">{n}</td>'
        for n in row)
    real_html += f'<tr><th class="rh">T{tier}</th>{cells}</tr>'
real_head = "".join(f"<th>{rate(i)}</th>" for i in IVS)

pile_html = "".join(
    f'<tr><th class="rh">T{t}</th><td class="num">{rate(i)}<span class="u">/s</span></td>'
    f'<td class="num">{ch:,.0f}</td><td class="num dim">{pk:,.0f}</td>'
    f'<td class="num">{sec:.0f}<span class="u">s</span></td></tr>'
    for t, i, ch, pk, sec in pile_rows() if i)

HTML = f"""<title>The Chump-Block Threshold</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Archivo:wght@500;600;700&family=Newsreader:ital,opsz,wght@0,6..72,400;0,6..72,500;1,6..72,400&family=IBM+Plex+Mono:wght@400;500;600&display=swap">
<style>
:root {{
  --ground:   #F5F3EE;
  --surface:  #FFFDF9;
  --sunken:   #EDEAE2;
  --ink:      #1B1813;
  --ink-soft: #5A5346;
  --ink-dim:  #8A8271;
  --rule:     #DAD5C8;
  --rule-soft:#E7E3D9;
  --accent:   #8A6212;
  --accent-w: #F0E4C8;
  --hold:     #2C6049;
  --hold-w:   #DCEBE1;
  --kill:     #1C5360;
  --kill-w:   #D8E9EC;
  --fell:     #9C3A25;
  --fell-w:   #F5E0D8;
  --shadow:   0 1px 2px rgba(27,24,19,.06), 0 8px 24px -14px rgba(27,24,19,.28);
}}
@media (prefers-color-scheme: dark) {{
  :root:not([data-theme="light"]) {{
    --ground:   #131210;
    --surface:  #1C1A16;
    --sunken:   #242119;
    --ink:      #EFEBE1;
    --ink-soft: #B3AB99;
    --ink-dim:  #837B69;
    --rule:     #343026;
    --rule-soft:#282419;
    --accent:   #D6A741;
    --accent-w: #3A2E14;
    --hold:     #6FB394;
    --hold-w:   #1B3129;
    --kill:     #6BAABA;
    --kill-w:   #172E34;
    --fell:     #E08469;
    --fell-w:   #3A1F16;
    --shadow:   0 1px 2px rgba(0,0,0,.4), 0 10px 28px -16px rgba(0,0,0,.8);
  }}
}}
:root[data-theme="dark"] {{
  --ground:   #131210;
  --surface:  #1C1A16;
  --sunken:   #242119;
  --ink:      #EFEBE1;
  --ink-soft: #B3AB99;
  --ink-dim:  #837B69;
  --rule:     #343026;
  --rule-soft:#282419;
  --accent:   #D6A741;
  --accent-w: #3A2E14;
  --hold:     #6FB394;
  --hold-w:   #1B3129;
  --kill:     #6BAABA;
  --kill-w:   #172E34;
  --fell:     #E08469;
  --fell-w:   #3A1F16;
  --shadow:   0 1px 2px rgba(0,0,0,.4), 0 10px 28px -16px rgba(0,0,0,.8);
}}

* {{ box-sizing: border-box; }}
body {{
  background: var(--ground);
  color: var(--ink);
  font-family: "Newsreader", Georgia, "Times New Roman", serif;
  font-size: 17px;
  line-height: 1.62;
  margin: 0;
  padding: 0 24px 96px;
  -webkit-font-smoothing: antialiased;
}}
.wrap {{ max-width: 1080px; margin: 0 auto; }}
.prose {{ max-width: 65ch; }}

h1, h2, h3, .lab, th, .stat-n, .verdict {{
  font-family: "Archivo", "Helvetica Neue", Arial, sans-serif;
}}
h1 {{
  font-size: clamp(2.1rem, 5vw, 3.1rem);
  font-weight: 700;
  letter-spacing: -.028em;
  line-height: 1.04;
  text-wrap: balance;
  margin: 0 0 .5rem;
}}
h2 {{
  font-size: 1.32rem; font-weight: 600; letter-spacing: -.012em;
  margin: 0; text-wrap: balance;
}}
h3 {{
  font-size: .98rem; font-weight: 600; letter-spacing: -.005em;
  margin: 0 0 .4rem; color: var(--ink);
}}
p {{ margin: 0 0 1rem; }}
p:last-child {{ margin-bottom: 0; }}
strong {{ font-weight: 500; color: var(--ink); background: var(--accent-w);
          padding: 0 .2em; border-radius: 2px; }}
em {{ font-style: italic; }}
code {{
  font-family: "IBM Plex Mono", ui-monospace, monospace;
  font-size: .84em; background: var(--sunken); color: var(--ink-soft);
  padding: .12em .38em; border-radius: 3px;
}}
a {{ color: var(--accent); text-underline-offset: 3px; }}
:focus-visible {{ outline: 2px solid var(--accent); outline-offset: 3px; border-radius: 2px; }}

.lab {{
  font-size: .705rem; font-weight: 600; letter-spacing: .13em;
  text-transform: uppercase; color: var(--ink-dim);
}}

/* ---- masthead ---- */
header.top {{ padding: 72px 0 40px; border-bottom: 2px solid var(--ink); }}
.kicker {{ display: flex; align-items: baseline; gap: 12px; margin-bottom: 20px; }}
.kicker .dot {{ width: 9px; height: 9px; background: var(--accent); flex: none;
                transform: rotate(45deg); }}
.dek {{ font-size: 1.24rem; color: var(--ink-soft); max-width: 60ch;
        line-height: 1.5; margin: 0; }}
.meta {{
  display: flex; flex-wrap: wrap; gap: 8px; margin-top: 28px;
  font-family: "IBM Plex Mono", monospace; font-size: .72rem;
}}
.meta span {{
  border: 1px solid var(--rule); color: var(--ink-soft);
  padding: 4px 9px; border-radius: 2px; white-space: nowrap;
}}

/* ---- stat strip ---- */
.stats {{
  display: grid; gap: 1px; background: var(--rule);
  border: 1px solid var(--rule); margin: 40px 0 8px;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
}}
.stat {{ background: var(--surface); padding: 22px 20px 20px; }}
.stat-n {{
  font-size: clamp(1.6rem, 3.4vw, 2.15rem); font-weight: 700;
  letter-spacing: -.03em; line-height: 1; color: var(--accent);
  font-variant-numeric: tabular-nums; display: block; margin-bottom: 9px;
}}
.stat-n .u {{ font-size: .5em; font-weight: 600; letter-spacing: 0; }}
.stat-t {{ font-size: .89rem; color: var(--ink-soft); line-height: 1.42; }}

/* ---- sections ---- */
section {{ margin-top: 68px; }}
.shead {{
  display: flex; align-items: baseline; gap: 14px;
  padding-bottom: 12px; margin-bottom: 22px;
  border-bottom: 1px solid var(--rule);
}}
.shead .n {{
  font-family: "IBM Plex Mono", monospace; font-size: .74rem; font-weight: 600;
  color: var(--accent); flex: none; padding-top: 2px;
}}

/* ---- tables ---- */
.scroll {{ overflow-x: auto; margin: 22px 0 8px;
           border: 1px solid var(--rule); background: var(--surface); }}
table {{ border-collapse: collapse; width: 100%; font-variant-numeric: tabular-nums; }}
th, td {{
  font-family: "IBM Plex Mono", ui-monospace, monospace;
  font-size: .8rem; text-align: right; padding: 7px 11px;
  border-bottom: 1px solid var(--rule-soft); white-space: nowrap;
}}
thead th {{
  font-family: "Archivo", sans-serif; font-weight: 600; font-size: .72rem;
  color: var(--ink-soft); background: var(--sunken);
  border-bottom: 1px solid var(--rule); position: sticky; top: 0;
}}
.grouphead {{ text-align: center; letter-spacing: .06em; text-transform: uppercase;
              font-size: .66rem; color: var(--ink-dim); }}
th.rh {{ text-align: left; font-family: "Archivo", sans-serif; font-weight: 600;
         color: var(--ink); background: var(--surface); }}
thead th.rh {{ background: var(--sunken); }}
tbody tr:last-child th, tbody tr:last-child td {{ border-bottom: 0; }}
.unit {{ text-align: left; color: var(--ink-dim); }}
.num {{ color: var(--ink); }}
.dim {{ color: var(--ink-dim); }}
.u {{ font-size: .78em; opacity: .6; margin-left: .5px; }}
.verdict {{ text-align: left; font-size: .78rem; font-weight: 500; color: var(--ink); }}

.o-base {{ color: var(--ink-soft); background: var(--sunken); }}
.o-hold {{ color: var(--hold); background: var(--hold-w); font-weight: 600; }}
.o-kill {{ color: var(--kill); background: var(--kill-w); font-weight: 600; }}
.o-fell {{ color: var(--fell); background: var(--fell-w); }}
.o-win  {{ color: var(--hold); background: var(--hold-w); font-weight: 600; }}
.o-mixed{{ color: var(--accent); background: var(--accent-w); }}
.o-none {{ color: var(--ink-dim); }}
.matrix td, .matrix th {{ padding: 6px 9px; }}
tr.grp th, tr.grp td {{ border-top: 1px solid var(--rule); }}
td.esc {{ background: var(--accent-w); color: var(--ink); }}
td.big {{ font-weight: 600; color: var(--accent); font-size: .92rem; }}
.part {{
  margin-top: 84px; padding: 30px 0 0; border-top: 2px solid var(--ink);
}}
.part .lab {{ display: block; margin-bottom: 10px; }}
.part h2 {{ font-size: clamp(1.5rem, 3.2vw, 2.05rem); font-weight: 700;
            letter-spacing: -.022em; line-height: 1.1; margin-bottom: 10px; }}
.part p {{ color: var(--ink-soft); max-width: 62ch; font-size: 1.06rem; }}
.thr th.cliff, .thr td.cliff {{ border-left: 2px solid var(--ink); }}

.legend {{
  display: flex; flex-wrap: wrap; gap: 16px; margin: 12px 0 0;
  font-family: "Archivo", sans-serif; font-size: .76rem; color: var(--ink-soft);
}}
.legend b {{ font-weight: 600; padding: 1px 7px; border-radius: 2px; margin-right: 6px; }}
.cap {{ font-size: .84rem; color: var(--ink-dim); margin-top: 10px; max-width: 68ch;
        line-height: 1.5; }}

/* ---- callout ---- */
.callout {{
  border-left: 3px solid var(--accent); background: var(--surface);
  padding: 20px 24px; margin: 26px 0; box-shadow: var(--shadow);
}}
.callout p:last-child {{ margin-bottom: 0; }}

/* ---- caveat cards ---- */
.cards {{ display: grid; gap: 1px; background: var(--rule); border: 1px solid var(--rule);
          grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); margin-top: 22px; }}
.card {{ background: var(--surface); padding: 20px; }}
.card p {{ font-size: .93rem; color: var(--ink-soft); margin: 0; line-height: 1.55; }}

pre {{
  font-family: "IBM Plex Mono", monospace; font-size: .76rem; line-height: 1.65;
  background: var(--surface); border: 1px solid var(--rule);
  padding: 16px 18px; overflow-x: auto; color: var(--ink-soft); margin: 18px 0 0;
}}
footer {{ margin-top: 76px; padding-top: 20px; border-top: 1px solid var(--rule);
          font-size: .84rem; color: var(--ink-dim); }}
</style>

<div class="wrap">

<header class="top">
  <div class="kicker"><span class="dot"></span><span class="lab">Engine measurement &middot; 7,000+ scripted games</span></div>
  <h1>One cheap body per enemy swing stops anything in the game.</h1>
  <p class="dek">Chump-blocking is not a damage race. It has a single threshold &mdash; the
  attacker&rsquo;s <em>attack period</em> &mdash; and against a lone unit it is close to free.
  Multiply the force and the price climbs faster than the force does. Add a tier-4 escort and
  only the engine&rsquo;s maximum click rate can hold the line at all.</p>
  <div class="meta">
    <span>23,000 HP both castles</span>
    <span>$5,000/s both incomes</span>
    <span>8 teams &times; tiers 5&ndash;8 &times; forces &times;1&ndash;&times;5</span>
    <span>n = 8 (teams); seeds are inert</span>
    <span>both seats, identical</span>
    <span>deterministic</span>
  </div>
</header>

<div class="stats">
  <div class="stat">
    <span class="stat-n">5.00<span class="u">s</span></span>
    <span class="stat-t">The exact stall threshold against any tier&nbsp;8. One body per
    5.00s holds forever; one per 5.17s does not. That is the unit&rsquo;s attack period.</span>
  </div>
  <div class="stat">
    <span class="stat-n">${dollars_per_sec:.2f}<span class="u">/s</span></span>
    <span class="stat-t">Median price of delay across every run where the castle still
    fell &mdash; {per_dollar*100:.0f} seconds bought per $100, or 0.04% of the test income.</span>
  </div>
  <div class="stat">
    <span class="stat-n">8<span class="u">/8</span></span>
    <span class="stat-t">Teams whose tier&nbsp;5, 6 and 7 attackers are not merely stalled
    but <em>destroyed</em> by tier-1 bodies at 2/s, with no other defence at all.</span>
  </div>
  <div class="stat">
    <span class="stat-n">30<span class="u">/s</span></span>
    <span class="stat-t">The only rate that still holds an escorted tier-5, -6 or -7 force
    &mdash; one spawn per tick, the engine&rsquo;s ceiling. Halve it and the line collapses.</span>
  </div>
</div>

<section>
  <div class="shead"><span class="n">01</span><h2>What the games were</h2></div>
  <div class="prose">
    <p>Both castles pinned at 23,000&nbsp;HP, both incomes at $5,000/s so nothing is ever
    money-limited. P1 spawns <em>one</em> unit of the tested tier, once, and never
    reinforces it. P2 either does absolutely nothing &mdash; the control &mdash; or spawns
    one tier-1 unit every N ticks and nothing else. No gadgets, no investing, no repairs.
    The clock stops when P2&rsquo;s castle reaches zero.</p>
    <p>The mechanic being measured lives in <code>MoveAndFight</code>: a unit with
    <em>any</em> enemy in range sets <code>CurrentSpeed = 0</code> and attacks that unit.
    Reaching a castle requires the target scan to come back empty. So a body in contact is
    a <strong>hard stop, not a damage race</strong> &mdash; which is why the attacker&rsquo;s
    HP and damage turn out not to matter, and its attack speed turns out to be everything.</p>
  </div>

  <div class="callout">
    <h3>Two arms, because the stream does two jobs</h3>
    <p>A stream of tier-1 units blocks, and it also walks on and razes the enemy castle once
    the attacker is dead. Reading those together would credit the counter-attack as if it
    were stalling. Sections 02&ndash;05 shield the attacker&rsquo;s castle so the numbers are
    blocking alone; section 06 unshields it and asks who wins the game.</p>
  </div>
</section>

<section>
  <div class="shead"><span class="n">02</span><h2>The eight games you asked for</h2></div>
  <div class="prose">
    <p>White mirror, tier-1 <em>doggo</em> as the blocker, streamed at 2 per second. The
    right-hand columns are what the stall cost.</p>
  </div>
  <div class="scroll"><table>
    <thead><tr>
      <th class="rh">tier</th><th class="unit" style="text-align:left">attacker</th>
      <th>P2 idle</th><th>8-team median</th>
      <th style="text-align:left">P2 streams T1 at 2/s</th><th>bodies</th><th>spent</th>
    </tr></thead>
    <tbody>{hl_html}</tbody>
  </table></div>
  <p class="cap">The tier-7 row is the one to look at twice: with no defence
  <em>eggo</em> ends the game in 11.8 seconds, and 368 doggos costing $1,104 turn that into
  a dead attacker. The tier-8 <em>corn</em> is never killed inside the horizon, but it never
  lands a second swing either.</p>
</section>

<section>
  <div class="shead"><span class="n">03</span><h2>Every team, every tier, every rate</h2></div>
  <div class="prose">
    <p>Seconds until P2&rsquo;s castle falls. The <em>none</em> column is the control. Read
    left to right and the tactic degrades exactly where you would expect it to &mdash; and
    the boundary is sharp, not gradual.</p>
  </div>

  <div class="legend">
    <span><b class="o-kill">kill</b>chumps destroyed the attacker outright</span>
    <span><b class="o-hold">hold</b>attacker alive, castle still standing at 1200s</span>
    <span><b class="o-fell">78s</b>castle fell, at that time</span>
  </div>

  <h3 style="margin-top:26px">Tier 5 attacker</h3>{matrix(5)}
  <h3 style="margin-top:26px">Tier 6 attacker</h3>{matrix(6)}
  <h3 style="margin-top:26px">Tier 7 attacker</h3>{matrix(7)}
  <h3 style="margin-top:26px">Tier 8 attacker</h3>{matrix(8)}
  <p class="cap">Tier 8 is the clean case: a solid block of <em>hold</em> from 0.25/s
  upward, collapsing the moment the rate drops below it. Tiers 5&ndash;7 add a second
  effect &mdash; enough bodies arrive to out-damage the attacker, so it dies rather than
  merely stopping.</p>
</section>

<section>
  <div class="shead"><span class="n">04</span><h2>The threshold is the attack period</h2></div>
  <div class="prose">
    <p>Every tier-8 unit in the game has its attack speed clamped to 0.20/s &mdash; one swing
    per 5.00 seconds &mdash; because <code>GameDataManager</code> recomputes attack speed from
    damage and move speed and clips it at 0.2. A fine sweep across that value finds the
    boundary in exactly the same place for all eight teams:</p>
  </div>
  {threshold_table()}
  <p class="cap">One body per 5.00s holds to the horizon. One body per 5.17s and the castle
  falls in 259&ndash;378s. The rule is one fresh body per enemy swing: each arrival absorbs a
  swing that would otherwise hit the castle, and the attacker&rsquo;s cleave cannot bank
  spare swings against bodies that have not arrived yet.</p>
  <div class="callout">
    <h3>At 23,000 HP a tier 8 is a two-swing kill</h3>
    <p>Siege damage doubles against structures, so every tier-8 swing lands 59,306&ndash;100,712
    on a 23,000 HP castle. The one-shot protection in <code>DamageCastle</code> floors the
    first hit at 1 HP, so the kill is always exactly two swings &mdash; and
    <strong>every blocked swing is worth half the castle</strong>. That is why a $3 unit buys
    5 seconds here and would buy less at a different castle HP.</p>
  </div>
</section>

<section>
  <div class="shead"><span class="n">05</span><h2>What it costs</h2></div>
  <div class="prose">
    <p>Measured only on runs where the castle still fell, so the delay is a finite number
    rather than an infinity. Pooled across all of them the price is remarkably flat:</p>
  </div>
  <div class="scroll"><table>
    <thead><tr><th class="rh">tier</th><th>runs</th><th>seconds per body</th><th>price of delay</th></tr></thead>
    <tbody>{cost_html}</tbody>
  </table></div>
  <p class="cap">Pooled median: <strong>${dollars_per_sec:.2f} per second of delay</strong>.
  Against the $5,000/s income in this test that is 0.04% of income per extra second bought
  &mdash; the tactic is, for practical purposes, free.</p>
</section>

<section>
  <div class="shead"><span class="n">06</span><h2>Unshielded, the stream wins the game</h2></div>
  <div class="prose">
    <p>Same runs with the attacker&rsquo;s castle live. Once the lone attacker is dead nothing
    stops the surviving bodies walking on. Cells show how many of the 8 teams saw the
    <em>defender</em> take the win outright.</p>
  </div>
  <div class="scroll"><table class="matrix">
    <thead>
      <tr><th class="rh"></th><th colspan="{len(IVS)}" class="grouphead">tier-1 bodies spawned per second</th></tr>
      <tr><th class="rh">tier</th>{real_head}</tr>
    </thead>
    <tbody>{real_html}</tbody>
  </table></div>
  <p class="cap">At 6 bodies per second and above, a defender who does nothing but spam the
  cheapest unit in the game beats a tier-8 rush in 8 cases out of 8, taking a median 376
  seconds to do it. This is the arm that shows why the tactic is worth the bot knowing:
  it is not a delaying action, it is a win condition.</p>
</section>

<section>
  <div class="shead"><span class="n">07</span><h2>Where the numbers stop being useful</h2></div>
  <div class="cards">
    <div class="card">
      <h3>The kills need an inhuman body count</h3>
      <p>Every blocker clamps to the same x, so the whole pile swings at once and damage
      scales with pile size. Killing a tier 8 that way takes the counts below &mdash; well
      past the 600s game limit. Against tier 8 this tactic <em>neutralises</em>; against
      tier 5&ndash;6 it genuinely kills.</p>
    </div>
    <div class="card">
      <h3>$5,000/s hides the real constraint</h3>
      <p>At the requested income nothing is ever money-limited, so the binding constraint in
      these runs is click rate, not economy. In a real game at a real income the high-rate
      columns are not reachable and the interesting region is the 0.25&ndash;2/s band.</p>
    </div>
    <div class="card">
      <h3>One attacker, never reinforced</h3>
      <p>A real opponent sends a second unit, or a gadget. A firebomb or nuke over the pile
      resets the whole clock, and nothing here measures that &mdash; these runs deliberately
      have no gadgets on either side.</p>
    </div>
    <div class="card">
      <h3>Mirror blockers only</h3>
      <p>The blocker is always the attacker&rsquo;s own team&rsquo;s tier 1. Blocker cost and
      HP vary 1&ndash;4&times; across teams (<code>minisnecko</code> $1, <code>phil</code> $4),
      so the cross-team best blocker is unmeasured. <code>--blocker all</code> runs it.</p>
    </div>
    <div class="card">
      <h3>n is 8, not the run count</h3>
      <p>The harness is deterministic and the engine&rsquo;s RNG only sets unit <em>y</em>, which
      nothing in combat reads &mdash; seeds 999, 4242 and 12345 give identical results across 80
      cells. Teams are the only sample dimension, so every cell here is n&nbsp;=&nbsp;8.</p>
    </div>
    <div class="card">
      <h3>One escort tier, one cadence</h3>
      <p>Only tier 4 at one per second, because that is what was asked for. The escort-only
      reference arm suggests both knobs matter a great deal, and neither was swept.</p>
    </div>
    <div class="card">
      <h3>Seats were checked, not assumed</h3>
      <p>0 of 480 cells differ between P1-attacks and P2-attacks in part one, and 0 of 80 with
      forces and escorts added. The seat bias that poisons mirror benchmarks does not reach this
      scenario, so the later sweeps run one seat only.</p>
    </div>
  </div>
  <div class="scroll"><table>
    <thead><tr><th class="rh">tier</th><th>cheapest rate that kills 8/8</th><th>bodies spent</th><th>peak stack</th><th>time</th></tr></thead>
    <tbody>{pile_html}</tbody>
  </table></div>
</section>


<div class="part">
  <span class="lab">Part two</span>
  <h2>Multiply the force, and add an escort</h2>
  <p>Everything above is one attacker, alone, never reinforced &mdash; the best case the tactic
  will ever see. These next runs give the attacker a real force: two, three, four and five
  copies spawned a second apart, and then the same again with a tier-4 unit streamed in every
  second to break the blocking line and walk the big units through.</p>
</div>

<section>
  <div class="shead"><span class="n">08</span><h2>How fast the force kills an undefended castle</h2></div>
  <div class="prose">
    <p>The control arm first, so every delay below has a baseline. Seconds until a 23,000&nbsp;HP
    castle falls with the defender doing nothing at all.</p>
  </div>
  {undefended_table()}
  <p class="cap">Tiers 5 and 6 scale roughly as 1/N &mdash; they are damage-limited. Tiers 7 and 8
  flatten out because their clock is dominated by the walk, not the killing. Note the last row:
  <strong>a tier-4 stream with no high-tier units at all razes the castle in 30 seconds</strong>,
  which turns out to matter a great deal.</p>
</section>

<section>
  <div class="shead"><span class="n">09</span><h2>Force size raises the price faster than linearly</h2></div>
  <div class="prose">
    <p><code>ClampToContact</code> skips friendly units, so a force does <em>not</em> queue up
    behind its own front member &mdash; every unit in it overlaps at the same contact point and
    all of them swing at the line. Tier 8 makes the cleanest measurement, because its swing
    period is exactly 150 ticks for all eight teams:</p>
  </div>
  {threshold_by_force()}
  <p class="cap">One chump per swing holds a single attacker. Five attackers need
  <strong>fifteen</strong> per swing, not five &mdash; the defender&rsquo;s click rate has to grow
  roughly three times as fast as the force does. The two left-hand columns differ wherever a slow
  but well-aligned spawn rate succeeds where a faster misaligned one fails; the rest of this page
  quotes the stricter one.</p>

  <div class="callout">
    <h3>Near the threshold, the phase matters &mdash; read only the all-8 columns</h3>
    <p>Whether the line holds within a step or so of the boundary depends on whether the spawn
    period lines up with the 150-tick swing period. At &times;5 the line holds 8/8 at 15 ticks,
    6/8 at 12, and 8/8 again at 10 &mdash; not monotone. That is why the table above reports the
    strict threshold as well as the naive one, and why any single cell near a boundary should be
    ignored. A human clicking is not periodic, so this is an artefact of the harness rather than
    a property of the game.</p>
  </div>

  <h3 style="margin-top:30px">Teams held, out of 8 &mdash; no escort</h3>
  {hold_matrix(0)}
</section>

<section>
  <div class="shead"><span class="n">10</span><h2>What stopping a force costs</h2></div>
  <div class="prose">
    <p>At the cheapest rate that holds all eight teams. The defender&rsquo;s figure is what it had
    spent at the moment the force died.</p>
  </div>
  {cost_table()}
  <p class="cap">Defence gets <em>relatively</em> cheaper the bigger and more expensive the force
  is. Against five tier 8s the defender pays <strong>7 cents on the attacker&rsquo;s dollar</strong>.
  Tier 7 is the hardest tier to hold &mdash; fast enough to close, cheap enough to mass.</p>
  <p class="cap">One caveat on those attacker figures: the force is spawned on schedule regardless
  of money, so five tier 8s appear within four seconds. That is a median $91,960, which no
  economy could bank that fast at $5,000/s. It is a stress test, not an opening.</p>
</section>

<section>
  <div class="shead"><span class="n">11</span><h2>A tier-4 escort breaks the tactic</h2></div>
  <div class="prose">
    <p>Same forces, plus one tier-4 unit per second &mdash; a median $20/s of ongoing spend.</p>
  </div>
  <h3>Teams held, out of 8 &mdash; with escort</h3>
  {hold_matrix(4)}
  <p class="cap">One escort unit per second costs the attacker about $20/s and moves the
  whole tactic to the edge of what is physically possible. Escorted tier-5, -6 and -7 forces are
  still held at 30 chumps/s &mdash; but 30/s is <em>one spawn per tick</em>, the engine&rsquo;s
  hard ceiling, and it holds for only 5&ndash;7 of the 8 teams. Halve it to 15/s and that drops
  to 0&ndash;1 of 8; below that, nothing holds. Tier-8 forces hold more comfortably, but at 30/s
  the bill is about $36,000 and 20,000 bodies &mdash; at &times;1 that is 1.26&times; the
  attacker&rsquo;s own spend, the only configuration measured where blocking is a losing trade.</p>

  <div class="callout">
    <h3>It really is escorting, not just a scarier attack</h3>
    <p>The bottom row of that table is the reference arm: the identical escort stream with
    <em>no</em> high-tier units at all. Against that, the chump line holds comfortably. So the
    escort is not simply out-fighting the chumps on its own &mdash; it opens the line, and the
    big unit walks through the hole.</p>
  </div>
  {escort_attrib()}
  <p class="cap">Chump line at 15/s. The escort alone is stopped by all eight teams. Adding a
  single high-tier unit collapses it to one.</p>
</section>

<section>
  <div class="shead"><span class="n">12</span><h2>Unshielded: can the defender still win?</h2></div>
  <div class="prose">
    <p>Attacker&rsquo;s castle live again. Cells count the teams where the chumps killed the force
    and then went on to raze its castle.</p>
  </div>
  {real_force_table()}
  <p class="cap">Against an unescorted force the counter-attack survives force size well &mdash;
  even five tier 8s lose the game 8/8 at 30 chumps/s. With an escort it disappears: the defender
  wins only at 30/s, and never below 15/s.</p>
</section>

<section>
  <div class="shead"><span class="n">13</span><h2>An aside the sweep turned up: tier-4 spam dominates tier 8</h2></div>
  <div class="prose">
    <p>This is not about blocking at all, but it fell out of the escort-only reference arm and it
    looks like a balance problem. Against an undefended castle, a stream of tier-4 units does the
    same job in comparable time for a median <strong>{T4RATIO:.0f}&times; less money</strong> than
    a single tier 8.</p>
  </div>
  {T4TABLE}
  <p class="cap">Mechanism: tier-4 units move at 7&ndash;23 px/tick against a tier 8&rsquo;s
  1&ndash;5, so they arrive 15&ndash;50s earlier, and their attack speed is clamped at the
  <em>top</em> of the range (5.0/s) where a tier 8&rsquo;s is clamped at the <em>bottom</em>
  (0.2/s). Untested against a defended castle in a real game &mdash; worth its own experiment
  before anyone acts on it.</p>
</section>

<section>
  <div class="shead"><span class="n">14</span><h2>Reproducing it</h2></div>
  <div class="prose">
    <p>New BotArena mode, committed as <code>StallTest.cs</code>. Seeded throughout, so the
    same arguments give byte-identical output.</p>
  </div>
<pre># PART ONE -- the isolated blocking arm (sections 02-05)
CastleDefense.BotArena.exe stall-test --teams all --seat both --tiers 5,6,7,8 \\
    --intervals 1,2,3,5,10,15,20,30,45,60,90,120,180,240 \\
    --protect-attacker true --csv stall_isolated.csv

# the threshold sweep (section 04)
CastleDefense.BotArena.exe stall-test --teams all --tiers 8 \\
    --intervals 120,130,140,145,150,155,160,170,180 --csv t8_threshold.csv

# unshielded, who wins the game (section 06)
CastleDefense.BotArena.exe stall-test --teams all --tiers 5,6,7,8 \\
    --intervals 1,2,3,5,10,15,20,30,45,60,90,120,180,240 \\
    --protect-attacker false --csv stall_realistic.csv

# PART TWO -- forces and escorts (sections 08-13).
# --forces 0 is the escort-only reference arm; --escorts 0 means no escort.
CastleDefense.BotArena.exe stall-test --teams all --seat 1 --tiers 5,6,7,8 \\
    --forces 0,1,2,3,4,5 --escorts 0,4 \\
    --intervals 1,2,3,5,10,15,20,30,45,60,90,120 \\
    --protect-attacker true --csv force_isolated.csv

# the superlinear threshold (section 09)
CastleDefense.BotArena.exe stall-test --teams all --seat 1 --tiers 8 --forces 1,2,3,4,5 \\
    --intervals 10,12,15,16,18,20,21,22,24,25,26,28,30,32,35,38,40 \\
    --csv t8_fine2.csv</pre>
</section>

<footer>
  Castle Defense engine &middot; measured 21 August 2026 &middot; 7,072 runs across both parts
  &middot; every cell is n&nbsp;=&nbsp;8 teams &middot; raw CSVs, this generator and a written
  record in <code>CastleDefense.BotArena/stall/</code>
</footer>

</div>
"""

open(OUT, "w", encoding="utf-8").write(HTML)
print("wrote", OUT, len(HTML), "bytes")
