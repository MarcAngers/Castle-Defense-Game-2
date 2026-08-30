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

        /// <summary>
        /// The tick at which this player's ARMAGEDDON divine shield expires — for the
        /// castle AND for every allied unit, which is the whole point of storing it here
        /// rather than deriving it per-unit. 0 means "never cast".
        ///
        /// WHY IT IS SHARED STATE RATHER THAN A LOCAL DURATION. Both players can reach
        /// ARMAGEDDON, and a second cast must NOT outlast the first one — otherwise the
        /// player who got there SECOND wins by default, simply because their shield is
        /// still up when the first player's drops. ArmageddonEffect reads the enemy's copy
        /// of this field at cast time and, if that shield is still live, ends its own
        /// window at exactly the same tick. See ArmageddonEffect.ClaimShieldWindow.
        ///
        /// Distinct from <see cref="InvulnerableUntilTick"/>, which is the castle-only
        /// flag the engine polls and which a plain divine_3 cast also writes.
        /// </summary>
        public long ArmageddonShieldUntilTick { get; set; }

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
            // Enough to open with a tier-1 unit or two rather than standing empty-handed
            // waiting for the first income tick. Raised from 0 on 2026-08-26; it shifts
            // every opening, so any benchmark measured before that date is not comparable.
            Money = 10;
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

        /// <summary>
        /// Rounds a price UP to a whole dollar. Every price the player can be charged goes
        /// through here.
        ///
        /// WHY THIS EXISTS: money is a double and ticks up in fractional income, but the
        /// client draws it with Math.floor -- so a player holding $31.78 reads "$31". The
        /// prices were fractional too, and were drawn with a DIFFERENT rounding
        /// (Math.ceil for invest, toFixed(0) for repair), so the number on the button and
        /// the number in the wallet were rounded opposite ways. An invest priced 31.779
        /// displayed as $32 and became affordable while the wallet still read $31; a
        /// repair priced 26.483 displayed as $26 and stayed DISABLED at a wallet reading
        /// $26. Both are the same defect seen from either side.
        ///
        /// An integral price fixes it outright rather than papering over it, because for
        /// integer P, `floor(money) >= P` is exactly equivalent to `money >= P`. That
        /// makes "the button lights up when your displayed money reaches the displayed
        /// price" true by construction, for every price, on both the client and the
        /// engine -- rather than something each display has to be careful about.
        ///
        /// UP rather than nearest, for two reasons: the quoted price is a promise, and
        /// rounding down would charge less than the button says; and invest already
        /// DISPLAYED Math.ceil, so the number the player reads does not move at all.
        ///
        /// NOTE this is a balance change, small but real: prices rise by under a dollar
        /// (at most +0.22, e.g. the first invest 31.779 -> 32). Unit and gadget costs come
        /// from the CSVs already integral and do not need it.
        /// </summary>
        private static double WholeDollars(double price) => Math.Ceiling(price);

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
            // Each investment should take twice as long as the last.
            // ROUNDED UP TO A WHOLE DOLLAR -- see the note on WholeDollars below.
            InvestmentPrice = WholeDollars(Income * (InvestmentCount * 4 + 8));

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
            // Read the health outcome BEFORE the increment -- PreviewRepairStep is written
            // from the caller's point of view ("if I bought one more"), and it needs the
            // pre-repair HP percentage as well as the pre-repair RepairCount.
            var (nextHealth, nextMax) = PreviewRepairStep();

            RepairCount++;
            double rp = Math.Pow(Math.E, 0.0109 * Math.Pow(RepairCount, 3) + 0.0011 * Math.Pow(RepairCount, 2) + 0.4351 * RepairCount + 0.5268);
            RepairPrice = rp * (RepairCount * 5 + 5);
            if (RepairCount >= 8)
                RepairPrice *= 2;
            // Rounded last, so the >=8 doubling cannot reintroduce a fraction.
            RepairPrice = WholeDollars(RepairPrice);

            CastleMaxHealth = nextMax;
            CastleHealth = nextHealth;
        }

        // What ONE more repair would leave the castle at, without buying it. Same
        // single-source-of-truth reasoning as ApplyRepairStep itself: HeuristicBot's
        // incoming-nuke check has to know whether a repair actually clears the blast
        // before it spends the money, and a second copy of this formula sitting in the
        // bot would be free to drift away from the one the engine applies.
        //
        // Note this is called BEFORE RepairCount is incremented, hence the +1.
        public (int Health, int MaxHealth) PreviewRepairStep()
        {
            float pct = (float)CastleHealth / CastleMaxHealth;
            // Equation for increasing castle health:
            int nextMax = 1000 + 11000 * (RepairCount + 1);
            // Increase castle health and heal by 20%:
            return ((int)Math.Min(nextMax * (pct + 0.2), nextMax), nextMax);
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
