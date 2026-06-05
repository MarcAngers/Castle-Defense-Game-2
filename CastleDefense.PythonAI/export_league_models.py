"""
Exports all league model .zip files to ONNX format for use by the C# training arenas.
Output goes to CastleDefense.Simulation/bin/Release/net10.0/league_models/

Run from CastleDefense.PythonAI/ with the ai_env activated:
    python export_league_models.py
"""
import os
import torch as th
from sb3_contrib import MaskablePPO
from stable_baselines3 import PPO

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
OUTPUT_DIR = os.path.normpath(os.path.join(
    SCRIPT_DIR, "..", "CastleDefense.Simulation", "bin", "Release", "net10.0", "league_models"
))

class CleanActionNetwork(th.nn.Module):
    def __init__(self, policy):
        super().__init__()
        self.policy = policy

    def forward(self, observation):
        features = self.policy.extract_features(observation)
        latent_pi, _ = self.policy.mlp_extractor(features)
        return self.policy.action_net(latent_pi)


def export_model(model_name, output_dir):
    zip_path = model_name + ".zip"
    if not os.path.exists(zip_path):
        print(f"[SKIP] {zip_path} not found")
        return False

    try:
        model = MaskablePPO.load(model_name, device="cpu")
    except Exception:
        try:
            model = PPO.load(model_name, device="cpu")
        except Exception as e:
            print(f"[ERROR] Could not load {model_name}: {e}")
            return False

    os.makedirs(output_dir, exist_ok=True)
    out_path = os.path.join(output_dir, model_name + ".onnx")

    onnx_net = CleanActionNetwork(model.policy)
    dummy_input = th.randn(1, 348)
    th.onnx.export(
        onnx_net, dummy_input, out_path,
        opset_version=17,
        input_names=["observation"],
        output_names=["action_logits"],
        dynamo=False,
    )
    print(f"[OK] {model_name} → {out_path}")
    return True


if __name__ == "__main__":
    import glob
    import shutil
    zips = sorted(glob.glob("castle_defense_p1_v*.zip"))
    models = [os.path.splitext(z)[0] for z in zips]

    if not models:
        print("No castle_defense_p1_vN.zip files found in current directory.")
    else:
        print(f"Found {len(models)} model(s): {', '.join(models)}")
        print(f"Output directory: {OUTPUT_DIR}\n")

        if os.path.exists(OUTPUT_DIR):
            shutil.rmtree(OUTPUT_DIR)
            print(f"[Cleared] {OUTPUT_DIR}")

        success = sum(export_model(m, OUTPUT_DIR) for m in models)
        print(f"\nDone! {success}/{len(models)} exported.")
