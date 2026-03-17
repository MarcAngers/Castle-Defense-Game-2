// --- The Blueprint for a Unit ---

using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Definitions
{
    public class UnitDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public TeamColour Team { get; set; }
        public string Description { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        // Progression
        public int Tier { get; set; }           // 1-8

        // Economy
        public int Cost { get; set; }           // Money to buy
        public int CooldownMs { get; set; }
        public int MaxCharges { get; set; }

        // Combat Stats
        public int MaxHealth { get; set; }
        public int MaxShield { get; set; }
        public int Weight { get; set; }
        public int Damage { get; set; }
        public int Range { get; set; }
        public float MoveSpeed { get; set; }
        public float AttackSpeed { get; set; }

        // Mechanics
        public AttackType AttackType { get; set; }
        public ArmorType ArmorType { get; set; }
        public float PushForce { get; set; }
        public float EffectiveWeight { get; set; }
    }
}