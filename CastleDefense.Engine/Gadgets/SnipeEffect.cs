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

            var baseXp = _def.Level == 2 ? 1000 : 100;
            engine.AddGadgetXp(side, "snipe", baseXp);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleAction(_def.Delay, () =>
            {
                int preHealth = target.CurrentHealth + Math.Max(0, target.CurrentShield);

                engine.ApplyDamage(target, _def.BaseValue, Models.AttackType.Melee, _def.PushForce);

                int postHealth = target.CurrentHealth + Math.Max(0, target.CurrentShield);

                engine.AddGadgetXp(side, "snipe", (preHealth - postHealth) / 10);

                if (postHealth <= 0)
                {
                    engine.AddGadgetXp(side, "snipe", 50);
                }
            });
        }
    }
}
