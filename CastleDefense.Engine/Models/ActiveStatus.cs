namespace CastleDefense.Engine.Models
{
    public class ActiveStatus
    {
        public string Name { get; set; } // "Freeze", "Burn", "Poison", "SpeedBuff"
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
