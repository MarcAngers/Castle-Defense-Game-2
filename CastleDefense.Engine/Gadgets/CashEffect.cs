using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;

namespace CastleDefense.Engine.Gadgets
{
    public class CashEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        /// <summary>
        /// Ticks between consecutive payouts at level 3. Eight of them, so the whole run
        /// takes <c>7 * PayoutIntervalTicks</c> ticks after the first.
        ///
        /// The CLIENT mirrors this number: `cash-animator.js` releases one crate per payout
        /// on the same cadence, so a change here without one there desynchronises the crates
        /// from the money. It is the only part of the cash animation that is not derivable
        /// from the gadget's own CSV row.
        /// </summary>
        public const int PayoutIntervalTicks = 10;

        /// <summary>Payouts a cast produces. Level 3 rains; levels 1 and 2 drop once.</summary>
        public static int PayoutCount(GadgetDefinition def) => def.Level >= 3 ? 8 : 1;

        public CashEffect(GadgetDefinition def)
        {
            _def = def;
        }

        public void Execute(GameEngine engine, int side, int position)
        {
            // ONE ANIMATION PER CAST, AT EVERY LEVEL.
            //
            // Level 3 used to raise this here AND again on each of its eight payouts, i.e.
            // NINE times for eight crates. On screen that read as one crate falling on its
            // own, a pause, and then the other eight -- the extra crate being the cast-time
            // trigger, which fires Delay ticks before the payout stream starts.
            //
            // The client now draws the whole run from this single trigger: the plane flies in,
            // then releases one crate per payout on PayoutIntervalTicks. That also means the
            // plane is drawn once rather than eight times on top of itself.
            engine.TriggerGadgetAnimation(_def.Id, side, position);

            engine.AddGadgetXp(side, "cash", 100);

            int payouts = PayoutCount(_def);
            for (int i = 0; i < payouts; i++)
            {
                engine.ScheduleEffect(_def.Delay + i * PayoutIntervalTicks, new PendingEffect
                {
                    GadgetId = _def.Id,
                    Phase = PhasePayout,
                    Side = side,
                    Position = position,
                });
            }
        }

        private const int PhasePayout = 0;

        public void ExecuteScheduled(GameEngine engine, in PendingEffect e)
        {
            if (e.Phase != PhasePayout) return;

            var player = e.Side == 1 ? engine._state.Player1 : engine._state.Player2;
            player.Money += _def.BaseValue;
        }
    }
}
