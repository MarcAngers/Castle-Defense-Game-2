using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Bot
{
    /// <summary>
    /// A running estimate of the OPPONENT'S money and income, built only from what a player
    /// can actually see. Added 2026-09-02 to Marc's design.
    ///
    /// WHY IT EXISTS. From his three recorded games: in both he won, the bot reached
    /// InvestmentCount 8 -- max income, ARMAGEDDON the only rung left -- and then finished
    /// holding $65,253 and $77,332 against a $121,221 rung. It never knew it was losing the
    /// race. `hasIncomeAdvantage` already reads `enemy.Income`, but in those games BOTH sides
    /// finish at 2500, so that signal is flat at exactly the moment it matters. The
    /// discriminating quantity is the opponent's MONEY.
    ///
    /// THE FAIRNESS RULE. `enemy.Money`, `enemy.Income`, `enemy.InvestmentCount` and
    /// `enemy.InvestmentPrice` are NEVER read. A human cannot see them, so neither does this.
    /// Everything used below is either drawn on screen or visible on the field:
    ///
    ///   - castle max HP is on the enemy's HP bar, and RepairCount falls straight out of it
    ///   - the auto-spawner's LEVEL NUMBER IS PAINTED ON THE MACHINE (View.drawAutoSpawner)
    ///   - a gadget cast is animated, and its tier is visible from the effect
    ///   - every unit that reaches the field is visible, and its cost is its roster row
    ///
    /// THE MODEL (Marc's, and it is better than the alternative it replaced). Simulate a money
    /// balance from the known opening position, accrue income on the engine's own schedule,
    /// subtract every observed purchase, credit gadget income, and ASSUME THEY INVEST THE
    /// MOMENT THEY CAN AFFORD TO.
    ///
    /// Assume-ASAP works at both extremes, and the reason is the spending subtraction rather
    /// than the assumption:
    ///   - a SPAM bot pours everything into units, so the balance never reaches the rung and
    ///     the tracker correctly infers it has never invested;
    ///   - a PATIENT investor spends almost nothing, so the balance climbs and the rungs get
    ///     credited. An estimator that inferred income only FROM SPENDING -- the design this
    ///     replaced -- would report income 2 for such an opponent after five minutes, which is
    ///     exactly the opponent who wins the ARMAGEDDON race.
    ///
    /// KNOW WHICH WAY EACH NUMBER ERRS, because assume-ASAP is not neutral. If they COULD have
    /// invested but held the cash, this subtracts a rung they never bought. So by construction:
    ///   - <see cref="Income"/> is an UPPER bound -- safe for "am I losing the race, save harder"
    ///   - <see cref="Money"/> is a LOWER bound -- so do NOT read it as "they cannot afford to
    ///     punish me". That reading is the one way this makes the bot reckless.
    ///
    /// THE LADDER IS WALKED, NOT COPIED. Every price comes from a real PlayerState via
    /// ApplyInvestmentStep / ApplyRepairStep / ApplyAutoSpawnStep. The first draft of this file
    /// reimplemented the curve and had the repair price off by one rung within ten minutes --
    /// which is the drift CLAUDE.md records the time-machine constructor causing. There is one
    /// source of truth for these formulas and this is not it.
    ///
    /// VALIDATED, NOT ASSUMED. `--economy-tracker-check` plays real games and reports the error
    /// against the engine's true values.
    /// </summary>
    public sealed class OpponentEconomy
    {
        private readonly int _enemySide;

        /// <summary>
        /// The simulated opponent. A real PlayerState so every price and every step comes from
        /// the same code the engine charges them with -- see the class note.
        /// </summary>
        private readonly PlayerState _sim = new PlayerState();

        public double Money => _sim.Money;
        public double Income => _sim.Income;
        public double InvestmentPrice => _sim.InvestmentPrice;
        public int InvestmentCount => _sim.InvestmentCount;
        public bool ArmageddonAssumed => _sim.ArmageddonUsed;

        /// <summary>Gross spend we have watched them make. Diagnostic.</summary>
        public double SpendSeen { get; private set; }
        /// <summary>Gadget income we have credited them (cash). Diagnostic.</summary>
        public double IncomeSeen { get; private set; }

        private long _lastTick = -1;
        private int _repairCount;
        private int _autoSpawnLevel;
        private readonly HashSet<Guid> _seen = new HashSet<Guid>();

        // Free units still expected, per TIER. A unit matching an outstanding expectation is
        // not charged for. Mirrors the engine's free-spawn generators rather than guessing.
        private readonly Dictionary<int, int> _freeOwed = new Dictionary<int, int>();

        // The auto-spawner's own accumulator, mirrored. INTEGER, counting ticks, for the same
        // reason the engine's is: the rates do not divide the tick rate, and the fractional
        // form was measurably wrong there (0.97/s where it wanted 1.00).
        private int _autoAcc;
        private int _autoCycleIndex;

        public OpponentEconomy(int enemySide) => _enemySide = enemySide;

        /// <summary>Charge an observed gadget cast, and credit what it pays back.</summary>
        public void ObserveGadgetCast(GadgetDefinition def)
        {
            if (def == null) return;
            Spend(def.Cost);

            // CASH PAYS BACK, AND IT IS NOT SMALL. cash_3 fires EIGHT staggered payouts of
            // BaseValue for one cost -- 8 x $1,500 = $12,000 for $7,800 -- and cash is White's
            // signature, which the strongest measured loadout plays. Tracking spend without
            // this under-credits a cash opponent by five figures a game.
            if (def.Id != null && def.Id.StartsWith("cash"))
            {
                double paid = (def.Level >= 3 ? 8 : 1) * def.BaseValue;
                _sim.Money += paid;
                IncomeSeen += paid;
            }

            // Reinforcements hands them five FREE units of tier BaseValue, so those must not
            // be charged for when they walk on.
            if (def.Id != null && def.Id.StartsWith("reinforcements"))
                OweFree((int)def.BaseValue, 5);
        }

        private void Spend(double amount)
        {
            if (amount <= 0) return;
            _sim.Money -= amount;
            SpendSeen += amount;
        }

        private void OweFree(int tier, int count)
        {
            if (tier < 1) return;
            _freeOwed.TryGetValue(tier, out int n);
            _freeOwed[tier] = n + count;
        }

        private bool ClaimFree(int tier)
        {
            if (_freeOwed.TryGetValue(tier, out int n) && n > 0)
            {
                _freeOwed[tier] = n - 1;
                return true;
            }
            return false;
        }

        public void Update(GameEngine engine)
        {
            var state = engine._state;
            var them = _enemySide == 1 ? state.Player1 : state.Player2;
            long now = state.CurrentTick;
            if (_lastTick < 0) { _lastTick = now; return; }

            for (long t = _lastTick + 1; t <= now; t++)
            {
                // Income lands on the ENGINE's schedule (INCOME_FREQUENCY = 30), not on a
                // decision boundary, so this is stepped rather than approximated.
                if (t % 30 == 0) _sim.Money += _sim.Income;

                // The opening squad: OpeningSquadSize free tier-1 on ticks 1, 31, 61, ...
                if (t >= 1 && t <= (GameEngine.OpeningSquadSize - 1) * 30 + 1 && (t - 1) % 30 == 0)
                    OweFree(1, 1);

                // Mirror the auto-spawner's generator so its free stream is never charged for.
                int perSecond = PlayerState.AutoSpawnUnitsPerSecond(_autoSpawnLevel);
                if (perSecond > 0)
                {
                    _autoAcc += perSecond;
                    while (_autoAcc >= GameEngine.TICKS_PER_SECOND)
                    {
                        _autoAcc -= GameEngine.TICKS_PER_SECOND;
                        var cycle = PlayerState.AutoSpawnCycle(_autoSpawnLevel);
                        if (cycle.Count == 0) break;
                        OweFree(cycle[((_autoCycleIndex % cycle.Count) + cycle.Count) % cycle.Count], 1);
                        _autoCycleIndex++;
                    }
                }
            }
            _lastTick = now;

            // --- REPAIRS: exactly recoverable from the HP bar ---------------------------
            // PreviewRepairStep sets nextMax = 1000 + 11000 * (RepairCount + 1), so the count
            // inverts exactly. A fresh castle is 2000 and inverts to 0.
            int seenRepairs = them.CastleMaxHealth >= 12000
                ? (them.CastleMaxHealth - 1000) / 11000 : 0;
            while (_repairCount < seenRepairs)
            {
                Spend(_sim.RepairPrice);
                _sim.ApplyRepairStep();     // advances RepairPrice exactly as the engine does
                _repairCount++;
            }

            // --- AUTO-SPAWNER: the level is painted on the machine -----------------------
            while (_autoSpawnLevel < them.AutoSpawnLevel)
            {
                Spend(_sim.AutoSpawnPrice);
                _sim.ApplyAutoSpawnStep();
                _autoSpawnLevel++;
                _autoCycleIndex = 0;        // ApplyAutoSpawnStep restarts the pattern
            }

            // --- UNITS: every one that reaches the field is visible ----------------------
            var roster = GameDataManager.Teams.Find(t => t.Color == them.Team)?.Roster;
            if (roster != null)
            {
                for (int i = 0; i < state.Units.Count; i++)
                {
                    var u = state.Units[i];
                    if (u.Side != _enemySide) continue;
                    if (!_seen.Add(u.InstanceId)) continue;
                    if (ClaimFree(u.Tier)) continue;     // opening squad / auto-spawn / reinforcements
                    var def = roster.Find(d => d.Id == u.DefinitionId);
                    if (def != null) Spend(def.Cost);
                }
            }

            // --- INVESTMENT: assume they take a rung the moment they can afford it -------
            // A loop, not an if: a cash payout can cross more than one rung at once.
            while (!_sim.ArmageddonUsed && _sim.Money >= _sim.InvestmentPrice)
            {
                _sim.Money -= _sim.InvestmentPrice;
                if (_sim.InvestmentCount >= PlayerState.ArmageddonInvestmentCount)
                {
                    // The rung IS ARMAGEDDON -- income and price stay where they are, exactly
                    // as GameEngine.Invest leaves them.
                    _sim.ArmageddonUsed = true;
                    break;
                }
                _sim.ApplyInvestmentStep();
            }

            // An unobserved spend can only push the estimate low, never negative.
            if (_sim.Money < 0) _sim.Money = 0;
        }
    }
}
