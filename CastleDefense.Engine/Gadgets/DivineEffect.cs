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

            engine.AddGadgetXp(side, "divine", 100);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleEffect(_def.Delay, new PendingEffect
            {
                GadgetId = _def.Id,
                Phase = PhaseApply,
                Side = side,
                Position = position,
            });
        }

        private const int PhaseApply = 0;

        public void ExecuteScheduled(GameEngine engine, in PendingEffect e)
        {
            if (e.Phase != PhaseApply) return;

            int side = e.Side;

            // Make all allies invulnerable
            var allies = engine._state.Units.Where(u => u.Side == side).ToList();
            var player = side == 1 ? engine._state.Player1 : engine._state.Player2;

            // EVERY TIER SHIELDS THE CASTLE, and the grants STACK. Unlike the unit shield
            // below -- which is a SET, so a fresh cast merely refreshes a unit back to
            // BaseValue -- the castle total is added to, so repeated casts bank a bigger
            // and bigger buffer for as long as nothing spends it. PlayerState.CastleShield
            // is independent of CastleMaxHealth, so a later repair leaves it alone.
            player.CastleShield += (int)_def.BaseValue;

            // EVERY TIER ALSO SHIELDS THE UNITS, tier 3 included. Unlike the castle total
            // this is a SET rather than an add, so a recast refreshes a unit back up to
            // BaseValue instead of banking on top of it.
            //
            // Tier 3 laying a shield UNDER its invulnerability is the point of the tier,
            // not a bonus: ApplyDamage returns early for an Invulnerable unit, so nothing
            // touches the shield while the window is open, and the moment the status
            // expires the unit drops to an ordinary shielded body with BaseValue left to
            // spend. That transition is what the client reads to swap divine_3 art back to
            // divine art -- see View.drawUnit.
            foreach (var ally in allies)
            {
                ally.CurrentShield = (int)_def.BaseValue;
            }

            if (_def.Level >= 3)
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
                player.IsInvulnerable = true;
                player.InvulnerableUntilTick = engine._state.CurrentTick + _def.StatusDuration;
            }
        }
    }
}
