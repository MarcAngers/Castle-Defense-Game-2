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
        public int Tier { get; set; }
        public bool Targeted { get; set; }

        public int Cost { get; set; }
        public int UpgradeCost { get; set; }
        public string NextTierId {  get; set; }
        public int CooldownMs { get; set; }

        // Data-Driven Effects
        public int BaseValue { get; set; }      // Damage/Heal/Cash amount/etc.
        public int Radius { get; set; }
        public int Delay { get; set; }
        public int HazardDuration { get; set; }
        public int StatusDuration { get; set; }
        public int PushForce { get; set; }

        public int Level
        {
            get
            {
                if (string.IsNullOrEmpty(Id)) return 1;

                var parts = Id.Split('_');

                // If there's an underscore and the second part is a valid number, return it!
                if (parts.Length > 1 && int.TryParse(parts[1], out int parsedLevel))
                {
                    return parsedLevel;
                }

                // Otherwise, it's the base gadget, so default to Level 1
                return 1;
            }
        }
    }
}