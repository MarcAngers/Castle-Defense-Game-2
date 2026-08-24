using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;

namespace CastleDefense.Engine.Models
{
    /// <summary>
    /// TIME-TO-TERMINAL-STATE evaluator features, added 2026-08-19.
    ///
    /// The deployed six features are all STOCKS: castle HP, income, money, army pressure,
    /// gadget readiness, repair accessibility. Nothing in the vector is a RATE, so the
    /// evaluator can see that a castle is at 40% but not whether it is stable there or
    /// losing 5%/second -- and the rollout truncates at a fixed horizon, so the leaf
    /// routinely lands mid-battle where the derivative carries the information.
    ///
    /// These two features are the same idea on the two axes that actually end the game:
    ///
    ///   t_arma  -- seconds until I can fire ARMAGEDDON, the economic terminal state.
    ///   t_death -- seconds until my castle falls, the military terminal state.
    ///
    /// BOTH ARE DIFFERENTIALS at the feature level, matching the house style: every
    /// existing component is Sig(k * (f(p1) - f(p2))) and equals 0.5 in an even position.
    /// Note that computing t_death for both sides gives "time to kill them" for free --
    /// t_kill(me) IS t_death(them) -- so the single differential encodes the whole race and
    /// no separate offensive feature is needed.
    ///
    /// WHY t_arma AND NOT "time to next investment", which was the obvious first idea:
    /// time-to-next-investment RESETS TO ITS WORST VALUE the instant you invest. At
    /// investment 3 with a full wallet it reads 0s; invest, and it reads 24s. A positively
    /// weighted feature of that shape teaches search that investing makes the position
    /// worse -- the same consequence-variable trap that took the 2026-08-05 refit to 34.2%.
    /// Summing to the TERMINAL state instead removes the discontinuity exactly.
    /// </summary>
    public partial class GameState
    {
        private const int ArmaCount = PlayerState.ArmageddonInvestmentCount;   // 8
        private const float TimeFeatureMapWidth = 2000f;
        private const float CastleWallInset = 200f;   // P1's wall at x=200, P2's at 1800

        // -- THE INVESTMENT LADDER, DERIVED RATHER THAN TRANSCRIBED -------------------
        //
        // Driven through PlayerState.ApplyInvestmentStep itself so there is exactly one
        // source of truth. Transcribing the numbers would let them drift silently if the
        // ladder is ever rebalanced, and the ladder has TWO hand-tuned overrides a reader
        // of the general formula would miss: the constructor pins count 0 to price 18 /
        // income 2, and ApplyInvestmentStep pins count 7 to 40000 / 750 and count 8 to
        // 121221 / 2500. The general (4*count + 8) seconds rule covers neither.
        private static readonly double[] LadderPrice = new double[ArmaCount + 1];
        private static readonly double[] LadderIncome = new double[ArmaCount + 1];

        /// <summary>
        /// CumFrom[c] = seconds from an EMPTY wallet at count c through to firing
        /// ARMAGEDDON. CumFrom[ArmaCount + 1] = 0 terminates the recursion.
        /// </summary>
        private static readonly double[] CumFrom = new double[ArmaCount + 2];

        static GameState()
        {
            // PlayerState's constructor is self-contained (no GameDataManager dependency),
            // so driving it here is safe in a static initialiser.
            var probe = new PlayerState();
            LadderPrice[0] = probe.InvestmentPrice;
            LadderIncome[0] = probe.Income;
            for (int c = 1; c <= ArmaCount; c++)
            {
                probe.ApplyInvestmentStep();
                LadderPrice[c] = probe.InvestmentPrice;
                LadderIncome[c] = probe.Income;
            }

            CumFrom[ArmaCount + 1] = 0.0;
            for (int c = ArmaCount; c >= 0; c--)
                CumFrom[c] = CumFrom[c + 1] + LadderPrice[c] / LadderIncome[c];
        }

