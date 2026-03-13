namespace CastleDefense.Engine.Models.Hazards
{
    public class PoisonHazard : Hazard
    {
        public override void ProcessEffect(GameState state)
        {
            foreach (var unit in state.Units)
            {
                // 1D Hitbox overlap check: 
                // Unit's right edge > Hazard's left edge AND Unit's left edge < Hazard's right edge
                if (unit.Position + unit.Width >= this.Position && unit.Position <= this.Position + this.Width)
                {
                    // Poison only affects enemy units
                    if (unit.Side != this.Side)
                    {
                        // Check if they are already poisoned
                        var existingBurn = unit.Statuses.FirstOrDefault(s => s.Name == "Poison");

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
                                "Poison",
                                state.CurrentTick + (30 * 3),
                                this.BaseValue
                            ));
                        }
                    }
                }
            }
        }
    }
}
