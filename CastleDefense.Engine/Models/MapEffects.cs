using System;

namespace CastleDefense.Engine.Models
{
    /// <summary>
    /// The gameplay effect each map has on the units fighting on it.
    ///
    /// Until 2026-08-27 <see cref="GameState.Map"/> and <see cref="GameState.ShadowMap"/> were
    /// PURELY COSMETIC -- they picked which art the client drew and nothing else. They are now
    /// live gameplay inputs, which has two consequences worth knowing before reading any
    /// number measured with this build:
    ///
    ///   - Every benchmark taken before this date is stale, in the same way the opening-squad
    ///     change made everything before 2026-08-26 stale. A bot-vs-bot sweep that does not
    ///     pin the map is now averaging over eight different rule sets.
    ///   - The map is NOT in GetStateVector's 348 floats, so the trained policy cannot see
    ///     which map it is on and cannot learn map-specific play. That is a deliberate
    ///     deferral (adding it would grow the vector and invalidate every ONNX checkpoint),
    ///     not an oversight.
    ///
    /// The numbers here mirror the Effect column of wwwroot/assets/master_maps.csv, which is
    /// what the Collection screen shows the player. THE TWO ARE HAND-SYNCED: the CSV is client
    /// text and the engine never reads it, so changing a number in one place and not the other
    /// makes the game lie to the player about its own rules.
    /// </summary>
    public static class MapEffects
    {
        // --- The multipliers, one per map -------------------------------------------------
        // Named for the map rather than the mechanic so they read against the CSV rows.
        public const float CalmHillsHealth      = 1.10f;  // White  -- "+10% HP"
        public const float WarehouseSpeed       = 1.10f;  // Purple -- "+10% movement speed"
        public const float RainyDockBurn        = 0.75f;  // Blue   -- "-25% fire damage"
        public const float MarshySwampSpeed     = 0.90f;  // Green  -- "-10% movement speed"
        public const float SunbakedDesertDamage = 0.90f;  // Yellow -- "-10% damage"
        public const float VolcanoBurn          = 1.10f;  // Orange -- "+10% fire damage"
        public const float LowGravityKnockback  = 1.50f;  // Black  -- "fly farther"
        public const float ShadowDamage         = 1.10f;  // shadow -- "+10% damage"

        /// <summary>
        /// How long a knocked-back unit stays staggered, in ticks -- the "Knockback" hard-CC
        /// status applied where displacement happens in MoveAndFight.
        ///
        /// This is the FLIGHT TIME. The engine moves a knocked-back unit instantly and the
        /// client animates the arc afterwards (VisualUnit.knockbackDuration), so the second
        /// the unit spends unable to act IS the second it is visibly in the air. Doubling it
        /// on the low-gravity map is what makes units hang there rather than merely land
        /// farther away; the client's arc is doubled to match, and the two must stay equal or
        /// units will act while still drawn mid-flight.
        /// </summary>
        public const int KnockbackStaggerTicks          = GameEngine.TICKS_PER_SECOND;
        public const int LowGravityKnockbackStaggerTicks = 2 * GameEngine.TICKS_PER_SECOND;

        // --- Red's heal pulse -------------------------------------------------------------
        // "intermittently heal all units on the field": every 10-30s, everything alive gains
        // 10-50% of its max health, capped at full.
        public const int HealPulseMinDelayTicks = 10 * GameEngine.TICKS_PER_SECOND;
        public const int HealPulseMaxDelayTicks = 30 * GameEngine.TICKS_PER_SECOND;
        public const float HealPulseMinFraction = 0.10f;
        public const float HealPulseMaxFraction = 0.50f;

        /// <summary>
        /// How long the "Heal" status sits on a unit after a pulse, purely so the player can
        /// see it happen -- the client spawns heal particles from a status's NAME and never
        /// reads its Value. One second, as specified.
        /// </summary>
        public const int HealPulseStatusTicks = GameEngine.TICKS_PER_SECOND;

