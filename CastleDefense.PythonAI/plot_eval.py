"""
Board evaluator visualization — plots P1 win-probability over time for every .replay file.

Usage (from CastleDefense.PythonAI/ with ai_env active):
    python plot_eval.py                        # auto-detect recordings dir, re-export
    python plot_eval.py --no-export            # skip C# export, re-use existing CSV
    python plot_eval.py --replay-dir <path>    # specify recordings directory explicitly
    python plot_eval.py --output eval.png      # custom output filename

The C# exporter reconstructs each game state tick by tick from the binary .replay file,
calling EvaluateBoard() at every tick.  Output is eval_trajectories.csv + eval_trajectories.png.

Reading the chart:
  - Blue line / shading = P1 is ahead
  - Red line / shading  = P2 is ahead
  - Bold colour title = who actually won
  - Smooth curve pointing upward all game then reversing at the end = "late comeback" game
"""

import os
import sys
import subprocess
import numpy as np
import pandas as pd
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import matplotlib.ticker as mticker
from pathlib import Path

# ─── Paths ────────────────────────────────────────────────────────────────────

_SCRIPT_DIR      = Path(__file__).parent.resolve()
_NET10_DIR       = (_SCRIPT_DIR / ".." / "CastleDefense.Simulation" / "bin" / "Release" / "net10.0").resolve()
_WEBAPP_DEBUG    = (_SCRIPT_DIR / ".." / "CastleDefenseGame2" / "bin" / "Debug"   / "net10.0").resolve()
_WEBAPP_RELEASE  = (_SCRIPT_DIR / ".." / "CastleDefenseGame2" / "bin" / "Release" / "net10.0").resolve()

ARENA_EXE = str(_NET10_DIR / "CastleDefense.Simulation.exe")
EVAL_CSV  = str(_SCRIPT_DIR / "eval_trajectories.csv")
EVAL_PNG  = str(_SCRIPT_DIR / "eval_trajectories.png")

# Visual constants
P1_COLOR   = '#3b82f6'   # blue
P2_COLOR   = '#ef4444'   # red
EVEN_COLOR = '#6b7280'   # grey
SMOOTH_WIN = 30          # rolling-average window (ticks); 30 ticks = 1 second of game time


# ─── Export helpers ────────────────────────────────────────────────────────────

def find_replay_dir():
    # Prefer multiplayer (human vs human) directory — the gold-standard games.
    # Falls back to the legacy flat recordings/ directory for older replays.
    candidates = [
        _WEBAPP_DEBUG   / "recordings" / "multiplayer",
        _WEBAPP_RELEASE / "recordings" / "multiplayer",
        _WEBAPP_DEBUG   / "recordings",
        _WEBAPP_RELEASE / "recordings",
    ]
    for c in candidates:
        if c.exists() and any(c.glob("*.replay")):
            return str(c)
    return None


def export_eval_data(replay_dir):
    if not os.path.exists(ARENA_EXE):
        print(f"[Eval] ERROR: Simulation exe not found at {ARENA_EXE}")
        print("  Build first: dotnet build CastleDefense.Simulation -c Release")
        sys.exit(1)

    print(f"[Eval] Running C# exporter on {replay_dir} ...")
    result = subprocess.run(
        [ARENA_EXE, "--export-eval", replay_dir, EVAL_CSV],
        cwd=str(_NET10_DIR),
    )
    if result.returncode != 0:
        print("[Eval] ERROR: C# exporter failed.")
        sys.exit(1)


# ─── Plotting ─────────────────────────────────────────────────────────────────

def smooth(series, window=SMOOTH_WIN):
    return series.rolling(window=window, min_periods=1, center=True).mean()


def plot_game(ax, gdf, game_id, winner):
    """Draw one game subplot."""
    times = gdf['time_seconds'].values
    evals = smooth(gdf['board_eval']).values

    # Determine whether P1 or P2 won
    winner_color  = P1_COLOR if winner == 1 else P2_COLOR
    loser_color   = P2_COLOR if winner == 1 else P1_COLOR
    winner_label  = f"P{winner} wins"

    # Main line — coloured by winner
    ax.plot(times, evals, color=winner_color, linewidth=1.8, alpha=0.95, zorder=3)

    # Fill above/below 0.5
    ax.fill_between(times, 0.5, evals,
                    where=(evals >= 0.5), alpha=0.20, color=P1_COLOR, zorder=2)
    ax.fill_between(times, evals, 0.5,
                    where=(evals <  0.5), alpha=0.20, color=P2_COLOR, zorder=2)

    # 50% reference line
    ax.axhline(0.5, color=EVEN_COLOR, linewidth=0.8, linestyle='--', alpha=0.6, zorder=1)

    # Axis labels and limits
    ax.set_ylim(0, 1)
    ax.set_xlim(0, times[-1] if len(times) > 0 else 1)
    ax.yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1, decimals=0))
    ax.set_xlabel("Time (seconds)", fontsize=8)
    ax.set_ylabel("P1 win prob.", fontsize=8)
    ax.tick_params(labelsize=7)
    ax.grid(True, alpha=0.25)

    # Title: winner colour + game id
    ax.set_title(f"{game_id}  —  {winner_label}",
                 fontsize=9, color=winner_color, fontweight='bold')

    # Add start/end eval annotations
    start_eval = evals[0] if len(evals) > 0 else 0.5
    end_eval   = evals[-1] if len(evals) > 0 else 0.5
    ax.annotate(f"{start_eval:.2f}", xy=(times[0],  start_eval),
                xytext=(4, 4), textcoords='offset points', fontsize=7, color=EVEN_COLOR)
    ax.annotate(f"{end_eval:.2f}", xy=(times[-1], end_eval),
                xytext=(-28, 4), textcoords='offset points', fontsize=7, color=winner_color)


