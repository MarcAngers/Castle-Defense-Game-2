using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Gadgets
{
    public class FreezeEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public FreezeEffect(GadgetDefinition def)
        {
            _def = def;
        }

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation("freeze", side, position);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleAction(_def.Delay, () =>
            {
                // Find all enemy units currently on the board
                var enemies = engine._state.Units.Where(u => u.Side != side).ToList();

                foreach (var enemy in enemies)
                {
                    engine.ApplyDamage(enemy, _def.BaseValue, Models.AttackType.Melee, 0);
                    enemy.Statuses.Add(new ActiveStatus("Freeze", engine._state.CurrentTick + _def.StatusDuration, _def.PushForce));
                }
            });
        }
    }
}
