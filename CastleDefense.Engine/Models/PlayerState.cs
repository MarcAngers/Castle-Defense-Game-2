using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;

namespace CastleDefense.Engine.Models
{
    public class PlayerState
    {
        public string ConnectionId { get; set; }
        public int Side { get; set; } // 1 or 2
        public TeamColour Team { get; set; }

        // Economy
        public double Money { get; set; }
        public double Income { get; set; }
        public double InvestmentPrice { get; set; }
        public int InvestmentCount { get; set; }

        // Base
        public int CastleHealth { get; set; }
        public int CastleMaxHealth { get; set; }
        public int RepairPrice { get; set; }
        public bool IsInvulnerable { get; set; }
        public long InvulnerableUntilTick { get; set; }
        public GadgetDefinition OffensiveGadget { get; set; }
        public GadgetDefinition DefensiveGadget { get; set; }
        public GadgetDefinition SignatureGadget { get; set; }

        // Cooldowns
        public Dictionary<string, int> UnitCharges { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, long> CooldownTimers { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> GadgetCooldowns { get; set; } = new Dictionary<string, long>();

        public PlayerState()
        {
            Money = 0;
            Income = 2;
            InvestmentPrice = 3;
            InvestmentCount = 0;
            CastleMaxHealth = 100000;
            RepairPrice = 4000;
            CastleHealth = 100000;
        }

        public void SetLoadout(string[] loadout)
        {
            OffensiveGadget = GameDataManager.Gadgets.Find(g => g.Id == loadout[0]);
            DefensiveGadget = GameDataManager.Gadgets.Find(g => g.Id == loadout[1]);
            SignatureGadget = GameDataManager.Gadgets.Find(g => g.Id == loadout[2]);
        }
    }
}
