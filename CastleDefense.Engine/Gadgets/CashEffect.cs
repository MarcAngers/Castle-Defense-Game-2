using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Gadgets
{
    public class CashEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public CashEffect(GadgetDefinition def)
        {
            _def = def;
        }
        public void Execute(GameEngine engine, int side, int position)
        {
            var player = side == 1 ? engine._state.Player1 : engine._state.Player2;
            player.Money += _def.BaseValue;
        }
    }
}