        /// <summary>
        /// Unit definitions by id. GameEngine builds a per-instance _unitCache the same way,
        /// but GameState has no engine reference and t_death needs BASE MoveSpeed -- which
        /// Unit does not carry (it has CurrentSpeed, the live value). Lazy because
        /// GameDataManager.Initialize() may not have run when a GameState is first touched,
        /// and Lazy is thread-safe by default, which matters: search evaluates leaves on
        /// ~18 threads at once.
        /// </summary>
        private static readonly System.Lazy<Dictionary<string, UnitDefinition>> DefById =
            new System.Lazy<Dictionary<string, UnitDefinition>>(() =>
            {
                var d = new Dictionary<string, UnitDefinition>();
                foreach (var team in GameDataManager.Teams)
                    foreach (var u in team.Roster)
                        if (!d.ContainsKey(u.Id)) d[u.Id] = u;
                for (int lvl = 1; lvl <= 3; lvl++)
                {
                    var w = GameDataManager.WallDefinition(lvl);
                    string id = lvl == 1 ? "wall" : "wall_" + lvl;
                    if (!d.ContainsKey(id)) d[id] = w;
                }
                return d;
            });

        // Per-thread scratch for the arrival sweep. Allocating per call would mean ~1.35M
        // allocations per benchmark arm; the existing army loop allocates nothing and this
        // must not regress that.
        [System.ThreadStatic] private static double[] _arrival;
        [System.ThreadStatic] private static double[] _arrivalDps;
        [System.ThreadStatic] private static double[] _arrivalBlockHp;

        // ============================================================================
        //  t_arma
        // ============================================================================

        /// <summary>
        /// Seconds until this player could fire ARMAGEDDON, assuming they spend nothing else
        /// from now on. A POTENTIAL, not a forecast -- the zero-spending assumption is benign
        /// here because income accrues unless the player chooses to spend, so the error is
        /// one-sided and under their own control.
        ///
        /// Continuous across an investment by construction: paying a rung converts saved
        /// money into a completed rung, which on this clock is exactly a wash.
        /// </summary>
        public static double TimeToArmageddonSeconds(PlayerState p)
        {
            if (p.ArmageddonUsed) return 0.0;

            int c = p.InvestmentCount;
            if (c < 0) c = 0;
            if (c > ArmaCount) c = ArmaCount;
            double m = p.Money;

            // Bank any rungs already affordable. Without this, money in excess of the
            // current rung price would drive the first term negative and the feature would
            // stop being monotone in money.
            while (c < ArmaCount && m >= LadderPrice[c]) { m -= LadderPrice[c]; c++; }

            double first = (LadderPrice[c] - m) / LadderIncome[c];
            if (first < 0) first = 0;                 // count 8 with the purchase affordable
            return first + CumFrom[c + 1];
        }

        // ============================================================================
        //  t_death
        // ============================================================================

