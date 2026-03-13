// --- The Blueprint for a Team ---

// --- The Blueprint for a Team ---
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Definitions
{
    public class TeamDefinition
    {
        public string Id { get; set; }
        public TeamColour Color { get; set; }
        public string Name { get; set; }
        public string PassiveName { get; set; }
        public string PassiveDescription { get; set; }

        // The 8 Units (Ordered Tier 1 -> 8)
        public List<UnitDefinition> Roster { get; set; } = new List<UnitDefinition>();

        // The Signature Ability
        public GadgetDefinition SignatureGadget { get; set; }
    }
}