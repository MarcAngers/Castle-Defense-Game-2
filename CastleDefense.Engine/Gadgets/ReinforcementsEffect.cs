using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Gadgets
{
    public class ReinforcementsEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public ReinforcementsEffect(GadgetDefinition def)
        {
            _def = def;
        }
        public void Execute(GameEngine engine, int side, int position)
        {
            var player = side == 1 ? engine._state.Player1 : engine._state.Player2;
            var teamDef = GameDataManager.Teams.Find(team => team.Color == player.Team);
            var unit = teamDef.Roster.Find(u => u.Tier == _def.BaseValue);

            // Spawn one unit immediately, then spawn more after a short delay, for a total of 5
            for (int delay = 0; delay < 50; delay += 10) {
                engine.ScheduleAction(delay, () =>
                {
                    engine.SpawnUnit(side, unit.Id, true); // (since we already payed for the gadget, spawn units for free)
                });
            }
        }
    }
}
