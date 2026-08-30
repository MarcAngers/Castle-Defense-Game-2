using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;

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
            engine.AddGadgetXp(side, "reinforcements", 100);

            var player = side == 1 ? engine._state.Player1 : engine._state.Player2;
            var teamDef = GameDataManager.Teams.Find(team => team.Color == player.Team);
            var unit = teamDef.Roster.Find(u => u.Tier == _def.BaseValue);

            // Spawn one unit immediately, then spawn more after a short delay, for a total of 5.
            // The unit is resolved here (from the caster's team) and carried as an id, so a
            // cloned engine spawns exactly what the original had already committed to.
            for (int delay = 0; delay < 50; delay += 10) {
                engine.ScheduleEffect(delay, new PendingEffect
                {
                    GadgetId = _def.Id,
                    Phase = PhaseSpawn,
                    Side = side,
                    Position = position,
                    UnitId = unit.Id,
                });
            }
        }

        private const int PhaseSpawn = 0;

        public void ExecuteScheduled(GameEngine engine, in PendingEffect e)
        {
            if (e.Phase != PhaseSpawn || e.UnitId == null) return;

            // (since we already payed for the gadget, spawn units for free)
            engine.SpawnUnit(e.Side, e.UnitId, true);
        }
    }
}
