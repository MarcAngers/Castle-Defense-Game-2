using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Gadgets
{
    public class HealEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public HealEffect(GadgetDefinition def)
        {
            _def = def;
        }

        public void Execute(GameEngine engine, int side, int position)
        {
            // Heal gadget works immediately
            var allies = engine._state.Units.Where(u => u.Side == side).ToList();

            foreach (var ally in allies)
            {
                ally.Statuses.Add(new ActiveStatus(
                    "Heal",
                    engine._state.CurrentTick + _def.StatusDuration,
                    -1f * _def.BaseValue // -1 damage per tick (30 hps)
                ));
            }
        }
    }
}
