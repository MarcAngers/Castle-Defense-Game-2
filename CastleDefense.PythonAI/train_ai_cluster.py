import socket
import json
import random
import os
import numpy as np
import gymnasium as gym
from gymnasium import spaces
from sb3_contrib import MaskablePPO
from stable_baselines3 import PPO
from stable_baselines3.common.vec_env import SubprocVecEnv

def make_env(rank, opponent_models, base_speed):
    def _init():
        # Each environment gets a unique port: 5000, 5001, 5002, etc.
        unique_port = 5000 + rank 
        env = CastleDefenseEnv(opponent_models, base_speed, unique_port)
        return env
    return _init

HOST = '127.0.0.1'
PORT = 5000

class CastleDefenseEnv(gym.Env):
    # 1. Add 'port=5000' to the accepted arguments
    def __init__(self, opponent_models=None, base_speed=3, port=5000):
        super().__init__()
        self.action_space = spaces.Discrete(14)
        self.observation_space = spaces.Box(low=-np.inf, high=np.inf, shape=(348,), dtype=np.float32)
        
        self.base_speed = base_speed 
        self.opponents = []
        
        if opponent_models:
            for opp in opponent_models:
                path = opp["path"]
                if os.path.exists(path):
                    print(f"Drafting {path} into the League (APM Speed: {opp['speed']})...")
                    
                    # Try MaskablePPO first. If it crashes, it's a legacy PPO model!
                    try:
                        model = MaskablePPO.load(path, device="cpu")
                        is_maskable = True
                    except:
                        model = PPO.load(path, device="cpu")
                        is_maskable = False
                    
                    # Store everything we need to know about this sparring partner
                    self.opponents.append({
                        "name": path.replace(".zip", ""), # Clean name for analytics
                        "model": model,
                        "is_maskable": is_maskable,
                        "speed": opp["speed"]
                    })
                else:
                    print(f"[WARNING] Could not find sparring partner: {path}")

        self.current_opponent = None
        self.current_opp_state = None
        self.current_opp_mask = None
        self.opp_step_counter = 0 

        # 2. Update the connection logic to use the new 'port' variable instead of the global 'PORT'
        print(f"Connecting to C# Engine at {HOST}:{port}...")
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        
        # Make sure to change it here too!
        self.sock.connect((HOST, port))
        
        self.stream = self.sock.makefile('rw', buffering=1)

        self.current_step = 0
        self.total_anneal_steps = 25_000_000

    def reset(self, seed=None, options=None):
        super().reset(seed=seed)
        
        message = self.stream.readline()
        result = json.loads(message)

        self.ai_is_p1 = True
        
        league_pool = self.opponents + [None]
        self.current_opponent = random.choice(league_pool)
        self.opp_step_counter = 0 # Reset the time dilation counter!
        
        if self.ai_is_p1:
            ai_state = np.array(result["P1State"], dtype=np.float32)
            self.current_mask = np.array(result["P1ActionMask"], dtype=np.int8)
            self.current_opp_state = np.array(result["P2State"], dtype=np.float32)
            self.current_opp_mask = np.array(result["P2ActionMask"], dtype=np.int8)
        else:
            ai_state = np.array(result["P2State"], dtype=np.float32)
            self.current_mask = np.array(result["P2ActionMask"], dtype=np.int8)
            self.current_opp_state = np.array(result["P1State"], dtype=np.float32)
            self.current_opp_mask = np.array(result["P1ActionMask"], dtype=np.int8)
            
        return ai_state, {}

    def step(self, action):
        ai_action = int(action)
        self.current_step += 1
        dense_weight = max(0.0, 1.0 - (self.current_step / self.total_anneal_steps))
        
        # --- NEW: Time Dilation Opponent Logic ---
        opp_name = "Random Dummy"
        if self.current_opponent is not None and self.current_opp_state is not None:
            opp_name = self.current_opponent["name"]
            
            # Calculate the ratio (e.g., 15 // 3 = 5). 
            # The old AI only acts every 5 steps!
            ratio = max(1, self.current_opponent["speed"] // self.base_speed)
            
            if self.opp_step_counter % ratio == 0:
                # Time for the opponent to think!
                if self.current_opponent["is_maskable"]:
                    opp_action, _ = self.current_opponent["model"].predict(
                        self.current_opp_state, action_masks=self.current_opp_mask, deterministic=True)
                else:
                    opp_action, _ = self.current_opponent["model"].predict(
                        self.current_opp_state, deterministic=True)
                opp_action = int(opp_action)
            else:
                # Opponent is frozen in slow motion. Send a Wait action (0).
                opp_action = 0 
                
            self.opp_step_counter += 1
        else:
            opp_action = random.randint(0, 13)

        # Notice we are passing the Opponent Name to C# now!
        if self.ai_is_p1:
            payload = {"P1Action": ai_action, "P2Action": opp_action, "DenseRewardWeight": float(dense_weight), "OpponentName": opp_name}
        else:
            payload = {"P1Action": opp_action, "P2Action": ai_action, "DenseRewardWeight": float(dense_weight), "OpponentName": opp_name}
            
        self.stream.write(json.dumps(payload) + '\n')

        message = self.stream.readline()
        result = json.loads(message)

        # Route the resulting states and rewards... (Unchanged)
        if self.ai_is_p1:
            ai_state = np.array(result["P1State"], dtype=np.float32)
            self.current_mask = np.array(result["P1ActionMask"], dtype=np.int8)
            ai_reward = float(result["P1Reward"])
            self.current_opp_state = np.array(result["P2State"], dtype=np.float32)
            self.current_opp_mask = np.array(result["P2ActionMask"], dtype=np.int8)
        else:
            ai_state = np.array(result["P2State"], dtype=np.float32)
            self.current_mask = np.array(result["P2ActionMask"], dtype=np.int8)
            ai_reward = float(result["P2Reward"])
            self.current_opp_state = np.array(result["P1State"], dtype=np.float32)
            self.current_opp_mask = np.array(result["P1ActionMask"], dtype=np.int8)

        done = bool(result["IsDone"])
        return ai_state, ai_reward, done, False, {}

    def action_masks(self):
        return self.current_mask

    def close(self):
        self.sock.close()

if __name__ == "__main__":
    training_model_name = "castle_defense_p1_v10"
    
    # Define the models and their native speeds!
    sparring_models = [
        {"path": "castle_defense_p1_v1.zip", "speed": 15}, 
        {"path": "castle_defense_p1_v2.zip", "speed": 15}, 
        {"path": "castle_defense_p1_v3.zip", "speed": 15},
        {"path": "castle_defense_p1_v4.zip", "speed": 15},
        {"path": "castle_defense_p1_v5.zip", "speed": 15},
        {"path": "castle_defense_p1_v6.zip", "speed": 3},
        {"path": "castle_defense_p1_v7.zip", "speed": 3},
        # Bart V8 is 3x more likely as an opponent since they will make for a stronger opponent
        {"path": "castle_defense_p1_v8.zip", "speed": 3},
        {"path": "castle_defense_p1_v8.zip", "speed": 3},
        {"path": "castle_defense_p1_v8.zip", "speed": 3},
        {"path": "castle_defense_p1_v9.zip", "speed": 3},
    ]

    num_cpu = 14 # Set this to how many parallel games you want!
    
    # Create the vectorized environment
    env = SubprocVecEnv([make_env(i, sparring_models, 3) for i in range(num_cpu)])

    model_file = f"{training_model_name}.zip"
    
    custom_hyperparams = {
        "n_steps": 8192,
        "batch_size": 1024,
        "n_epochs": 10,
        "gamma": 0.9998,
        "learning_rate": 0.0003,
        "ent_coef": 0.02,
        "policy_kwargs": dict(net_arch=[512, 512])
    }

    if os.path.exists(model_file):
        print(f"\nResuming training for {model_file} with upgraded hyperparameters...")
        model = MaskablePPO.load(training_model_name, env=env, custom_objects=custom_hyperparams, verbose=1)
    else:
        print("\nInitializing brand new Neural Network with macro-strategy settings...")
        model = MaskablePPO("MlpPolicy", env, verbose=1, **custom_hyperparams)

    print("\nBeginning Training... (Press Ctrl+C to stop early and save)")
    
    try:
        model.learn(total_timesteps=50000000, reset_num_timesteps=False)
    
    except KeyboardInterrupt:
        print("\nTraining interrupted by user! Wrapping up and saving brain...")

    model.save(training_model_name)
    print("Model saved successfully!")
    env.close()