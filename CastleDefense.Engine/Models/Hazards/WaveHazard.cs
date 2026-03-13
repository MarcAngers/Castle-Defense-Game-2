namespace CastleDefense.Engine.Models.Hazards
{
    public class WaveHazard : Hazard
    {
        public override void ProcessEffect(GameState state)
        {
            float direction = (this.Side == 1) ? 1f : -1f;
            var enemies = state.Units.Where(u => u.Side != this.Side).ToList();

            foreach (var enemy in enemies)
            {
                // 1D Hitbox overlap check: 
                // Unit's right edge > Hazard's left edge AND Unit's left edge < Hazard's right edge
                if (enemy.Position + enemy.Width >= this.Position && enemy.Position <= this.Position + this.Width)
                {
                    // Launch the unit
                    enemy.PendingKnockback += (1500 * direction);
                }
            }

            // Move the wave
            this.Position += 10 * direction;
        }
    }
}
