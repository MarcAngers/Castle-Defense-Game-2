namespace CastleDefense.Engine.Models.Hazards
{
    public abstract class Hazard
    {
        public string Type { get; set; } // "Fire", "Ice", "PoisonCloud", etc.
        public int Side { get; set; }
        public float BaseValue { get; set; }

        public float Position { get; set; } // The starting X coordinate (left edge)
        public float Width { get; set; }    // How far the hazard stretches
        public int ExpiresAtTick { get; set; } // When the hazard disappears

        public abstract void ProcessEffect(GameState state);
    }
}
