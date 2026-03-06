using System;
using System.Collections.Generic;
using System.Text;

namespace CastleDefense.Engine.Gadgets
{
    public class FirebombEffect : IGadgetEffect
    {
        private int radius = 100;
        private int duration = 120;
        private int delay = 48;

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation("firebomb", side, position);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleAction(delay, () =>
            {
                var fireZone = new Hazard
                {
                    Type = "Fire",
                    Side = side,
                    Position = position - radius,
                    Width = radius * 2,
                    ExpiresAtTick = (int)engine._state.CurrentTick + duration
                };

                engine._state.Hazards.Add(fireZone);
            });
        }
    }
}
