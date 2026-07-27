"""
2026-07-27 cheap validation probe (zero training cost): does the ALREADY-TRAINED
critic (value function) inside castle_defense_p1_v30.zip already recognize investing
as a good decision, or is even the critic blind to it?

This is the complement to invest-counterfactual's causal A/B result (which showed a
huge, real, ground-truth reward benefit from investing). If the critic agrees
(positive TD-style advantage for the forced-invest transitions), that confirms the
ONLY blocker is the policy's sampling probability (P(invest) collapsed to ~1e-187) --
a probability floor should be a clean fix, since the credit-assignment machinery
already "wants" to invest more, it just never gets sampled. If the critic disagrees
(near-zero or negative advantage) despite the real causal benefit being large, that
would mean the critic itself is miscalibrated around investing (plausible if it's
never seen enough real invest transitions to learn their value well) -- a floor
would still help by generating more such transitions to recalibrate the critic, but
the credit-assignment gap would be a compounding factor, not just a sampling one.

Usage:
    python analyze_invest_advantage.py invest_dump_heuristic.csv invest_dump_selfplay.csv
"""
import sys
import numpy as np
import torch as th
from sb3_contrib import MaskablePPO

GAMMA = 0.9998
MODEL_NAME = "castle_defense_p1_v30"
N_OBS = 348


def load_dump(path):
    import csv
    rows = []
    with open(path, newline="") as f:
        reader = csv.DictReader(f)
        for r in reader:
            before = np.array([float(r[f"before_{i}"]) for i in range(N_OBS)], dtype=np.float32)
            after = np.array([float(r[f"after_{i}"]) for i in range(N_OBS)], dtype=np.float32)
            rows.append({
                "opponent": r["opponent"],
                "reward": float(r["decision_reward"]),
                "done": int(r["done"]),
                "before": before,
                "after": after,
            })
    return rows


def main():
    paths = sys.argv[1:] or ["invest_dump_heuristic.csv", "invest_dump_selfplay.csv"]
    print(f"Loading {MODEL_NAME}...")
    model = MaskablePPO.load(MODEL_NAME, device="cpu")
    model.policy.set_training_mode(False)

    all_rows = []
    for p in paths:
        try:
            rows = load_dump(p)
            print(f"  {p}: {len(rows)} transitions")
            all_rows.extend(rows)
        except FileNotFoundError:
            print(f"  {p}: not found, skipping")

    if not all_rows:
        print("No data loaded.")
        return

    before_batch = np.stack([r["before"] for r in all_rows])
    after_batch = np.stack([r["after"] for r in all_rows])

    with th.no_grad():
        v_before = model.policy.predict_values(th.FloatTensor(before_batch)).flatten().numpy()
        v_after = model.policy.predict_values(th.FloatTensor(after_batch)).flatten().numpy()

    print(f"\n{'opponent':<12} {'reward':>10} {'V(before)':>10} {'V(after)':>10} {'advantage':>10}")
    by_opp = {}
    for i, r in enumerate(all_rows):
        reward = r["reward"]
        vb = v_before[i]
        va = 0.0 if r["done"] else v_after[i]
        advantage = reward + GAMMA * va - vb
        by_opp.setdefault(r["opponent"], []).append((reward, vb, va, advantage))

    for opp, vals in by_opp.items():
        rewards = [v[0] for v in vals]
        vbs = [v[1] for v in vals]
        vas = [v[2] for v in vals]
        advs = [v[3] for v in vals]
        print(f"\n=== {opp} (n={len(vals)}) ===")
        print(f"  mean reward:     {np.mean(rewards):+.4f}")
        print(f"  mean V(before):  {np.mean(vbs):+.4f}")
        print(f"  mean V(after):   {np.mean(vas):+.4f}")
        print(f"  mean advantage:  {np.mean(advs):+.4f}   (median {np.median(advs):+.4f}, std {np.std(advs):.4f})")
        pct_positive = 100.0 * sum(1 for a in advs if a > 0) / len(advs)
        print(f"  % transitions with positive advantage: {pct_positive:.1f}%")

    all_advs = [v[3] for vals in by_opp.values() for v in vals]
    print(f"\n=== OVERALL (n={len(all_advs)}) ===")
    print(f"  mean advantage: {np.mean(all_advs):+.4f}  % positive: {100.0*sum(1 for a in all_advs if a > 0)/len(all_advs):.1f}%")


if __name__ == "__main__":
    main()
