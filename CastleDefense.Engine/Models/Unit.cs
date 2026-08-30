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

        /// <summary>
        /// The unit's LOGICAL size, always its definition's. Every distance in the engine is
        /// measured with it -- contact clamping, targeting range, spawn placement, distance
        /// to the enemy castle.
        ///
        /// DO NOT MAKE THIS PER-INSTANCE. It was briefly scaled per spawn for the
        /// random-stat unit and the result was broken combat: ClampToContact stopped a unit
        /// using its instance width while FindTargetsFast measured reach from the
        /// DEFINITION's, so a large one halted 33px short of what its own targeting thought
        /// it could reach and simply stood there, while its opponent could hit it without
        /// being hit back. The two must agree, and half the engine reads the definition.
        /// See <see cref="VisualScale"/> for the appearance-only version.
        /// </summary>
        public int Width { get; set; }
        public int Height { get; set; }

        /// <summary>
        /// Multiplier the CLIENT draws this unit at. Purely cosmetic: nothing in the engine
        /// reads it, so a unit that looks twice the size still occupies, reaches and is
        /// reached exactly as its <see cref="Width"/> says.
        ///
        /// 1.0 for every unit except <see cref="GameEngine.RandomStatUnitId"/>, whose roll it
        /// advertises. The deliberate trade is that a scaled sprite's edges do not line up
        /// with where it actually fights -- accepted as much cheaper and far less bug-prone
        /// than making the whole engine size-aware, which it was never designed to be.
        /// </summary>
        public float VisualScale { get; set; } = 1f;


        // --- HEALTH ---
        public int CurrentHealth { get; set; }
        public int MaxHealth { get; set; }
        public int CurrentShield { get; set; }

        // --- POSITION & MOVEMENT ---
        public float Position { get; set; }
        public int YPosition { get; set; }

        /// <summary>
        /// This unit's own movement speed before status effects, in px/tick.
        ///
        /// SEPARATE FROM <see cref="CurrentSpeed"/> BECAUSE CurrentSpeed IS NOT A STAT --
        /// it is the live value, and MoveAndFight rewrites it every tick (to 0 while in
        /// contact, to speed x modifier otherwise). Anything written to it at spawn is gone
        /// on the next tick, which is why a per-unit speed needs a field of its own.
        ///
        /// Initialised from UnitDefinition.MoveSpeed, so for every ordinary unit this is
        /// exactly the definition's value and reading one or the other is the same thing.
        /// Only <see cref="GameEngine.RandomStatUnitId"/> differs.
        /// </summary>
        public float BaseSpeed { get; set; }

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
