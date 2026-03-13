namespace CastleDefense.Engine.Models.Hazards
{
    public class BlackholeHazard : Hazard
    {
        public override void ProcessEffect(GameState state)
        {
            var hazardCenter = this.Position + this.Width / 2;
            float pullDirection = 1f;

            foreach (var unit in state.Units)
            {
                // 1D Hitbox overlap check: 
                // Unit's right edge > Hazard's left edge AND Unit's left edge < Hazard's right edge
                if (unit.Position + unit.Width >= this.Position && unit.Position <= this.Position + this.Width)
                {
                    // Pull the unit in
                    if (unit.Position + unit.Width / 2 > hazardCenter)
                    {
                        pullDirection = -1f;
                    }
                    unit.Position += (unit.CurrentSpeed + 2) * pullDirection;

                    // If the unit is IN the black hole, deal damage to them
                    if (unit.Position + unit.Width >= (hazardCenter - 100) && unit.Position <= (hazardCenter + 100))
                    {
                        // Check if they are already blackholed
                        var existingBurn = unit.Statuses.FirstOrDefault(s => s.Name == "Blackhole");

                        if (existingBurn != null)
                        {
                            existingBurn.ExpiresAtTick = state.CurrentTick + 10;
                        }
                        else
                        {
                            unit.Statuses.Add(new ActiveStatus(
                                "Blackhole",
                                state.CurrentTick + 10,
                                this.BaseValue
                            ));
                        }
                    }
                }
            }
        }
    }
}
