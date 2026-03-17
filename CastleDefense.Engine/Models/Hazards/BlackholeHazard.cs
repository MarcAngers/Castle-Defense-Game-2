namespace CastleDefense.Engine.Models.Hazards
{
    public class BlackholeHazard : Hazard
    {
        public override void ProcessEffect(GameState state)
        {
            var hazardCenter = this.Position + this.Width / 2;

            // --- 1. DETERMINE LEVEL FROM ID ---
            int level = 1;
            if (!string.IsNullOrEmpty(this.SourceGadgetId))
            {
                var parts = this.SourceGadgetId.Split('_');
                if (parts.Length > 1 && int.TryParse(parts[1], out int parsedLevel))
                {
                    level = parsedLevel;
                }
            }

            foreach (var unit in state.Units)
            {
                // BUG FIX: Reset pull direction for EACH unit!
                float pullDirection = 1f;

                // 1D Hitbox overlap check
                if (unit.Position + unit.Width >= this.Position && unit.Position <= this.Position + this.Width)
                {
                    float unitCenter = unit.Position + unit.Width / 2;

                    // --- 2. THE EVENT HORIZON (Level 3 Only) ---
                    if (level == 3 && unitCenter >= hazardCenter - 50 && unitCenter <= hazardCenter + 50)
                    {
                        // Only the strongest units can survive the black hole!
                        if (unit.Tier != 8)
                        {
                            unit.CurrentHealth = 0; // Spaghettification complete.
                            continue; // Skip the rest of the logic since this unit is dead   
                        }
                    }

                    // --- 3. PULL THE UNIT IN ---
                    if (unitCenter > hazardCenter)
                    {
                        pullDirection = -1f;
                    }

                    // Calculate how far they are from the center (0 = dead center, radius = edge)
                    float radius = this.Width / 2f;
                    float distance = Math.Abs(unitCenter - hazardCenter);

                    // Invert the distance so it's stronger in the middle (1.0 at center, 0.0 at edge)
                    float proximityMultiplier = Math.Max(0, 1f - (distance / radius));

                    // Base pull guarantees they can't escape. Bonus pull yanks them hard into the center!
                    float basePull = unit.CurrentSpeed + 2f;
                    float bonusPull = 15f * proximityMultiplier;

                    unit.Position += (basePull + bonusPull) * pullDirection;

                    // --- 4. APPLY DAMAGE OVER TIME ---
                    // If the unit is deep IN the black hole (but outside the event horizon)
                    if (unit.Position + unit.Width >= (hazardCenter - 100) && unit.Position <= (hazardCenter + 100))
                    {
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
                                this.BaseValue,
                                this.Side,
                                this.SourceGadgetId  // Passed attribution tags so it gets XP!
                            ));
                        }
                    }
                }
            }
        }

        public override void OnExpire(GameState state)
        {
            var hazardCenter = this.Position + this.Width / 2;

            foreach (var unit in state.Units)
            {
                // If they are inside the black hole when it vanishes...
                if (unit.Position + unit.Width >= this.Position && unit.Position <= this.Position + this.Width)
                {
                    // Push them violently AWAY from the center
                    float pushDirection = (unit.Position + unit.Width / 2 > hazardCenter) ? 1f : -1f;

                    // 1500 is a massive knockback, adjust as needed!
                    unit.PendingKnockback += (500 * pushDirection);
                }
            }
        }
    }
}
