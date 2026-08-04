using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Gadgets
{
    public class SnipeEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public SnipeEffect(GadgetDefinition def)
        {
            _def = def;
        }
        public void Execute(GameEngine engine, int side, int position)
        {
            var enemies = engine._state.Units.Where(u => u.Side != side).ToList();

            if (enemies.Count == 0)
                return;

            var target = enemies[0];

            foreach (var enemy in enemies)
            {
                if (Math.Abs(enemy.Position - position) < Math.Abs(target.Position - position))
                {
                    target = enemy;
                }
                else if (Math.Abs(enemy.Position - position) == Math.Abs(target.Position - position))
                {
                    if (enemy.MaxHealth > target.MaxHealth)
                    {
                        target = enemy;
                    }
                }
            }

            engine.TriggerGadgetAnimation(_def.Id, side, position, target.InstanceId);

            engine.AddGadgetXp(side, "snipe", 100);

            // Schedule the gadget effect to happen after the animation.
            // The target is stored by InstanceId rather than as an object reference —
            // a captured reference would point into the original game after a clone.
            engine.ScheduleEffect(_def.Delay, new PendingEffect
            {
                GadgetId = _def.Id,
                Phase = PhaseFire,
                Side = side,
                Position = position,
                TargetId = target.InstanceId,
            });
        }

        private const int PhaseFire = 0;

        public void ExecuteScheduled(GameEngine engine, in PendingEffect e)
        {
            if (e.Phase != PhaseFire) return;

            // BEHAVIOUR CHANGE, deliberate (2026-07-28, agreed with Marc): if the target
            // died during the delay it is gone from Units, so the shot now fizzles. The
            // old closure held the Unit object directly and applied damage to a corpse
            // that was no longer on the board. Fizzling is the correct reading of "the
            // target got sniped" and the old behaviour was a latent bug.
            // Copy out of the `in` parameter first — CS1628: an in/ref/out parameter
            // cannot be captured by a lambda.
            var targetId = e.TargetId;
            var target = engine._state.Units.FirstOrDefault(u => u.InstanceId == targetId);
            if (target == null) return;

            engine.ApplyDamage(target, (int)_def.BaseValue, Models.AttackType.Melee, _def.PushForce);
        }
    }
}
