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

            var baseXp = _def.Level == 2 ? 1000 : 100;
            engine.AddGadgetXp(side, "cash", baseXp);

            if (_def.Level < 3)
            {
                engine.ScheduleAction(_def.Delay, () =>
                {
                    var player = side == 1 ? engine._state.Player1 : engine._state.Player2;
                    player.Money += _def.BaseValue;

                    engine.AddGadgetXp(side, "cash", baseXp);
                });
            } else
            {
                for (int i = _def.Delay; i < _def.Delay + 80; i += 10)
                {
                    engine.ScheduleAction(i, () =>
                    {
                        var player = side == 1 ? engine._state.Player1 : engine._state.Player2;
                        player.Money += _def.BaseValue;

                        engine.AddGadgetXp(side, "cash", baseXp);
                    });
                }
            }
        }
    }
}
