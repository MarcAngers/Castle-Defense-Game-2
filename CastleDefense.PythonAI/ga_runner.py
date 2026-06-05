"""
Genetic Algorithm runner for Castle Defense reward function tuning.

Architecture:
  - 14 arenas started once at the top of the run, stay alive across all models
  - 7 candidate models evaluated sequentially, each using all 14 arenas
  - Each generation: train all 7 in sequence for TOTAL_STEPS each, then select + mutate
  - Fitness: win rate over last FITNESS_WINDOW episodes of training
  - Selection: top 3 survive, 4 new mutations
  - Mutation: lognormal (sigma=SIGMA), scale-invariant, always positive
  - Logs 1/5 success rate per generation for manual sigma tuning

Run from CastleDefense.PythonAI/:
    ai_env\\Scripts\\activate
    python ga_runner.py

Ctrl+C for a clean stop after the current model finishes.
"""

import os
import sys
import json
import time
import csv
import math
import random
import subprocess
import signal
import shutil
from pathlib import Path

# ─── Constants ────────────────────────────────────────────────────────────────

MAX_GENERATIONS = 30
N_MODELS        = 7
N_ARENAS        = 14
BASE_PORT       = 5000
SIGMA           = 0.3    # lognormal mutation std — watch success_rate in ga_progress.csv
N_PARENTS       = 3      # top N survive each generation

ALL_PORTS = list(range(BASE_PORT, BASE_PORT + N_ARENAS))

# Absolute path to the Simulation release directory (arenas run from here)
_SCRIPT_DIR = Path(__file__).parent.resolve()
NET10_DIR   = (_SCRIPT_DIR / ".." / "CastleDefense.Simulation" / "bin" / "Release" / "net10.0").resolve()
ARENA_EXE   = str(NET10_DIR / "CastleDefense.Simulation.exe")
ONNX_PATH        = str(NET10_DIR / "current_model.onnx")
BEST_MODEL_ONNX  = str(_SCRIPT_DIR / "ga_best_model.onnx")

PARAM_NAMES   = ["WinReward", "InvestReward", "InvestDecay", "AntiSpend",
                 "SavingsWeight", "CombatScale", "GadgetUpgrade", "GadgetUse"]
GA_LOG        = str(_SCRIPT_DIR / "ga_progress.csv")
DEFAULTS_FILE = str(_SCRIPT_DIR / "reward_defaults.json")

_stop_requested = False

def _handle_sigint(sig, frame):
    global _stop_requested
    print("\n[GA] Ctrl+C detected — will stop after the current model finishes.")
    _stop_requested = True

signal.signal(signal.SIGINT, _handle_sigint)


# ─── Reward params helpers ────────────────────────────────────────────────────

def load_defaults():
    with open(DEFAULTS_FILE) as f:
        d = json.load(f)
    return {k: float(d[k]) for k in PARAM_NAMES}


def mutate(params, sigma):
    """Lognormal mutation: new = old × exp(N(0, sigma)). Scale-invariant, always positive."""
    return {k: v * math.exp(random.gauss(0.0, sigma)) for k, v in params.items()}


def write_reward_json(params, path):
    with open(path, "w") as f:
        json.dump(params, f, indent=2)


# ─── Arena management ─────────────────────────────────────────────────────────

