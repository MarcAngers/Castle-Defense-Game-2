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

            engine.AddGadgetXp(side, "freeze", 100);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleEffect(_def.Delay, new PendingEffect
            {
                GadgetId = _def.Id,
                Phase = PhaseFreeze,
                Side = side,
                Position = position,
            });

            if (_def.Level == 3)
            {
                engine.ScheduleEffect(_def.Delay + _def.StatusDuration, new PendingEffect
                {
                    GadgetId = _def.Id,
                    Phase = PhaseSlowFollowup,
                    Side = side,
                    Position = position,
                });
            }
        }

        private const int PhaseFreeze = 0;
        private const int PhaseSlowFollowup = 1;

        public void ExecuteScheduled(GameEngine engine, in PendingEffect e)
        {
            int side = e.Side;

            // Find all enemy units currently on the board
            var enemies = engine._state.Units.Where(u => u.Side != side).ToList();

            if (e.Phase == PhaseFreeze)
            {
                foreach (var enemy in enemies)
                {
                    engine.ApplyDamage(enemy, (int)_def.BaseValue, Models.AttackType.Melee, 0);
                    // Tier 8 units only get frozen for a second
                    if (enemy.Tier == 8)
                    {
                        enemy.Statuses.Add(new ActiveStatus("Freeze", engine._state.CurrentTick + 30, _def.PushForce));
                    }
                    else
                    {
                        enemy.Statuses.Add(new ActiveStatus("Freeze", engine._state.CurrentTick + _def.StatusDuration, _def.PushForce));
                    }
                }
            }
            else if (e.Phase == PhaseSlowFollowup)
            {
                foreach (var enemy in enemies)
                {
                    enemy.Statuses.Add(new ActiveStatus("Slow", engine._state.CurrentTick + _def.StatusDuration, 0.25f));
                }
            }
        }
    }
}
