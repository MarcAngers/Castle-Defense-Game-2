using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// WHY a candidate scores what it scores: replicates RolloutSearchBot.Rollout exactly and
    /// decomposes the leaf evaluation into its six weighted components, differenced against a
    /// chosen baseline action.
    ///
    /// The score table from --nuke-repro says WHICH action search prefers. It cannot say why.
    /// EvaluateBoard is sigmoid(sum w_i * (x_i - 0.5)), so the sum is ADDITIVE in logit space:
    /// w_i * (x_i[candidate] - x_i[baseline]) is exactly that component's contribution to the
    /// preference, in units that add up. That is the decomposition printed below.
    ///
    /// FAITHFULNESS IS VERIFIED, NOT ASSUMED. This re-implements the rollout loop rather than
    /// calling the private original, so it prints its own mean score next to the score
    /// RolloutSearchBot reports for the same action and seeds. If those two columns disagree,
    /// the decomposition below them is describing a different computation and must not be
    /// read. Same discipline as --divergence --bot replay having to read exactly 1.000.
    ///
    /// FINDING, FC1462 tick 781, horizon 1600, 12 seeds (faithfulness check: 0.0000 delta on
    /// all 8 candidates). spawnT4 scores 0.6451 against wait 0.5012, and the decomposition
    /// attributes it to hp (-0.2856), money (-0.1992) and army (-0.1886). But the LEAF
    /// STATES show what those numbers are really tracking:
    ///
    ///   action        ownU enemU  money  inv    myHP     enHP
    ///   4 spawn T4     5.0   3.0    422  4.00    1900    10592
    ///   0 wait         0.0   5.0    455  4.00   11979     2000
    ///   10 repair      5.0   0.0    401  4.00   11370     2000
    ///
    /// In the wait line P2 REPAIRS (castle 2000 -> 12000 max, sitting at 11979). In the
    /// spawnT4 line P2 does not repair and P1 does. So search is not comparing "army vs
    /// savings" at all -- it is comparing two futures that differ mainly in WHO REPAIRED.
    ///
    /// A WRONG EXPLANATION WAS RECORDED HERE FIRST AND IS WORTH KEEPING AS A WARNING. It
    /// said "repairing raises your denominator, so your own repair reads as a percentage
    /// DROP." That is FALSE. PlayerState.ApplyRepairStep preserves the percentage and adds
    /// 20 points to it, capped at 100:
    ///     pct = CastleHealth / CastleMaxHealth;
    ///     CastleMaxHealth = 1000 + 11000 * RepairCount;
    ///     CastleHealth = min(CastleMaxHealth * (pct + 0.2), CastleMaxHealth);
    /// A repair can never lower your percentage. Marc caught this from the formula.
    ///
    /// ROOT CAUSE, measured on the IMMEDIATE effect at tick 781 (no rollout, P2 at 100% HP):
    ///
    ///   action      castle after      my%   money    dHp      dMoney   dTotal
    ///   0 wait        2000/2000     100.0%     22   0.0000    0.0000   0.0000
    ///   10 repair    12000/12000    100.0%      2   0.0000   +0.7196  +0.7196
    ///   4 spawn T4    2000/2000     100.0%      4   0.0000   +0.5494  +0.5492
    ///
    /// Repairing SEXTUPLES absolute castle HP, 2,000 -> 12,000, and moves the evaluator's HP
    /// term by EXACTLY 0.0000 -- because hpScore is Sig(3 * (p1HpPct - p2HpPct)), a
    /// PERCENTAGE, and at full HP the +20 points is entirely capped away. Meanwhile the \$20
    /// cost registers as +0.7196 AGAINST us, since money is scored on a log scale and this
    /// is 90% of a \$22 wallet. So the largest single swing in the game is worth literally
    /// nothing to the evaluator while its price is worth a lot.
    ///
    /// The same scale-blindness governs the leaf: 1,900/2,000 (95.0%) and 11,979/12,000
    /// (99.8%) differ by 4.8 percentage points and by 10,079 ABSOLUTE HP -- the difference
    /// between dying to one nuke_2 and shrugging it off. The evaluator sees the 4.8 points.
    ///
    /// Corroboration in the same run: action 10, repair, is the WORST-scoring candidate of
    /// all eight (0.4416). Search does not merely tolerate skipping the repair; it prefers
    /// to, because the repair is all cost and no visible benefit.
    ///
    /// WHY spawnT4 BEATS WAIT -- and the investment-delay theory does NOT survive contact
    /// with the numbers. Investment timing inside the rollout, plus the P2 score at 25/50/75%
    /// of the horizon:
    ///
    ///   action       inv#3 at   inv#4 at  |    q1      q2      q3    leaf
    ///   0 wait        350        900      | 0.8696  0.4033  0.4999  0.5012
    ///   4 spawn T4    470        960      | 0.5833  0.6098  0.6495  0.6451
    ///
    /// The delay IS modelled: investment 3 slips 350 -> 470 ticks, i.e. 120 ticks = 4.0s,
    /// exactly the \$18 / \$4.4-per-second the arithmetic predicts. It is simply SMALL, and by
    /// the leaf both lines sit on invest 4 and income 19.7, so it has been fully repaid and
    /// is invisible at scoring time. "Spending early is a huge investment delay" is not what
    /// the rollout finds; it finds a four-second delay that pays for itself.
    ///
    /// The trajectory is where the preference is actually made. WAIT IS FAR AHEAD EARLY --
    /// 0.8696 at q1 against 0.5833, almost entirely the money term rewarding a fat wallet --
    /// and then COLLAPSES to 0.4033 by q2, when the four doggos land damage and force P2
    /// into a repair. spawnT4 rises monotonically instead: its ringo kills the doggos, P2
    /// keeps the initiative, and the line ends 5 units to 3 with P1's castle chewed to 88%.
    ///
    /// So search is not valuing "a tier-4 unit" and it is not ignoring the economy. It is
    /// buying the early skirmish, and the price it pays -- ending on 1,900 ABSOLUTE HP
    /// instead of 11,979 -- is charged at 4.8 percentage points, i.e. almost nothing. Same
    /// scale-blindness as above, arriving from the other direction.
    ///
    /// NOTE THIS DOES NOT ESTABLISH THE DECISION IS WRONG. Four doggos are inbound and P2 is
    /// on a 2,000 HP castle; buying a defender may well be correct. What it establishes is
    /// WHAT IS BEING TRADED, and that the trade is priced with the castle-HP side missing.
    ///
    /// ── 7A385A TICK 781: THE SAME BUY WITH **ZERO ENEMY UNITS**, AND THE REAL CAUSE ──
    ///
    /// Marc's recollection that he had seen this unprompted was correct. Same tick, same
    /// bot, same \$22.20 / income 4.4 / invest 2 -- but P1 has not spawned anything yet (its
    /// first unit is tick 915), so there is nothing to defend against. spawnT3 (0.6367) and
    /// spawnT4 (0.6315) still beat wait (0.5223). The "it is buying the early skirmish"
    /// explanation from FC1462 therefore does NOT generalise. Leaf states:
    ///
    ///   action        score  ownU enemU   myInc myInv   enInc enInv    my%     en%
    ///   3 spawn T3   0.6367   3.0   5.0    19.7  4.00    19.7  4.00  100.0%   87.5%
    ///   4 spawn T4   0.6315   5.0   3.0    19.7  4.00    19.7  4.00   95.0%   86.6%
    ///   0 wait       0.5223   0.0   0.0    59.9  5.00    59.9  5.00  100.0%  100.0%
    ///   10 repair    0.1896   5.0   5.0    19.7  4.00    59.9  5.00  100.0%  100.0%
    ///
    /// IN THE WAIT LINE BOTH PLAYERS REACH INCOME 59.9 AND INVESTMENT 5. IN THE SPAWN LINES
    /// BOTH END ON 19.7 AND INVESTMENT 4, and dIncome between them is 0.0000.
    ///
    /// **CORRECTION 2026-08-20 -- THE "3x ECONOMY" READING OF THIS TABLE WAS WRONG.** It was
    /// first written up as "spending \$18 costs both sides a full rung and two thirds of their
    /// income, and the evaluator charges zero for it." That reads the income column without
    /// the MONEY column. The wait line holds \$79 at investment 5; the spawn line holds \$422
    /// at investment 4, and rung 4 costs 474 -- so the spawn line is one purchase away from
    /// the rung the wait line already bought. Priced properly, by TimeToArmageddonSeconds,
    /// the wait line is roughly FOUR SECONDS ahead on a ~243-second clock, not three times
    /// richer. The two lines are economically near-identical and the income differential
    /// reading 0.5 in both is CORRECT, not blind.
    ///
    /// What survives: every component IS a differential, so a genuinely symmetric change
    /// would indeed be invisible. What does NOT survive is the claim that this example
    /// demonstrates it. Measured with an absolute yardstick there is barely any economic
    /// difference here to be blind to -- see the ECONOMY BLEND note in RolloutSearchBot,
    /// where an own-side absolute prosperity term separates these two lines by 0.0078.
    ///
    /// The reason is structural and applies to all six components, not just income: every
    /// term is a DIFFERENTIAL between the two players. 59.9-vs-59.9 and 19.7-vs-19.7 both
    /// score exactly 0.5. A MUTUALLY DESTRUCTIVE ACTION IS FREE. The repair row is the
    /// control that proves it: there P2 alone falls to 19.7 while P1 keeps 59.9, the
    /// asymmetry is visible, and income is charged a colossal +2.0832 against P2.
    ///
    /// So the trade search is making is: surrender a 3x economy for both players (invisible,
    /// because mutual) in exchange for a ~12 percentage-point castle-HP edge (visible,
    /// because asymmetric). That is a terrible trade in the real game and a positive one in
    /// the evaluator. Note the evaluator is not wrong AS A WIN-PROBABILITY ESTIMATE -- both
    /// positions really are near 50/50 -- it is wrong as an ACTION RANKER, because "even but
    /// poor" and "even but rich" are equal in win probability and very different in what
    /// they concede to a human who plays the economy.
    ///
    /// CONSEQUENCE FOR THE FIX: earlySpendGuardMinInvest would suppress the tier-4 buy, but
    /// it does not touch this. The same blindness remains for every repair decision, every
    /// trade involving absolute castle HP, and every mutually-destructive line.
    ///
    /// Usage: --score-decomp &lt;replay&gt; &lt;tick&gt; [--horizon N] [--seeds N] [--baseline A]
    /// </summary>
    public static class ScoreDecomp
    {
        private sealed class Agg
        {
            public double Score, Hp, Income, Money, Army, Gadget, Repair;
            public int N, Terminal, Wins, Losses;
            public double OwnUnits, EnemyUnits, OwnMoney, OwnIncome, OwnInvest, OwnCastle, EnemyCastle, OwnMax, EnemyMax;
            public double Q1, Q2, Q3, EnemyIncome, EnemyInvest;
            public List<int> InvestTicks = new List<int>();
            public double[] InvestTickSum = new double[10];
            public int[] InvestTickN = new int[10];
        }

        public static void Run(string path, string[] args)
        {
            long target = long.Parse(args[0]);
            int horizon = 1600, seeds = 12, baseline = 0;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--horizon" && i + 1 < args.Length) horizon = int.Parse(args[++i]);
                if (args[i] == "--seeds" && i + 1 < args.Length) seeds = int.Parse(args[++i]);
                if (args[i] == "--baseline" && i + 1 < args.Length) baseline = int.Parse(args[++i]);
            }

            var rf = ReplayFile.Read(path);
            var (state, engine) = rf.BuildStart();
            state.Player1.SetLoadout(new[] { B(rf.P1Off), B(rf.P1Def), B(rf.P1Sig) });
            state.Player2.SetLoadout(new[] { B(rf.P2Off), B(rf.P2Def), B(rf.P2Sig) });
            for (int i = 0; i < rf.TickCount && state.CurrentTick < target; i++)
            {
                if (rf.A1[i] != 0) engine.ApplyAction(1, rf.A1[i]);
                if (rf.A2[i] != 0) engine.ApplyAction(2, rf.A2[i]);
                engine.Tick();
                if (state.IsGameOver) break;
            }

            int side = 2;
            var me = state.Player2;
            Console.WriteLine();
            Console.WriteLine("=== " + rf.GameId + " @ tick " + state.CurrentTick + "  horizon " + horizon
                            + "  seeds " + seeds + " ===");
            Console.WriteLine("  P2 money " + me.Money.ToString("F1") + "  income " + me.Income.ToString("F1")
                            + "  invest " + me.InvestmentCount + "  investPrice " + me.InvestmentPrice.ToString("F1")
                            + "  own units " + state.Units.Count(u => u.Side == 2)
                            + "  enemy units " + state.Units.Count(u => u.Side == 1));

            var mask = state.GetActionMask(side);
            var cands = new List<int> { 0 };
            for (int a = 1; a <= 13; a++) if (a < mask.Length && mask[a] == 1) cands.Add(a);

            // ── IMMEDIATE effect, before any rollout ────────────────────────────────
            // Separates "what does this action do to the board right now" from "what does
            // the 53-second continuation do". Without this split it is impossible to tell
            // whether a component moved because of the ACTION or because of the rollout.
            var ev0 = state.GetEvalComponents();
            float base0 = state.EvaluateBoard();
            Console.WriteLine();
            Console.WriteLine("  IMMEDIATE effect of each action (applied once, NO rollout).");
            Console.WriteLine("  P2 score = 1-sigmoid(z); negative logit delta favours P2.");
            Console.WriteLine("    action           myHP/max        my%   money      dHp     dMoney    dTotal");
            Console.WriteLine("    " + new string('-', 82));
            foreach (int a in cands)
            {
                var c = engine.Clone(rngSeed: 7);
                c.ApplyAction(side, a);
                var p = side == 1 ? c._state.Player1 : c._state.Player2;
                var e = c._state.GetEvalComponents();
                double dHp = GameState.LogitWeightHp * (e.Hp - ev0.Hp);
                double dMo = GameState.LogitWeightMoney * (e.Money - ev0.Money);
                double dAll = GameState.LogitWeightHp * (e.Hp - ev0.Hp)
                            + GameState.LogitWeightIncome * (e.Income - ev0.Income)
                            + GameState.LogitWeightMoney * (e.Money - ev0.Money)
                            + GameState.LogitWeightArmy * (e.Army - ev0.Army)
                            + GameState.LogitWeightGadget * (e.Gadget - ev0.Gadget)
                            + GameState.LogitWeightRepair * (e.Repair - ev0.Repair);
                Console.WriteLine("    " + Name(a).PadRight(16)
                    + (p.CastleHealth + "/" + p.CastleMaxHealth).PadLeft(12)
                    + (100.0 * p.CastleHealth / p.CastleMaxHealth).ToString("F1").PadLeft(8) + "%"
                    + p.Money.ToString("F0").PadLeft(7)
                    + dHp.ToString("F4").PadLeft(10) + dMo.ToString("F4").PadLeft(10)
                    + dAll.ToString("F4").PadLeft(10));
            }

            var res = new Dictionary<int, Agg>();
            foreach (int a in cands) res[a] = RunCandidate(engine, side, a, horizon, seeds);

            Console.WriteLine();
            Console.WriteLine("  mean LEAF state per candidate (terminal rollouts excluded from leaf means):");
            Console.WriteLine("    action            score  ownU enemU  LEAFmny  myInc myInv  enInc enInv     myHP/max      my%    enHP/max      en%");
            Console.WriteLine("    " + new string('-', 82));
            foreach (var a in cands.OrderByDescending(x => res[x].Score / res[x].N))
            {
                var r = res[a];
                int n = Math.Max(r.N, 1);
                int ln = Math.Max(r.N - r.Terminal, 1);
                Console.WriteLine("    " + Name(a).PadRight(16)
                    + (r.Score / n).ToString("F4").PadLeft(7)
                    + (r.OwnUnits / ln).ToString("F1").PadLeft(6)
                    + (r.EnemyUnits / ln).ToString("F1").PadLeft(6)
                    + (r.OwnMoney / ln).ToString("F0").PadLeft(8)
                    + (r.OwnIncome / ln).ToString("F1").PadLeft(7)
                    + (r.OwnInvest / ln).ToString("F2").PadLeft(6)
                    + (r.EnemyIncome / ln).ToString("F1").PadLeft(7)
                    + (r.EnemyInvest / ln).ToString("F2").PadLeft(6)
                    + ((r.OwnCastle / ln).ToString("F0") + "/" + (r.OwnMax / ln).ToString("F0")).PadLeft(13)
                    + (100.0 * (r.OwnCastle / ln) / Math.Max(r.OwnMax / ln, 1)).ToString("F1").PadLeft(8) + "%"
                    + ((r.EnemyCastle / ln).ToString("F0") + "/" + (r.EnemyMax / ln).ToString("F0")).PadLeft(13)
                    + (100.0 * (r.EnemyCastle / ln) / Math.Max(r.EnemyMax / ln, 1)).ToString("F1").PadLeft(8) + "%");
            }

            Console.WriteLine();
            Console.WriteLine("  INVESTMENT TIMING inside the rollout (mean tick after the decision at which");
            Console.WriteLine("  each further investment landed), and the P2 score at 25/50/75/100% of horizon:");
            Console.WriteLine("    action           inv#3    inv#4    inv#5  |    q1      q2      q3    leaf");
            Console.WriteLine("    " + new string('-', 78));
            foreach (var a in cands.OrderByDescending(x => res[x].Score / res[x].N))
            {
                var r = res[a];
                int n = Math.Max(r.N, 1);
                Console.WriteLine("    " + Name(a).PadRight(16)
                    + Tk(r, 3).PadLeft(8) + Tk(r, 4).PadLeft(9) + Tk(r, 5).PadLeft(9)
                    + "  |" + (r.Q1 / n).ToString("F4").PadLeft(8)
                    + (r.Q2 / n).ToString("F4").PadLeft(8)
                    + (r.Q3 / n).ToString("F4").PadLeft(8)
                    + (r.Score / n).ToString("F4").PadLeft(8));
            }

            var b = res[baseline];
            int bn = Math.Max(b.N - b.Terminal, 1);
            Console.WriteLine();
            Console.WriteLine("  LOGIT DECOMPOSITION vs baseline '" + Name(baseline) + "'.");
            Console.WriteLine("  Each cell is w_i * (x_i[cand] - x_i[base]) -- the component's additive push on the");
            Console.WriteLine("  P1-perspective logit. P2 score is 1-sigmoid(z), so NEGATIVE numbers favour P2 (us).");
            Console.WriteLine();
            Console.WriteLine("    action              hp    income     money      army    gadget    repair  |   total");
            Console.WriteLine("    " + new string('-', 86));
            foreach (var a in cands.OrderByDescending(x => res[x].Score / res[x].N))
            {
                var r = res[a];
                int ln = Math.Max(r.N - r.Terminal, 1);
                double dHp = GameState.LogitWeightHp * (r.Hp / ln - b.Hp / bn);
                double dIn = GameState.LogitWeightIncome * (r.Income / ln - b.Income / bn);
                double dMo = GameState.LogitWeightMoney * (r.Money / ln - b.Money / bn);
                double dAr = GameState.LogitWeightArmy * (r.Army / ln - b.Army / bn);
                double dGa = GameState.LogitWeightGadget * (r.Gadget / ln - b.Gadget / bn);
                double dRe = GameState.LogitWeightRepair * (r.Repair / ln - b.Repair / bn);
                double tot = dHp + dIn + dMo + dAr + dGa + dRe;
                Console.WriteLine("    " + Name(a).PadRight(16)
                    + dHp.ToString("F4").PadLeft(8) + dIn.ToString("F4").PadLeft(10)
                    + dMo.ToString("F4").PadLeft(10) + dAr.ToString("F4").PadLeft(10)
                    + dGa.ToString("F4").PadLeft(10) + dRe.ToString("F4").PadLeft(10)
                    + "  |" + tot.ToString("F4").PadLeft(9));
            }

            Console.WriteLine();
            Console.WriteLine("  FAITHFULNESS CHECK -- this tool's mean score vs RolloutSearchBot's own:");
            RolloutSearchBot.CaptureDecisionTrace = true;
            var sums = new Dictionary<int, double>();
            var cnts = new Dictionary<int, int>();
            for (int s = 0; s < seeds; s++)
            {
                var bot = new RolloutSearchBot(side: 2, decisionInterval: 15, horizon: horizon,
                    rolloutsPerAction: 1, seed: 1000 + s, usePrior: true, overrideMargin: 0.10,
                    useMacro: true, usePressMacro: true, maxDecisionMs: 0,
                    maxParallelism: Math.Max(1, Environment.ProcessorCount - 2), asyncDecisions: false);
                bot.Update(engine.Clone(2000 + s));
                if (bot.LastScores == null) continue;
                foreach (var kv in bot.LastScores)
                {
                    sums[kv.Key] = (sums.TryGetValue(kv.Key, out var v) ? v : 0) + kv.Value;
                    cnts[kv.Key] = (cnts.TryGetValue(kv.Key, out var c) ? c : 0) + 1;
                }
            }
            Console.WriteLine("    action             this tool   RolloutSearchBot    delta");
            foreach (int a in cands)
            {
                if (!sums.ContainsKey(a)) continue;
                double thisTool = res[a].Score / Math.Max(res[a].N, 1);
                double theirs = sums[a] / cnts[a];
                Console.WriteLine("    " + Name(a).PadRight(16) + thisTool.ToString("F4").PadLeft(10)
                    + theirs.ToString("F4").PadLeft(18) + (thisTool - theirs).ToString("F4").PadLeft(10)
                    + (Math.Abs(thisTool - theirs) > 0.02 ? "   <-- MISMATCH" : ""));
            }
        }

        private static Agg RunCandidate(GameEngine engine, int side, int action, int horizon, int seeds)
        {
            var agg = new Agg();
            for (int s = 0; s < seeds; s++)
            {
                // Mirrors Rollout(): same seed derivation, same clone, same policies.
                var clone = engine.Clone(rngSeed: 1000 + s);
                var cs = clone._state;
                if (action > 0) clone.ApplyAction(side, action);
                var mine = new HeuristicBot(side);
                var theirs = new HeuristicBot(side == 1 ? 2 : 1);
                var mpTrack = side == 1 ? cs.Player1 : cs.Player2;
                // INVESTMENT TIMING, not the leaf COUNT. The leaf count is identical across
                // every candidate here (4.00), which is exactly why it cannot show the cost
                // of spending: 53 seconds is long enough for the economy to catch up. What
                // an \$18 purchase actually buys is a DELAY, so measure the delay.
                int prevInvest = mpTrack.InvestmentCount;
                int t = 0;
                for (; t < horizon && !cs.IsGameOver; t++)
                {
                    clone.Tick();
                    mine.Update(clone);
                    theirs.Update(clone);
                    if (mpTrack.InvestmentCount > prevInvest)
                    {
                        prevInvest = mpTrack.InvestmentCount;
                        if (agg.InvestTicks.Count < 6) agg.InvestTicks.Add(t);
                        agg.InvestTickSum[Math.Min(prevInvest, 9)] += t;
                        agg.InvestTickN[Math.Min(prevInvest, 9)]++;
                    }
                    if (t == horizon / 4 || t == horizon / 2 || t == 3 * horizon / 4)
                    {
                        float mid = cs.EvaluateBoard();
                        double sc = side == 1 ? mid : 1.0 - mid;
                        if (t == horizon / 4) agg.Q1 += sc;
                        else if (t == horizon / 2) agg.Q2 += sc;
                        else agg.Q3 += sc;
                    }
                }
                agg.N++;
                if (cs.IsGameOver)
                {
                    agg.Terminal++;
                    double term = cs.WinnerSide == 0 ? 0.5 : (cs.WinnerSide == side ? 1.0 : 0.0);
                    if (cs.WinnerSide == side) agg.Wins++; else if (cs.WinnerSide != 0) agg.Losses++;
                    agg.Score += term;
                    continue;
                }
                var ev = cs.GetEvalComponents();
                float raw = cs.EvaluateBoard();
                agg.Score += side == 1 ? raw : 1.0 - raw;
                agg.Hp += ev.Hp; agg.Income += ev.Income; agg.Money += ev.Money;
                agg.Army += ev.Army; agg.Gadget += ev.Gadget; agg.Repair += ev.Repair;
                var mp = side == 1 ? cs.Player1 : cs.Player2;
                var ep = side == 1 ? cs.Player2 : cs.Player1;
                agg.OwnUnits += cs.Units.Count(u => u.Side == side);
                agg.EnemyUnits += cs.Units.Count(u => u.Side != side);
                agg.OwnMoney += mp.Money; agg.OwnIncome += mp.Income; agg.OwnInvest += mp.InvestmentCount;
                agg.EnemyIncome += ep.Income; agg.EnemyInvest += ep.InvestmentCount;
                agg.OwnCastle += mp.CastleHealth; agg.EnemyCastle += ep.CastleHealth;
                agg.OwnMax += mp.CastleMaxHealth; agg.EnemyMax += ep.CastleMaxHealth;
            }
            return agg;
        }

        private static string Tk(Agg r, int investNo)
        {
            if (investNo >= r.InvestTickN.Length || r.InvestTickN[investNo] == 0) return "-";
            return (r.InvestTickSum[investNo] / r.InvestTickN[investNo]).ToString("F0")
                 + "(" + r.InvestTickN[investNo] + ")";
        }

        private static string B(string id) => string.IsNullOrEmpty(id) ? id : id.Split('_')[0];

        private static string Name(int a)
        {
            switch (a)
            {
                case 0: return "0 wait";
                case 9: return "9 invest";
                case 10: return "10 repair";
                case 11: return "11 offence gdt";
                case 12: return "12 defence gdt";
                case 13: return "13 signature";
                default: return a + " spawn T" + a;
            }
        }
    }
}
