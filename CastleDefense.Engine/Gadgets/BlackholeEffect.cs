using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;
using CastleDefense.Engine.Models.Hazards;

namespace CastleDefense.Engine.Gadgets
{
    public class BlackholeEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public BlackholeEffect(GadgetDefinition def)
        {
            _def = def;
        }

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation(_def.Id, side, position);

            var baseXp = _def.Level == 2 ? 1000 : 100;
            engine.AddGadgetXp(side, "blackhole", baseXp);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleAction(_def.Delay, () =>
            {
                var blackhole = new BlackholeHazard
                {
                    Type = "Blackhole",
                    SourceGadgetId = _def.Id,
                    Side = side,
                    BaseValue = _def.BaseValue,
                    Position = position - _def.Radius,
                    Width = _def.Radius * 2,
                    ExpiresAtTick = (int)engine._state.CurrentTick + _def.HazardDuration
                };

                engine._state.Hazards.Add(blackhole);
            });
        }
    }
}
