namespace CastleDefense.Engine.Models.Hazards
{
    public class FireHazard : Hazard
    {
        public override void ProcessEffect(GameState state)
        {
            // Find every unit currently standing inside the fire's X-coordinates
            foreach (var unit in state.Units)
            {
                // 1D Hitbox overlap check: 
                // Unit's right edge > Hazard's left edge AND Unit's left edge < Hazard's right edge
                if (unit.Position + unit.Width >= this.Position && unit.Position <= this.Position + this.Width)
                {
                    // Check if they are already burning!
                    var existingBurn = unit.Statuses.FirstOrDefault(s => s.Name == "Burn");

                    if (existingBurn != null)
                    {
                        // They are already on fire! Just keep the fire going.
                        // Refresh the duration to last 3 seconds AFTER they leave the zone.
                        existingBurn.ExpiresAtTick = state.CurrentTick + (30 * 3);
                    }
                    else
                    {
                        // They just stepped into the fire! Ignite them.
                        unit.Statuses.Add(new ActiveStatus(
                            "Burn",
                            state.CurrentTick + (30 * 3),
                            this.BaseValue
                        ));
                    }
                }
            }
        }
    }
}
