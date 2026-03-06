using System;
using System.Collections.Generic;
using System.Text;

namespace CastleDefense.Engine.Gadgets
{
    public class SnipeEffect : IGadgetEffect
    {
        private int damage = 50;
        private int delay = 70;
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

            engine.TriggerGadgetAnimation("snipe", side, position, target.InstanceId);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleAction(delay, () =>
            {
                engine.ApplyDamage(target, damage, Models.AttackType.Melee, 500000);
            });
        }
    }
}
