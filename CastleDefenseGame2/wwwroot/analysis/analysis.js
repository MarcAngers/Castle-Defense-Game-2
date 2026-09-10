// ---------------------------------------------------------------------------
// Shared helpers for the /analysis pages: formatting, the top bar, and the tiny
// SVG line-chart used by the per-replay report.
//
// No build step and no libraries -- these pages are served straight out of
// wwwroot like the rest of the client, and the CSP-free local server is not a
// reason to pull a chart library in for three line charts.
// ---------------------------------------------------------------------------

export const fmt = {
  money: v => (v == null ? '—' : '$' + Math.round(v).toLocaleString()),
  rate: v => (v == null ? '—' : '$' + Math.round(v).toLocaleString() + '/s'),
  pct: v => (v == null ? '—' : v.toFixed(1) + '%'),
  secs: s => {
    if (s == null) return '—';
    const m = Math.floor(s / 60), r = Math.round(s % 60);
    return m > 0 ? `${m}m ${String(r).padStart(2, '0')}s` : `${r}s`;
  },
  clock: s => {
    const m = Math.floor(s / 60), r = Math.round(s % 60);
    return `${m}:${String(r).padStart(2, '0')}`;
  },
  when: unix => {
    if (!unix) return '—';
    const d = new Date(unix * 1000);
    return d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric',
                                         hour: '2-digit', minute: '2-digit' });
  },
  // Compact form for the replay table, where the date column was wrapping to three lines
  // and costing more vertical space than any other column.
  whenShort: unix => {
    if (!unix) return '—';
    const d = new Date(unix * 1000);
    const sameYear = d.getFullYear() === new Date().getFullYear();
    return d.toLocaleString(undefined, sameYear
      ? { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }
      : { year: '2-digit', month: 'short', day: 'numeric' });
  },
  // Loadouts are gadget ids: turn "reinforcements_2" into "reinforcements 2".
  gadget: id => (!id ? '—' : id.replace(/_(\d)$/, ' $1'))
};

export function el(tag, attrs = {}, ...kids) {
  const n = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v == null || v === false) continue;
    if (k === 'class') n.className = v;
    else if (k === 'text') n.textContent = v;
    else if (k.startsWith('on')) n.addEventListener(k.slice(2), v);
    else n.setAttribute(k, v);
  }
  for (const k of kids.flat()) {
    if (k == null || k === false) continue;
    n.appendChild(typeof k === 'string' ? document.createTextNode(k) : k);
  }
  return n;
}

export function topBar(current) {
  const links = [['/analysis/', 'Overview'], ['/analysis/dashboard.html', 'Balance dashboard'],
                 ['/analysis/replays.html', 'Replays'], ['/', 'Back to game']];
  return el('div', { class: 'bar' },
    el('span', { class: 'brand', text: 'Castle Defense · Analysis' }),
    el('nav', {}, links.map(([href, label]) =>
      el('a', { href, 'aria-current': href === current ? 'page' : null, text: label }))));
}

export async function getJson(url) {
  const r = await fetch(url, { headers: { 'Accept': 'application/json' } });
  const body = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(body.error || `${r.status} ${r.statusText}`);
  return body;
}

// ---------------------------------------------------------------------------
// Line chart. Data is [{x, y1, y2}]; everything else is presentation.
//
// Rendered as an SVG with a fixed viewBox and width:100% so it scales with the
// column rather than needing a resize listener -- which is also what makes it
// legible on a landscape phone without a second code path.
// ---------------------------------------------------------------------------
export function lineChart(points, opts = {}) {
  const W = 960, H = opts.height || 260;
  const ML = opts.marginLeft || 76, MR = 14, MT = 12, MB = 30;
  const PW = W - ML - MR, PH = H - MT - MB;
  const xs = points.map(p => p.x);
  const xMax = Math.max(1, ...xs);
  const yMax = niceMax(Math.max(1e-9, ...points.map(p => Math.max(p.y1 ?? 0, p.y2 ?? 0))));
  const fmtY = opts.fmtY || fmt.money;

  const sx = x => ML + PW * (x / xMax);
  const sy = y => MT + PH * (1 - y / yMax);
  const parts = [];

  const step = yMax / 4;
  for (let i = 0; i <= 4; i++) {
    const v = i * step, y = sy(v);
    parts.push(`<line class="grid" x1="${ML}" y1="${y.toFixed(1)}" x2="${ML + PW}" y2="${y.toFixed(1)}"/>`);
    parts.push(`<text class="ax" text-anchor="end" x="${ML - 8}" y="${(y + 4).toFixed(1)}">${esc(fmtY(v))}</text>`);
  }
  const tickEvery = xMax > 240 ? 60 : xMax > 90 ? 30 : 15;
  for (let x = 0; x <= xMax; x += tickEvery) {
    const px = sx(x);
    parts.push(`<line class="grid" x1="${px.toFixed(1)}" y1="${MT}" x2="${px.toFixed(1)}" y2="${MT + PH}"/>`);
    parts.push(`<text class="ax" text-anchor="middle" x="${px.toFixed(1)}" y="${MT + PH + 20}">${x}s</text>`);
  }

  // The region the fidelity check says is untrustworthy, shaded rather than hidden:
  // it is still what the rebuild produced, it just is not the recorded game.
  if (opts.deadFrom != null && opts.deadFrom >= 0 && opts.deadFrom < xMax) {
    const x0 = sx(opts.deadFrom);
    parts.push(`<rect class="dead" x="${x0.toFixed(1)}" y="${MT}" width="${(ML + PW - x0).toFixed(1)}" height="${PH}"/>`);
    parts.push(`<line class="cut" x1="${x0.toFixed(1)}" y1="${MT}" x2="${x0.toFixed(1)}" y2="${MT + PH}"/>`);
  }

  for (const [key, cls] of [['y1', 'l1'], ['y2', 'l2']]) {
    const d = points.map((p, i) => `${i ? 'L' : 'M'}${sx(p.x).toFixed(1)},${sy(p[key] ?? 0).toFixed(1)}`).join('');
    parts.push(`<path class="${cls}" d="${d}"/>`);
  }

  for (const m of opts.marks || []) {
    if (m.x > xMax) continue;
    const px = sx(m.x);
    parts.push(`<line class="mark" x1="${px.toFixed(1)}" y1="${MT}" x2="${px.toFixed(1)}" y2="${MT + PH}"/>`);
    if (m.label) {
      const anchor = px > ML + PW * 0.72 ? 'end' : 'start';
      const dx = anchor === 'end' ? -5 : 5;
      parts.push(`<text class="markt" text-anchor="${anchor}" x="${(px + dx).toFixed(1)}" y="${MT + 11}">${esc(m.label)}</text>`);
    }
  }

  for (const d of opts.dots || []) {
    if (d.x > xMax) continue;
    parts.push(`<circle class="${d.cls}" cx="${sx(d.x).toFixed(1)}" cy="${sy(d.y).toFixed(1)}" r="3.1"/>`);
  }

  const svg = `<svg class="chart" viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(opts.title || 'chart')}">${parts.join('')}</svg>`;
  const holder = el('div');
  holder.innerHTML = svg;
  return holder.firstChild;
}

function niceMax(v) {
  const e = Math.pow(10, Math.floor(Math.log10(v)));
  const m = v / e;
  const f = m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10;
  return f * e;
}

export function esc(s) {
  return String(s).replace(/[&<>"']/g, c =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
