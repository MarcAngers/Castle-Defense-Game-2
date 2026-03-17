using CastleDefense.Engine.Definitions;
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
            var baseXp = _def.Level == 2 ? 1000 : 100;
            engine.AddGadgetXp(side, "speed", baseXp);

            var allies = engine._state.Units.Where(u => u.Side == side).ToList();

            foreach (var ally in allies)
            {
                engine.AddGadgetXp(side, "speed", 10);
                ally.Statuses.Add(new ActiveStatus(
                    "Speed",
                    engine._state.CurrentTick + _def.StatusDuration,
                    _def.BaseValue
                ));
            }
        }
    }
}
