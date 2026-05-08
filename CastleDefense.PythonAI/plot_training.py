"""
Usage:
    python plot_training.py                           # one-shot: save PNG and exit
    python plot_training.py training_progress.csv     # explicit path, one-shot
    python plot_training.py --watch                   # live window, auto-refreshes
    python plot_training.py --watch training_progress.csv

Live window is launched automatically by train_ai_cluster.py during training.
"""
import sys
import os
import csv
import time
from collections import defaultdict

# Backend must be chosen before pyplot is imported
import matplotlib
_WATCH = "--watch" in sys.argv
if _WATCH:
    try:
        matplotlib.use("TkAgg")
    except Exception:
        matplotlib.use("Agg")
        _WATCH = False  # Fall back to one-shot if no display
else:
    matplotlib.use("Agg")

import matplotlib.pyplot as plt
import matplotlib.ticker as mticker

# ─── Colors ───────────────────────────────────────────────────────────────────

BG_DARK  = "#1e1e2e"
BG_PANEL = "#2a2a3e"
GRID_COL = "#333355"

COLORS = {
    "Overall":       "dodgerblue",
    "Random Dummy":  "limegreen",
    "Anti-Spam Bot": "crimson",
    "Spam Bot T1":   "#ffaa33",
    "Spam Bot T4":   "#ff6600",
    "Spam Bot T8":   "#994400",
    "League Models": "slategray",
}

# ─── Data helpers ──────────────────────────────────────────────────────────────

def read_csv(path):
    if not os.path.exists(path):
        return []
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))

def smooth(values, window=7):
    half, out = window // 2, []
    for i in range(len(values)):
        lo, hi = max(0, i - half), min(len(values), i + half + 1)
        out.append(sum(values[lo:hi]) / (hi - lo))
    return out

def to_M(steps):
    return [int(s) / 1_000_000 for s in steps]

def display_name(opp):
    return "League Models" if "castle_defense" in opp.lower() else opp

# ─── Drawing ──────────────────────────────────────────────────────────────────

def _style_ax(ax, is_winrate=True):
    ax.set_facecolor(BG_PANEL)
    ax.tick_params(colors="white")
    ax.yaxis.label.set_color("white")
    ax.title.set_color("white")
    ax.xaxis.label.set_color("white")
    for spine in ax.spines.values():
        spine.set_color("#444466")
    ax.grid(True, color=GRID_COL, linewidth=0.6)
    if is_winrate:
        ax.set_ylim(0, 100)
        ax.yaxis.set_major_formatter(mticker.FormatStrFormatter("%d%%"))
        ax.axhline(50, color="#555577", linewidth=0.8, linestyle="--")


