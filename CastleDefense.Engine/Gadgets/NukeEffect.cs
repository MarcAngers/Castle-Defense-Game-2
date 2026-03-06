namespace CastleDefense.Engine.Gadgets
{
    public class NukeEffect : IGadgetEffect
    {
        private int damage = 40;
        private int radius = 300;
        private int delay = 48;

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.TriggerGadgetAnimation("nuke", side, position);

            // Schedule the gadget effect to happen after the animation
            engine.ScheduleAction(delay, () =>
            {
                foreach (var unit in engine._state.Units)
                {
                    if (Math.Abs(unit.Position - position) <= radius)
                    {
                        engine.ApplyDamage(unit, damage, Models.AttackType.Melee, 50000);
                    }
                }

                // TODO: Damage enemy castle if in range
            });
        }
    }
}
