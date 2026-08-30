using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;

namespace CastleDefense.Engine.Gadgets
{
    public class CashEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public CashEffect(GadgetDefinition def)
        {
            _def = def;
        }
        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation(_def.Id, side, position);

            engine.AddGadgetXp(side, "cash", 100);

            if (_def.Level < 3)
            {
                engine.ScheduleEffect(_def.Delay, new PendingEffect
                {
                    GadgetId = _def.Id,
                    Phase = PhasePayout,
                    Side = side,
                    Position = position,
                });
            } else
            {
                for (int i = _def.Delay; i < _def.Delay + 80; i += 10)
                {
                    engine.ScheduleEffect(i, new PendingEffect
                    {
                        GadgetId = _def.Id,
                        Phase = PhasePayoutWithAnimation,
                        Side = side,
                        Position = position,
                    });
                }
            }
        }

        private const int PhasePayout = 0;
        // Level 3 fires eight staggered payouts, each re-triggering the animation. The
        // old closure captured `i` implicitly via the loop body; as data each tick gets
        // its own record, which is both clearer and copyable.
        private const int PhasePayoutWithAnimation = 1;

        public void ExecuteScheduled(GameEngine engine, in PendingEffect e)
        {
            if (e.Phase != PhasePayout && e.Phase != PhasePayoutWithAnimation) return;

            int side = e.Side;

            if (e.Phase == PhasePayoutWithAnimation)
            {
                engine.TriggerGadgetAnimation(_def.Id, side, e.Position);
            }

            var player = side == 1 ? engine._state.Player1 : engine._state.Player2;
            player.Money += _def.BaseValue;
        }
    }
}
