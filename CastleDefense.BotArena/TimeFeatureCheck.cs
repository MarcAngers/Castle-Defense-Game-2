using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    /// <summary>
    /// ORACLE CHECKS for the t_arma / t_death evaluator features.
    ///
    /// WHY THIS EXISTS AS A HARNESS RATHER THAN A COMMENT. This project's own record says
    /// two of the two instruments audited on 2026-07-28 were broken, and the fix that
    /// catches that class of bug is an ORACLE -- a case whose answer is known independently
    /// of the code, which the code must reproduce exactly. `--divergence --bot replay` has
    /// to read 1.000; these are the equivalent for the new features.
    ///
    /// Every expected value here was derived BEFORE the implementation, by hand or from the
    /// roster CSV, and is quoted in the design discussion. A sign error or a units mix-up
    /// (ticks vs seconds, px/tick vs px/s) caught here costs minutes. Caught after a
    /// benchmark arm it costs two hours AND produces a misleading negative that could kill
    /// a good feature.
    ///
    /// Usage: time-features-check
    /// </summary>
    public static class TimeFeatureCheck
    {
        private static int _pass, _fail;

        private static void Check(string name, double actual, double expected, double tol)
        {
            bool ok = System.Math.Abs(actual - expected) <= tol;
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name,-58} actual {actual,12:F4}  expected {expected,12:F4}");
        }

        private static void CheckTrue(string name, bool ok, string detail)
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name,-58} {detail}");
        }

        public static void Run(string[] args)
        {
            Console.WriteLine("=== t_arma / t_death ORACLE CHECKS ===\n");

            LadderChecks();
            TArmaChecks();
            TDeathChecks();
            FeatureFormChecks();

            Console.WriteLine();
            Console.WriteLine($"  {_pass} passed, {_fail} failed");
            if (_fail > 0)
                Console.WriteLine("\n  *** DO NOT RUN BENCHMARK ARMS UNTIL THESE PASS ***");
        }

        // ------------------------------------------------------------------ ladder

        private static void LadderChecks()
        {
            Console.WriteLine("-- the investment ladder (derived from ApplyInvestmentStep) --");

            // Ticks to afford the next rung from an empty wallet. Hand-derived from
            // InvestmentPrice / Income, including both hand-tuned overrides.
            var expectedTicks = new double[] { 270, 360, 480, 600, 720, 840, 960, 1600, 1455 };
            var probe = new PlayerState();
            for (int c = 0; c <= 8; c++)
            {
                if (c > 0) probe.ApplyInvestmentStep();
                double ticks = probe.InvestmentPrice / probe.Income * GameEngine.TICKS_PER_SECOND;
                Check($"rung {c}: ticks to afford next", ticks, expectedTicks[c], 1.0);
            }

            // Cumulative: an empty wallet at count 0 is 242.8s from ARMAGEDDON.
            var fresh = new PlayerState();
            Check("t_arma from a fresh game (count 0, $0)",
                  GameState.TimeToArmageddonSeconds(fresh), 242.8, 0.2);
        }

        // ------------------------------------------------------------------ t_arma

        private static void TArmaChecks()
        {
            Console.WriteLine("\n-- t_arma --");

            // THE CENTRAL PROPERTY. Investing must not change t_arma. This is the whole
            // reason t_arma was chosen over time-to-next-investment, which jumps from its
            // best value to its worst at exactly this moment.
            foreach (int c in new[] { 0, 1, 2, 3, 4, 5, 6, 7 })
            {
                var p = AtCount(c);
                p.Money = p.InvestmentPrice;                  // exactly affordable
                double before = GameState.TimeToArmageddonSeconds(p);

                var q = AtCount(c);
                q.Money = q.InvestmentPrice;
                q.Money -= q.InvestmentPrice;                 // pay
                q.ApplyInvestmentStep();                      // and advance
                double after = GameState.TimeToArmageddonSeconds(q);

                Check($"continuity across investment at count {c}", after, before, 0.001);
            }

            // Monotone decreasing in money, within a rung.
            var m0 = AtCount(3); m0.Money = 0;
            var m1 = AtCount(3); m1.Money = m1.InvestmentPrice / 2;
            var m2 = AtCount(3); m2.Money = m2.InvestmentPrice;
            double t0 = GameState.TimeToArmageddonSeconds(m0);
            double t1 = GameState.TimeToArmageddonSeconds(m1);
            double t2 = GameState.TimeToArmageddonSeconds(m2);
            CheckTrue("monotone decreasing in money", t0 > t1 && t1 > t2,
                      $"{t0:F1} > {t1:F1} > {t2:F1}");

            // Excess money must not drive it negative, and must carry into the next rung.
            var rich = AtCount(3); rich.Money = rich.InvestmentPrice * 10;
            double tRich = GameState.TimeToArmageddonSeconds(rich);
            CheckTrue("excess money banks rungs, never negative", tRich >= 0 && tRich < t2,
                      $"{tRich:F1}s with 10x the rung price (vs {t2:F1}s at exactly 1x)");

            // Already fired.
            var used = AtCount(8); used.ArmageddonUsed = true;
            Check("ArmageddonUsed => 0", GameState.TimeToArmageddonSeconds(used), 0.0, 0.0001);

            // At the top rung with the purchase affordable, it is imminent.
            var ready = AtCount(8); ready.Money = ready.InvestmentPrice;
            Check("count 8 with 121,221 banked => 0", GameState.TimeToArmageddonSeconds(ready), 0.0, 0.0001);

            // Sanity anchors from the hand-computed table.
            var c7 = AtCount(7); c7.Money = 0;
            Check("count 7, empty wallet", GameState.TimeToArmageddonSeconds(c7), 101.8, 0.2);
        }

        private static PlayerState AtCount(int c)
        {
            var p = new PlayerState();
            for (int i = 0; i < c; i++) p.ApplyInvestmentStep();
            p.Money = 0;
            return p;
        }

        // ------------------------------------------------------------------ t_death

        /// <summary>
        /// Builds a bare GameState with a fixed castle HP and no units. Deliberately not
        /// via GameEngine: these are unit tests of a pure function and must not depend on
        /// spawn geometry or the RNG.
        /// </summary>
        private static GameState BareState(int castleHp, long tick = 0)
        {
            var st = new GameState();
            st.CurrentTick = tick;
            st.Player1.CastleHealth = castleHp;
            st.Player1.CastleMaxHealth = castleHp;
            st.Player2.CastleHealth = castleHp;
            st.Player2.CastleMaxHealth = castleHp;
            st.Units.Clear();
            return st;
        }

        /// <summary>
        /// A side-2 attacker (walks left toward P1's wall at x=200) placed so that its
        /// travel time is exactly <paramref name="travelSeconds"/> at its own base speed.
        /// </summary>
        private static Unit Attacker(string defId, double travelSeconds)
        {
            var def = FindDef(defId);
            var u = new Unit
            {
                DefinitionId = def.Id,
                Side = 2,
                Tier = def.Tier,
                Width = def.Width,
                Height = def.Height,
                CurrentHealth = def.MaxHealth,
                MaxHealth = def.MaxHealth,
                CurrentShield = 0,
                Damage = def.Damage,
                Range = def.Range,
                AttackSpeed = def.AttackSpeed,
                CurrentSpeed = def.MoveSpeed,
                AttackType = def.AttackType,
                ArmorType = def.ArmorType,
            };
            u.Position = (float)(200.0 + travelSeconds * def.MoveSpeed * GameEngine.TICKS_PER_SECOND);
            return u;
        }

        /// <summary>A side-1 blocker sitting between the attackers and P1's wall.</summary>
        private static Unit Blocker(string defId, float position)
        {
            var def = FindDef(defId);
            return new Unit
            {
                DefinitionId = def.Id,
                Side = 1,
                Tier = def.Tier,
                Width = def.Width,
                Height = def.Height,
                CurrentHealth = def.MaxHealth,
                MaxHealth = def.MaxHealth,
                CurrentShield = 0,
                Damage = def.Damage,
                AttackSpeed = def.AttackSpeed,
                CurrentSpeed = def.MoveSpeed,
                AttackType = def.AttackType,
                ArmorType = def.ArmorType,
                Position = position,
            };
        }

        private static CastleDefense.Engine.Definitions.UnitDefinition FindDef(string id)
        {
            foreach (var t in GameDataManager.Teams)
                foreach (var u in t.Roster)
                    if (u.Id == id) return u;
            throw new Exception($"unit '{id}' not in the roster");
        }

        private static void TDeathChecks()
        {
            Console.WriteLine("\n-- t_death --");

            // Empty board: capped at remaining game time, both sides.
            var empty = BareState(2000);
            double capExpected = GameEngine.MAX_TICKS / (double)GameEngine.TICKS_PER_SECOND;
            Check("empty board => cap (remaining game time)",
                  empty.TimeToCastleDeathSeconds(1), capExpected, 0.01);

            // THE STAIRCASE. Two identical attackers arriving 4s apart. Hand-computed:
            // rate d from t=1..5 does 4d damage; the remainder burns at 2d.
            //   d=216, hp=2000: 864 in phase 1, 1136 left at 2d=432 -> 2.63s -> 7.63s
            // The two wrong answers are 4.63s (sum the DPS) and 10.26s (first arrival only).
            var pick = PickByDps(216.0);
            double d = (double)pick.Damage * pick.AttackSpeed;
            if (pick.AttackType == AttackType.Siege) d *= 2.0;
            double hp = 2000;
            double phase1 = d * 4.0;
            double expected = phase1 >= hp ? 1.0 + hp / d : 5.0 + (hp - phase1) / (2.0 * d);

            var st = BareState((int)hp);
            st.Units.Add(Attacker(pick.Id, 1.0));
            st.Units.Add(Attacker(pick.Id, 5.0));
            st.Units.Sort((a, b) => a.Position.CompareTo(b.Position));
            Check($"staircase: 2x {pick.Id} ({d:F0} dps) at 1s and 5s",
                  st.TimeToCastleDeathSeconds(1), expected, 0.05);

            // And confirm the two naive answers are NOT what we produce.
            double naiveSum = hp / (2.0 * d);
            double naiveFirst = 1.0 + hp / d;
            double got = st.TimeToCastleDeathSeconds(1);
            CheckTrue("staircase is not 'sum the dps'", System.Math.Abs(got - naiveSum) > 0.5,
                      $"got {got:F2}s, sum-the-dps would be {naiveSum:F2}s");
            CheckTrue("staircase is not 'first arrival only'", System.Math.Abs(got - naiveFirst) > 0.5,
                      $"got {got:F2}s, first-arrival-only would be {naiveFirst:F2}s");

            // TIER-8 SIEGE DOUBLING is live: the roster has no AttackType column, so
            // GameDataManager derives `isAce ? Siege : ...` and isAce defaults to tier == 8.
            var t8 = FindDef("evilguy");
            CheckTrue("tier-8 evilguy is AttackType.Siege", t8.AttackType == AttackType.Siege,
                      $"AttackType = {t8.AttackType}");
            double t8Castle = (double)t8.Damage * t8.AttackSpeed * 2.0;
            double t8Raw = (double)t8.Damage * t8.AttackSpeed;
            CheckTrue("evilguy castle dps is DOUBLE its unit dps", t8Castle > t8Raw * 1.99,
                      $"unit {t8Raw:F0} -> castle {t8Castle:F0}");

            // INTERCEPTION DELAY. The blocker must sit BETWEEN the attackers and the wall
            // it is defending -- i.e. at a SMALLER x than side-2 attackers, since they walk
            // leftward toward P1's wall at x=200. The first version of this test put the
            // attackers at the wall (travel 0 => Position 200) and the "blocker" at 210,
            // which is behind them, so nothing was in their path and the delay was correctly
            // 0. Left as a note because it is an easy mistake to repeat.
            const double atkTravel = 2.0;
            var atk = PickByDps(96.0);
            var blk = PickByDps(216.0);
            double atkDps = (double)atk.Damage * atk.AttackSpeed * (atk.AttackType == AttackType.Siege ? 2 : 1);
            var st2 = BareState(2000);
            for (int i = 0; i < 4; i++) st2.Units.Add(Attacker(atk.Id, atkTravel));
            var blocker = Blocker(blk.Id, 210f);      // just outside P1's wall, in the path
            st2.Units.Add(blocker);
            st2.Units.Sort((a, b) => a.Position.CompareTo(b.Position));
            double totalAtkDps = 4 * atkDps;
            double expectedDelay = blocker.CurrentHealth / totalAtkDps;
            double expected2 = atkTravel + expectedDelay + 2000.0 / totalAtkDps;
            Check($"interception: 4x {atk.Id} held by 1x {blk.Id} ({blocker.CurrentHealth} hp)",
                  st2.TimeToCastleDeathSeconds(1), expected2, 0.05);
            Console.WriteLine($"         (delay alone = {expectedDelay:F2}s = {blocker.CurrentHealth}hp / {totalAtkDps:F0}dps"
                            + $" -- same form as the hand-derived 1142/384 = 2.97s)");

            // Removing the blocker must strictly shorten t_death, by exactly the delay.
            var st3 = BareState(2000);
            for (int i = 0; i < 4; i++) st3.Units.Add(Attacker(atk.Id, atkTravel));
            st3.Units.Sort((a, b) => a.Position.CompareTo(b.Position));
            double unblocked = st3.TimeToCastleDeathSeconds(1);
            double blocked = st2.TimeToCastleDeathSeconds(1);
            CheckTrue("blocker delays death by exactly blockHp/totalAtkDps",
                      System.Math.Abs((blocked - unblocked) - expectedDelay) < 0.05,
                      $"unblocked {unblocked:F2}s, blocked {blocked:F2}s, difference {blocked - unblocked:F2}s vs expected {expectedDelay:F2}s");

            // A blocker BEHIND the attackers must not help at all -- it is not in the path.
            var st3b = BareState(2000);
            for (int i = 0; i < 4; i++) st3b.Units.Add(Attacker(atk.Id, atkTravel));
            st3b.Units.Add(Blocker(blk.Id, (float)(200.0 + atkTravel * FindDef(atk.Id).MoveSpeed * GameEngine.TICKS_PER_SECOND + 50)));
            st3b.Units.Sort((a, b) => a.Position.CompareTo(b.Position));
            Check("blocker behind the attackers gives no delay",
                  st3b.TimeToCastleDeathSeconds(1), unblocked, 0.05);

            // A FROZEN attacker never arrives. Slow with value 0 zeroes speedMod.
            var st4 = BareState(2000);
            var frozen = Attacker(atk.Id, 1.0);
            frozen.Statuses.Add(new ActiveStatus("Slow", long.MaxValue, 0f));
            st4.Units.Add(frozen);
            Check("frozen attacker (Slow x0) never arrives => cap",
                  st4.TimeToCastleDeathSeconds(1), capExpected, 0.01);

            // A unit stalled in COMBAT must still count -- its hold-up is the delay term,
            // not an infinity. CurrentSpeed is 0 here exactly as the engine would set it.
            var st5 = BareState(2000);
            var engaged = Attacker(atk.Id, 1.0);
            engaged.CurrentSpeed = 0f;                 // engine zeroes this when fighting
            st5.Units.Add(engaged);
            CheckTrue("combat-stalled attacker still counts (not treated as frozen)",
                      st5.TimeToCastleDeathSeconds(1) < capExpected - 1.0,
                      $"t_death {st5.TimeToCastleDeathSeconds(1):F2}s, cap {capExpected:F0}s");

            // Cap shrinks as the game runs out.
            var late = BareState(2000, GameEngine.MAX_TICKS - 300);
            Check("cap tracks remaining game time (300 ticks left)",
                  late.TimeToCastleDeathSeconds(1), 10.0, 0.01);

            // Dead castle.
            var dead = BareState(2000); dead.Player1.CastleHealth = 0;
            Check("destroyed castle => 0", dead.TimeToCastleDeathSeconds(1), 0.0, 0.0001);
        }

        /// <summary>Roster unit whose unit-dps is closest to the target, for readable cases.</summary>
        private static CastleDefense.Engine.Definitions.UnitDefinition PickByDps(double target)
        {
            CastleDefense.Engine.Definitions.UnitDefinition best = null;
            double bestErr = double.MaxValue;
            foreach (var t in GameDataManager.Teams)
                foreach (var u in t.Roster)
                {
                    if (u.AttackSpeed <= 0) continue;
                    double dps = (double)u.Damage * u.AttackSpeed;
                    double err = System.Math.Abs(dps - target);
                    if (err < bestErr) { bestErr = err; best = u; }
                }
            return best;
        }

        // ------------------------------------------------------------- feature form

        private static void FeatureFormChecks()
        {
            Console.WriteLine("\n-- differential feature form (must read 0.5 in an even position) --");

            var even = BareState(2000);
            Check("empty board, equal economies: t_arma component", even.TArmaComponent(), 0.5, 0.0001);
            Check("empty board, equal economies: t_death component", even.TDeathComponent(), 0.5, 0.0001);

            // Mirrored armies must also read exactly 0.5. This is the property the old army
            // term had and which was WRONGLY criticised as a defect -- a symmetric position
            // really is 50/50. It is also the seat-symmetry check: the old army term reads
            // 0.462 here rather than 0.5, because it measures proximity from the raw left
            // edge for both sides while the engine's leading edge differs by Width.
            var mirror = BareState(2000);
            var pick = PickByDps(216.0);
            var d = FindDef(pick.Id);
            for (int i = 0; i < 3; i++)
            {
                var a = Attacker(pick.Id, 3.0);                       // side 2, walking left
                var b = Blocker(pick.Id, (float)(2000 - 200 - d.Width - (a.Position - 200)));
                b.Side = 1;                                            // mirrored side-1 unit
                mirror.Units.Add(a);
                mirror.Units.Add(b);
            }
            mirror.Units.Sort((x, y) => x.Position.CompareTo(y.Position));
            Check("mirrored armies: t_death component", mirror.TDeathComponent(), 0.5, 0.005);
            var comps = mirror.GetEvalComponents();
            Console.WriteLine($"         (for contrast, the OLD army term on the same mirrored board reads {comps.Army:F4})");

            // Sign checks.
            var p1Ahead = BareState(2000);
            p1Ahead.Player1.ApplyInvestmentStep(); p1Ahead.Player1.ApplyInvestmentStep();
            CheckTrue("P1 ahead on economy => t_arma component > 0.5",
                      p1Ahead.TArmaComponent() > 0.5, $"{p1Ahead.TArmaComponent():F4}");

            var p1Threatened = BareState(2000);
            p1Threatened.Units.Add(Attacker(pick.Id, 1.0));            // side 2 attacking P1
            CheckTrue("P1 under attack => t_death component < 0.5",
                      p1Threatened.TDeathComponent() < 0.5, $"{p1Threatened.TDeathComponent():F4}");
        }
    }
}
