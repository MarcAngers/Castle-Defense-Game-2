using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using System.Numerics;

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

        /// <summary>
        /// The top of the economy ladder. At this InvestmentCount the invest button stops
        /// being an economy upgrade and becomes ARMAGEDDON — see GameEngine.Invest.
        /// </summary>
        public const int ArmageddonInvestmentCount = 8;

        /// <summary>
        /// Set once ARMAGEDDON has been bought. It is a one-time purchase: invest is
        /// permanently unavailable afterwards (mask[9] = 0, button reads "INVEST: MAX").
        ///
        /// Deliberately a separate flag rather than InvestmentCount == 9. Buying
        /// ARMAGEDDON does NOT run ApplyInvestmentStep, so Income and InvestmentPrice
        /// stay where they were — the money buys the end of the game, not more economy.
        /// Bumping the count instead would silently move both, and would also shift
        /// log10(InvestmentPrice) in the observation vector.
        /// </summary>
        public bool ArmageddonUsed { get; set; }

        // Base
        public int CastleHealth { get; set; }
        public int CastleMaxHealth { get; set; }
        public double RepairPrice { get; set; }
        public int RepairCount { get; set; }

        /// <summary>
        /// Absorbing shield HP sitting in front of <see cref="CastleHealth"/>, granted by
        /// every divine cast (see DivineEffect) and consumed first by
        /// GameEngine.DamageCastle. Casts STACK ADDITIVELY -- a second cast before the
        /// first shield is spent builds on top of it, so the shield is a bankable
        /// investment rather than a refresh.
        ///
        /// DELIBERATELY UNRELATED TO CastleMaxHealth. A repair raises CastleMaxHealth and
        /// must NOT move this value: a 1,000 HP shield is 1,000 HP whether the castle
        /// behind it holds 2,000 or 12,000. Only the client's shield BAR is scaled by
        /// CastleMaxHealth, because a bar is a proportion and the shield is not.
        ///
        /// It does not expire, and nothing clears it except damage.
        /// </summary>
        public int CastleShield { get; set; }
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
            Income = 2; //2
            InvestmentPrice = 18;
            InvestmentCount = 0;
            CastleHealth = 2000;
            CastleMaxHealth = 2000;
            CastleShield = 0;
            RepairPrice = 20;
            RepairCount = 0;
        }

        /// <summary>
        /// Deep copy for engine cloning.
        ///
        /// THE SUBTLE PART IS THE EVENT. MemberwiseClone copies the OnGadgetUpgraded
        /// delegate field along with everything else, which would leave the ORIGINAL
        /// game's subscribers attached to the CLONE — so a speculative gadget upgrade
        /// inside a search rollout would fire real UI notifications and real training
        /// callbacks. It is cleared explicitly below. This is not hypothetical: the
        /// engine already carries a RewirePlayerEvents() method because event wiring
        /// has gone wrong here once before.
        ///
        /// The three GadgetDefinition references are intentionally SHARED, not copied —
        /// definitions are process-wide singletons built once by GameDataManager and are
        /// immutable during play.
        /// </summary>
        public PlayerState Clone()
        {
            var copy = (PlayerState)MemberwiseClone();
            copy.OnGadgetUpgraded = null;
            copy.UnitCharges = new Dictionary<string, int>(UnitCharges);
            copy.CooldownTimers = new Dictionary<string, long>(CooldownTimers);
            copy.GadgetXp = new Dictionary<string, int>(GadgetXp);
            copy.GadgetCooldowns = new Dictionary<string, long>(GadgetCooldowns);
            return copy;
        }

        // Constructor to give the AI training program a time machine into later game states
        public PlayerState(int timeSkip) : this()
        {
            if (timeSkip == 0) return;

            for (int i = 1; i <= timeSkip; i++)
            {
                ApplyInvestmentStep();
                ApplyRepairStep();
            }

            // Don't start at full HP
            CastleHealth = (int)(0.75 * CastleMaxHealth);

            // Start with a bit of money
            Money += Income;
        }

        // Applies exactly one investment step in place -- the SINGLE source of truth for
        // the income/price formula (and its two hardcoded high-tier overrides at
        // InvestmentCount 7/8). Previously this math was duplicated between
        // GameEngine.Invest and the timeSkip constructor above; when the InvestmentCount
        // 7/8 overrides were hand-tuned directly in GameEngine.Invest, the time-machine
        // constructor was never updated to match, so any headstart game (including every
        // BotArena "headstart" benchmark run) at InvestmentCount>=7 silently used the
        // stale pre-override formula instead of the real values a live game would produce.
        // Factoring this out means a future rebalance can only ever happen in one place.
        public void ApplyInvestmentStep()
        {
            InvestmentCount++;
            // Equation for player income: e^(0.0109x^3 + 0.0011x^2 + 0.4351x + 0.5268), R^2 = 0.9997
            Income = Math.Pow(Math.E, 0.0109 * Math.Pow(InvestmentCount, 3) + 0.0011 * Math.Pow(InvestmentCount, 2) + 0.4351 * InvestmentCount + 0.5268);
            // Each investment should take twice as long as the last
            InvestmentPrice = Income * (InvestmentCount * 4 + 8);

            if (InvestmentCount == 7)
            {
                Income = 750;
                InvestmentPrice = 40000;
            }
            if (InvestmentCount == 8)
            {
                Income = 2500;
                InvestmentPrice = 121221;
            }
        }

        // Applies exactly one repair step in place -- same single-source-of-truth
        // reasoning as ApplyInvestmentStep, previously duplicated between
        // GameEngine.Repair and the timeSkip constructor above.
        public void ApplyRepairStep()
        {
            RepairCount++;
            double rp = Math.Pow(Math.E, 0.0109 * Math.Pow(RepairCount, 3) + 0.0011 * Math.Pow(RepairCount, 2) + 0.4351 * RepairCount + 0.5268);
            RepairPrice = rp * (RepairCount * 5 + 5);
            if (RepairCount >= 8)
                RepairPrice *= 2;

            float pct = (float)CastleHealth / CastleMaxHealth;
            // Equation for increasing castle health:
            CastleMaxHealth = 1000 + 11000 * RepairCount;
            // Increase castle health and heal by 20%:
            CastleHealth = (int)Math.Min(CastleMaxHealth * (pct + 0.2), CastleMaxHealth);
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
                if (upgradedDef == null) return; // NextTierId not found — data configuration error

                // Swap the loadout!
                if (OffensiveGadget == currentDef) OffensiveGadget = upgradedDef;
                else if (DefensiveGadget == currentDef) DefensiveGadget = upgradedDef;
                else if (SignatureGadget == currentDef) SignatureGadget = upgradedDef;

                GadgetXp[baseGadgetId] = 0;

                // Set Gadget cooldown on upgrade
                GadgetCooldowns[upgradedDef.Id] = upgradedDef.CooldownMs / (1000 / 30);

                OnGadgetUpgraded?.Invoke(this.Side, upgradedDef);
            }
        }
    }
}
