using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models.Hazards;

namespace CastleDefense.Engine.Gadgets
{
    public class FirebombEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public FirebombEffect(GadgetDefinition def)
        {
            _def = def;
        }

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation(_def.Id, side, position);

            engine.AddGadgetXp(side, "firebomb", 100);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleEffect(_def.Delay, new PendingEffect
            {
                GadgetId = _def.Id,
                Phase = PhaseSpawnHazard,
                Side = side,
                Position = position,
            });
        }

        // Public so ArmageddonEffect can schedule the hazard directly. See MeteorEffect.
        public const int PhaseSpawnHazard = 0;

        public void ExecuteScheduled(GameEngine engine, in PendingEffect e)
        {
            if (e.Phase != PhaseSpawnHazard) return;

            int side = e.Side;
            int position = e.Position;

            var fireZone = new FireHazard
            {
                Type = "Fire",
                SourceGadgetId = _def.Id,
                Side = side,
                BaseValue = _def.BaseValue,
                Position = position - _def.Radius,
                Width = _def.Radius * 2,
                ExpiresAtTick = (int)engine._state.CurrentTick + _def.HazardDuration
            };

            engine._state.Hazards.Add(fireZone);
        }
    }
}
