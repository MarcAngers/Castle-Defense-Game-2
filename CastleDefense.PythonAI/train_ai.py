import socket
import json
import random
import os
import numpy as np
import gymnasium as gym
from gymnasium import spaces
from sb3_contrib import MaskablePPO
from stable_baselines3 import PPO

HOST = '127.0.0.1'
PORT = 5000

class CastleDefenseEnv(gym.Env):
    def __init__(self, opponent_model_path=None):
        super().__init__()
        self.action_space = spaces.Discrete(14)
        self.observation_space = spaces.Box(low=-np.inf, high=np.inf, shape=(348,), dtype=np.float32)
        
        # --- NEW: Load the Sparring Partner ---
        self.opponent = None
        if opponent_model_path and os.path.exists(opponent_model_path):
            print(f"Loading Sparring Partner from {opponent_model_path}...")
            # We load the opponent strictly on the CPU for fast inference
            self.opponent = MaskablePPO.load(opponent_model_path, device="cpu")
        else:
            print("No opponent model found. Player 2 will be a Random Dummy.")
            
        self.current_p2_state = None

        print(f"Connecting to C# Engine at {HOST}:{PORT}...")
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.sock.connect((HOST, PORT))
        self.stream = self.sock.makefile('rw', buffering=1)

        self.current_step = 0
        self.total_anneal_steps = 20_000_000 # The training wheels fall off at 20M

    def reset(self, seed=None, options=None):
        super().reset(seed=seed)
        
        message = self.stream.readline()
        result = json.loads(message)

        # --- NEW: Flip the coin for this match! ---
        self.ai_is_p1 = True #random.choice([True, False])
        
        # Route the states based on who the AI is playing as this match
        if self.ai_is_p1:
            ai_state = np.array(result["P1State"], dtype=np.float32)
            self.current_opp_state = np.array(result["P2State"], dtype=np.float32)
            self.current_mask = np.array(result["P1ActionMask"], dtype=np.int8)
        else:
            ai_state = np.array(result["P2State"], dtype=np.float32)
            self.current_opp_state = np.array(result["P1State"], dtype=np.float32)
            self.current_mask = np.array(result["P2ActionMask"], dtype=np.int8)
            
        return ai_state, {}

    def step(self, action):
        ai_action = int(action)
        self.current_step += 1

        dense_weight = max(0.0, 1.0 - (self.current_step / self.total_anneal_steps))
        
        # --- NEW: Opponent Logic (Now side-agnostic!) ---
        if self.opponent is not None and self.current_opp_state is not None:
            # deterministic=True makes the opponent play its absolute best
            opp_action, _ = self.opponent.predict(self.current_opp_state, deterministic=True)
            opp_action = int(opp_action)
        else:
            opp_action = random.randint(0, 13)

        # Route the actions based on the coin flip
        if self.ai_is_p1:
            payload = {"P1Action": ai_action, "P2Action": opp_action, "DenseRewardWeight": float(dense_weight)}
        else:
            payload = {"P1Action": opp_action, "P2Action": ai_action, "DenseRewardWeight": float(dense_weight)}
            
        self.stream.write(json.dumps(payload) + '\n')

        message = self.stream.readline()
        result = json.loads(message)

        # Route the resulting states and rewards back to the correct brains
        if self.ai_is_p1:
            ai_state = np.array(result["P1State"], dtype=np.float32)
            self.current_opp_state = np.array(result["P2State"], dtype=np.float32)
            self.current_mask = np.array(result["P1ActionMask"], dtype=np.int8)
            ai_reward = float(result["P1Reward"])
        else:
            ai_state = np.array(result["P2State"], dtype=np.float32)
            self.current_opp_state = np.array(result["P1State"], dtype=np.float32)
            self.current_mask = np.array(result["P2ActionMask"], dtype=np.int8)
            ai_reward = float(result["P2Reward"])

        done = bool(result["IsDone"])

        return ai_state, ai_reward, done, False, {}

    def action_masks(self):
        return self.current_mask

    def close(self):
        self.sock.close()

# --- THE TRAINING LOOP ---
if __name__ == "__main__":
    
    # 1. Define your model names
    training_model_name = "castle_defense_p1_v5"
    sparring_model_name = "castle_defense_p1_v4.zip"

    # 2. Pass the sparring model into the environment
    env = CastleDefenseEnv(opponent_model_path=sparring_model_name)

    # 3. Resume or Create the Training Brain
    model_file = f"{training_model_name}.zip"
    
    # Bundle our new long-term vision settings into a dictionary
    custom_hyperparams = {
        "n_steps": 8192,
        "batch_size": 1024,
        "n_epochs": 10,
        "gamma": 0.999,
        "learning_rate": 0.0003,
        "ent_coef": 0.0001,
        "policy_kwargs": dict(net_arch=[256, 256])
    }

    if os.path.exists(model_file):
        print(f"Resuming training for {model_file} with upgraded hyperparameters...")
        # We use custom_objects to force the old brain to adopt the new vision/batch settings
        model = MaskablePPO.load(training_model_name, env=env, custom_objects=custom_hyperparams, verbose=1)
    else:
        print("Initializing brand new Neural Network with macro-strategy settings...")
        # We unpack the dictionary using ** to apply the settings to the new brain
        model = MaskablePPO("MlpPolicy", env, verbose=1, **custom_hyperparams)

    # 4. Train!
    print("Beginning Training... (Press Ctrl+C to stop early and save)")
    
    try:
        # We put the learn function inside a "try" block
        model.learn(total_timesteps=50000000, reset_num_timesteps=False)
    
    except KeyboardInterrupt:
        # If you hit Ctrl+C, Python catches the interrupt here instead of crashing
        print("\nTraining interrupted by user! Wrapping up and saving brain...")

    model.save(training_model_name)
    print("Model saved successfully!")
    env.close()