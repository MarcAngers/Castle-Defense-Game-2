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
            else
            {
                // `position` arrives as the point the player aimed at, but SpawnUnit takes
                // the sprite's LEFT edge, so passing it straight through hung the whole
                // wall off to the RIGHT of the crosshair. wall_3 is 450 wide, so aiming
                // anywhere past ~1350 put its right edge over P2's castle -- and past 1700
                // put it off the map entirely. Centre it on the aim point instead, then
                // keep it inside the corridor between the two castle walls (x=200 and
                // x=1800) so it can never overlap either castle whichever seat placed it.
                //
                // This is also what let a wall reach a castle at all, which crashed the
                // game outright: see the AttackSpeed guard in GameEngine.MoveAndFight.
                var wallDef = GameDataManager.WallDefinition(_def.Level);
                int minLeft = GameEngine.P1_CASTLE_WALL;
                int maxLeft = GameEngine.P2_CASTLE_WALL - wallDef.Width;

                position -= wallDef.Width / 2;
                // Guard the degenerate case of a wall too wide for the corridor, which
                // would make the clamp invert and place it somewhere arbitrary.
                position = maxLeft < minLeft
                    ? (GameEngine.MAP_WIDTH - wallDef.Width) / 2
                    : Math.Max(minLeft, Math.Min(maxLeft, position));
            }

            // Can only spawn 1 wall
            if (engine._state.Units.Any(u => u.Side == side && u.DefinitionId.StartsWith("wall")))
            {
                // Refund cost and cooldown
                var player = side == 1 ? engine._state.Player1 : engine._state.Player2;
                var wallDefId = _def.Level == 1 ? "wall" : "wall_" + _def.Level.ToString();
                var wallDef = GameDataManager.Gadgets.Find(g => g.Id == wallDefId);
                player.Money += wallDef.Cost;
                player.GadgetCooldowns[_def.Id] = 0;

                return;
            }

            engine.AddGadgetXp(side, "wall", 100);
            engine.SpawnUnit(side, _def.Id, true, position, yposition);
        }
    }
}
