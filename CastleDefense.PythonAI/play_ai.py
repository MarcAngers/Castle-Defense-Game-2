import socket
import json
import random
import numpy as np
from stable_baselines3 import PPO

HOST = '127.0.0.1'
PORT = 5000

print("Loading Trained Brain...")
# Load the saved model!
model = PPO.load("castle_defense_p1_v1")

print(f"Connecting to C# Arena at {HOST}:{PORT}...")
with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
    s.connect((HOST, PORT))
    stream = s.makefile('rw', buffering=1)
    
    # Read the first state
    message = stream.readline()
    result = json.loads(message)
    obs = np.array(result["P1State"], dtype=np.float32)

    print("Match Started! AI is taking control...")
    ticks = 0
    
    while True:
        # deterministic=True tells the AI to use its best learned strategy with NO random guessing
        action, _states = model.predict(obs, deterministic=True)
        
        p1_action = int(action)
        p2_action = random.randint(0, 13) # P2 is still a random dummy

        # Send actions to C#
        stream.write(json.dumps({"P1Action": p1_action, "P2Action": p2_action}) + '\n')

        # Get the result
        message = stream.readline()
        if not message:
            print("Server disconnected.")
            break
            
        result = json.loads(message)
        ticks += 1
        
        if result.get("IsDone", False):
            print(f"Game Over! AI finished the match in {ticks} ticks.")
            break
            
        # Update the observation for the next loop
        obs = np.array(result["P1State"], dtype=np.float32)