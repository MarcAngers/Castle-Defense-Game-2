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

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation(_def.Id, side, position);

            var baseXp = _def.Level == 2 ? 1000 : 100;
            engine.AddGadgetXp(side, "nuke", baseXp);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleAction(_def.Delay, () =>
            {
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

                int bonusXp = totalDamageDealt / 10;

                if (bonusXp > 0)
                {
                    // "nuke" acts as the base ID for tracking, regardless of what tier they are currently on
                    engine.AddGadgetXp(side, "nuke", bonusXp);
                }

                // Damage castles:
                engine.DamageCastle(engine._state.Player1, _def.BaseValue / 2);
                engine.DamageCastle(engine._state.Player2, _def.BaseValue / 2);
            });
        }
    }
}
