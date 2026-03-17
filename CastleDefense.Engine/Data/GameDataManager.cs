using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Gadgets;
using CastleDefense.Engine.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;

namespace CastleDefense.Engine.Data
{
    public static class GameDataManager
    {
        public static List<TeamDefinition> Teams { get; private set; } = new List<TeamDefinition>();
        public static List<GadgetDefinition> Gadgets { get; private set; } = new List<GadgetDefinition>();

        private const int DAMAGE_MULTIPLIER = 1;
        private const int HEALTH_MULTIPLIER = 1;
        private const int PRICE_MULTIPLIER = 1;
        private const float ATTACK_SPEED_MULTIPLIER = 1f;
        private const int COOLDOWN_PER_DOLLAR = 800;

        public static void Initialize()
        {
            Teams.Clear();
            Gadgets.Clear();

            LoadGadgetsFromCsv();
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
                Enum.TryParse<TeamColour>(teamStr, true, out var teamColor);

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
                    if (tier < 6)
                    {
                        calculatedAps = ATTACK_SPEED_MULTIPLIER * (tier * 2 * speed / (float)dmg);
                    }
                    // Try to keep the attack speed reasonable in higher tiers
                    else if (tier < 7)
                    {
                        calculatedAps = ATTACK_SPEED_MULTIPLIER * (tier * tier * speed / (float)dmg);
                    }
                    else if (tier >= 7)
                    {
                        calculatedAps = ATTACK_SPEED_MULTIPLIER * (tier * tier * tier * speed / (float)dmg);
                    }
                }

                float finalAps = Math.Clamp(calculatedAps, 0.2f, 5.0f);

                // --- APPLY BALANCING FORMULAS ---
                int charges = Math.Max(1, 25 / (price > 0 ? price : 1));
                if (isAce) charges = 1;

                var unit = new UnitDefinition
                {
                    Id = GetCol("ID"),
                    Name = GetCol("Name"),
                    Team = teamColor,
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

                    AttackSpeed = finalAps,
                    PushForce = weight * speed * dmg,
                    EffectiveWeight = tier * tier * weight / speed
                };

                // --- BUILD THE 3D TEAM STRUCTURE ---
                string teamKey = teamStr.ToLower();

                // If this is the first time seeing this team, create it
                if (!teamDictionary.ContainsKey(teamKey))
                {
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

        private static void LoadGadgetsFromCsv()
        {
            var filePath = Path.Combine(AppContext.BaseDirectory, "Data", "master_gadgets.csv");

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[WARNING] Could not find gadgets file at {filePath}");
                return;
            }

            var lines = File.ReadAllLines(filePath);
            if (lines.Length <= 1) return;

            string cleanHeaderRow = lines[0].Replace("\uFEFF", "");
            var headers = ParseCsvRow(cleanHeaderRow).Select(h => h.Trim().ToLower()).ToList();

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                var cols = ParseCsvRow(lines[i]);

                string GetCol(string name)
                {
                    int idx = headers.IndexOf(name.ToLower());
                    return (idx >= 0 && idx < cols.Count) ? cols[idx].Trim() : string.Empty;
                }

                string id = GetCol("ID");
                if (string.IsNullOrEmpty(id)) continue;

                // --- PARSE DATA WITH SAFE FALLBACKS ---
                Enum.TryParse<GadgetSlot>(GetCol("Slot"), true, out var slot);
                int targeted = int.TryParse(GetCol("Targeted"), out var t) ? t : 1;
                int cost = int.TryParse(GetCol("Cost"), out var c) ? c : 0;
                int upgradeCost = int.TryParse(GetCol("UpgradeCost"), out var uc) ? uc : 1000;
                int baseValue = int.TryParse(GetCol("BaseValue"), out var bv) ? bv : 0;
                int radius = int.TryParse(GetCol("Radius"), out var r) ? r : 0;
                int delay = int.TryParse(GetCol("Delay"), out var d) ? d : 0;
                int pushForce = int.TryParse(GetCol("PushForce"), out var pf) ? pf : 0;
                int hazardDuration = int.TryParse(GetCol("HazardDuration"), out var hd) ? hd : 0;
                int StatusDuration = int.TryParse(GetCol("StatusDuration"), out var sd) ? sd : 0;
                int cooldownMs = int.TryParse(GetCol("CooldownMs"), out var cm) ? cm : 10000;
                string effectType = GetCol("ID");

                var gadgetDef = new GadgetDefinition
                {
                    Id = id,
                    Name = GetCol("Name"),
                    Slot = slot,
                    NextTierId = GetCol("NextTierId"),
                    Targeted = (targeted == 1),
                    Cost = cost,
                    UpgradeCost = upgradeCost,
                    BaseValue = baseValue,
                    Radius = radius,
                    Delay = delay,
                    PushForce = pushForce,
                    HazardDuration = hazardDuration,
                    StatusDuration = StatusDuration,
                    CooldownMs = cooldownMs,
                    Description = GetCol("Description")
                };

                // --- ATTACH THE EFFECT LOGIC ---
                // We pass the definition directly into the constructor!
                gadgetDef.GadgetEffect = CreateGadgetEffect(effectType, gadgetDef);

                Gadgets.Add(gadgetDef);
            }
        }

        // A clean factory method to map strings to your actual C# classes
        private static IGadgetEffect CreateGadgetEffect(string effectType, GadgetDefinition def)
        {
            string baseEffect = effectType.Split('_')[0].ToLower();

            return baseEffect switch
            {
                "nuke" => new NukeEffect(def),
                "snipe" => new SnipeEffect(def),
                "firebomb" => new FirebombEffect(def),
                "freeze" => new FreezeEffect(def),
                "heal" => new HealEffect(def),
                "wall" => new WallEffect(def),
                "speed" => new SpeedEffect(def),
                "reinforcements" => new ReinforcementsEffect(def),
                "cash" => new CashEffect(def),
                "rage" => new RageEffect(def),
                "goo" => new GooEffect(def),
                "poison" => new PoisonEffect(def),
                "divine" => new DivineEffect(def),
                "meteor" => new MeteorEffect(def),
                "wave" => new WaveEffect(def),
                "blackhole" => new BlackholeEffect(def),

                _ => throw new ArgumentException($"Unknown gadget effect type: {effectType}")
            };
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

        public static UnitDefinition WallDefinition(int level)
        {
            var healthMultiplier = 1;
            if (level == 2) healthMultiplier = 15;
            if (level == 3) healthMultiplier = 150;

            var sizeMultiplier = level == 3 ? 6 : level;

            return new UnitDefinition
            {
                Id = "wall",
                Name = "wall",
                Tier = 4,
                Cost = 0,
                CooldownMs = 0,
                MaxCharges = 1,
                MaxHealth = 400 * healthMultiplier * HEALTH_MULTIPLIER,
                MaxShield = 0,
                Damage = 0,
                MoveSpeed = 0,
                Width = 75 * sizeMultiplier,
                Height = 150 * sizeMultiplier,
                Description = "The Wall",

                Weight = int.MaxValue,
                Range = 0,
                AttackType = AttackType.Support,
                ArmorType = ArmorType.None,

                AttackSpeed = 0,
                PushForce = 0,
                EffectiveWeight = float.MaxValue
            };
        }
    }
}