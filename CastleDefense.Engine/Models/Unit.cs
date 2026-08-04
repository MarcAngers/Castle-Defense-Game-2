using System.Text.Json.Serialization;

namespace CastleDefense.Engine.Models
{
    public class Unit
    {
        // --- IDENTITY ---
        public Guid InstanceId { get; set; } = Guid.NewGuid();
        public string DefinitionId { get; set; }
        public int Side { get; set; } // 1 (Left/P1) or 2 (Right/P2)
        public int Tier { get; set; }

        /// <summary>
        /// Walls ("wall", "wall_2", "wall_3") are immovable scenery rather than combatants.
        /// The id prefix is the only thing that distinguishes them -- all three share a
        /// UnitDefinition whose own Id is just "wall" -- and no roster unit starts with it.
        ///
        /// [JsonIgnore] because Unit goes over SignalR and into saved replays; a derived
        /// flag the client can compute itself has no business changing either wire format.
        /// </summary>
        [JsonIgnore]
        public bool IsWall => DefinitionId != null && DefinitionId.StartsWith("wall");

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

        /// <summary>
        /// Deep copy. Every field except <see cref="Statuses"/> is a value type or an
        /// immutable string, so MemberwiseClone handles them and only the status list
        /// needs its own copy.
        ///
        /// NOTE: InstanceId is deliberately PRESERVED, not regenerated. Scheduled effects
        /// reference their target by InstanceId (see PendingEffect.TargetId), so a clone
        /// whose units had fresh ids would drop every in-flight snipe. It also means unit
        /// identity stays comparable between a rollout and the position it branched from.
        ///
        /// This is the field that a previous shallow copy got wrong: sharing Unit objects
        /// between a shadow clone and the real game let the shadow bot's counterfactual
        /// queries attach real Heal/Speed statuses to the live trajectory. See the notes
        /// around CloneStateForShadow in CastleDefense.Simulation.
        /// </summary>
        public Unit Clone()
        {
            var copy = (Unit)MemberwiseClone();
            copy.Statuses = new List<ActiveStatus>(Statuses.Count);
            foreach (var s in Statuses) copy.Statuses.Add(s.Clone());
            return copy;
        }
    }
}
