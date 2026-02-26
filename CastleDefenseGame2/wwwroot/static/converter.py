import os
import json
import csv

# Directory paths
chars_dir = './characters'
teams_dir = './fullteams'
output_file = 'master_roster.csv'

# Set up the header row for the spreadsheet
headers = ['Team', 'Tier', 'ID', 'Name', 'Price', 'Health', 'Damage', 'Speed', 'Size', 'Y', 'Description']

# Open the new CSV file for writing
with open(output_file, mode='w', newline='', encoding='utf-8') as csv_file:
    writer = csv.writer(csv_file)
    writer.writerow(headers)

    # 1. Read the team definition files in 'fullteams'
    for team_file in os.listdir(teams_dir):
        if not team_file.endswith('.json'):
            continue
            
        team_name = team_file.replace('.json', '')
        team_def_path = os.path.join(teams_dir, team_file)
        
        with open(team_def_path, 'r', encoding='utf-8') as tf:
            team_data = json.load(tf)
            
        unit_list = team_data.get('team', [])
        
        # 2. Loop through the list of units in the exact order they appear (Tier order)
        for index, unit_id in enumerate(unit_list):
            tier = index + 1
            
            # Construct the path to the actual character data file
            unit_file_path = os.path.join(chars_dir, team_name, f"{unit_id}.json")
            
            if os.path.exists(unit_file_path):
                with open(unit_file_path, 'r', encoding='utf-8') as uf:
                    unit = json.load(uf)
                    
                    # Write the row directly to the CSV
                    writer.writerow([
                        team_name,
                        tier,
                        unit_id,
                        unit.get('name', ''),
                        unit.get('price', 0),
                        unit.get('health', 0),
                        unit.get('damage', 0),
                        unit.get('speed', 0),
                        unit.get('size', 0),
                        unit.get('y', 0),
                        unit.get('description', '').replace('\n', ' ')
                    ])
            else:
                print(f"Warning: Data file for '{unit_id}' not found at {unit_file_path}")

print(f"Success! Generated {output_file}")