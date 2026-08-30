namespace CastleDefense.Engine.Models
{
    public class ActiveStatus
    {
        /// The status kind, as a bare string. The COMPLETE set the engine constructs or
        /// tests for is: "Blackhole", "Burn", "Freeze", "Heal", "Invulnerable", "Knockback",
        /// "Poison", "Rage", "Slow", "Speed", "Stun".
        ///
        /// Corrected 2026-08-29: this line used to read `"Freeze", "Burn", "Poison",
        /// "SpeedBuff"` -- and "SpeedBuff" has never existed; the speed status is "Speed".
        /// That matters beyond tidiness, because the name is a LOOKUP KEY on both sides:
        /// ProcessStatuses switches on it, and the client picks a particle effect from it
        /// (wwwroot/src/status-particle-map.js). A status constructed under a name nothing
        /// recognises applies no effect and draws nothing, silently.
        public string Name { get; set; }
        public string SourceGadgetId { get; set; }
        public long ExpiresAtTick { get; set; }
        public float Value { get; set; }   // e.g., Burn Damage amount, or Speed % boost
        public int Side { get; set; }

        public ActiveStatus(string name, long tick, float value, int side = 0, string source = "none")
        {
            Name = name;
            ExpiresAtTick = tick;
            Value = value;
            Side = side;
            SourceGadgetId = source;
        }

        /// <summary>
        /// Every field here is a value type or an immutable string, so a memberwise copy
        /// is a complete deep copy. Using MemberwiseClone rather than listing fields by
        /// hand is deliberate: a hand-written copy silently misses any field added later,
        /// and silent incompleteness is exactly the failure mode cloning has to avoid.
        /// </summary>
        public ActiveStatus Clone() => (ActiveStatus)MemberwiseClone();
    }
}
