using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;
using CastleDefense.Engine.Models.Hazards;

namespace CastleDefense.Engine.Gadgets
{
    public class GooEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public GooEffect(GadgetDefinition def)
        {
            _def = def;
        }

        /// <summary>Goo heals allies standing in it. The enemy slow is a bonus, not the anchor.</summary>
        public GadgetAim Aim => GadgetAim.Ally;

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation(_def.Id, side, position);

            engine.AddGadgetXp(side, "goo", 100);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleEffect(_def.Delay, new PendingEffect
            {
                GadgetId = _def.Id,
                Phase = PhaseSpawnHazard,
                Side = side,
                Position = position,
            });
        }

        private const int PhaseSpawnHazard = 0;

        public void ExecuteScheduled(GameEngine engine, in PendingEffect e)
        {
            if (e.Phase != PhaseSpawnHazard) return;

            int side = e.Side;
            int position = e.Position;

            var gooZone = new GooHazard
            {
                Type = "Goo",
                Side = side,
                BaseValue = _def.BaseValue,
                Position = position - _def.Radius,
                Width = _def.Radius * 2,
                ExpiresAtTick = (int)engine._state.CurrentTick + _def.HazardDuration
            };

            engine._state.Hazards.Add(gooZone);
        }
    }
}
