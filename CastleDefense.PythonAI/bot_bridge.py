import socket
import json
import random
import time

# 1. Setup the connection parameters
HOST = '127.0.0.1'  # Localhost
PORT = 5000         # Must match the C# TcpListener port

def main():
    print(f"Attempting to connect to C# engine at {HOST}:{PORT}...")
    
    # 2. Open the socket
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        # Loop until the C# server is awake and accepting connections
        while True:
            try:
                s.connect((HOST, PORT))
                print("Successfully connected to the arena!")
                break
            except ConnectionRefusedError:
                print("Waiting for C# server...")
                time.sleep(1)

        # 3. Open a file-like object to easily read lines ending in '\n'
        network_stream = s.makefile('rw', buffering=1)

        ticks = 0
        
        # 4. The Ping-Pong Loop
        while True:
            # Read the JSON state from C#
            message = network_stream.readline()
            
            if not message:
                print("C# server disconnected.")
                break
                
            # Parse the JSON string into a Python dictionary
            step_result = json.loads(message)
            
            # Check if the game is over
            if step_result.get("IsDone", False):
                print(f"Game Over reached after {ticks} ticks!")
                break

            ticks += 1
            if ticks % 1000 == 0:
                print(f"Simulating tick {ticks}...")

            # --- THE AI BRAIN GOES HERE LATER ---
            # For now, we just pick a random action (0 through 13) for both players
            p1_action = random.randint(0, 13)
            p2_action = random.randint(0, 13)

            # Create the payload dictionary matching the C# ActionPayload class
            action_payload = {
                "P1Action": p1_action,
                "P2Action": p2_action
            }

            # Serialize to JSON, add the newline character, and fire it back to C#
            network_stream.write(json.dumps(action_payload) + '\n')

if __name__ == "__main__":
    main()