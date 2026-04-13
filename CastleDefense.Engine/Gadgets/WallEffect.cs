using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using System.Net.Security;

namespace CastleDefense.Engine.Gadgets
{
    public class WallEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public WallEffect(GadgetDefinition def)
        {
            _def = def;
        }
        public void Execute(GameEngine engine, int side, int position)
        {
            var yposition = _def.Level == 2 ? 180 : -180;

            if (_def.Level == 1)
            {
                position = (side == 1) ? 600 : 1400;
                yposition = 240;
            } 

            // Can only spawn 1 wall
            if (engine._state.Units.Any(u => u.Side == side && u.DefinitionId.StartsWith("wall")))
            {
                // Refund cost (probably will need to refund the cooldown as well)
                var player = side == 1 ? engine._state.Player1 : engine._state.Player2;
                var wallDefId = _def.Level == 1 ? "wall" : "wall_" + _def.Level.ToString();
                var wallDef = GameDataManager.Gadgets.Find(g => g.Id == wallDefId);
                player.Money += wallDef.Cost;

                return;
            }

            engine.AddGadgetXp(side, "wall", 100);
            engine.SpawnUnit(side, _def.Id, true, position, yposition);
        }
    }
}