        /// <summary>
        /// Everything the rules need to know about the map being played, resolved once.
        /// A struct so reading it inside a per-unit loop costs nothing.
        /// </summary>
        public readonly struct MapModifiers
        {
            /// <summary>Multiplier on a unit's max/starting health, applied at spawn.</summary>
            public float Health { get; }

            /// <summary>Multiplier on a unit's movement speed, applied at spawn.</summary>
            public float Speed { get; }

            /// <summary>Multiplier on a unit's attack damage, applied at spawn.</summary>
            public float Damage { get; }

            /// <summary>Multiplier on Burn damage-over-time, applied as the tick lands.</summary>
            public float BurnDamage { get; }

            /// <summary>Multiplier on knockback displacement.</summary>
            public float Knockback { get; }

            /// <summary>Ticks a knocked-back unit stays staggered -- its flight time.</summary>
            public int KnockbackStaggerTicks { get; }

            /// <summary>Does this map periodically heal everything on the field?</summary>
            public bool HealPulse { get; }

            public MapModifiers(float health, float speed, float damage, float burnDamage,
                                float knockback, int knockbackStaggerTicks, bool healPulse)
            {
                Health = health;
                Speed = speed;
                Damage = damage;
                BurnDamage = burnDamage;
                Knockback = knockback;
                KnockbackStaggerTicks = knockbackStaggerTicks;
                HealPulse = healPulse;
            }
        }

        /// <summary>
        /// Resolves the modifiers for a board. Never returns null and never throws on an
        /// unrecognised map -- an unknown colour is simply a map with no effect.
        /// </summary>
        public static MapModifiers For(GameState state)
        {
            if (state == null) return None;

            float health = 1f;
            float speed = 1f;
            float damage = 1f;
            float burn = 1f;
            float knockback = 1f;
            int stagger = KnockbackStaggerTicks;
            bool healPulse = false;

            switch (state.Map)
            {
                case TeamColour.White:  health = CalmHillsHealth;          break;
                case TeamColour.Purple: speed  = WarehouseSpeed;           break;
                case TeamColour.Blue:   burn   = RainyDockBurn;            break;
                case TeamColour.Green:  speed  = MarshySwampSpeed;         break;
                case TeamColour.Yellow: damage = SunbakedDesertDamage;     break;
                case TeamColour.Orange: burn   = VolcanoBurn;              break;
                case TeamColour.Red:    healPulse = true;                  break;
                case TeamColour.Black:
                    knockback = LowGravityKnockback;
                    stagger   = LowGravityKnockbackStaggerTicks;
                    break;
            }

            // "on top of the regular map effects" -- shadow stacks MULTIPLICATIVELY with
            // whatever the underlying map already does, so a shadow Sunbaked Desert is
            // 0.9 x 1.1 rather than a flat wash either way.
            //
            // Black can never reach here with ShadowMap set: GameState's constructor turns a
            // shadow roll that lands back on Black into the plain Black map. So low gravity
            // and the shadow damage bonus are mutually exclusive by construction.
            if (state.ShadowMap) damage *= ShadowDamage;

            return new MapModifiers(health, speed, damage, burn, knockback, stagger, healPulse);
        }

        /// <summary>The identity modifiers -- a map that changes nothing.</summary>
        public static MapModifiers None =>
            new MapModifiers(1f, 1f, 1f, 1f, 1f, KnockbackStaggerTicks, false);

        /// <summary>
        /// Applies a percentage modifier to a stat that must stay a whole number.
        ///
        /// Three deliberate properties:
        ///
        ///   - A multiplier of exactly 1 returns the input untouched, with no arithmetic at
        ///     all. That makes "this feature cannot have changed a unit on a map without the
        ///     effect" true by construction rather than true by an argument about
        ///     floating-point identity -- the same reasoning as the random-stat unit's
        ///     "every other unit takes the definition's values verbatim".
        ///   - A base of 0 stays 0. WallDefinition sets Damage = 0, and a blanket floor of 1
        ///     here would hand every wall a point of damage and turn defensive scenery into
        ///     an attacker. CLAUDE.md records this exact trap being hit once already.
        ///   - Anything that started positive stays at least 1, so a reduction can never
        ///     silently produce a unit with no health or no bite.
        /// </summary>
        public static int ScaleStat(int baseValue, float multiplier)
        {
            if (multiplier == 1f || baseValue == 0) return baseValue;

            int scaled = (int)MathF.Round(baseValue * multiplier);
            return baseValue > 0 ? Math.Max(1, scaled) : scaled;
        }
    }
}
