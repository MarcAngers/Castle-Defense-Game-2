using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Gadgets
{
    public class SpeedEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public SpeedEffect(GadgetDefinition def)
        {
            _def = def;
        }

        public void Execute(GameEngine engine, int side, int position)
        {
            // Speed gadget works immediately
            var allies = engine._state.Units.Where(u => u.Side == side).ToList();

            foreach (var ally in allies)
            {
                ally.Statuses.Add(new ActiveStatus(
                    "Speed",
                    engine._state.CurrentTick + _def.StatusDuration,
                    _def.BaseValue
                ));
            }
        }
    }
}
