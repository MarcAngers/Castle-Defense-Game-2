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
        public bool IsGameOver { get; set; }
        public int WinnerSide { get; set; } // 0 = Playing, 1 = Player 1 (Left), 2 = Player 2 (Right)

        public long CurrentTick { get; set; }

        public PlayerState Player1 { get; set; }
        public PlayerState Player2 { get; set; }

        public List<Unit> Units { get; set; } = new List<Unit>();

        public GameState() : this(GetRandomMap())
        {
        }

        public GameState(TeamColour map)
        {
            GameId = Guid.NewGuid();
            Units = new List<Unit>();

            Player1 = new PlayerState();
            Player2 = new PlayerState();

            Map = map;
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
        public float Money { get; set; }
        public float Income { get; set; }

        // Base
        public int CastleHealth { get; set; }
        public int CastleMaxHealth { get; set; }
        public bool IsInvulnerable { get; set; }
        public long InvulnerableUntilTick { get; set; } // Logic for Divine Shield

        // Cooldowns
        public Dictionary<string, int> UnitCharges { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, long> CooldownTimers { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> GadgetCooldowns { get; set; } = new Dictionary<string, long>();

        public PlayerState()
        {
            Money = 0f;
            Income = 5f;
            CastleMaxHealth = 1000;
            CastleHealth = 1000;
        }
    }

    public class Unit
    {
        public Guid InstanceId { get; set; } = Guid.NewGuid();
        public string DefinitionId { get; set; }
        public int Side { get; set; } // 1 (Left/P1) or 2 (Right/P2)
        public int Width { get; set; }

        // Current Stats
        public int CurrentHealth { get; set; }
        public int MaxHealth { get; set; }
        public int CurrentShield { get; set; }
        public float Position { get; set; }
        public int YPosition { get; set; }
        public float AttackCooldown { get; set; }
        public float CurrentSpeed { get; set; }

        // Stacking Status Effects (Poisoned + Frozen + Burning)
        public List<ActiveStatus> Statuses { get; set; } = new List<ActiveStatus>();
    }

    public class ActiveStatus
    {
        public string Name { get; set; } // "Freeze", "Burn", "Poison", "SpeedBuff"
        public long ExpiresAtTick { get; set; }
        public float Value { get; set; }   // e.g., Burn Damage amount, or Speed % boost
    }
}