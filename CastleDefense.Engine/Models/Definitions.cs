using System;
using System.Collections.Generic;
using System.Text;

using System.Collections.Generic;

namespace CastleDefense.Engine.Models
{
    // --- The Blueprint for a Unit ---
    public class UnitDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }

        // Progression
        public int Tier { get; set; }           // 1-8

        // Economy
        public int Cost { get; set; }           // Money to buy
        public int CooldownMs { get; set; }
        public int MaxCharges { get; set; }

        // Combat Stats
        public int MaxHealth { get; set; }
        public int MaxShield { get; set; }      // NEW: The "Blue Health" amount
        public int Weight { get; set; }
        public int Damage { get; set; }
        public int Range { get; set; }
        public float MoveSpeed { get; set; }
        public float AttackSpeed { get; set; }
        public int Width { get; set; }

        // Mechanics
        public AttackType AttackType { get; set; }
        public ArmorType ArmorType { get; set; }
        public float PushForce { get; set; }
        public float EffectiveWeight { get; set; }
        public bool IsAce { get; set; }
    }

    // --- The Blueprint for a Gadget ---
    public class GadgetDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public GadgetSlot Slot { get; set; }
        public GadgetType Type { get; set; }

        public int Cost { get; set; }           // CHANGED: Was ManaCost
        public int CooldownMs { get; set; }

        // Data-Driven Effects
        public int BaseValue { get; set; }      // Damage/Heal/Cash amount
        public int Radius { get; set; }
        public int DurationMs { get; set; }
        public string SpawnUnitId { get; set; }
        public int PushForce { get; set; }

        public bool TargetsCastle { get; set; }
    }

    // --- The Blueprint for a Team ---
    public class TeamDefinition
    {
        public string Id { get; set; }
        public TeamColor Color { get; set; }
        public string Name { get; set; }
        public string PassiveName { get; set; }
        public string PassiveDescription { get; set; }

        // The 8 Units (Ordered Tier 1 -> 8)
        public List<UnitDefinition> Roster { get; set; } = new List<UnitDefinition>();

        // The Signature Ability
        public GadgetDefinition SignatureGadget { get; set; }
    }
}