def start_all_arenas():
    """Launch all 14 C# arenas. Called once at the start of the run."""
    procs = []
    for port in ALL_PORTS:
        proc = subprocess.Popen(
            [ARENA_EXE, str(port)],
            cwd=str(NET10_DIR),
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        procs.append(proc)
        print(f"  [Arena] Started port {port}, PID={proc.pid}")
    return procs


def stop_all_arenas(procs):
    for p in procs:
        try:
            p.terminate()
        except Exception:
            pass
    for p in procs:
        try:
            p.wait(timeout=5)
        except Exception:
            pass


# ─── Single-model training ────────────────────────────────────────────────────

def train_model(model_idx, params):
    """
    Write reward params for all arenas, then run ga_train_model.py.
    Arenas reload params on the next Python connection — no restart needed.
    Returns fitness float.
    """
    for port in ALL_PORTS:
        write_reward_json(params, str(NET10_DIR / f"reward_params_{port}.json"))

    ports_csv = ",".join(str(p) for p in ALL_PORTS)
    out_file  = str(_SCRIPT_DIR / f"ga_fitness_{model_idx}.json")

    cmd = [sys.executable, str(_SCRIPT_DIR / "ga_train_model.py"),
           str(model_idx), ports_csv, ONNX_PATH, out_file]

    print(f"  [Model {model_idx}] Training (all {N_ARENAS} arenas)...")
    subprocess.run(cmd, timeout=7200)

    try:
        with open(out_file) as f:
            return json.load(f)["fitness"]
    except Exception as e:
        print(f"  [Model {model_idx}] Could not read fitness: {e}")
        return 0.0


# ─── GA log ───────────────────────────────────────────────────────────────────

def init_log():
    if not os.path.exists(GA_LOG):
        with open(GA_LOG, "w", newline="") as f:
            writer = csv.writer(f)
            writer.writerow(["generation", "model_idx", "parent_id", "fitness",
                             "improved", "success_rate", "sigma"] + PARAM_NAMES)


def log_generation(gen_idx, results, sigma):
    n_mutations  = sum(1 for r in results if r["parent_id"] is not None)
    n_improved   = sum(1 for r in results if r.get("improved", False))
    success_rate = n_improved / n_mutations if n_mutations > 0 else float("nan")

    with open(GA_LOG, "a", newline="") as f:
        writer = csv.writer(f)
        for r in results:
            row = [gen_idx, r["model_idx"], r["parent_id"], round(r["fitness"], 5),
                   int(r.get("improved", False)), round(success_rate, 4), sigma]
            row += [round(r["params"][k], 6) for k in PARAM_NAMES]
            writer.writerow(row)

    print(f"\n[GA Gen {gen_idx}] Success rate: {success_rate:.0%} "
          f"({n_improved}/{n_mutations} mutations improved) | sigma={sigma}")
    if not math.isnan(success_rate):
        if success_rate > 0.4:
            print("  >> Consider increasing sigma (>1/5 rule, mutations are too conservative)")
        elif success_rate < 0.1:
            print("  >> Consider decreasing sigma (<1/5 rule, mutations are too aggressive)")
        else:
            print("  >> sigma appears well-calibrated")


# ─── Main GA loop ─────────────────────────────────────────────────────────────

def run_generation(gen_idx, candidates, best_fitness_ever=0.0):
    """
    Evaluate all N_MODELS candidates sequentially against the shared arena pool.
    Returns (results, updated_best_fitness_ever).
    Saves ga_best_model.onnx whenever a new all-time fitness record is set.
    """
    print(f"\n{'='*60}")
    print(f"[GA] Generation {gen_idx} — {N_MODELS} models, {N_ARENAS} arenas")
    print(f"{'='*60}")

    results = []
    for model_idx, cand in enumerate(candidates):
        if _stop_requested:
            break  # don't log unevaluated models — avoids 0-fitness outliers in the graph

        fitness  = train_model(model_idx, cand["params"])
        improved = (cand["parent_id"] is not None and fitness > cand["parent_fitness"])

        if fitness > best_fitness_ever:
            best_fitness_ever = fitness
            save_best_model(fitness)

        print(f"  [Model {model_idx}] Fitness: {fitness:.4f}"
              + (" (+improved)" if improved else ""))

        results.append({
            "model_idx":      model_idx,
            "params":         cand["params"],
            "parent_id":      cand["parent_id"],
            "parent_fitness": cand["parent_fitness"],
            "fitness":        fitness,
            "improved":       improved,
        })

    return results, best_fitness_ever


def build_next_generation(results, sigma):
    sorted_results = sorted(results, key=lambda r: r["fitness"], reverse=True)
    parents = sorted_results[:N_PARENTS]

    print(f"\n[GA] Top {N_PARENTS} survivors:")
    for rank, r in enumerate(parents):
        print(f"  #{rank+1}: Model {r['model_idx']} — fitness={r['fitness']:.4f}")

    next_gen = []
    for parent in parents:
        next_gen.append({
            "params":         parent["params"],
            "parent_id":      parent["model_idx"],
            "parent_fitness": parent["fitness"],
        })
    for i in range(N_MODELS - N_PARENTS):
        parent = parents[i % len(parents)]
        next_gen.append({
            "params":         mutate(parent["params"], sigma),
            "parent_id":      parent["model_idx"],
            "parent_fitness": parent["fitness"],
        })

    return next_gen


def cleanup_temp_files():
    """Delete per-run temp files that would pollute a new run."""
    for i in range(N_MODELS):
        f = _SCRIPT_DIR / f"ga_fitness_{i}.json"
        if f.exists():
            f.unlink()
    for port in ALL_PORTS:
        for suffix in [".json", ".txt"]:
            f = NET10_DIR / f"training_stats_{port}{suffix}"
            if f.exists():
                f.unlink()
        f = NET10_DIR / f"reward_params_{port}.json"
        if f.exists():
            f.unlink()
    for old_onnx in NET10_DIR.glob("current_model_ga*.onnx"):
        old_onnx.unlink()
    for old_data in NET10_DIR.glob("current_model_ga*.onnx.data"):
        old_data.unlink()


def archive_previous_run():
    """
    Archive the previous run's results to a timestamped folder, then clean temp files.
    Only called when --reset is passed; by default the run continues from prior data.
    """
    if os.path.exists(GA_LOG):
        ts          = time.strftime("%Y%m%d_%H%M%S", time.localtime(os.path.getmtime(GA_LOG)))
        archive_dir = _SCRIPT_DIR / "ga_runs" / f"run_{ts}"
        archive_dir.mkdir(parents=True, exist_ok=True)
        for fname in ["ga_progress.csv", "ga_progress.png", "ga_best_params.json",
                      "ga_best_model.onnx", "ga_best_model.onnx.data"]:
            src = _SCRIPT_DIR / fname
            if src.exists():
                shutil.move(str(src), str(archive_dir / fname))
        print(f"[GA] Previous run archived to ga_runs/run_{ts}/")
    cleanup_temp_files()


def save_best_model(fitness):
    """Copy current_model.onnx → ga_best_model.onnx when a new fitness record is set."""
    shutil.copy2(ONNX_PATH, BEST_MODEL_ONNX)
    data_src = ONNX_PATH + ".data"
    if os.path.exists(data_src):
        shutil.copy2(data_src, BEST_MODEL_ONNX + ".data")
    print(f"  ★ New best model saved to ga_best_model.onnx (fitness={fitness:.4f})")


def save_best_params():
    all_rows = []
    try:
        with open(GA_LOG) as f:
            all_rows = list(csv.DictReader(f))
    except Exception:
        return

    if not all_rows:
        return

    best_row    = max(all_rows, key=lambda r: float(r["fitness"]))
    best_params = {k: float(best_row[k]) for k in PARAM_NAMES}
    best_out    = str(_SCRIPT_DIR / "ga_best_params.json")
    with open(best_out, "w") as f:
        json.dump(best_params, f, indent=2)
    print(f"\n[GA] Best params saved to {best_out}")
    print(f"  fitness={best_row['fitness']}  gen={best_row['generation']}  model={best_row['model_idx']}")


def load_last_generation():
    """
    Read ga_progress.csv and reconstruct candidates for the next generation.
    Returns (start_gen, candidates), or (0, None) if no prior data exists.
    """
    if not os.path.exists(GA_LOG):
        return 0, None
    try:
        with open(GA_LOG) as f:
            rows = list(csv.DictReader(f))
    except Exception:
        return 0, None
    if not rows:
        return 0, None

    last_gen  = max(int(r["generation"]) for r in rows)
    last_rows = [r for r in rows if int(r["generation"]) == last_gen]

    results = [{
        "model_idx":      int(r["model_idx"]),
        "params":         {k: float(r[k]) for k in PARAM_NAMES},
        "parent_id":      r["parent_id"] or None,   # csv writes None as ""
        "parent_fitness": float(r["fitness"]),
        "fitness":        float(r["fitness"]),
        "improved":       r.get("improved", "0") == "1",
    } for r in last_rows]

    return last_gen + 1, build_next_generation(results, SIGMA)


# ─── Entry point ──────────────────────────────────────────────────────────────

def main():
    global _stop_requested

    if not NET10_DIR.exists():
        print(f"[GA] ERROR: Arena directory not found: {NET10_DIR}")
        print("  Build the project first: dotnet build -c Release")
        sys.exit(1)

    if not Path(DEFAULTS_FILE).exists():
        print(f"[GA] ERROR: {DEFAULTS_FILE} not found.")
        sys.exit(1)

    fresh_start = "--reset" in sys.argv

    if fresh_start:
        archive_previous_run()
        print("[GA] Fresh start — previous data archived.")
    else:
        cleanup_temp_files()

    # Load prior results before init_log() so we read the existing CSV (if any)
    start_gen, candidates = load_last_generation()
    if candidates is None:
        defaults   = load_defaults()
        candidates = [{"params": mutate(defaults, SIGMA), "parent_id": None, "parent_fitness": 0.0}
                      for _ in range(N_MODELS)]
        best_fitness_ever = 0.0
        print("[GA] No previous run found — starting from defaults.")
    else:
        try:
            with open(GA_LOG) as f:
                all_rows = list(csv.DictReader(f))
            best_fitness_ever = max(float(r["fitness"]) for r in all_rows) if all_rows else 0.0
        except Exception:
            best_fitness_ever = 0.0
        print(f"[GA] Resuming from generation {start_gen} using last generation's survivors.")
        print(f"[GA] Historical best fitness: {best_fitness_ever:.4f}")

    init_log()

    plot_script = str(_SCRIPT_DIR / "plot_ga.py")
    if os.path.exists(plot_script):
        subprocess.Popen([sys.executable, plot_script, "--watch", GA_LOG])
        print("[GA] Live graph launched.")

    print(f"[GA] Starting. MAX_GENERATIONS={MAX_GENERATIONS}, N_MODELS={N_MODELS}, "
          f"N_ARENAS={N_ARENAS}, sigma={SIGMA}")
    print(f"[GA] Arena dir: {NET10_DIR}")
    print(f"[GA] Progress log: {GA_LOG}")

    print(f"\n[GA] Starting {N_ARENAS} arenas (loading league models once)...")
    arena_procs = start_all_arenas()
    print(f"[GA] Waiting for arenas to finish loading...")
    time.sleep(15)  # give arenas time to load all league models before first connection
    print(f"[GA] Arenas ready.")

    try:
        for gen_idx in range(start_gen, start_gen + MAX_GENERATIONS):
            results, best_fitness_ever = run_generation(gen_idx, candidates, best_fitness_ever)

            if results:
                log_generation(gen_idx, results, SIGMA)
                best = max(results, key=lambda r: r["fitness"])
                print(f"\n[GA Gen {gen_idx}] Best: fitness={best['fitness']:.4f}  "
                      f"(Model {best['model_idx']})")
                print(f"  Params: { {k: round(v,4) for k,v in best['params'].items()} }")
                save_best_params()

            if _stop_requested:
                print("\n[GA] Stop requested.")
                break

            candidates = build_next_generation(results, SIGMA)

    finally:
        print(f"\n[GA] Stopping {N_ARENAS} arenas...")
        stop_all_arenas(arena_procs)
        print("[GA] Done.")


if __name__ == "__main__":
    main()
