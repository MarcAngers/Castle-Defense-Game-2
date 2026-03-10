using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Gadgets
{
    public class WaveEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public WaveEffect(GadgetDefinition def)
        {
            _def = def;
        }

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation("wave", side, position);

            var waveZone = new Hazard
            {
                Type = "Wave",
                Side = side,
                Position = side == 1 ? -100 : 2100,
                Width = _def.Radius * 2,
                ExpiresAtTick = (int)engine._state.CurrentTick + _def.HazardDuration
            };

            engine._state.Hazards.Add(waveZone);
        }
    }
}
