using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Gadgets
{
    public class DivineEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public DivineEffect(GadgetDefinition def)
        {
            _def = def;
        }

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation(_def.Id, side, position);

            var baseXp = _def.Level == 2 ? 1000 : 100;
            engine.AddGadgetXp(side, "divine", baseXp);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleAction(_def.Delay, () =>
            {
                // Make all allies invulnerable
                var allies = engine._state.Units.Where(u => u.Side == side).ToList();

                if (_def.Level < 3)
                {
                    foreach (var ally in allies)
                    {
                        ally.CurrentShield = _def.BaseValue;
                        engine.AddGadgetXp(side, "divine", _def.BaseValue / 10);
                    }
                } 
                else
                {
                    foreach (var ally in allies)
                    {
                        ally.Statuses.Add(new ActiveStatus(
                            "Invulnerable",
                            engine._state.CurrentTick + _def.StatusDuration,
                            _def.BaseValue
                        ));
                    }

                    // Make castle invulnerable
                    var player = side == 1 ? engine._state.Player1 : engine._state.Player2;
                    player.IsInvulnerable = true;
                    player.InvulnerableUntilTick = engine._state.CurrentTick + _def.StatusDuration;
                }
            });
        }
    }
}
