using System.Drawing;

namespace CastleDefense.Engine.Models.Hazards
{
    public class GooHazard : Hazard
    {
        public override void ProcessEffect(GameState state)
        {
            var player = Side == 1 ? state.Player1 : state.Player2;

            foreach (var unit in state.Units)
            {
                // 1D Hitbox overlap check: 
                // Unit's right edge > Hazard's left edge AND Unit's left edge < Hazard's right edge
                if (unit.Position + unit.Width >= this.Position && unit.Position <= this.Position + this.Width)
                {
                    // If it's an enemy, slow it
                    if (unit.Side != this.Side)
                    {
                        // Check if they are already slowed
                        var existingSlow = unit.Statuses.FirstOrDefault(s => s.Name == "Slow");

                        if (existingSlow != null)
                        {
                            existingSlow.ExpiresAtTick = state.CurrentTick + (30 * 3);
                        }
                        else
                        {
                            // They just stepped into the goo! Slow them.
                            unit.Statuses.Add(new ActiveStatus(
                                "Slow",
                                state.CurrentTick + (30 * 3),
                                0.5f // Slow them to half speed
                            ));

                            player.AddGadgetXp("goo", 10);
                        }
                    }
                    else
                    {
                        // It's an ally, heal them!
                        var existingHeal = unit.Statuses.FirstOrDefault(s => s.Name == "Heal");

                        if (existingHeal != null)
                        {
                            // They are already on fire! Just keep the fire going.
                            // Refresh the duration to last 3 seconds AFTER they leave the zone.
                            existingHeal.ExpiresAtTick = state.CurrentTick + (30 * 3);
                        }
                        else
                        {
                            // They just stepped into the fire! Ignite them.
                            unit.Statuses.Add(new ActiveStatus(
                                "Heal",
                                state.CurrentTick + (30 * 3),
                                -1f * this.BaseValue
                            ));

                            player.AddGadgetXp("goo", 10);
                        }
                    }
                }
            }
        }
    }
}
