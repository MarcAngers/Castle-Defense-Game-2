using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Gadgets
{
    public class RageEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public RageEffect(GadgetDefinition def)
        {
            _def = def;
        }
        public void Execute(GameEngine engine, int side, int position)
        {
            var allies = engine._state.Units.Where(u => u.Side == side).ToList();

            foreach (var ally in allies)
            {
                ally.Statuses.Add(new ActiveStatus(
                    "Rage",
                    engine._state.CurrentTick + _def.StatusDuration,
                    _def.BaseValue
                ));
            }
        }
    }
}
