using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;
using CastleDefense.Engine.Models.Hazards;

namespace CastleDefense.Engine.Gadgets
{
    public class WaveEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public WaveEffect(GadgetDefinition def)
        {
            _def = def;
        }

        /// <summary>
        /// How many DISTINCT units a wave of this level may launch before it collapses.
        ///
        /// Levels 1 and 2 are `BaseValue / 10` -- 50 and 100 against the CSV's 500 and 1,000
        /// KNOCKBACK -- so the cap tracks the gadget's own strength column and a rebalance of
        /// one moves the other. Level 3 deliberately does NOT follow that rule: BaseValue/10
        /// would be 300, and the design wants the Tsunami effectively uncapped, so it is a
        /// flat 1,000. Nothing in the game puts 1,000 units on one side of the field, so in
        /// practice a level-3 wave still crosses the whole map and expires on its
        /// HazardDuration exactly as it did before this cap existed.
        /// </summary>
        public static int CapFor(GadgetDefinition def)
            => def.Level >= 3 ? 1000 : (int)(def.BaseValue / 10);

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation(_def.Id, side, position);

            engine.AddGadgetXp(side, "wave", 100);

            var waveZone = new WaveHazard
            {
                Type = "Wave",
                SourceGadgetId = _def.Id,
                Side = side,
                Position = side == 1 ? -100 : 2100,
                Width = _def.Radius * 2,
                MaxKnockbacks = CapFor(_def),
                // The wave still has a hard ceiling on its life. HazardDuration is now the
                // LONGER of the two limits rather than the only one: whichever of "crossed
                // the map" and "spent its budget" comes first ends it.
                ExpiresAtTick = (int)engine._state.CurrentTick + _def.HazardDuration
            };

            engine._state.Hazards.Add(waveZone);
        }
    }
}
