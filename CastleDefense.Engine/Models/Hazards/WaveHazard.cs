using System.Drawing;

namespace CastleDefense.Engine.Models.Hazards
{
    public class WaveHazard : Hazard
    {
        public override void ProcessEffect(GameState state)
        {
            var player = Side == 1 ? state.Player1 : state.Player2;
            float direction = (this.Side == 1) ? 1f : -1f;
            var enemies = state.Units.Where(u => u.Side != this.Side).ToList();

            int level = 1;
            if (!string.IsNullOrEmpty(this.SourceGadgetId))
            {
                var parts = this.SourceGadgetId.Split('_');
                if (parts.Length > 1 && int.TryParse(parts[1], out int parsedLevel))
                {
                    level = parsedLevel;
                }
            }

            // Calculate wave speed and knockback strength
            float durationSeconds = 5f; // Level 1 defaults to 5 seconds
            float knockbackDist = 500f;
            if (level == 2)
            {
                durationSeconds = 7f;
                knockbackDist = 1500f;
            }
            if (level == 3)
            {
                durationSeconds = 10f;
                knockbackDist = 3000f;
            }

            float speed = GameEngine.MAP_WIDTH / (durationSeconds * GameEngine.TICKS_PER_SECOND);

            foreach (var enemy in enemies)
            {
                // 1D Hitbox overlap check: 
                // Unit's right edge > Hazard's left edge AND Unit's left edge < Hazard's right edge
                if (enemy.Position + enemy.Width >= this.Position && enemy.Position <= this.Position + this.Width)
                {
                    if (enemy.DefinitionId == "wall")
                    {
                        continue;
                    }

                    // Launch the unit
                    enemy.PendingKnockback += (knockbackDist * direction);

                    player.AddGadgetXp("wave", 10);
                }
            }

            // Move the wave
            this.Position += speed * direction;
        }
    }
}
