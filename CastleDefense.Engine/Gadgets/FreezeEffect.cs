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
            engine.TriggerGadgetAnimation(_def.Id, side, position);

            var baseXp = _def.Level == 2 ? 1000 : 100;
            engine.AddGadgetXp(side, "freeze", baseXp);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleAction(_def.Delay, () =>
            {
                // Find all enemy units currently on the board
                var enemies = engine._state.Units.Where(u => u.Side != side).ToList();

                foreach (var enemy in enemies)
                {
                    engine.ApplyDamage(enemy, _def.BaseValue, Models.AttackType.Melee, 0);
                    enemy.Statuses.Add(new ActiveStatus("Freeze", engine._state.CurrentTick + _def.StatusDuration, _def.PushForce));
                    engine.AddGadgetXp(side, "freeze", 10);
                }
            });

            if (_def.Level == 3)
            {
                engine.ScheduleAction(_def.Delay + _def.StatusDuration, () =>
                {
                    // Find all enemy units currently on the board
                    var enemies = engine._state.Units.Where(u => u.Side != side).ToList();

                    foreach (var enemy in enemies)
                    {
                        enemy.Statuses.Add(new ActiveStatus("Slow", engine._state.CurrentTick + _def.StatusDuration, 0.25f));
                    }
                });
            }
        }
    }
}
