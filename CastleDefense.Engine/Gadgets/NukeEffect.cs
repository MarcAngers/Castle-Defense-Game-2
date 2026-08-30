using CastleDefense.Engine.Definitions;

namespace CastleDefense.Engine.Gadgets
{
    public class NukeEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public NukeEffect(GadgetDefinition def)
        {
            _def = def;
        }

        /// <summary>ExecuteScheduled below loops every unit in radius with no side check, and damages BOTH castles.</summary>
        public bool HarmsAllies => true;

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation(_def.Id, side, position);

            engine.AddGadgetXp(side, "nuke", 100);

            // Schedule the gadget effect to happen after the animation.
            // Converted 2026-07-28 from a closure to a data record — see PendingEffect.cs.
            engine.ScheduleEffect(_def.Delay, new PendingEffect
            {
                GadgetId = _def.Id,
                Phase = PhaseDetonate,
                Side = side,
                Position = position,
            });
        }

        // Public so ArmageddonEffect can schedule the detonation directly. See MeteorEffect.
        public const int PhaseDetonate = 0;

        /// <summary>
        /// Damage one detonation of <paramref name="def"/> deals to EACH castle -- both of
        /// them, always, regardless of who cast it. 100 / 1500 / 12000 by level.
        ///
        /// Single source of truth for the `/2`, which used to be written out separately
        /// here and in HeuristicBot's suicide guard. A castle blast this large is the
        /// reason both the suicide guard and the incoming-nuke repair exist, so the two
        /// must never be able to disagree about its size.
        /// </summary>
        public static int CastleBlastFor(GadgetDefinition def) => (int)def.BaseValue / 2;

        /// <summary>
        /// Lets a bot see this detonation coming while it is still in flight. Ignores both
        /// `side` and `e.Side`: a nuke damages the caster's castle exactly as hard as its
        /// target's.
        /// </summary>
        public int PendingCastleDamage(in PendingEffect e, int side)
            => e.Phase == PhaseDetonate ? CastleBlastFor(_def) : 0;

        public void ExecuteScheduled(GameEngine engine, in PendingEffect e)
        {
            if (e.Phase != PhaseDetonate) return;

            int side = e.Side;
            int position = e.Position;

            int totalDamageDealt = 0;
            var enemyPlayer = side == 1 ? engine._state.Player2 : engine._state.Player1;

            foreach (var unit in engine._state.Units)
            {
                if (Math.Abs(unit.Position - position) <= _def.Radius)
                {
                    // Record health before impact
                    int preHealth = unit.CurrentHealth + Math.Max(0, unit.CurrentShield);

                    engine.ApplyDamage(unit, (int)_def.BaseValue, Models.AttackType.Melee, _def.PushForce);

                    // Record health after impact
                    int postHealth = unit.CurrentHealth + Math.Max(0, unit.CurrentShield);

                    totalDamageDealt += (preHealth - postHealth);
                }
            }

            // Damage castles:
            engine.DamageCastle(engine._state.Player1, CastleBlastFor(_def));
            engine.DamageCastle(engine._state.Player2, CastleBlastFor(_def));
        }
    }
}
