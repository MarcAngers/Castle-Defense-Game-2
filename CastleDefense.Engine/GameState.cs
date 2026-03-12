using CastleDefense.Engine.Data;
using CastleDefense.Engine.Gadgets;
using CastleDefense.Engine.Models;
using System;
using System.Collections.Generic;
using System.Data;

namespace CastleDefense.Engine
{
    public class GameState
    {
        public Guid GameId { get; set; }
        public TeamColour Map { get; set; }
        public bool ShadowMap { get; set; }
        public bool IsGameOver { get; set; }
        public int WinnerSide { get; set; } // 0 = Playing, 1 = Player 1 (Left), 2 = Player 2 (Right)

        public long CurrentTick { get; set; }

        public PlayerState Player1 { get; set; }
        public PlayerState Player2 { get; set; }

        public List<Unit> Units { get; set; } = new List<Unit>();
        public List<Hazard> Hazards { get; set; } = new List<Hazard>();

        public GameState() : this(GetRandomMap())
        {
        }

        public GameState(TeamColour map)
        {
            GameId = Guid.NewGuid();
            Units = new List<Unit>();
            ShadowMap = false;

            Player1 = new PlayerState();
            Player2 = new PlayerState();

            Map = map;

            if (Map == TeamColour.Black)
            {
                Random rand = new Random();

                // 50/50 for regular black map, or shadow version of a different map
                if (rand.Next(2) == 0)
                {
                    ShadowMap = true;
                    Map = GetRandomMap();

                    // 1/8 chance to still get the black map
                    if (Map == TeamColour.Black)
                    {
                        ShadowMap = false;
                    }
                }
            }
        }

        private static TeamColour GetRandomMap()
        {
            Array values = Enum.GetValues(typeof(TeamColour));

            return (TeamColour)values.GetValue(Random.Shared.Next(values.Length));
        }
    }

    public class PlayerState
    {
        public string ConnectionId { get; set; }
        public int Side { get; set; } // 1 or 2
        public TeamColour Team { get; set; }

        // Economy
        public double Money { get; set; }
        public double Income { get; set; }
        public double InvestmentPrice { get; set; }
        public int InvestmentCount { get; set; }

        // Base
        public int CastleHealth { get; set; }
        public int CastleMaxHealth { get; set; }
        public int RepairPrice { get; set; }
        public bool IsInvulnerable { get; set; }
        public long InvulnerableUntilTick { get; set; }
        public GadgetDefinition OffensiveGadget { get; set; }
        public GadgetDefinition DefensiveGadget { get; set; }
        public GadgetDefinition SignatureGadget { get; set; }

        // Cooldowns
        public Dictionary<string, int> UnitCharges { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, long> CooldownTimers { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> GadgetCooldowns { get; set; } = new Dictionary<string, long>();

        public PlayerState()
        {
            Money = 0;
            Income = 2;
            InvestmentPrice = 3;
            InvestmentCount = 0;
            CastleMaxHealth = 100000;
            RepairPrice = 4000;
            CastleHealth = 100000;
        }

        public void SetLoadout(string[] loadout)
        {
            OffensiveGadget = GameDataManager.Gadgets.Find(g => g.Id == loadout[0]);
            DefensiveGadget = GameDataManager.Gadgets.Find(g => g.Id == loadout[1]);
            SignatureGadget = GameDataManager.Gadgets.Find(g => g.Id == loadout[2]);
        }
    }

    public class Unit
    {
        // --- IDENTITY ---
        public Guid InstanceId { get; set; } = Guid.NewGuid();
        public string DefinitionId { get; set; }
        public int Side { get; set; } // 1 (Left/P1) or 2 (Right/P2)

        // --- DRAWING & HITBOX ---
        public int Width { get; set; }
        public int Height { get; set; }


        // --- HEALTH ---
        public int CurrentHealth { get; set; }
        public int MaxHealth { get; set; }
        public int CurrentShield { get; set; }

        // --- POSITION & MOVEMENT ---
        public float Position { get; set; }
        public int YPosition { get; set; }
        public float CurrentSpeed { get; set; }
        public float PendingKnockback { get; set; }
        public long LastKnockbackTick {  get; set; }
        public int AttacksWithoutKnockback { get; set; }

        // --- COMBAT STATS ---
        public int Damage { get; set; }
        public int Range { get; set; }
        public float AttackSpeed { get; set; }
        public float AttackCooldown { get; set; } // The active running timer

        // --- PHYSICS & MECHANICS ---
        public int Weight { get; set; }
        public float PushForce { get; set; }
        public float EffectiveWeight { get; set; }
        public AttackType AttackType { get; set; }
        public ArmorType ArmorType { get; set; }

        // --- ACTIVE EFFECTS ---
        // Stacking Status Effects (Poisoned + Frozen + Burning)
        public List<ActiveStatus> Statuses { get; set; } = new List<ActiveStatus>();
    }

    public class Hazard
    {
        public string Type { get; set; } // "Fire", "Ice", "PoisonCloud", etc.
        public int Side { get; set; }
        public float BaseValue { get; set; }

        public float Position { get; set; } // The starting X coordinate (left edge)
        public float Width { get; set; }    // How far the hazard stretches
        public int ExpiresAtTick { get; set; } // When the hazard disappears
    }

    public class ActiveStatus
    {
        public string Name { get; set; } // "Freeze", "Burn", "Poison", "SpeedBuff"
        public long ExpiresAtTick { get; set; }
        public float Value { get; set; }   // e.g., Burn Damage amount, or Speed % boost

        public ActiveStatus(string name, long tick, float value)
        {
            Name = name;
            ExpiresAtTick = tick;
            Value = value;
        }
    }
}