        /// <summary>
        /// Seconds until <paramref name="side"/>'s castle is destroyed by what is on the
        /// board right now, capped at the remaining game time.
        ///
        /// Each enemy unit contributes castle-DPS starting at its own arrival time, so the
        /// damage rate is a STAIRCASE and the answer is a piecewise-linear solve rather than
        /// hp/totalDps. That distinction is large: two 216-DPS units arriving at 1s and 5s
        /// against a 2000 HP castle give 7.63s, where "sum the DPS" says 4.63s (39% too
        /// pessimistic) and "first arrival only" says 10.26s (34% too optimistic).
        ///
        /// INTERCEPTION is modelled as a DELAY, not as a fight: an attacker is held up by
        /// friendlyHpInItsPath / TOTAL attacking castle-DPS seconds. That is deliberately not
        /// a combat model -- resolving who wins would mean re-implementing armour, attack
        /// types and knockback inside the leaf, and duplicated simulation drifts from the
        /// engine. The crude proxy is exact in the case that matters: 4 tier-5 units (96 DPS
        /// each) against a 1142 HP tier-6 blocker gives 1142/384 = 2.97s, which is precisely
        /// when that blocker dies.
        ///
        /// THE DENOMINATOR IS THE WHOLE FORCE, NOT THE UNIT'S OWN DPS. The first version
        /// used the unit's own DPS and the oracle caught it: with four attackers it produced
        /// a 4x delay, because each one was made to chew through the blocker alone. Attackers
        /// focus fire, so the blocking mass falls at their combined rate and all of them are
        /// freed at once. blockHp stays PER UNIT, which is what preserves positional
        /// information -- a friendly unit standing behind the attackers is not in their path
        /// and correctly buys nothing.
        ///
        /// KNOWN BIAS: friendly units contribute their HP but not their DAMAGE, so a winning
        /// blocker's hold time is understated and t_death is systematically PESSIMISTIC.
        /// Tolerable in a differential (both sides computed identically), but it means the
        /// sigmoid scale must be fit against the differential's real distribution rather than
        /// against absolute seconds.
        ///
        /// The cap is REMAINING GAME TIME rather than an arbitrary clamp: the game ends at
        /// MAX_TICKS and is awarded on castle HP, so "t_death exceeds the time left" is the
        /// true statement "I do not lose by castle destruction".
        /// </summary>
        public double TimeToCastleDeathSeconds(int side)
        {
            double cap = (GameEngine.MAX_TICKS - CurrentTick) / (double)GameEngine.TICKS_PER_SECOND;
            if (cap < 0) cap = 0;

            var me = side == 1 ? Player1 : Player2;
            if (me.CastleHealth <= 0) return 0.0;

            // Castle invulnerability (Divine_3) postpones every arrival equally.
            double invulnDelay = 0.0;
            if (me.IsInvulnerable && me.InvulnerableUntilTick > CurrentTick)
                invulnDelay = (me.InvulnerableUntilTick - CurrentTick) / (double)GameEngine.TICKS_PER_SECOND;

            int n = Units.Count;
            if (n == 0) return cap;
            if (_arrival == null || _arrival.Length < n)
            {
                _arrival = new double[n + 16];
                _arrivalDps = new double[n + 16];
                _arrivalBlockHp = new double[n + 16];
            }

            // Total defender effective HP, needed for the right-hand path sums below.
            double totalDefHp = 0.0;
            for (int i = 0; i < n; i++)
            {
                var u = Units[i];
                if (u.Side == side && u.CurrentHealth > 0) totalDefHp += u.CurrentHealth + u.CurrentShield;
            }

            // ONE left-to-right sweep. Units is kept sorted ascending by Position every tick
            // (GameEngine.Tick), so the running defender-HP total IS the HP to the left of
            // the current index, and (total - running) is the HP to its right. No prefix
            // array and no second pass.
            double runningDefHp = 0.0;
            double totalAtkDps = 0.0;
            int k = 0;
            var defs = DefById.Value;

            for (int i = 0; i < n; i++)
            {
                var u = Units[i];
                if (u.CurrentHealth <= 0) continue;

                if (u.Side == side)
                {
                    runningDefHp += u.CurrentHealth + u.CurrentShield;
                    continue;
                }

                if (!defs.TryGetValue(u.DefinitionId, out var def)) continue;
                if (def.AttackSpeed <= 0f) continue;   // walls cannot attack; the engine guards on this too

                // Castle DPS: damage per hit x hits per second, doubled for Siege -- which
                // is LIVE, not dead code. The roster has no AttackType column, so
                // GameDataManager derives it, and `isAce ? Siege : ...` with isAce defaulting
                // to (tier == 8) makes EVERY tier-8 unit Siege. Rage scales castle damage
                // too, matching GameEngine's castle branch.
                double dps = (double)def.Damage * def.AttackSpeed;
                if (def.AttackType == AttackType.Siege) dps *= 2.0;
                if (u.Statuses != null)
                    for (int s = 0; s < u.Statuses.Count; s++)
                        if (u.Statuses[s].Name == "Rage") dps *= u.Statuses[s].Value;
                if (dps <= 0) continue;

                // Effective speed from the unit's OWN statuses rather than from CurrentSpeed.
                // CurrentSpeed is zeroed both by freezing and by being in combat, and those
                // must not be conflated: a frozen unit genuinely is not coming (speed 0 ->
                // never arrives), whereas a unit stalled in combat will resume, and its
                // hold-up is already priced by the interception delay below. Reading
                // CurrentSpeed would double-count the second case as infinite.
                float speedMod = 1.0f;
                if (u.Statuses != null)
                    for (int s = 0; s < u.Statuses.Count; s++)
                    {
                        var st = u.Statuses[s];
                        if (st.Name == "Slow" || st.Name == "Speed") speedMod *= st.Value;
                    }
                double effSpeed = def.MoveSpeed * speedMod;   // px per tick
                if (effSpeed <= 0) continue;                 // frozen: never arrives

                // Distance to the defender's wall, mirroring GetDistanceToEnemyCastle's
                // leading-edge convention exactly (side 1 leads with Position + Width,
                // side 2 leads with Position).
                double dist = u.Side == 1
                    ? (TimeFeatureMapWidth - CastleWallInset) - (u.Position + u.Width)
                    : u.Position - CastleWallInset;
                if (dist < 0) dist = 0;

                double travel = dist / effSpeed / GameEngine.TICKS_PER_SECOND;

                // Friendly HP standing between this attacker and the wall it is walking at.
                // The delay it buys is applied in the pass below, once the whole attacking
                // force's DPS is known.
                _arrival[k] = travel;
                _arrivalDps[k] = dps;
                _arrivalBlockHp[k] = u.Side == 1 ? (totalDefHp - runningDefHp) : runningDefHp;
                totalAtkDps += dps;
                k++;
            }

            if (k == 0) return cap;

            // Resolve travel -> arrival now that the force's combined DPS is known, and drop
            // anything that cannot land before the game ends.
            int kept = 0;
            for (int i = 0; i < k; i++)
            {
                double arrive = _arrival[i] + _arrivalBlockHp[i] / totalAtkDps + invulnDelay;
                if (arrive >= cap) continue;
                _arrival[kept] = arrive;
                _arrivalDps[kept] = _arrivalDps[i];
                kept++;
            }
            k = kept;
            if (k == 0) return cap;

            // Insertion sort: k is small (<= 50/side in practice) and the data arrives nearly
            // sorted, since position correlates with arrival time.
            for (int i = 1; i < k; i++)
            {
                double a = _arrival[i], d = _arrivalDps[i];
                int j = i - 1;
                while (j >= 0 && _arrival[j] > a)
                {
                    _arrival[j + 1] = _arrival[j];
                    _arrivalDps[j + 1] = _arrivalDps[j];
                    j--;
                }
                _arrival[j + 1] = a;
                _arrivalDps[j + 1] = d;
            }

            // Piecewise-linear solve over the damage staircase.
            //
            // THE CASTLE SHIELD IS PLAIN EXTRA HP HERE, and adding it is exact rather than
            // approximate: DamageCastle spends shield and health from one pool at the same
            // rate, with no Siege multiplier on the castle's shield (unlike a unit's) and
            // no separate expiry, so the staircase does not care which half a given point
            // of damage lands in. The one-shot floor is the only thing that distinguishes
            // them, and this model already ignores it.
            double hp = me.CastleHealth + Math.Max(0, me.CastleShield);
            double rate = 0.0, t = 0.0;
            for (int i = 0; i < k; i++)
            {
                if (rate > 0)
                {
                    double dt = _arrival[i] - t;
                    double dmg = rate * dt;
                    if (dmg >= hp) return t + hp / rate;
                    hp -= dmg;
                }
                t = _arrival[i];
                rate += _arrivalDps[i];
            }
            if (rate <= 0) return cap;
            double death = t + hp / rate;
            return death < cap ? death : cap;
        }

