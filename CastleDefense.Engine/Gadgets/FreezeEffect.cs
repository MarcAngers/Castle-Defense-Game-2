using System;
using System.Collections.Generic;
using System.Text;

namespace CastleDefense.Engine.Gadgets
{
    public class FreezeEffect : IGadgetEffect
    {
        private int damage = 10;
        private int delay = 48;

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation("freeze", side, position);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleAction(delay, () =>
            {
                // Find all enemy units currently on the board
                var enemies = engine._state.Units.Where(u => u.Side != side).ToList();

                foreach (var enemy in enemies)
                {
                    engine.ApplyDamage(enemy, damage, Models.AttackType.Melee, 0);
                    enemy.Statuses.Add(new ActiveStatus("Freeze", engine._state.CurrentTick + 150, 0));
                }
            });
        }
    }
}
