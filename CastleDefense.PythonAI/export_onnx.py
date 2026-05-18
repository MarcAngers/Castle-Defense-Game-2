import torch as th
from sb3_contrib import MaskablePPO

print("Loading the trained brain...")
# 1. Load your best model. We force it onto the CPU so the resulting 
# ONNX file will run on any server, even if it doesn't have a fancy graphics card.
model = MaskablePPO.load("castle_defense_p1_v18", device="cpu")

# 2. Create a clean wrapper that ONLY outputs the action choices
class CleanActionNetwork(th.nn.Module):
    def __init__(self, policy):
        super().__init__()
        self.policy = policy

    def forward(self, observation):
        # Pass the observation through the network's feature extractor
        features = self.policy.extract_features(observation)
        # Pass it through the hidden layers (the "brain")
        latent_pi, _ = self.policy.mlp_extractor(features)
        # Output the final 14 raw scores (logits) for the 14 possible actions
        return self.policy.action_net(latent_pi)

print("Stripping away training data...")
onnx_policy = CleanActionNetwork(model.policy)

# 3. Create a dummy input so the ONNX exporter knows the exact shape of your data.
# This represents 1 player submitting an array of 340 floats.
dummy_input = th.randn(1, 348)

print("Exporting to ONNX...")
# 4. Export the clean network!
th.onnx.export(
    onnx_policy,
    dummy_input,
    "castle_defense_bot.onnx",
    opset_version=17,
    input_names=["observation"],
    output_names=["action_logits"]
)

print("Success! Your AI is now packed into 'castle_defense_bot.onnx'!")