        // ============================================================================
        //  Feature form
        // ============================================================================

        /// <summary>
        /// UNFITTED hyperparameters. These live INSIDE the sigmoid and control curvature and
        /// saturation, which is a different job from the fitted weight outside it -- so they
        /// cannot be absorbed by the weight fit and have to be swept separately. 1.0 is a
        /// neutral starting point, not a measured value.
        /// </summary>
        public static float TArmaScale = 1.0f;
        public static float TDeathScale = 1.0f;

        private static float TimeSigmoid(float x) => 1f / (1f + MathF.Exp(-x));

        /// <summary>Lower t_arma is better, so P1 is favoured when its own time is smaller.</summary>
        public float TArmaComponent()
        {
            double t1 = TimeToArmageddonSeconds(Player1);
            double t2 = TimeToArmageddonSeconds(Player2);
            return TimeSigmoid(TArmaScale * (float)(System.Math.Log(t2 + 1.0) - System.Math.Log(t1 + 1.0)));
        }

        /// <summary>Higher t_death is better, so P1 is favoured when it survives longer.</summary>
        public float TDeathComponent()
        {
            double t1 = TimeToCastleDeathSeconds(1);
            double t2 = TimeToCastleDeathSeconds(2);
            return TimeSigmoid(TDeathScale * (float)(System.Math.Log(t1 + 1.0) - System.Math.Log(t2 + 1.0)));
        }
    }
}
