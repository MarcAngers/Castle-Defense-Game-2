using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Gadgets
{
    public class MeteorEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public MeteorEffect(GadgetDefinition def)
        {
            _def = def;
        }

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation("meteor", side, position);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleAction(_def.Delay, () =>
            {
                var enemies = engine._state.Units.Where(u => u.Side != side).ToList();

                foreach (var enemy in enemies)
                {
                    if (Math.Abs(enemy.Position - position) <= _def.Radius)
                    {
                        engine.ApplyDamage(enemy, _def.BaseValue, Models.AttackType.Melee, _def.PushForce);

                        enemy.Statuses.Add(new ActiveStatus(
                            "Burn",
                            engine._state.CurrentTick + _def.StatusDuration,
                            1f
                        ));
                    }
                }
            });
        }
    }
}
