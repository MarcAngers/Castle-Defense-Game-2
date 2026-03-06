using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CastleDefense.Engine.Gadgets;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Data
{
    public static class GameDataManager
    {
        public static List<TeamDefinition> Teams { get; private set; } = new List<TeamDefinition>();
        public static List<GadgetDefinition> GenericGadgets { get; private set; } = new List<GadgetDefinition>();

        private const int DAMAGE_MULTIPLIER = 1;
        private const int HEALTH_MULTIPLIER = 2;
        private const int PRICE_MULTIPLIER = 1;
        private const float ATTACK_SPEED_MULTIPLIER = 0.8f;
        private const int COOLDOWN_PER_DOLLAR = 800;

        public static void Initialize()
        {
            Teams.Clear();
            GenericGadgets.Clear();

            LoadGenericGadgets();
            LoadTeamsFromCsv();
        }

        private static void LoadTeamsFromCsv()
        {
            var filePath = Path.Combine(AppContext.BaseDirectory, "Data", "master_roster.csv");

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[WARNING] Could not find roster file at {filePath}");
                return;
            }

            var lines = File.ReadAllLines(filePath);
            if (lines.Length <= 1) return; // Exit if the file only has headers or is empty

            // NUKE the invisible BOM character from the first line!
            string cleanHeaderRow = lines[0].Replace("\uFEFF", "");

            // 1. Read the header row so we know which column is which
            var headers = ParseCsvRow(lines[0]).Select(h => h.Trim().ToLower()).ToList();

            // We use a dictionary to dynamically build the teams as we find them in the CSV
            var teamDictionary = new Dictionary<string, TeamDefinition>();

            // 2. Parse all data rows
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                var cols = ParseCsvRow(lines[i]);

                // Quick helper to grab a column by its header name safely
                string GetCol(string name)
                {
                    int idx = headers.IndexOf(name.ToLower());
                    return (idx >= 0 && idx < cols.Count) ? cols[idx].Trim() : string.Empty;
                }

                string teamStr = GetCol("Team");
                if (string.IsNullOrEmpty(teamStr)) continue; // Skip invalid rows

                // --- REQUIRE BASE STATS ---
                int tier = int.TryParse(GetCol("Tier"), out var t) ? t : 1;
                int price = int.TryParse(GetCol("Price"), out var p) ? p : 0;
                int hp = int.TryParse(GetCol("Health"), out var h) ? h : 0;
                int shield = int.TryParse(GetCol("Shield"), out var sh) ? sh : 0;
                int dmg = int.TryParse(GetCol("Damage"), out var d) ? d : 0;
                float speed = float.TryParse(GetCol("Speed"), out var sp) ? sp : 0f;
                int width = int.TryParse(GetCol("Width"), out var wd) ? wd : 50;
                int height = int.TryParse(GetCol("Height"), out var ht) ? ht : 50;

                // --- DYNAMIC FALLBACKS ---
                // If it's in the CSV, use it. Otherwise, calculate a smart default.
                int weight = int.TryParse(GetCol("Weight"), out var w) ? w : hp;
                int range = int.TryParse(GetCol("Range"), out var r) ? r : 0;
                bool isAce = bool.TryParse(GetCol("IsAce"), out var a) ? a : (tier == 8);

                if (!Enum.TryParse<AttackType>(GetCol("AttackType"), true, out var attackType))
                {
                    attackType = isAce ? AttackType.Siege : (range > 50 ? AttackType.Ranged : AttackType.Melee);
                }

                if (!Enum.TryParse<ArmorType>(GetCol("ArmorType"), true, out var armorType))
                {
                    armorType = isAce ? ArmorType.Shield : ArmorType.None;
                }

                // --- CALCULATE ATTACK SPEED ---
                float calculatedAps = 0f;

                if (dmg > 0)
                {
                    // The Doggo Formula: 0.4 * (Speed / Damage)
                    calculatedAps = ATTACK_SPEED_MULTIPLIER * (speed / (float)dmg);
                }

                float finalAps = Math.Clamp(calculatedAps, 0.2f, 5.0f);

                // --- APPLY BALANCING FORMULAS ---
                int charges = Math.Max(1, 25 / (price > 0 ? price : 1));
                if (isAce) charges = 1;

                var unit = new UnitDefinition
                {
                    Id = GetCol("ID"),
                    Name = GetCol("Name"),
                    Tier = tier,
                    Cost = price * PRICE_MULTIPLIER,
                    CooldownMs = price * COOLDOWN_PER_DOLLAR,
                    MaxCharges = charges,
                    MaxHealth = hp * HEALTH_MULTIPLIER,
                    MaxShield = shield,
                    Damage = dmg,
                    MoveSpeed = speed,
                    Width = width,
                    Height = height,
                    Description = GetCol("Description"),

                    Weight = weight,
                    Range = range,                               // All units are melee for now
                    AttackType = attackType,
                    ArmorType = armorType,
                    IsAce = isAce,

                    AttackSpeed = finalAps,
                    PushForce = weight * speed * dmg,
                    EffectiveWeight = tier * tier * weight / speed
                };

                // --- BUILD THE 3D TEAM STRUCTURE ---
                string teamKey = teamStr.ToLower();

                // If this is the first time seeing this team, create it
                if (!teamDictionary.ContainsKey(teamKey))
                {
                    Enum.TryParse<TeamColour>(teamStr, true, out var teamColor);
                    teamDictionary[teamKey] = new TeamDefinition
                    {
                        Id = $"team_{teamKey}",
                        Color = teamColor,
                        Name = char.ToUpper(teamKey[0]) + teamKey.Substring(1) + " Team",
                        PassiveName = "Team Passive",
                        PassiveDescription = "Loaded from CSV",
                        Roster = new List<UnitDefinition>()
                    };
                }

                // Add the unit to the correct team's roster
                teamDictionary[teamKey].Roster.Add(unit);
            }

            // 3. Finalize: Sort each roster by tier, then add to the master list
            foreach (var team in teamDictionary.Values)
            {
                team.Roster = team.Roster.OrderBy(u => u.Tier).ToList();
                Teams.Add(team);
            }
        }

        // --- CUSTOM CSV PARSER ---
        // Reads a row and safely ignores commas that are trapped inside quotes
        private static List<string> ParseCsvRow(string row)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var buffer = new StringBuilder();

            for (int i = 0; i < row.Length; i++)
            {
                char c = row[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes; // Toggle quote state
                }
                else if (c == ',' && !inQuotes)
                {
                    // End of column
                    result.Add(buffer.ToString());
                    buffer.Clear();
                }
                else
                {
                    buffer.Append(c);
                }
            }

            // Add the final column
            result.Add(buffer.ToString());
            return result;
        }

        private static void LoadGenericGadgets()
        {
            // --- Offensive Gadgets ---
            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "nuke",
                Name = "The Nuke",
                Slot = GadgetSlot.Offense,
                Cost = 50,
                CooldownMs = 120000,
                Description = "Massive area damage. Hurts Castles.",
                GadgetEffect = new NukeEffect()
            });

            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "snipe",
                Name = "Snipe",
                Slot = GadgetSlot.Offense,
                Cost = 30,
                CooldownMs = 90000,
                BaseValue = 30,
                Description = "High single target damage.",
                GadgetEffect = new SnipeEffect()
            });

            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "firebomb",
                Name = "Fire Bomb",
                Slot = GadgetSlot.Offense,
                Cost = 40,
                CooldownMs = 90000,
                BaseValue = 5,
                Radius = 50,
                DurationMs = 10000,
                Description = "Sets ground on fire.",
                GadgetEffect = new FirebombEffect()
            });

            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "freeze",
                Name = "Freeze Ray",
                Slot = GadgetSlot.Offense,
                Cost = 45,
                CooldownMs = 100000,
                BaseValue = 10,
                DurationMs = 5000,
                Description = "Freezes units.",
                GadgetEffect = new FreezeEffect()
            });

            // --- Defensive Gadgets ---
            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "medpack",
                Name = "Medpack",
                Slot = GadgetSlot.Defense,
                Cost = 25,
                CooldownMs = 60000,
                BaseValue = 30,
                Description = "Heals all units."
            });

            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "wall",
                Name = "Wall",
                Slot = GadgetSlot.Defense,
                Cost = 50,
                CooldownMs = 120000,
                BaseValue = 100,
                Description = "Spawns a temporary barrier."
            });

            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "speed",
                Name = "Spped Boost",
                Slot = GadgetSlot.Defense,
                Cost = 30,
                CooldownMs = 90000,
                DurationMs = 6000,
                Description = "Boosts speed and damage."
            });

            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "reinforcements",
                Name = "Reinforcements",
                Slot = GadgetSlot.Defense,
                Cost = 40,
                CooldownMs = 100000,
                SpawnUnitId = "doggo", // Default placeholder
                Description = "Spawns 5 reinforcement units."
            });
        }
    }
}