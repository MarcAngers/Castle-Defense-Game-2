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

        // ── AUTO-SPAWNER ────────────────────────────────────────────────────────────────
        //
        // A third economy upgrade sitting between INVEST and +HP. Each level buys a faster,
        // stronger free stream of units: the engine spawns them on a timer with
        // ignoreCost, exactly like the opening squad, so they cost nothing beyond the
        // upgrade itself and never enter the purchase counters or the action recording.

        /// <summary>Current auto-spawner level, 0 (not bought) to <see cref="MaxAutoSpawnLevel"/>.</summary>
        public int AutoSpawnLevel { get; set; }

        /// <summary>Price of the NEXT auto-spawner level. Meaningless once at max level.</summary>
        public double AutoSpawnPrice { get; set; }

        /// <summary>
        /// Spawn progress, advanced by UnitsPerSecond each tick and spending one unit every
        /// time it reaches TICKS_PER_SECOND.
        ///
        /// AN ACCUMULATOR RATHER THAN A TICK MODULO, because the rates do not all divide
        /// the tick rate: 4 units/s is one unit every 7.5 ticks at 30 ticks/s. A modulo
        /// would have to round that to 7 or 8 and deliver 4.29/s or 3.75/s. Accumulating
        /// lands the spawns on ticks 8/15/23/30 and averages exactly 4.0/s.
        ///
        /// INTEGER, COUNTING TICKS, rather than a double counting fractions of a unit. The
        /// fractional form was measurably wrong: 1/30 added thirty times is
        /// 0.9999999999999999, so every level delivered one spawn fewer per cycle than the
        /// table promises (AutoSpawnCheck read 0.97/s where it wanted 1.00). Counting in
        /// whole ticks is exact at every rate, and -- because this feeds replay
        /// reconstruction and search rollouts -- it is also free of any float reproducibility
        /// question across builds and machines.
        ///
        /// Lives on PlayerState, NOT on GameEngine: GameEngine.Clone is shallow, so
        /// engine-side mutable state would be shared with every search rollout.
        /// PlayerState.Clone is a MemberwiseClone, which copies this by value.
        /// </summary>
        public int AutoSpawnAccumulator { get; set; }

        /// <summary>Position in the current level's tier cycle. Reset to 0 on upgrade.</summary>
        public int AutoSpawnCycleIndex { get; set; }

        /// <summary>Top of the auto-spawner ladder; the button is dead at this level.</summary>
        public const int MaxAutoSpawnLevel = 19;

        /// <summary>
        /// The repeating tier pattern each level spawns, indexed by level (index 0 = level 0
        /// = not bought = spawns nothing). Level 5's [3,2,1] means tier 3, then tier 2, then
        /// tier 1, then back to tier 3.
        ///
        /// UNITS PER SECOND IS THE CYCLE LENGTH, deliberately not stored as a separate
        /// column. In the design table the two are equal at every level, which means the
        /// pattern repeats exactly once per second; deriving one from the other makes that
        /// invariant impossible to break by editing only one of them.
        /// </summary>
        private static readonly int[][] AutoSpawnCycles =
        {
            new int[0],                          // 0: not bought
            new[] { 1 },                         // 1: 1/s
            new[] { 1, 1 },                      // 2: 2/s
            new[] { 2, 1 },                      // 3
            new[] { 2, 2 },                      // 4
            new[] { 3, 2, 1 },                   // 5: 3/s
            new[] { 3, 2, 2 },                   // 6
            new[] { 3, 3, 2 },                   // 7
            new[] { 4, 2, 1, 1 },                // 8: 4/s
            new[] { 4, 3, 2, 1 },                // 9
            new[] { 4, 4, 1, 1 },                // 10
            new[] { 4, 4, 3, 2 },                // 11
            new[] { 4, 4, 3, 3, 2 },             // 12: 5/s
            new[] { 4, 4, 4, 3, 2 },             // 13
            new[] { 5, 3, 3, 2, 1 },             // 14
            new[] { 5, 4, 3, 2, 2 },             // 15
            new[] { 5, 4, 4, 2, 2, 2 },          // 16: 6/s
            new[] { 6, 5, 4, 3, 2, 1 },          // 17
            new[] { 7, 6, 6, 5, 5, 4 },          // 18
            new[] { 8, 7, 7, 6, 6, 5 },          // 19
        };

        /// <summary>Units spawned per second at <paramref name="level"/>. 0 when not bought.</summary>
        public static int AutoSpawnUnitsPerSecond(int level)
            => level <= 0 || level >= AutoSpawnCycles.Length ? 0 : AutoSpawnCycles[level].Length;

        /// <summary>
        /// The tier this player's auto-spawner should produce next, or 0 for "nothing".
        /// Wraps the cycle itself so callers never have to know the pattern length.
        /// </summary>
        public int NextAutoSpawnTier()
        {
            if (AutoSpawnLevel <= 0 || AutoSpawnLevel >= AutoSpawnCycles.Length) return 0;
            var cycle = AutoSpawnCycles[AutoSpawnLevel];
            if (cycle.Length == 0) return 0;
            // Modulo rather than a wrapping increment so a level change mid-cycle (which
            // shortens the array) can never index out of range.
            return cycle[((AutoSpawnCycleIndex % cycle.Length) + cycle.Length) % cycle.Length];
        }

        /// <summary>
        /// Cost of auto-spawner <paramref name="level"/>.
        ///
        /// Reuses the investment curve, but walked at a DIFFERENT SCALE: level 1 sits at
        /// curve position 2.5 and each level advances it by 0.25 rather than 1. That is
        /// what makes the auto-spawner ladder 19 rungs long where investing is 8.
        ///
        /// The multiplier is (x * 4 + 7), one less than investing's (x * 4 + 8).
        ///
        /// The last three rungs are hardcoded for the same reason investing hardcodes its
        /// top two: past level 16 the curve is being asked to price the end of the game,
        /// and a hand-picked number does that better than an extrapolation.
        /// </summary>
        public static double AutoSpawnPriceFor(int level)
        {
            // QUERY ONLY -- the PositiveInfinity below must never be STORED. It is a
            // serialisable-state crash if it reaches AutoSpawnPrice; see ApplyAutoSpawnStep.
            if (level <= 0 || level > MaxAutoSpawnLevel) return double.PositiveInfinity;
            if (level == 17) return 43614;
            if (level == 18 || level == 19) return 100000;

            double x = 2.5 + 0.25 * (level - 1);
            return WholeDollars(EconomyCurve(x) * (x * 4 + 7));
        }

        /// <summary>
        /// Applies exactly one auto-spawner step in place -- the single source of truth for
        /// the level/price pair, same reasoning as ApplyInvestmentStep.
        /// </summary>
        public void ApplyAutoSpawnStep()
        {
            if (AutoSpawnLevel >= MaxAutoSpawnLevel) return;

            AutoSpawnLevel++;

            // AT THE TOP OF THE LADDER THE PRICE IS LEFT WHERE IT IS, and this is a crash
            // fix, not a cosmetic one. AutoSpawnPriceFor returns PositiveInfinity for "no
            // such level", so buying level 19 used to ask it for level 20 and store infinity
            // in AutoSpawnPrice -- a field serialised to both clients every single tick.
            // System.Text.Json cannot write a non-finite number, so the throw surfaced
            // inside SignalR's per-connection write pipeline and aborted that client while
            // the server kept simulating: the player saw a freeze, then a rejoin that failed
            // on malformed JSON. Exactly the wall_3 / Unit.AttackCooldown failure of commit
            // 29d64bfe, whose rule was that nothing may write a non-finite value into
            // serialisable state. Guarded by AutoSpawnCheck section 6.
            //
            // Leaving the price untouched also matches what ARMAGEDDON does at the top of
            // the investment ladder, and the button reads "MAX" from the LEVEL anyway, so
            // the stale value is never displayed.
            if (AutoSpawnLevel < MaxAutoSpawnLevel)
                AutoSpawnPrice = AutoSpawnPriceFor(AutoSpawnLevel + 1);

            // Restart the pattern so an upgrade visibly pays off on its next spawn (every
            // cycle leads with its strongest tier) instead of resuming mid-cycle on a
            // leftover tier-1. The ACCUMULATOR is deliberately left alone: spawn cadence is
            // continuous, and resetting it would let a well-timed purchase cancel a spawn
            // that was already nearly paid for.
            AutoSpawnCycleIndex = 0;
        }

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
        // ── UNIT CHARGES ────────────────────────────────────────────────────────────────
        //
        // Every buyable unit holds up to five charges and regains one per second. Spending
        // the last one puts that unit on cooldown until the next charge lands, exactly like
        // a gadget. The point is to stop mindless spamming: money alone no longer gates how
        // fast a single unit can be poured onto the field.
        //
        // A FLAT RULE FOR EVERY UNIT, deliberately. UnitDefinition.MaxCharges and
        // UnitDefinition.CooldownMs still carry the older price-scaled formula
        // (max(1, 25/price) charges, price*COOLDOWN_PER_DOLLAR ms) which would hand a $3
        // tier-1 eight charges and a $23,000 tier-8 exactly one. Those fields are NOT what
        // the live rule reads -- if they are ever revived, this is the code to reconcile
        // with.

        /// <summary>Charges a unit holds when full. Also the value it starts a game at.</summary>
        public const int UnitMaxCharges = 5;

        /// <summary>How long one charge takes to come back.</summary>
        public const int UnitChargeRegenMs = 1000;

        /// <summary>
        /// Charges spent so far, keyed by unit id. ABSENT MEANS FULL -- the dictionary only
        /// holds units that have been used.
        ///
        /// Lazy rather than seeded at game start because a PlayerState does not know its
        /// Team when it is constructed (only the hub assigns it, and the time-machine
        /// constructor never does), so there is no point at which a "fill every roster
        /// entry" loop could run correctly for every caller. Treating a missing key as full
        /// makes every construction path -- live game, timeSkip, rollout clone, a bare
        /// `new GameState()` in a harness -- correct with no initialisation step at all.
        /// </summary>
        public Dictionary<string, int> UnitCharges { get; set; } = new Dictionary<string, int>();

        /// <summary>Charges <paramref name="unitId"/> currently has, treating absent as full.</summary>
        public int GetUnitCharges(string unitId)
            => unitId != null && UnitCharges.TryGetValue(unitId, out int c) ? c : UnitMaxCharges;

        /// <summary>Whether a purchase of <paramref name="unitId"/> is allowed by charges alone.</summary>
        public bool HasUnitCharge(string unitId) => GetUnitCharges(unitId) > 0;
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
            AutoSpawnLevel = 0;
            AutoSpawnPrice = AutoSpawnPriceFor(1);
            AutoSpawnAccumulator = 0;
            AutoSpawnCycleIndex = 0;
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

        /// <summary>
        /// The economy curve every ladder in the game is priced from:
        /// e^(0.0109x^3 + 0.0011x^2 + 0.4351x + 0.5268), R^2 = 0.9997.
        ///
        /// Investing and repairing walk it one integer step per purchase; the auto-spawner
        /// walks the SAME curve in 0.25 steps starting at 2.5, which is why it has 19 rungs
        /// instead of 8. Factored out when the auto-spawner became the third caller -- the
        /// expression is unchanged from the two copies it replaces, so prices are
        /// bit-identical to before.
        /// </summary>
        private static double EconomyCurve(double x)
            => Math.Pow(Math.E, 0.0109 * Math.Pow(x, 3) + 0.0011 * Math.Pow(x, 2) + 0.4351 * x + 0.5268);

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
            Income = EconomyCurve(InvestmentCount);
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
            double rp = EconomyCurve(RepairCount);
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
