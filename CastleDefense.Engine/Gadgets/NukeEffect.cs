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
            engine.TriggerGadgetAnimation("nuke", side, position);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleAction(_def.Delay, () =>
            {
                foreach (var unit in engine._state.Units)
                {
                    if (Math.Abs(unit.Position - position) <= _def.Radius)
                    {
                        engine.ApplyDamage(unit, _def.BaseValue, Models.AttackType.Melee, _def.PushForce);
                    }
                }

                // Damage castles:
                engine.DamageCastle(engine._state.Player1, _def.BaseValue / 2);
                engine.DamageCastle(engine._state.Player2, _def.BaseValue / 2);
            });
        }
    }
}
