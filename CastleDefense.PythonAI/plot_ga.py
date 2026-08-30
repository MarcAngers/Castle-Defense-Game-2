"""
Visualize GA reward-tuning progress.

Usage:
    python plot_ga.py                      # one-shot: save PNG and exit
    python plot_ga.py ga_progress.csv      # explicit path, one-shot
    python plot_ga.py --watch              # live window, auto-refreshes
    python plot_ga.py --watch ga_progress.csv
"""

import sys
import os
import csv
import time
import json
import math
from collections import defaultdict
from pathlib import Path

import matplotlib
_WATCH = "--watch" in sys.argv
if _WATCH:
    try:
        matplotlib.use("TkAgg")
    except Exception:
        matplotlib.use("Agg")
        _WATCH = False
else:
    matplotlib.use("Agg")

import matplotlib.pyplot as plt
import matplotlib.ticker as mticker
import matplotlib.gridspec as gridspec

# ─── Style ────────────────────────────────────────────────────────────────────

BG_DARK  = "#1e1e2e"
BG_PANEL = "#2a2a3e"
GRID_COL = "#333355"

_SCRIPT_DIR   = Path(__file__).parent.resolve()
DEFAULTS_FILE = str(_SCRIPT_DIR / "reward_defaults.json")

PARAM_NAMES = ["WinReward", "InvestReward", "InvestDecay", "AntiSpend",
               "SavingsWeight", "CombatScale", "GadgetUpgrade", "GadgetUse"]

PARAM_COLORS = [
    "#60a5fa", "#34d399", "#f472b6", "#fbbf24",
    "#a78bfa", "#fb923c", "#38bdf8", "#4ade80",
]

FITNESS_COLOR     = "#60a5fa"
BEST_EVER_COLOR   = "#fbbf24"
MEAN_COLOR        = "#a0a0c0"
SUCCESS_COLOR     = "#34d399"
SIGMA_LINE_COLOR  = "#888"

ONE_FIFTH = 0.2   # the 1/5 success rule target


# ─── Data ─────────────────────────────────────────────────────────────────────

def read_csv(path):
    if not os.path.exists(path):
        return []
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def load_defaults():
    if not os.path.exists(DEFAULTS_FILE):
        return {}
    with open(DEFAULTS_FILE) as f:
        return {k: float(v) for k, v in json.load(f).items()}


def parse_rows(rows):
    """Group rows by generation. Returns dict generation→list of row dicts."""
    by_gen = defaultdict(list)
    for r in rows:
        try:
            by_gen[int(r["generation"])].append(r)
        except (KeyError, ValueError):
            pass
    return by_gen


# ─── Style helpers ─────────────────────────────────────────────────────────────

def _style_ax(ax, xlabel=None, ylabel=None, title=None, ylim=None, yscale=None):
    ax.set_facecolor(BG_PANEL)
    ax.tick_params(colors="white", labelsize=8)
    ax.xaxis.label.set_color("white")
    ax.yaxis.label.set_color("white")
    ax.title.set_color("white")
    for spine in ax.spines.values():
        spine.set_color("#444466")
    ax.grid(True, color=GRID_COL, linewidth=0.5)
    if xlabel:
        ax.set_xlabel(xlabel, fontsize=9)
    if ylabel:
        ax.set_ylabel(ylabel, fontsize=9)
    if title:
        ax.set_title(title, fontsize=10, pad=4)
    if ylim:
        ax.set_ylim(*ylim)
    if yscale:
        ax.set_yscale(yscale)


# ─── Drawing ──────────────────────────────────────────────────────────────────

