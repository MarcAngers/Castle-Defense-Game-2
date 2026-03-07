using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Gadgets
{
    public class PoisonEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public PoisonEffect(GadgetDefinition def)
        {
            _def = def;
        }

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation("poison", side, position);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleAction(_def.Delay, () =>
            {
                var poisonZone = new Hazard
                {
                    Type = "Poison",
                    Side = side,
                    Position = position - _def.Radius,
                    Width = _def.Radius * 2,
                    ExpiresAtTick = (int)engine._state.CurrentTick + _def.HazardDuration
                };

                engine._state.Hazards.Add(poisonZone);
            });
        }
    }
}