def plot_overlay(ax, df):
    """All games on a single axis for a quick overview."""
    for game_id, gdf in df.groupby('game_id'):
        winner = int(gdf['winner'].iloc[0])
        color  = P1_COLOR if winner == 1 else P2_COLOR
        evals  = smooth(gdf['board_eval'])
        times  = gdf['time_seconds'].values
        ax.plot(times, evals, color=color, linewidth=1.2, alpha=0.55)

    ax.axhline(0.5, color=EVEN_COLOR, linewidth=1.0, linestyle='--', alpha=0.7)
    ax.set_ylim(0, 1)
    ax.yaxis.set_major_formatter(mticker.PercentFormatter(xmax=1, decimals=0))
    ax.set_xlabel("Time (seconds)", fontsize=9)
    ax.set_ylabel("P1 win prob.", fontsize=9)
    ax.set_title("All Games (blue = P1 wins, red = P2 wins)", fontsize=10)
    ax.grid(True, alpha=0.25)

    # Manual legend patches
    from matplotlib.patches import Patch
    ax.legend(handles=[Patch(color=P1_COLOR, label='P1 wins'),
                        Patch(color=P2_COLOR, label='P2 wins')],
              fontsize=8, loc='upper left')


def make_figure(csv_path, output_path):
    df = pd.read_csv(csv_path)
    games   = df['game_id'].unique()
    n_games = len(games)

    if n_games == 0:
        print("[Eval] ERROR: no games in CSV.")
        sys.exit(1)

    print(f"[Eval] Plotting {n_games} game(s) ...")

    # Layout: top row = overlay, then grid of individual games
    NCOLS    = min(3, n_games)
    game_rows = (n_games + NCOLS - 1) // NCOLS
    total_rows = 1 + game_rows

    fig = plt.figure(figsize=(6 * NCOLS, 4 * total_rows))
    fig.suptitle("Board Evaluator — P1 Win Probability Over Time",
                 fontsize=13, fontweight='bold', y=0.98)

    # Overlay row
    ax_overlay = fig.add_subplot(total_rows, 1, 1)
    plot_overlay(ax_overlay, df)

    # Individual game grid
    for idx, game_id in enumerate(games):
        gdf    = df[df['game_id'] == game_id]
        winner = int(gdf['winner'].iloc[0])
        row    = 1 + idx // NCOLS
        col    = idx % NCOLS
        ax     = fig.add_subplot(total_rows, NCOLS, NCOLS + 1 + idx)
        plot_game(ax, gdf, game_id, winner)

    # Hide unused subplot slots in the last row
    for idx in range(n_games, game_rows * NCOLS):
        ax = fig.add_subplot(total_rows, NCOLS, NCOLS + 1 + idx)
        ax.set_visible(False)

    plt.tight_layout(rect=[0, 0, 1, 0.97])
    plt.savefig(output_path, dpi=150, bbox_inches='tight')
    print(f"[Eval] Saved -> {output_path}")

    # Print a quick summary table
    print("\n[Eval] Summary:")
    print(f"  {'Game ID':<10} {'Winner':<8} {'Duration':<12} {'Start':>7} {'End':>7} {'Max':>7} {'Min':>7}")
    print("  " + "-" * 60)
    for game_id in games:
        gdf      = df[df['game_id'] == game_id]
        winner   = int(gdf['winner'].iloc[0])
        dur      = f"{gdf['time_seconds'].iloc[-1]:.0f}s"
        e        = smooth(gdf['board_eval'])
        print(f"  {game_id:<10} P{winner:<7} {dur:<12} {e.iloc[0]:>6.3f}  {e.iloc[-1]:>6.3f}  {e.max():>6.3f}  {e.min():>6.3f}")


# ─── Entry point ──────────────────────────────────────────────────────────────

def main():
    replay_dir  = None
    output_path = EVAL_PNG
    skip_export = False

    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if   args[i] == "--replay-dir" and i + 1 < len(args): replay_dir  = args[i+1]; i += 2
        elif args[i] == "--output"     and i + 1 < len(args): output_path = args[i+1]; i += 2
        elif args[i] == "--no-export":                          skip_export = True; i += 1
        else: i += 1

    if not skip_export:
        if replay_dir is None:
            replay_dir = find_replay_dir()
            if replay_dir is None:
                print("[Eval] ERROR: Could not find a recordings directory with .replay files.")
                print("  Pass --replay-dir <path> explicitly.")
                sys.exit(1)
        print(f"[Eval] Replay dir: {replay_dir}")
        export_eval_data(replay_dir)

    if not os.path.exists(EVAL_CSV):
        print(f"[Eval] ERROR: {EVAL_CSV} not found. Run without --no-export first.")
        sys.exit(1)

    make_figure(EVAL_CSV, output_path)


if __name__ == "__main__":
    main()
