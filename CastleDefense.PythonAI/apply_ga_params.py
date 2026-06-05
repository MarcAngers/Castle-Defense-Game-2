"""
Copies GA best params to all 14 arena reward_params_{port}.json files so that
the next train_ai_cluster.py run picks them up.

Usage:
    python apply_ga_params.py                    # uses ga_best_params.json
    python apply_ga_params.py my_params.json     # uses a specific file
    python apply_ga_params.py --reset            # resets all arenas to defaults

Run this before starting the training cluster, or between cluster connections.
"""

import json
import sys
from pathlib import Path

_SCRIPT_DIR = Path(__file__).parent.resolve()
NET10_DIR   = (_SCRIPT_DIR / ".." / "CastleDefense.Simulation" / "bin" / "Release" / "net10.0").resolve()
PORTS       = list(range(5000, 5014))

DEFAULTS_FILE = _SCRIPT_DIR / "reward_defaults.json"


def apply(params: dict, label: str):
    for port in PORTS:
        out = NET10_DIR / f"reward_params_{port}.json"
        with open(out, "w") as f:
            json.dump(params, f, indent=2)
    print(f"Applied {label} to all {len(PORTS)} arena ports.")
    print(f"  WinReward={params.get('WinReward')}  InvestReward={params.get('InvestReward')}  "
          f"InvestDecay={params.get('InvestDecay')}  CombatScale={params.get('CombatScale')}")
    print(f"  AntiSpend={params.get('AntiSpend')}  SavingsWeight={params.get('SavingsWeight')}  "
          f"GadgetUpgrade={params.get('GadgetUpgrade')}  GadgetUse={params.get('GadgetUse')}")


def main():
    if "--reset" in sys.argv:
        if not DEFAULTS_FILE.exists():
            print(f"ERROR: {DEFAULTS_FILE} not found.")
            sys.exit(1)
        with open(DEFAULTS_FILE) as f:
            params = json.load(f)
        apply(params, "defaults")
        return

    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    src  = Path(args[0]) if args else _SCRIPT_DIR / "ga_best_params.json"

    if not src.exists():
        print(f"ERROR: {src} not found.")
        if src.name == "ga_best_params.json":
            print("  Run ga_runner.py first to generate best params.")
        sys.exit(1)

    if not NET10_DIR.exists():
        print(f"ERROR: Arena directory not found: {NET10_DIR}")
        print("  Build the project first: dotnet build -c Release")
        sys.exit(1)

    with open(src) as f:
        params = json.load(f)

    apply(params, src.name)


if __name__ == "__main__":
    main()