def draw_figure(fig, log_path):
    opp_log = log_path.replace(".csv", "_opponents.csv")
    overall_rows = read_csv(log_path)
    opp_rows     = read_csv(opp_log)

    fig.clear()
    fig.patch.set_facecolor(BG_DARK)

    if not overall_rows:
        ax = fig.add_subplot(111)
        _style_ax(ax)
        ax.text(0.5, 0.5, "Waiting for first checkpoint…",
                ha="center", va="center", color="white", fontsize=14,
                transform=ax.transAxes)
        return

    axes = fig.subplots(2, 2, sharex=True)
    ax1, ax2 = axes[0]
    ax3, ax4 = axes[1]
    _style_ax(ax1, is_winrate=True)
    _style_ax(ax2, is_winrate=False)
    _style_ax(ax3, is_winrate=True)
    _style_ax(ax4, is_winrate=False)

    xs         = to_M([r["timestep"] for r in overall_rows])
    cnt        = int(overall_rows[-1]["sample_count"])
    total_games = int(overall_rows[-1]["total_games"]) if "total_games" in overall_rows[-1] else cnt

    # ── Overall Win Rate (top-left) ────────────────────────────────────────────
    wr  = [float(r["overall_winrate"]) * 100 for r in overall_rows]
    swr = smooth(wr)

    ax1.plot(xs, swr, color=COLORS["Overall"], linewidth=2.5, label="Overall WR")
    ax1.fill_between(xs, swr, alpha=0.15, color=COLORS["Overall"])
    if swr:
        ax1.annotate(f"{swr[-1]:.1f}%", xy=(xs[-1], swr[-1]),
                     xytext=(6, 0), textcoords="offset points",
                     color=COLORS["Overall"], fontsize=10, va="center")
    ax1.set_title("Overall Win Rate", fontsize=12)
    ax1.set_ylabel("Win Rate")
    ax1.legend(facecolor=BG_PANEL, labelcolor="white", framealpha=0.8)

    # ── Overall Episode Reward (top-right) ─────────────────────────────────────
    has_reward = "mean_ep_reward" in overall_rows[0]
    if has_reward:
        rew  = [float(r["mean_ep_reward"]) for r in overall_rows]
        srew = smooth(rew)
        ax2.plot(xs, srew, color="#cc99ff", linewidth=2.5, label="Avg Episode Reward")
        ax2.fill_between(xs, srew, alpha=0.12, color="#cc99ff")
        ax2.axhline(0, color="#555577", linewidth=0.8, linestyle="--")
        if srew:
            ax2.annotate(f"{srew[-1]:+.1f}", xy=(xs[-1], srew[-1]),
                         xytext=(6, 0), textcoords="offset points",
                         color="#cc99ff", fontsize=10, va="center")
    ax2.set_title("Overall Episode Reward", fontsize=12)
    ax2.set_ylabel("Reward")
    ax2.legend(facecolor=BG_PANEL, labelcolor="white", framealpha=0.8)

    # ── Per-opponent Win Rate (bottom-left) ────────────────────────────────────
    opp_wr_series  = defaultdict(lambda: defaultdict(list))
    opp_rew_series = defaultdict(lambda: defaultdict(list))
    for r in opp_rows:
        name = display_name(r["opponent"])
        ts   = int(r["timestep"])
        opp_wr_series[name][ts].append(float(r["winrate"]))
        if "mean_ep_reward" in r:
            opp_rew_series[name][ts].append(float(r["mean_ep_reward"]))

    for name, step_map in sorted(opp_wr_series.items()):
        sorted_steps = sorted(step_map)
        xs_o = to_M(sorted_steps)
        ys_o = [sum(step_map[s]) / len(step_map[s]) * 100 for s in sorted_steps]
        syo  = smooth(ys_o, window=5)
        color = COLORS.get(name, "#aaaacc")
        ax3.plot(xs_o, syo, linewidth=1.8, label=name, color=color)
        if syo:
            ax3.annotate(f"{syo[-1]:.0f}%", xy=(xs_o[-1], syo[-1]),
                         xytext=(5, 0), textcoords="offset points",
                         color=color, fontsize=8, va="center")

    ax3.set_title("Win Rate by Opponent", fontsize=12)
    ax3.set_ylabel("Win Rate")
    ax3.set_xlabel("Training Steps (Millions)")
    ax3.legend(facecolor=BG_PANEL, labelcolor="white", framealpha=0.8,
               ncol=2, fontsize=8)

    # ── Per-opponent Episode Reward (bottom-right) ─────────────────────────────
    for name, step_map in sorted(opp_rew_series.items()):
        sorted_steps = sorted(step_map)
        xs_o = to_M(sorted_steps)
        ys_o = [sum(step_map[s]) / len(step_map[s]) for s in sorted_steps]
        syo  = smooth(ys_o, window=5)
        color = COLORS.get(name, "#aaaacc")
        ax4.plot(xs_o, syo, linewidth=1.8, label=name, color=color)
        if syo:
            ax4.annotate(f"{syo[-1]:+.0f}", xy=(xs_o[-1], syo[-1]),
                         xytext=(5, 0), textcoords="offset points",
                         color=color, fontsize=8, va="center")

    ax4.axhline(0, color="#555577", linewidth=0.8, linestyle="--")
    ax4.set_title("Episode Reward by Opponent", fontsize=12)
    ax4.set_ylabel("Reward")
    ax4.set_xlabel("Training Steps (Millions)")
    ax4.legend(facecolor=BG_PANEL, labelcolor="white", framealpha=0.8,
               ncol=2, fontsize=8)

    total_M = xs[-1] if xs else 0
    fig.suptitle(
        f"Castle Defense AI  —  {total_M:.1f}M steps  |  {total_games:,} games total  ({cnt:,} in window)",
        color="white", fontsize=11, y=1.002
    )
    plt.tight_layout(rect=[0, 0, 1, 1])


# ─── Modes ────────────────────────────────────────────────────────────────────

def one_shot(log_path):
    fig = plt.figure(figsize=(22, 12))
    draw_figure(fig, log_path)
    out = log_path.replace(".csv", ".png")
    fig.savefig(out, dpi=150, bbox_inches="tight", facecolor=BG_DARK)
    print(f"Graph saved → {out}")
    plt.close(fig)


def watch_mode(log_path, poll_seconds=5):
    fig = plt.figure(figsize=(22, 12))
    fig.canvas.manager.set_window_title("Castle Defense — Training Progress")
    last_mtime = -1

    print(f"[Live graph] Watching {log_path}  (close window to stop)")

    while plt.fignum_exists(fig.number):
        try:
            mtime = os.path.getmtime(log_path) if os.path.exists(log_path) else -1
        except OSError:
            mtime = -1

        if mtime != last_mtime:
            last_mtime = mtime
            try:
                draw_figure(fig, log_path)
                # Also save a PNG snapshot alongside the CSV
                out = log_path.replace(".csv", ".png")
                fig.savefig(out, dpi=150, bbox_inches="tight", facecolor=BG_DARK)
                fig.canvas.draw()
            except Exception as e:
                print(f"[Live graph] Draw error: {e}")

        plt.pause(poll_seconds)

    print("[Live graph] Window closed.")


# ─── Entry point ──────────────────────────────────────────────────────────────

if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if a != "--watch"]
    log_path = args[0] if args else "training_progress.csv"

    if _WATCH:
        watch_mode(log_path)
    else:
        one_shot(log_path)
