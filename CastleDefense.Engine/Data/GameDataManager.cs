using System;
using System.Collections.Generic;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Data
{
    public static class GameDataManager
    {
        public static List<TeamDefinition> Teams { get; private set; } = new List<TeamDefinition>();
        public static List<GadgetDefinition> GenericGadgets { get; private set; } = new List<GadgetDefinition>();

        public static void Initialize()
        {
            Teams.Clear();
            GenericGadgets.Clear();

            LoadGenericGadgets();
            LoadWhiteTeam();
        }

        // --- THE BALANCING FORMULA ---
        private const int PRICE_MULTIPLIER = 10;
        private const int COOLDOWN_PER_DOLLAR = 800;

        private static UnitDefinition CreateUnit(
            string id, string name, int tier, int oldPrice,
            int hp, int wt, int dmg, int range, float speed, int width,
            AttackType attack, ArmorType armor, string desc, bool isAce = false)
        {
            int charges = Math.Max(1, 25 / oldPrice);
            if (isAce) charges = 1;

            return new UnitDefinition
            {
                Id = id,
                Name = name,
                Tier = tier,
                Cost = oldPrice * PRICE_MULTIPLIER,
                CooldownMs = oldPrice * COOLDOWN_PER_DOLLAR,
                MaxCharges = charges,
                MaxHealth = hp,
                Weight = wt,
                Damage = dmg,
                Range = range,
                MoveSpeed = speed,
                Width = width,
                AttackType = attack,
                ArmorType = armor,
                PushForce = wt * speed,
                EffectiveWeight = wt - (speed * 20),
                Description = desc,
                IsAce = isAce
            };
        }

        private static void LoadWhiteTeam()
        {
            var whiteTeam = new TeamDefinition
            {
                Id = "team_white",
                Color = TeamColor.White,
                Name = "White Team",
                PassiveName = "Efficiency",
                PassiveDescription = "Gadget cooldowns are reduced by 20%.",
                SignatureGadget = new GadgetDefinition
                {
                    Id = "white_sig_payday",
                    Name = "Payday",
                    Description = "Instantly grants $2000 cash.",
                    Slot = GadgetSlot.Signature,
                    Type = GadgetType.Economy,
                    Cost = 0,
                    CooldownMs = 45000,
                    BaseValue = 2000
                }
            };

            // 1. Doggo
            whiteTeam.Roster.Add(CreateUnit("white_doggo", "Doggo", 1, 3,
                10, 500, 2, 50, 10f, 50,
                AttackType.Melee, ArmorType.None,
                "The basic unit. He's awesome. Excels at tearing down enemy castles."));

            // 2. Catto (Changed range to 50 for consistency unless you want it ranged?)
            whiteTeam.Roster.Add(CreateUnit("white_catto", "Catto", 2, 4,
                10, 500, 3, 50, 10f, 50,
                AttackType.Melee, ArmorType.None,
                "I'm PAWsitive that she'll be a great addition to your army ;)"));

            // 3. Squirt (Ranged unit logic applied)
            whiteTeam.Roster.Add(CreateUnit("white_squirt", "Squirt", 3, 4,
                13, 650, 2, 300, 10f, 50,
                AttackType.Ranged, ArmorType.None,
                "Young squirrel with many dreams and aspirations."));

            // 4. Ringo
            whiteTeam.Roster.Add(CreateUnit("white_ringo", "Ringo", 4, 5,
                15, 750, 4, 50, 13f, 50,
                AttackType.Melee, ArmorType.None,
                "its ya boi"));

            // 5. Alpacco
            whiteTeam.Roster.Add(CreateUnit("white_alpacco", "Alpacco", 5, 19,
                30, 2250, 12, 50, 10f, 75,
                AttackType.Melee, ArmorType.None,
                "Fun Alpaca Fact #42:\nDid you know that alpaca fur is fire resistant?\n:D Fun"));

            // 6. Bread
            whiteTeam.Roster.Add(CreateUnit("white_bread", "Bread", 6, 20,
                40, 2000, 3, 50, 8f, 75,
                AttackType.Melee, ArmorType.None,
                "Imagine dying to a loaf of white bread... Embarassing."));

            // 7. Eggo
            whiteTeam.Roster.Add(CreateUnit("white_eggo", "Eggo", 7, 69,
                45, 4500, 25, 50, 15f, 100,
                AttackType.Melee, ArmorType.None,
                "Is that... A buff egg?!"));

            // 8. Corn (Ace)
            whiteTeam.Roster.Add(CreateUnit("white_corn", "Corn", 8, 500,
                1000, 20000, 100, 50, 1f, 200,
                AttackType.Siege, ArmorType.Shield,
                "So magestic that she clips the page and I'm too lazy to fix it :O", true));

            Teams.Add(whiteTeam);
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
                SpawnUnitId = "white_doggo", // Default placeholder
                Description = "Spawns 3 Guard units."
            });
        }
    }
}