using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;

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
            // Check if the player already has a wall
            if (engine._state.Units.Any(u => u.Side == side && u.DefinitionId == "wall"))
            {
                // Refund cost (probably will need to refund the cooldown as well)
                var player = side == 1 ? engine._state.Player1 : engine._state.Player2;
                var wallDef = GameDataManager.Gadgets.Find(g => g.Id == "wall");
                player.Money += wallDef.Cost;
            }
            else
            {
                // Spawn wall immediately
                engine.SpawnUnit(side, "wall");
            }
        }
    }
}
