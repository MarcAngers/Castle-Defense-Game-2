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

            // Schedule the gadget effect to happen after the animation.
            // The level-3 slow follow-up is NOT scheduled here: it has to apply to the
            // units this cast actually froze, and that set is not known until the freeze
            // itself lands. PhaseFreeze schedules one follow-up per frozen unit instead.
            engine.ScheduleEffect(_def.Delay, new PendingEffect
            {
                GadgetId = _def.Id,
                Phase = PhaseFreeze,
                Side = side,
                Position = position,
            });
        }

        private const int PhaseFreeze = 0;
        private const int PhaseSlowFollowup = 1;

        public void ExecuteScheduled(GameEngine engine, in PendingEffect e)
        {
            int side = e.Side;

            if (e.Phase == PhaseFreeze)
            {
                // Find all enemy units currently on the board
                var enemies = engine._state.Units.Where(u => u.Side != side).ToList();

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

                    if (_def.Level == 3)
                    {
                        // One follow-up per unit THIS cast froze, carried by InstanceId.
                        // Previously a single follow-up was queued at cast time and slowed
                        // every enemy on the board when it fired — including units that
                        // walked on after the freeze and were never frozen at all.
                        // A unit that dies before the follow-up simply has no match and is
                        // skipped, the same way a sniped corpse fizzles.
                        engine.ScheduleEffect(_def.StatusDuration, new PendingEffect
                        {
                            GadgetId = _def.Id,
                            Phase = PhaseSlowFollowup,
                            Side = side,
                            Position = e.Position,
                            TargetId = enemy.InstanceId,
                        });
                    }
                }
            }
            else if (e.Phase == PhaseSlowFollowup)
            {
                // Copy out of the `in` parameter first — CS1628: an in/ref/out parameter
                // cannot be captured by a lambda.
                var targetId = e.TargetId;
                var target = engine._state.Units.FirstOrDefault(u => u.InstanceId == targetId);
                if (target == null) return;

                target.Statuses.Add(new ActiveStatus("Slow", engine._state.CurrentTick + _def.StatusDuration, 0.25f));
            }
        }
    }
}
