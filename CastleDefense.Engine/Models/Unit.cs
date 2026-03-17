namespace CastleDefense.Engine.Models
{
    public class Unit
    {
        // --- IDENTITY ---
        public Guid InstanceId { get; set; } = Guid.NewGuid();
        public string DefinitionId { get; set; }
        public int Side { get; set; } // 1 (Left/P1) or 2 (Right/P2)
        public int Tier { get; set; }

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
        public long LastKnockbackTick { get; set; }
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
}
