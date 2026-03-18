using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;

namespace CastleDefense.Engine.Models
{
    public class PlayerState
    {
        public string ConnectionId { get; set; }
        public int Side { get; set; } // 1 or 2
        public TeamColour Team { get; set; }
        public event Action<int, GadgetDefinition> OnGadgetUpgraded;

        // Economy
        public double Money { get; set; }
        public double Income { get; set; }
        public double InvestmentPrice { get; set; }
        public int InvestmentCount { get; set; }

        // Base
        public int CastleHealth { get; set; }
        public int CastleMaxHealth { get; set; }
        public int RepairPrice { get; set; }
        public int RepairCount { get; set; }
        public bool IsInvulnerable { get; set; }
        public long InvulnerableUntilTick { get; set; }
        public GadgetDefinition OffensiveGadget { get; set; }
        public GadgetDefinition DefensiveGadget { get; set; }
        public GadgetDefinition SignatureGadget { get; set; }

        // Cooldowns
        public Dictionary<string, int> UnitCharges { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, long> CooldownTimers { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, int> GadgetXp { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, long> GadgetCooldowns { get; set; } = new Dictionary<string, long>();

        public PlayerState()
        {
            Money = 0;
            Income = 1000; //2
            InvestmentPrice = 18;
            InvestmentCount = 0;
            CastleHealth = 1000;
            CastleMaxHealth = 1000;
            RepairPrice = 20;
            RepairCount = 0;
        }

        public void SetLoadout(string[] loadout)
        {
            OffensiveGadget = GameDataManager.Gadgets.Find(g => g.Id == loadout[0]);
            DefensiveGadget = GameDataManager.Gadgets.Find(g => g.Id == loadout[1]);
            SignatureGadget = GameDataManager.Gadgets.Find(g => g.Id == loadout[2]);
        }

        public void AddGadgetXp(string gadgetId, int amount)
        {
            if (string.IsNullOrEmpty(gadgetId)) return;

            string baseGadgetId = gadgetId.Split('_')[0].ToLower();

            if (!GadgetXp.ContainsKey(baseGadgetId))
                GadgetXp[baseGadgetId] = 0;

            GadgetXp[baseGadgetId] += amount;

            // Find the CURRENT gadget they have equipped in that slot
            GadgetDefinition currentDef = null;
            if (OffensiveGadget?.Id.StartsWith(baseGadgetId) == true) currentDef = OffensiveGadget;
            else if (DefensiveGadget?.Id.StartsWith(baseGadgetId) == true) currentDef = DefensiveGadget;
            else if (SignatureGadget?.Id.StartsWith(baseGadgetId) == true) currentDef = SignatureGadget;

            if (currentDef == null || string.IsNullOrEmpty(currentDef.NextTierId)) return; // Max tier reached!

            // Check if they crossed the threshold
            if (GadgetXp[baseGadgetId] >= currentDef.UpgradeCost)
            {
                // Use your global GameDataManager to grab the upgraded definition
                var upgradedDef = GameDataManager.Gadgets.Find(g => g.Id == currentDef.NextTierId);

                // Swap the loadout!
                if (OffensiveGadget == currentDef) OffensiveGadget = upgradedDef;
                else if (DefensiveGadget == currentDef) DefensiveGadget = upgradedDef;
                else if (SignatureGadget == currentDef) SignatureGadget = upgradedDef;

                GadgetXp[baseGadgetId] = 0;

                OnGadgetUpgraded?.Invoke(this.Side, upgradedDef);
            }
        }
    }
}
