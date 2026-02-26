using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Data
{
    public static class GameDataManager
    {
        public static List<TeamDefinition> Teams { get; private set; } = new List<TeamDefinition>();
        public static List<GadgetDefinition> GenericGadgets { get; private set; } = new List<GadgetDefinition>();

        private const int PRICE_MULTIPLIER = 10;
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
                int dmg = int.TryParse(GetCol("Damage"), out var d) ? d : 0;
                float speed = float.TryParse(GetCol("Speed"), out var sp) ? sp : 0f;
                int width = int.TryParse(GetCol("Size"), out var sz) ? sz : 50;

                // --- DYNAMIC FALLBACKS ---
                // If it's in the CSV, use it. Otherwise, calculate a smart default.
                int weight = int.TryParse(GetCol("Weight"), out var w) ? w : (hp * 50);
                int range = int.TryParse(GetCol("Range"), out var r) ? r : 50;
                bool isAce = bool.TryParse(GetCol("IsAce"), out var a) ? a : (tier == 8);

                if (!Enum.TryParse<AttackType>(GetCol("AttackType"), true, out var attackType))
                {
                    attackType = isAce ? AttackType.Siege : (range > 50 ? AttackType.Ranged : AttackType.Melee);
                }

                if (!Enum.TryParse<ArmorType>(GetCol("ArmorType"), true, out var armorType))
                {
                    armorType = isAce ? ArmorType.Shield : ArmorType.None;
                }

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
                    MaxHealth = hp,
                    Damage = dmg,
                    MoveSpeed = speed,
                    Width = width,
                    Description = GetCol("Description"),

                    Weight = weight,
                    Range = range,
                    AttackType = attackType,
                    ArmorType = armorType,
                    IsAce = isAce,

                    AttackSpeed = dmg * speed,
                    PushForce = weight * speed,
                    EffectiveWeight = weight - (speed * 20)
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
            // --- RED SLOT (Offense) ---
            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "g_nuke",
                Name = "The Nuke",
                Slot = GadgetSlot.Offense,
                Type = GadgetType.DirectDamage,
                TargetsCastle = true,
                Cost = 10000,
                CooldownMs = 120000,
                BaseValue = 500,
                Radius = 200,
                Description = "Massive area damage. Hurts Castles."
            });

            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "g_snipe",
                Name = "Snipe",
                Slot = GadgetSlot.Offense,
                Type = GadgetType.DirectDamage,
                TargetsCastle = true,
                Cost = 2000,
                CooldownMs = 15000,
                BaseValue = 100,
                Radius = 0,
                Description = "High single target damage."
            });

            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "g_napalm",
                Name = "Napalm",
                Slot = GadgetSlot.Offense,
                Type = GadgetType.ModifyTerrain,
                TargetsCastle = true,
                Cost = 4000,
                CooldownMs = 30000,
                DurationMs = 10000,
                Description = "Sets ground on fire."
            });

            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "g_freeze",
                Name = "Orbital Freeze",
                Slot = GadgetSlot.Offense,
                Type = GadgetType.StatusEffect,
                TargetsCastle = true,
                Cost = 3000,
                CooldownMs = 40000,
                DurationMs = 5000,
                Description = "Freezes units and Economy."
            });

            // --- BLUE SLOT (Defense) ---
            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "g_medkit",
                Name = "Medkit",
                Slot = GadgetSlot.Defense,
                Type = GadgetType.StatusEffect,
                Cost = 1000,
                CooldownMs = 20000,
                BaseValue = 50,
                Radius = 300,
                Description = "Heals units in area."
            });

            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "g_wall",
                Name = "The Wall",
                Slot = GadgetSlot.Defense,
                Type = GadgetType.ModifyTerrain,
                Cost = 1500,
                CooldownMs = 30000,
                BaseValue = 500, // Wall HP
                Description = "Spawns a temporary barrier."
            });

            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "g_stim",
                Name = "Stimpack",
                Slot = GadgetSlot.Defense,
                Type = GadgetType.StatusEffect,
                Cost = 2000,
                CooldownMs = 30000,
                DurationMs = 6000,
                Description = "Boosts speed and damage."
            });

            GenericGadgets.Add(new GadgetDefinition
            {
                Id = "g_guards",
                Name = "Reinforce",
                Slot = GadgetSlot.Defense,
                Type = GadgetType.SpawnUnit,
                Cost = 2500,
                CooldownMs = 45000,
                SpawnUnitId = "doggo", // Default placeholder
                Description = "Spawns 3 Guard units."
            });
        }
    }
}