def draw_figure(fig, log_path):
    rows     = read_csv(log_path)
    defaults = load_defaults()

    fig.clear()
    fig.patch.set_facecolor(BG_DARK)

    if not rows:
        ax = fig.add_subplot(111)
        _style_ax(ax)
        ax.text(0.5, 0.5, "Waiting for first generation…",
                ha="center", va="center", color="white", fontsize=14,
                transform=ax.transAxes)
        return

    by_gen = parse_rows(rows)
    gens   = sorted(by_gen.keys())

    # ── Aggregate per generation ──────────────────────────────────────────────
    gen_min, gen_max, gen_mean = [], [], []
    gen_success = []
    param_series = {p: [] for p in PARAM_NAMES}  # generation → list of values

    best_ever = -math.inf
    best_ever_line = []

    for g in gens:
        group = by_gen[g]
        fits  = [float(r["fitness"]) for r in group]
        gen_min.append(min(fits))
        gen_max.append(max(fits))
        gen_mean.append(sum(fits) / len(fits))
        best_ever = max(best_ever, max(fits))
        best_ever_line.append(best_ever)

        # success_rate is the same for all rows in a generation
        try:
            sr = float(group[0]["success_rate"])
        except (KeyError, ValueError):
            sr = float("nan")
        gen_success.append(sr)

        for p in PARAM_NAMES:
            vals = []
            for r in group:
                try:
                    vals.append(float(r[p]))
                except (KeyError, ValueError):
                    pass
            param_series[p].append(vals)

    n_gens = len(gens)
    last_gen = gens[-1]

    # ── Layout: top row (fitness + success), bottom 2×4 (params) ─────────────
    outer = gridspec.GridSpec(2, 1, figure=fig, hspace=0.42,
                              top=0.93, bottom=0.06, left=0.06, right=0.97,
                              height_ratios=[1, 1.6])

    top_gs  = gridspec.GridSpecFromSubplotSpec(1, 2, subplot_spec=outer[0], wspace=0.35)
    bot_gs  = gridspec.GridSpecFromSubplotSpec(2, 4, subplot_spec=outer[1],
                                               hspace=0.55, wspace=0.45)

    ax_fit  = fig.add_subplot(top_gs[0])
    ax_succ = fig.add_subplot(top_gs[1])
    param_axes = [fig.add_subplot(bot_gs[i // 4, i % 4]) for i in range(8)]

    # ── Fitness panel ─────────────────────────────────────────────────────────
    _style_ax(ax_fit, xlabel="Generation", ylabel="Fitness (Win Rate)",
              title="Fitness over Generations")
    ax_fit.yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1.0, decimals=0))

    ax_fit.fill_between(gens, gen_min, gen_max,
                        alpha=0.25, color=FITNESS_COLOR, label="Min–Max range")
    ax_fit.plot(gens, gen_mean, color=MEAN_COLOR, linewidth=1.5,
                linestyle="--", label="Mean")
    ax_fit.plot(gens, gen_max,  color=FITNESS_COLOR, linewidth=2.0, label="Best of gen")
    ax_fit.plot(gens, best_ever_line, color=BEST_EVER_COLOR, linewidth=2.0,
                linestyle=":", label="Best ever")

    if gen_max:
        ax_fit.annotate(f"{gen_max[-1]:.1%}",
                        xy=(gens[-1], gen_max[-1]),
                        xytext=(6, 0), textcoords="offset points",
                        color=FITNESS_COLOR, fontsize=9, va="center")
        ax_fit.annotate(f"{best_ever:.1%}",
                        xy=(gens[-1], best_ever_line[-1]),
                        xytext=(6, -10), textcoords="offset points",
                        color=BEST_EVER_COLOR, fontsize=9, va="center")

    ax_fit.legend(facecolor=BG_PANEL, labelcolor="white", framealpha=0.8, fontsize=8)

    # ── Success rate panel ────────────────────────────────────────────────────
    _style_ax(ax_succ, xlabel="Generation", ylabel="Success Rate",
              title="Mutation Success Rate  (target ≈ 1/5)")

    valid_gens    = [g for g, s in zip(gens, gen_success) if not math.isnan(s)]
    valid_success = [s for s in gen_success if not math.isnan(s)]

    if valid_success:
        bar_colors = []
        for s in valid_success:
            if 0.12 <= s <= 0.33:
                bar_colors.append(SUCCESS_COLOR)
            elif s > 0.33:
                bar_colors.append("#fbbf24")   # too high — increase sigma
            else:
                bar_colors.append("#f87171")   # too low  — decrease sigma

        ax_succ.bar(valid_gens, valid_success, color=bar_colors, alpha=0.75, width=0.6)
        ax_succ.axhline(ONE_FIFTH, color=SIGMA_LINE_COLOR, linewidth=1.2,
                        linestyle="--", label="1/5 target")
        ax_succ.axhspan(0.12, 0.33, alpha=0.08, color=SUCCESS_COLOR)

        ax_succ.yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1.0, decimals=0))
        ax_succ.set_ylim(0, max(0.6, max(valid_success) * 1.15))

        # Legend patches
        from matplotlib.patches import Patch
        legend_patches = [
            Patch(color=SUCCESS_COLOR, alpha=0.75, label="Well-calibrated"),
            Patch(color="#fbbf24",     alpha=0.75, label="Too conservative (raise σ)"),
            Patch(color="#f87171",     alpha=0.75, label="Too aggressive (lower σ)"),
        ]
        ax_succ.legend(handles=legend_patches, facecolor=BG_PANEL,
                       labelcolor="white", framealpha=0.8, fontsize=7)

    # ── Parameter panels ──────────────────────────────────────────────────────
    for i, (p, ax) in enumerate(zip(PARAM_NAMES, param_axes)):
        color   = PARAM_COLORS[i]
        default = defaults.get(p)

        all_vals_in_gen = param_series[p]
        gen_medians = [sorted(vs)[len(vs)//2] for vs in all_vals_in_gen if vs]
        gen_lo      = [min(vs) for vs in all_vals_in_gen if vs]
        gen_hi      = [max(vs) for vs in all_vals_in_gen if vs]
        plot_gens   = [g for g, vs in zip(gens, all_vals_in_gen) if vs]

        # Individual model dots
        for g, vs in zip(gens, all_vals_in_gen):
            if vs:
                ax.scatter([g] * len(vs), vs, color=color, alpha=0.35, s=14, zorder=3)

        if plot_gens:
            ax.fill_between(plot_gens, gen_lo, gen_hi, alpha=0.18, color=color)
            ax.plot(plot_gens, gen_medians, color=color, linewidth=1.8, zorder=4)

        if default is not None:
            ax.axhline(default, color="white", linewidth=0.9,
                       linestyle="--", alpha=0.55, label="Default")

        # Annotate final median
        if gen_medians:
            last_med = gen_medians[-1]
            if abs(last_med) >= 1000:
                label = f"{last_med:.0f}"
            elif abs(last_med) >= 1:
                label = f"{last_med:.2f}"
            else:
                label = f"{last_med:.4f}"
            ax.annotate(label, xy=(plot_gens[-1], last_med),
                        xytext=(5, 0), textcoords="offset points",
                        color=color, fontsize=7, va="center")

        # Log scale makes lognormal mutation symmetric and readable
        try:
            if all(v > 0 for vs in all_vals_in_gen for v in vs):
                ax.set_yscale("log")
                ax.yaxis.set_major_formatter(mticker.LogFormatterSciNotation(labelOnlyBase=False))
        except Exception:
            pass

        _style_ax(ax, xlabel="Generation", title=p)
        if i % 4 == 0:
            ax.set_ylabel("Value", fontsize=8)

    fig.suptitle(
        f"Castle Defense GA  —  Generation {last_gen}  |  {n_gens} complete  |  σ={rows[-1].get('sigma', '?')}",
        color="white", fontsize=11, y=0.97,
    )


# ─── Modes ────────────────────────────────────────────────────────────────────

def one_shot(log_path):
    fig = plt.figure(figsize=(22, 16))
    draw_figure(fig, log_path)
    out = log_path.replace(".csv", ".png")
    fig.savefig(out, dpi=150, bbox_inches="tight", facecolor=BG_DARK)
    print(f"Graph saved → {out}")
    plt.close(fig)


def watch_mode(log_path, poll_seconds=10):
    fig = plt.figure(figsize=(22, 16))
    fig.canvas.manager.set_window_title("Castle Defense — GA Progress")
    last_mtime = -1

    print(f"[Live graph] Watching {log_path}  (close window to stop)")

    try:
        while plt.fignum_exists(fig.number):
            try:
                mtime = os.path.getmtime(log_path) if os.path.exists(log_path) else -1
            except OSError:
                mtime = -1

            if mtime != last_mtime:
                last_mtime = mtime
                try:
                    draw_figure(fig, log_path)
                    out = log_path.replace(".csv", ".png")
                    fig.savefig(out, dpi=150, bbox_inches="tight", facecolor=BG_DARK)
                    fig.canvas.draw()
                except Exception as e:
                    print(f"[Live graph] Draw error: {e}")

            plt.pause(poll_seconds)
    except KeyboardInterrupt:
        pass

    print("[Live graph] Window closed.")


# ─── Entry point ──────────────────────────────────────────────────────────────

if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if a != "--watch"]
    log_path = args[0] if args else "ga_progress.csv"

    if _WATCH:
        watch_mode(log_path)
    else:
        one_shot(log_path)
