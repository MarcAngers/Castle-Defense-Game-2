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
            var baseXp = _def.Level == 2 ? 1000 : 100;
            engine.AddGadgetXp(side, "meteor", baseXp);

            // 1. Determine shower intensity
            int meteorCount = _def.Level == 3 ? 10 : (_def.Level == 2 ? 3 : 1);

            // Gap between meteors in game ticks (e.g., 10 ticks = 1/3 of a second)
            int staggerTicks = 10;
            Random rand = new Random();

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
                    engine.ScheduleAction(animationDelay, () =>
                    {
                        engine.TriggerGadgetAnimation("meteor", side, dropPos);
                    });
                }

                // --- SCHEDULE THE DAMAGE ---
                engine.ScheduleAction(damageDelay, () =>
                {
                    var enemies = engine._state.Units.Where(u => u.Side != side).ToList();

                    foreach (var enemy in enemies)
                    {
                        if (Math.Abs(enemy.Position - dropPos) <= _def.Radius)
                        {
                            engine.ApplyDamage(enemy, _def.BaseValue, Models.AttackType.Melee, _def.PushForce);

                            // Note: I added the attribution tags here so the meteor 
                            // properly gets Kill XP if the burn finishes them off!
                            enemy.Statuses.Add(new ActiveStatus(
                                "Burn",
                                engine._state.CurrentTick + _def.StatusDuration,
                                1f,
                                side,
                                _def.Id
                            ));

                            engine.AddGadgetXp(side, "meteor", 10);
                        }
                    }
                });
            }
        }
    }
}
