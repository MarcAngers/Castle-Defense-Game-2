// --- The Blueprint for a Gadget ---
using CastleDefense.Engine.Gadgets;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Definitions
{
    public class GadgetDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public IGadgetEffect GadgetEffect { get; set; }
        public GadgetSlot Slot { get; set; }
        public bool Targeted { get; set; }

        public int Cost { get; set; }
        public int CooldownMs { get; set; }

        // Data-Driven Effects
        public int BaseValue { get; set; }      // Damage/Heal/Cash amount/etc.
        public int Radius { get; set; }
        public int Delay { get; set; }
        public int HazardDuration { get; set; }
        public int StatusDuration { get; set; }

        public int PushForce { get; set; }
    }
}