namespace CastleDefense.Engine.Models
{
    public class ActiveStatus
    {
        public string Name { get; set; } // "Freeze", "Burn", "Poison", "SpeedBuff"
        public long ExpiresAtTick { get; set; }
        public float Value { get; set; }   // e.g., Burn Damage amount, or Speed % boost

        public ActiveStatus(string name, long tick, float value)
        {
            Name = name;
            ExpiresAtTick = tick;
            Value = value;
        }
    }
}
