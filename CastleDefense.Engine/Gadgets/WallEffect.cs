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
        /// <summary>A wall must stand in OUR line to tank for us; dropped on the enemy it blocks nothing.</summary>
        public GadgetAim Aim => GadgetAim.Ally;

        public void Execute(GameEngine engine, int side, int position)
        {
            var yposition = _def.Level == 2 ? 180 : -180;

            if (_def.Level == 1)
            {
                // Level 1 is UNTARGETED, so the engine places it: a fixed distance out in
                // front of the caster's own castle.
                //
                // REFLECT THE GEOMETRY, NOT THE COORDINATE. Position is the sprite's LEFT
                // EDGE, so the mirror of a left edge at 600 is `MAP_WIDTH - 600 - Width`,
                // not `MAP_WIDTH - 600`. This line used to read a bare `1400`, which left
                // P2's wall its own width closer to P2's castle than P1's was to P1's --
                // 75px at level 1, a third of the 225px gap the wall is meant to leave.
                // Measured before the fix: P1's wall stood 400px clear of its castle wall
                // and P2's only 325px.
                //
                // Same class of bug as the three fixed on 2026-07-31
                // (GetDistanceToEnemyCastle, SpawnUnit's default position and
                // FindTargetsFast), every one of which flipped a SIGN where it should have
                // reflected the geometry. This was the last one left on that list.
                const int WallGapFromCastle = 600;
                int wallWidth = GameDataManager.WallDefinition(1).Width;
                position = (side == 1)
                    ? WallGapFromCastle
                    : GameEngine.MAP_WIDTH - WallGapFromCastle - wallWidth;
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
