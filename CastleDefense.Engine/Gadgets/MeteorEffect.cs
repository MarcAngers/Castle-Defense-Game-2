using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Gadgets
{
    public class MeteorEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        public MeteorEffect(GadgetDefinition def)
        {
            _def = def;
        }

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.AddGadgetXp(side, "meteor", 100);

            // 1. Determine shower intensity
            int meteorCount = _def.Level == 3 ? 10 : (_def.Level == 2 ? 3 : 1);

            // Gap between meteors in game ticks (e.g., 10 ticks = 1/3 of a second)
            int staggerTicks = 10;
            Random rand = engine.Rng;   // was `new Random()` — meteor stagger and spread
                                        // are gameplay-affecting, so they must come from
                                        // the engine's seedable stream to be reproducible.

            // 2. Loop and schedule both the animation AND the damage!
            for (int i = 0; i < meteorCount; i++)
            {
                // Calculate the timeline for this specific meteor
                int animationDelay = i * staggerTicks + rand.Next(-5, 6);
                int damageDelay = animationDelay + _def.Delay;

                // Give each meteor a slight random spread so it looks like a real shower
                // (Level 1 gets no offset so it hits exactly where they clicked)
                int spread = _def.Level > 1 ? rand.Next(-300, 301) : 0;
                int dropPos = position + spread;

                // Clamp it to the map bounds just in case they target the absolute edge
                dropPos = Math.Max(0, Math.Min(GameEngine.MAP_WIDTH, dropPos));

                // --- SCHEDULE THE ANIMATION ---
                if (animationDelay == 0)
                {
                    // Fire the first one instantly
                    engine.TriggerGadgetAnimation("meteor", side, dropPos);
                }
                else
                {
                    // Delay subsequent animations!
                    engine.ScheduleEffect(animationDelay, new PendingEffect
                    {
                        GadgetId = _def.Id,
                        Phase = PhaseAnimation,
                        Side = side,
                        Position = dropPos,
                    });
                }

                // --- SCHEDULE THE DAMAGE ---
                // dropPos is baked into the record. The scatter is rolled here, once,
                // from engine.Rng — so a clone inherits already-decided impact points
                // rather than re-rolling them, which is what makes rollouts repeatable.
                engine.ScheduleEffect(damageDelay, new PendingEffect
                {
                    GadgetId = _def.Id,
                    Phase = PhaseDamage,
                    Side = side,
                    Position = dropPos,
                });
            }
        }

        // Public because ArmageddonEffect schedules meteor_3 damage pulses directly rather
        // than going through Execute (which would fire a whole 10-meteor shower and hand
        // out gadget XP on every drop). Phase numbering is still owned by this class --
        // see the PendingEffect.Phase docs for why there is no global enum.
        public const int PhaseAnimation = 0;
        public const int PhaseDamage = 1;

        public void ExecuteScheduled(GameEngine engine, in PendingEffect e)
        {
            int side = e.Side;
            int dropPos = e.Position;

            if (e.Phase == PhaseAnimation)
            {
                engine.TriggerGadgetAnimation("meteor", side, dropPos);
                return;
            }

            if (e.Phase != PhaseDamage) return;

            var enemies = engine._state.Units.Where(u => u.Side != side).ToList();

            foreach (var enemy in enemies)
            {
                if (Math.Abs(enemy.Position - dropPos) <= _def.Radius)
                {
                    engine.ApplyDamage(enemy, (int)_def.BaseValue, Models.AttackType.Melee, _def.PushForce);

                    // Note: I added the attribution tags here so the meteor
                    // properly gets Kill XP if the burn finishes them off!
                    enemy.Statuses.Add(new ActiveStatus(
                        "Burn",
                        engine._state.CurrentTick + _def.StatusDuration,
                        12f,
                        side,
                        _def.Id
                    ));
                }
            }
        }
    }
}
