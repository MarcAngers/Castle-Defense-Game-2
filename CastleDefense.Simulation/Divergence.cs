using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CastleDefense.Api.Data;
using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// REPLAY DIVERGENCE: puts the bot in the HUMAN's seat and scores how differently it
    /// would have played.
    ///
    /// WHY THIS AND NOT SELF-PLAY. Every other benchmark on this project is bot-vs-bot, so
    /// it only samples positions the bot walks itself into. Worse, the standard yardstick
    /// (win rate vs HeuristicBot) is partly self-referential: HeuristicBot is also
    /// RolloutSearchBot's policy prior AND its rollout policy for both sides, so that
    /// number can improve by exploiting the bot's own simulator. Marc beats HeuristicBot
    /// ~92% of the time, which means his games contain a region of state space the bot
    /// never generates and is never measured on. This walks his recorded trajectories and
    /// asks, at every decision point, "what would you have done here?" — then keeps
    /// following HIS action, so the trajectory stays human.
    ///
    /// WHAT IT MEASURES AND WHAT IT DOES NOT. This is POLICY DIVERGENCE, not error. It
    /// says where the two disagree; it cannot say who was right. The headline scalar is a
    /// SIMILARITY score, not a strength score: a bot that imitates Marc's timing without
    /// understanding it scores well. What it buys is a GRADIENT that does not require Marc
    /// to play, and one that cannot be moved by exploiting HeuristicBot. Read it alongside
    /// the ladder, never instead of it.
    ///
    /// ── HOW A "SHARED DECISION STATE" IS DEFINED (read before changing anything) ────────
    ///
    /// The two agents do not decide on the same clock. Marc clicks whenever he likes;
    /// HeuristicBot decides every 5 ticks; RolloutSearchBot every 15. So a decision point
    /// here is a WINDOW, not a tick:
    ///
    ///     At each decision tick T, both sides are asked what they commit to over
    ///     [T, T+interval) — the human by reading what he actually did over those ticks,
    ///     the bot by running on a CLONE forked from the state at T.
    ///
    /// THE PHASE BUG THIS REPLACES. The previous version compared the human's PRECEDING
    /// window [T-interval, T) against the bot's decision AT T. Those are conditioned on
    /// different states and can never coincide, which is the same failure that produced a
    /// plausible-looking 100% result elsewhere in this project. Marginal statistics (mean
    /// purchased tier) survived it because they only re-bin the same actions; every
    /// conditional statistic — which is all of the agreement scores below — did not.
    ///
    /// THE ORACLE THAT CATCHES IT. `--bot replay` uses the recorded human actions as the
    /// shadow policy. Its score MUST be exactly 1.000 with zero divergence: the same
    /// actions, scored against themselves. Off-by-a-window phase, broken diff accounting,
    /// or a leaky clone all drive it below 1.000. Run it after any edit to this file.
    /// `--bot none` is the opposite control: an all-wait shadow, which must score 0 on
    /// every active-window statistic. Both run in seconds.
    ///
    /// HOW THE BOT'S CHOICE IS OBSERVED. Not by reading an action id — HeuristicBot can
    /// spawn several units in one Decide(), and RolloutSearchBot commits through macros,
    /// so a single "action" undersells both. Instead the engine is CLONED and the clone is
    /// DIFFED against its own previous tick: money spent, units bought by tier, invest,
    /// repair, gadget cast. Per-tick diffs, never across a Tick(), because Diff() matches
    /// units positionally and a death mid-window would shift the indices. Both sides use
    /// the identical accounting path, which is what makes the replay oracle meaningful.
    ///
    /// The clone is ticked forward through the window and the shadow is updated on EVERY
    /// tick, exactly as it runs live, so its decision rate is its real one rather than one
    /// query per window. The opponent's recorded actions are replayed into the clone so
    /// the enemy keeps doing what it really did. The human's own side-1 actions are of
    /// course NOT applied — the shadow occupies that seat — so the clone drifts from
    /// reality within the window. That drift is bounded by `interval` ticks (0.5s at the
    /// default 15) and is the price of asking a counterfactual at all.
    ///
    /// ── KNOWN FIDELITY LIMIT, INHERITED FROM THE REPLAY FORMAT ─────────────────────────
    ///
    /// .replay stores only the discrete action id per tick and NEVER the gadget target
    /// position (see GameRecorder's format comment). Replaying action 11/12/13 therefore
    /// calls UseGadget(..., -1), the bot auto-target, not where the human actually aimed.
    /// This is the same defect already known in --trace-human, and --divergence DOES
    /// inherit it. Consequences, in order of severity:
    ///   * The reconstructed trajectory drifts from the real game after the human's first
    ///     gadget cast. Everything downstream is a plausible game, not his game.
    ///   * Marc's documented gadget doctrine — freeze/blackhole at the ENEMY's end to buy
    ///     the march back, damage gadgets at the front — is invisible here. The instrument
    ///     cannot score gadget targeting at all, only gadget TIMING.
    /// `gadget_uses` in game_records.db records id and tick but still no position, so this
    /// cannot be repaired from existing data; the recorder would have to change first.
    ///
    /// Usage: --divergence &lt;replayDir&gt; &lt;outCsv&gt;
    ///          [--bot search|heuristic|clone|replay|none] [--interval N]
    ///          [--all] [--filter substr] [--half a|b]
    ///
    /// `--half` holds out games. It matters for `--bot clone` and nothing else: the clone's
    /// policy table is fitted from these same recordings, so scoring it on the games it
    /// learned from measures memorisation. Fit with `--export-policy-table ... --half a`,
    /// score with `--divergence ... --bot clone --half b`.
    /// </summary>
    public static class Divergence
    {
        private static string ReadStr(BinaryReader r)
        {
            int len = r.ReadByte();
            return len > 0 ? Encoding.UTF8.GetString(r.ReadBytes(len)) : "";
        }

        // ── What a decision commits to ────────────────────────────────────────────────

        /// <summary>Everything one side committed to during a window, however many
        /// primitive actions it took to get there.</summary>
        private struct Spend
        {
            public double Money;          // total cash committed
            public int[] UnitsByTier;     // index 1..8
            public int Invests;
            public int Repairs;
            public int Gadgets;
            public static Spend New() => new Spend { UnitsByTier = new int[9] };
            public int TotalUnits { get { int n = 0; for (int t = 1; t <= 8; t++) n += UnitsByTier[t]; return n; } }
            public int TopTier { get { for (int t = 8; t >= 1; t--) if (UnitsByTier[t] > 0) return t; return 0; } }

            public void Add(in Spend o)
            {
                Money += o.Money;
                for (int t = 1; t <= 8; t++) UnitsByTier[t] += o.UnitsByTier[t];
                Invests += o.Invests; Repairs += o.Repairs; Gadgets += o.Gadgets;
            }
        }

        /// <summary>
        /// The categorical vocabulary the agreement scores are computed over. Unit buys are
        /// split by tier band rather than lumped, because the single sharpest measured
        /// difference between Marc and the bot is WHICH tier they buy, not whether they buy
        /// — collapsing them would throw away the signal this instrument exists to track.
        /// </summary>
        private enum Label { Wait = 0, UnitLo, UnitMid, UnitHi, Invest, Repair, Gadget }
        private const int NLabels = 7;
        private static readonly string[] LabelNames =
            { "wait", "unit_lo(t1-3)", "unit_mid(t4-5)", "unit_hi(t6-8)", "invest", "repair", "gadget" };

        /// <summary>A window can carry several labels at once (invest AND a tier-6 buy is
        /// one decision, not two), so this is a set, not a class.</summary>
        private static bool[] Labels(in Spend s)
        {
            var l = new bool[NLabels];
            if (s.Invests > 0) l[(int)Label.Invest] = true;
            if (s.Repairs > 0) l[(int)Label.Repair] = true;
            if (s.Gadgets > 0) l[(int)Label.Gadget] = true;
            int top = s.TopTier;
            if (top >= 1 && top <= 3) l[(int)Label.UnitLo] = true;
            else if (top >= 4 && top <= 5) l[(int)Label.UnitMid] = true;
            else if (top >= 6) l[(int)Label.UnitHi] = true;
            bool any = false;
            for (int i = 1; i < NLabels; i++) any |= l[i];
            if (!any) l[(int)Label.Wait] = true;
            return l;
        }

        /// <summary>
        /// Diffs a post-action state against the pre-action state, for ONE side. Valid only
        /// across a single ApplyAction/Update with no Tick() in between: units are matched
        /// positionally, so a death would shift the tail and miscount the buys.
        /// </summary>
        private static Spend Diff(GameState before, GameState after, int side)
        {
            var s = Spend.New();
            var mine = side == 1 ? before.Player1 : before.Player2;
            var theirs = side == 1 ? after.Player1 : after.Player2;

            // Money can also RISE within a decision (income accrues on Tick, cash gadgets
            // pay out), so clamp at zero: this is "what was committed", not net flow.
            s.Money = Math.Max(0, mine.Money - theirs.Money);
            s.Invests = Math.Max(0, theirs.InvestmentCount - mine.InvestmentCount);
            s.Repairs = Math.Max(0, theirs.RepairCount - mine.RepairCount);

            int beforeUnits = before.Units.Count(u => u.Side == side);
            foreach (var u in after.Units.Where(u => u.Side == side).Skip(beforeUnits))
                if (u.Tier >= 1 && u.Tier <= 8) s.UnitsByTier[u.Tier]++;

            // A gadget cast is visible as a cooldown that was not running before.
            foreach (var kv in theirs.GadgetCooldowns)
                if (kv.Value > 0 && (!mine.GadgetCooldowns.TryGetValue(kv.Key, out var prev) || prev <= 0))
                    s.Gadgets++;

            return s;
        }

        /// <summary>
        /// A cheap snapshot of only the fields Diff() reads. A full engine Clone per tick
        /// would dominate the runtime, and none of the deep state is needed here.
        /// </summary>
        private static GameState CloneStateOnly(GameState s)
        {
            var c = new GameState();
            c.Player1 = new PlayerState { Side = 1, Money = s.Player1.Money, InvestmentCount = s.Player1.InvestmentCount, RepairCount = s.Player1.RepairCount };
            c.Player2 = new PlayerState { Side = 2, Money = s.Player2.Money, InvestmentCount = s.Player2.InvestmentCount, RepairCount = s.Player2.RepairCount };
            foreach (var kv in s.Player1.GadgetCooldowns) c.Player1.GadgetCooldowns[kv.Key] = kv.Value;
            foreach (var kv in s.Player2.GadgetCooldowns) c.Player2.GadgetCooldowns[kv.Key] = kv.Value;
            c.Units = new List<Unit>(s.Units);
            return c;
        }

        // ── The shadow policy ─────────────────────────────────────────────────────────

        /// <summary>
        /// One tick of whatever is sitting in the human's seat. `tickIndex` is the offset
        /// into the current window, which only the replay oracle needs.
        /// </summary>
        private interface IShadow { void Step(GameEngine clone, byte recordedP1Action); }

        private sealed class HeuristicShadow : IShadow
        {
            private readonly HeuristicBot _bot = new HeuristicBot(1);
            public void Step(GameEngine clone, byte _) => _bot.Update(clone);
        }

        private sealed class SearchShadow : IShadow
        {
            private readonly RolloutSearchBot _bot;
            public SearchShadow(int interval) =>
                _bot = new RolloutSearchBot(1, interval, 300, 1, seed: 12345,
                                            usePrior: true, overrideMargin: 0.10);
            public void Step(GameEngine clone, byte _) => _bot.Update(clone);
        }

        /// <summary>THE ORACLE. Replays the human's own recorded actions as if they were a
        /// policy. Scoring this against the human must return a perfect match; any number
        /// below 1.000 means the instrument is measuring a phase, accounting or clone bug
        /// rather than a policy difference.</summary>
        private sealed class ReplayShadow : IShadow
        {
            public void Step(GameEngine clone, byte a1) => clone.ApplyAction(1, a1);
        }

        /// <summary>The floor control: commits to nothing, ever.</summary>
        private sealed class NullShadow : IShadow
        {
            public void Step(GameEngine clone, byte _) { }
        }

        /// <summary>The fitted behaviour clone. Scoring it here closes the loop: the rung
        /// built to be human-SHAPED is measured on the very metric that defines the shape.
        /// Use --half so the table is not scored on the games it was fitted to.</summary>
        private sealed class CloneShadow : IShadow
        {
            private readonly HumanCloneBot _bot = new HumanCloneBot(1);
            public void Step(GameEngine clone, byte _) => _bot.Update(clone);
        }

        private static IShadow MakeShadow(string kind, int interval) => kind switch
        {
            "heuristic" => new HeuristicShadow(),
            "replay" => new ReplayShadow(),
            "none" => new NullShadow(),
            "clone" => new CloneShadow(),
            _ => new SearchShadow(interval),
        };

        // ── Accumulated scalars ───────────────────────────────────────────────────────

        private sealed class Tally
        {
            public int Games, Windows;
            public readonly int[] HumanLabel = new int[NLabels];
            public readonly int[] BotLabel = new int[NLabels];
            public readonly int[] Both = new int[NLabels];   // agreement per label
            public double HumanTierSum, BotTierSum;          // over windows where that side bought
            public int HumanBuyWindows, BotBuyWindows;
            public int HumanHiTier, BotHiTier;               // top tier >= 6, given a buy
            public double HumanMoney, BotMoney;
            public int SharedBuyWindows;                     // both bought units
            public double SharedHumanTier, SharedBotTier;
            public int SharedHumanHi, SharedBotHi;

            // Per-game label masks, kept so the headline can carry a bootstrap interval.
            // Resampling has to be over GAMES, not windows: windows inside one game are
            // heavily autocorrelated (the same economic phase persists for seconds), so a
            // window-level bootstrap would report an interval several times too narrow.
            // One byte per side per window — ~70 KB for the whole recordings folder.
            public readonly List<List<(byte h, byte b)>> PerGame = new();
            private List<(byte h, byte b)> _current;

            public void BeginGame() { _current = new List<(byte, byte)>(); PerGame.Add(_current); Games++; }

            public void Observe(in Spend h, in Spend b)
            {
                Windows++;
                var lh = Labels(h);
                var lb = Labels(b);
                byte mh = 0, mb = 0;
                for (int i = 0; i < NLabels; i++)
                {
                    if (lh[i]) { HumanLabel[i]++; mh |= (byte)(1 << i); }
                    if (lb[i]) { BotLabel[i]++; mb |= (byte)(1 << i); }
                    if (lh[i] && lb[i]) Both[i]++;
                }
                _current?.Add((mh, mb));
                HumanMoney += h.Money; BotMoney += b.Money;

                int ht = h.TopTier, bt = b.TopTier;
                if (ht > 0) { HumanBuyWindows++; HumanTierSum += ht; if (ht >= 6) HumanHiTier++; }
                if (bt > 0) { BotBuyWindows++; BotTierSum += bt; if (bt >= 6) BotHiTier++; }
                if (ht > 0 && bt > 0)
                {
                    SharedBuyWindows++;
                    SharedHumanTier += ht; SharedBotTier += bt;
                    if (ht >= 6) SharedHumanHi++;
                    if (bt >= 6) SharedBotHi++;
                }
            }
        }

        // ── Entry point ───────────────────────────────────────────────────────────────

        public static void Run(string replayDir, string outCsv, string[] args)
        {
            string botKind = "search";
            int interval = 15;
            bool allGames = false;
            string filter = null, half = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--bot" && i + 1 < args.Length) botKind = args[++i];
                else if (args[i] == "--interval" && i + 1 < args.Length) interval = int.Parse(args[++i]);
                else if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
                else if (args[i] == "--half" && i + 1 < args.Length) half = args[++i];
                else if (args[i] == "--all") allGames = true;
            }

            if (!Directory.Exists(replayDir))
            {
                Console.Error.WriteLine($"[divergence] replay directory not found: {replayDir}");
                return;
            }

            // Game selection is shared with --export-policy-table so the metric is always
            // scored on the same population a cloned rung is fitted to.
            var selected = ReplayFile.SelectHumanGames(replayDir, "divergence", allGames, filter, half);

            Console.WriteLine($"[divergence] shadow = {botKind}, window = {interval} ticks " +
                              $"({interval / 30.0:F2}s), {selected.Count} replays");
            Console.WriteLine($"[divergence] the shadow plays SIDE 1 (the human's seat); " +
                              $"the trajectory follows the human\n");
            if (botKind == "replay")
                Console.WriteLine("[divergence] ORACLE RUN: shadow replays the human's own actions. " +
                                  "Every agreement score below must read 1.000 exactly.\n");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outCsv))!);
            using var sw = new StreamWriter(outCsv, false, Encoding.UTF8);
            sw.WriteLine("game_id,tick,winner,p1_team,p2_team,income,money,invest_count,hp_pct,enemy_hp_pct," +
                         "enemy_units,enemy_top_tier," +
                         "h_money,h_units,h_top_tier,h_invest,h_repair,h_gadget," +
                         "b_money,b_units,b_top_tier,b_invest,b_repair,b_gadget");

            var tally = new Tally();
            int done = 0, skipped = 0;
            foreach (var f in selected)
            {
                try { if (One(f, sw, botKind, interval, tally)) done++; else skipped++; }
                catch (Exception ex) { skipped++; Console.Error.WriteLine($"[divergence] skip {Path.GetFileName(f)}: {ex.Message}"); }
                if (done % 20 == 0 && done > 0) Console.WriteLine($"  ... {done}/{selected.Count}");
            }
            Console.WriteLine($"\n[divergence] {done} replays processed, {skipped} skipped -> {outCsv}");

            Report(tally, botKind, interval, outCsv);
        }

        /// <summary>Returns false if the replay was deliberately skipped rather than failed.</summary>
        private static bool One(string path, StreamWriter sw, string botKind, int interval, Tally tally)
        {
            var rf = ReplayFile.Read(path);
            if (rf.IsAbandoned)
            {
                Console.WriteLine($"[divergence] {rf.GameId}: skipped (P1 took zero actions — abandoned reroll)");
                return false;
            }

            string gameId = rf.GameId, p1Team = rf.P1Team, p2Team = rf.P2Team;
            byte winner = rf.Winner;
            byte[] a1 = rf.A1, a2 = rf.A2;
            int tickCount = rf.TickCount;

            var (state, engine) = rf.BuildStart();
            var shadow = MakeShadow(botKind, interval);
            tally.BeginGame();

            for (uint t = 0; t + interval <= tickCount && !state.IsGameOver; t += (uint)interval)
            {
                // ── Board context, captured BEFORE either side acts ───────────────────
                var me = state.Player1;
                var opp = state.Player2;
                long tick = state.CurrentTick;
                double income = me.Income, money = me.Money;
                int invCount = me.InvestmentCount;
                double hpPct = (double)me.CastleHealth / Math.Max(1, me.CastleMaxHealth);
                double eHpPct = (double)opp.CastleHealth / Math.Max(1, opp.CastleMaxHealth);
                int enemyUnits = state.Units.Count(u => u.Side == 2);
                int enemyTop = state.Units.Where(u => u.Side == 2).Select(u => u.Tier).DefaultIfEmpty(0).Max();

                // ── What the BOT would commit to over [T, T+interval) ─────────────────
                // Forked from the state at T, so both sides answer the same question from
                // the same position. The clone is ticked and the shadow updated every tick,
                // matching its live decision rate; the opponent's real actions are replayed
                // so the enemy keeps playing. The human's own actions are not applied —
                // the shadow has that seat — so the clone drifts within the window.
                var botWindow = Spend.New();
                {
                    var clone = engine.Clone(rngSeed: 777);
                    for (int k = 0; k < interval && !clone._state.IsGameOver; k++)
                    {
                        var pre = CloneStateOnly(clone._state);
                        shadow.Step(clone, a1[t + k]);
                        botWindow.Add(Diff(pre, clone._state, 1));
                        clone.ApplyAction(2, a2[t + k]);
                        clone.Tick();
                    }
                }

                // ── What the HUMAN actually committed to over the SAME window ─────────
                var humanWindow = Spend.New();
                for (int k = 0; k < interval && !state.IsGameOver; k++)
                {
                    var pre = CloneStateOnly(state);
                    engine.ApplyAction(1, a1[t + k]);
                    humanWindow.Add(Diff(pre, state, 1));
                    engine.ApplyAction(2, a2[t + k]);
                    engine.Tick();
                }

                tally.Observe(humanWindow, botWindow);

                sw.WriteLine($"{gameId},{tick},{winner},{p1Team},{p2Team}," +
                             $"{income:F1},{money:F0},{invCount}," +
                             $"{hpPct:F3},{eHpPct:F3},{enemyUnits},{enemyTop}," +
                             $"{humanWindow.Money:F0},{humanWindow.TotalUnits},{humanWindow.TopTier}," +
                             $"{humanWindow.Invests},{humanWindow.Repairs},{humanWindow.Gadgets}," +
                             $"{botWindow.Money:F0},{botWindow.TotalUnits},{botWindow.TopTier}," +
                             $"{botWindow.Invests},{botWindow.Repairs},{botWindow.Gadgets}");
            }
            return true;
        }

        // ── Scalars ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Macro-F1 from raw label masks. Shared by the point estimate and the bootstrap so
        /// the interval cannot silently drift from the number it is an interval for.
        /// </summary>
        private static double MacroF1From(int[] human, int[] bot, int[] both)
        {
            double sum = 0; int n = 0;
            for (int i = 0; i < NLabels; i++)
            {
                if (i == (int)Label.Wait) continue;
                double prec = bot[i] > 0 ? (double)both[i] / bot[i] : 0;
                double rec = human[i] > 0 ? (double)both[i] / human[i] : 0;
                sum += (prec + rec) > 0 ? 2 * prec * rec / (prec + rec) : 0;
                n++;
            }
            return n > 0 ? sum / n : 0;
        }

        /// <summary>
        /// 95% interval on macro_f1, resampling GAMES with replacement. Games are the
        /// independent unit; windows within a game are not. Seeded, so the interval is
        /// reproducible run to run and two runs differing only in the bot are comparable.
        /// </summary>
        private static (double lo, double hi) BootstrapMacroF1(Tally t, int draws = 400)
        {
            int g = t.PerGame.Count;
            if (g < 2) return (0, 0);
            var rng = new Random(20260807);
            var scores = new double[draws];
            var h = new int[NLabels]; var b = new int[NLabels]; var both = new int[NLabels];
            for (int d = 0; d < draws; d++)
            {
                Array.Clear(h); Array.Clear(b); Array.Clear(both);
                for (int k = 0; k < g; k++)
                {
                    foreach (var (mh, mb) in t.PerGame[rng.Next(g)])
                        for (int i = 0; i < NLabels; i++)
                        {
                            bool ih = (mh & (1 << i)) != 0, ib = (mb & (1 << i)) != 0;
                            if (ih) h[i]++;
                            if (ib) b[i]++;
                            if (ih && ib) both[i]++;
                        }
                }
                scores[d] = MacroF1From(h, b, both);
            }
            Array.Sort(scores);
            return (scores[(int)(0.025 * draws)], scores[(int)(0.975 * draws) - 1]);
        }

        private static void Report(Tally t, string botKind, int interval, string outCsv)
        {
            if (t.Windows == 0) { Console.WriteLine("[divergence] no windows scored."); return; }

            double N = t.Windows;
            var rows = new List<(string name, double f1, double prec, double rec, double ph, double pb, int support)>();

            // Macro-F1 deliberately EXCLUDES `wait`. Wait is the overwhelming majority
            // class (a human clicks a handful of times per second at most), so including
            // it would let a bot that does nothing score respectably — the exact degenerate
            // policy this metric has to be able to reject.
            double macroF1Sum = 0; int macroN = 0;
            for (int i = 0; i < NLabels; i++)
            {
                double prec = t.BotLabel[i] > 0 ? (double)t.Both[i] / t.BotLabel[i] : 0;
                double rec = t.HumanLabel[i] > 0 ? (double)t.Both[i] / t.HumanLabel[i] : 0;
                double f1 = (prec + rec) > 0 ? 2 * prec * rec / (prec + rec) : 0;
                rows.Add((LabelNames[i], f1, prec, rec, t.HumanLabel[i] / N, t.BotLabel[i] / N, t.HumanLabel[i]));
                if (i != (int)Label.Wait) { macroF1Sum += f1; macroN++; }
            }
            double macroF1 = macroN > 0 ? macroF1Sum / macroN : 0;

            // Total variation between the two label distributions. Answers "does the bot's
            // action MIX look like Marc's" — a shape check that ignores timing entirely,
            // and so is the one number here a mimic could game. Reported next to macro-F1
            // precisely so the two can be read against each other: mix right but F1 low
            // means the bot does Marc-like things at un-Marc-like moments.
            double sumH = 0, sumB = 0;
            for (int i = 0; i < NLabels; i++) { sumH += t.HumanLabel[i]; sumB += t.BotLabel[i]; }
            double tvd = 0;
            if (sumH > 0 && sumB > 0)
                for (int i = 0; i < NLabels; i++) tvd += Math.Abs(t.HumanLabel[i] / sumH - t.BotLabel[i] / sumB);
            tvd *= 0.5;

            double humanActive = N - t.HumanLabel[(int)Label.Wait];
            double botActive = N - t.BotLabel[(int)Label.Wait];
            var (f1Lo, f1Hi) = BootstrapMacroF1(t);

            // CHANCE CORRECTION, and why the headline is a ratio rather than macro_f1 itself.
            //
            // Raw macro_f1 rewards ACTING OFTEN. If a bot fires a label in a fraction p_b of
            // windows and the human in p_h, then even with completely unrelated timing they
            // coincide in p_h*p_b of windows, giving precision p_h, recall p_b and an
            // expected F1 of 2*p_h*p_b/(p_h+p_b) for free. A bot that sprays actions
            // therefore banks recall on every label without understanding anything.
            //
            // This is not hypothetical — it inverted the first real comparison. HeuristicBot
            // acts in 33.6% of windows against Marc's 10.8% and outscored the fitted human
            // clone on raw macro_f1 (0.066 vs 0.050), while matching his action MIX eight
            // times worse (TVD 0.314 vs 0.039). Dividing by the chance level at each bot's
            // own action volume removes exactly that advantage, and it is also what makes
            // the metric fair to a STOCHASTIC policy: a perfectly calibrated sampler only
            // ever coincides with the human by chance, so it scores ~1.0 on the ratio while
            // scoring near zero on the raw number.
            double chanceSum = 0; int chanceN = 0;
            for (int i = 0; i < NLabels; i++)
            {
                if (i == (int)Label.Wait) continue;
                double ph = t.HumanLabel[i] / N, pb = t.BotLabel[i] / N;
                chanceSum += (ph + pb) > 0 ? 2 * ph * pb / (ph + pb) : 0;
                chanceN++;
            }
            double chanceF1 = chanceN > 0 ? chanceSum / chanceN : 0;
            double timingLift = chanceF1 > 0 ? macroF1 / chanceF1 : 0;

            Console.WriteLine();
            Console.WriteLine(new string('=', 78));
            Console.WriteLine($"  DIVERGENCE FROM HUMAN PLAY   shadow={botKind}  window={interval} ticks");
            Console.WriteLine($"  {t.Games} games, {t.Windows} decision windows");
            Console.WriteLine(new string('=', 78));
            Console.WriteLine();
            Console.WriteLine("  per-label agreement on shared decision states");
            Console.WriteLine("    label             human    bot     prec     rec      F1   support");
            Console.WriteLine("    " + new string('-', 66));
            foreach (var r in rows)
                Console.WriteLine($"    {r.name,-16} {r.ph,6:P1} {r.pb,6:P1}  {r.prec,6:F3}  {r.rec,6:F3}  {r.f1,6:F3}   {r.support,6}");
            Console.WriteLine();

            Console.WriteLine("  ── SCALARS ────────────────────────────────────────────────────────────");
            Console.WriteLine($"    timing_lift           {timingLift,8:F4}    HEADLINE (timing). 1.0 = no better than");
            Console.WriteLine($"                                    random timing at this bot's own action volume.");
            Console.WriteLine($"    action_mix_tvd        {tvd,8:F4}    HEADLINE (shape). 0 = same action mix as Marc.");
            Console.WriteLine($"    macro_f1              {macroF1,8:F4}  [{f1Lo:F4},{f1Hi:F4}]  raw, NOT volume-corrected");
            Console.WriteLine($"    chance_macro_f1       {chanceF1,8:F4}    what this bot scores on timing alone");
            Console.WriteLine($"    commit_rate_human     {humanActive / N,8:F4}");
            Console.WriteLine($"    commit_rate_bot       {botActive / N,8:F4}    see SPEND PRESSURE note below");
            Console.WriteLine($"    mean_tier_human       {(t.HumanBuyWindows > 0 ? t.HumanTierSum / t.HumanBuyWindows : 0),8:F4}    over windows where that side bought");
            Console.WriteLine($"    mean_tier_bot         {(t.BotBuyWindows > 0 ? t.BotTierSum / t.BotBuyWindows : 0),8:F4}");
            Console.WriteLine($"    hi_tier_rate_human    {(t.HumanBuyWindows > 0 ? (double)t.HumanHiTier / t.HumanBuyWindows : 0),8:F4}    P(top tier >= 6 | bought)");
            Console.WriteLine($"    hi_tier_rate_bot      {(t.BotBuyWindows > 0 ? (double)t.BotHiTier / t.BotBuyWindows : 0),8:F4}");
            Console.WriteLine($"    spend_pressure        {(t.HumanMoney > 0 ? t.BotMoney / t.HumanMoney : 0),8:F4}    NOT a spend ratio — read the note.");
            Console.WriteLine();
            Console.WriteLine($"    shared buy windows    {t.SharedBuyWindows,8}    (both sides bought units)");
            if (t.SharedBuyWindows > 0)
            {
                Console.WriteLine($"      mean tier   human {t.SharedHumanTier / t.SharedBuyWindows,6:F2}   bot {t.SharedBotTier / t.SharedBuyWindows,6:F2}");
                Console.WriteLine($"      tier >= 6   human {100.0 * t.SharedHumanHi / t.SharedBuyWindows,5:F1}%   bot {100.0 * t.SharedBotHi / t.SharedBuyWindows,5:F1}%");
            }
            Console.WriteLine();
            Console.WriteLine("  SPEND PRESSURE, and why the bot's rates look enormous. Every window forks");
            Console.WriteLine("  from the HUMAN's wallet, and the human's savings are still sitting in it. A");
            Console.WriteLine("  bot that spends on affordability therefore re-spends the same dollar in every");
            Console.WriteLine("  window until the human finally spends it, so commit_rate_bot, spend_pressure");
            Console.WriteLine("  and every bot-side precision measure DURATION (how long an action stayed");
            Console.WriteLine("  attractive), not decision count. That is not an artefact to correct away —");
            Console.WriteLine("  it is the finding: Marc holds money and the bot cannot. But it does put a");
            Console.WriteLine("  ceiling well below 1.0 on macro_f1 for any bot that acts whenever it can, so");
            Console.WriteLine("  read macro_f1 as a gradient to climb, never as a percentage of Marc.");
            Console.WriteLine();
            Console.WriteLine("  The two CLEAN numbers here, unaffected by that, are the shared-buy-window");
            Console.WriteLine("  tier comparisons: they condition on both sides buying and ask only WHICH");
            Console.WriteLine("  tier, which duration cannot bias.");
            Console.WriteLine();

            // ── Oracle assertions ────────────────────────────────────────────────────
            Console.WriteLine("  READ THE TWO HEADLINES TOGETHER. action_mix_tvd asks whether the bot does");
            Console.WriteLine("  the same THINGS as Marc; timing_lift asks whether it does them at the same");
            Console.WriteLine("  MOMENTS. Either alone is gameable — match his mix with random timing and");
            Console.WriteLine("  tvd goes to 0 while lift stays at 1.0; spray actions constantly and raw");
            Console.WriteLine("  macro_f1 rises while both headlines expose it. Progress means tvd falling");
            Console.WriteLine("  AND lift rising.");
            Console.WriteLine();

            if (botKind == "replay")
            {
                bool ok = macroF1 > 0.99999 && tvd < 1e-9;
                Console.WriteLine(ok
                    ? "  ORACLE PASS: replaying the human against himself scores 1.000 with zero\n" +
                      "  divergence, so the window alignment, the diff accounting and the clone are\n" +
                      "  all sound. Any number this tool reports for a real bot is a policy\n" +
                      "  difference, not an instrument artefact."
                    : $"  *** ORACLE FAIL *** macro_f1={macroF1:F6} tvd={tvd:F9}, both must be exact.\n" +
                      "  The instrument is measuring itself. Do not trust any other run until this\n" +
                      "  passes — this is the phase-alignment failure mode, not a policy result.");
                Console.WriteLine();
            }
            if (botKind == "none")
            {
                bool ok = macroF1 < 1e-12 && botActive < 1e-9;
                Console.WriteLine(ok
                    ? "  CONTROL PASS: an all-wait shadow scores exactly 0, so the score is not being\n" +
                      "  propped up by the majority class."
                    : $"  *** CONTROL FAIL *** an all-wait shadow scored macro_f1={macroF1:F6} with\n" +
                      $"  commit rate {botActive / N:F6}. Something other than the shadow is spending.");
                Console.WriteLine();
            }

            // Machine-readable, one row, for tracking across runs.
            string summaryPath = Path.ChangeExtension(outCsv, null) + "_summary.csv";
            bool fresh = !File.Exists(summaryPath);
            using var s = new StreamWriter(summaryPath, append: true, Encoding.UTF8);
            if (fresh)
                s.WriteLine("timestamp,bot,interval,games,windows,timing_lift,chance_macro_f1," +
                            "macro_f1,macro_f1_lo,macro_f1_hi," +
                            "action_mix_tvd,commit_rate_human,commit_rate_bot,mean_tier_human,mean_tier_bot," +
                            "hi_tier_rate_human,hi_tier_rate_bot,spend_pressure," +
                            "f1_unit_lo,f1_unit_mid,f1_unit_hi,f1_invest,f1_repair,f1_gadget");
            s.WriteLine($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ},{botKind},{interval},{t.Games},{t.Windows}," +
                        $"{timingLift:F6},{chanceF1:F6}," +
                        $"{macroF1:F6},{f1Lo:F6},{f1Hi:F6}," +
                        $"{tvd:F6},{humanActive / N:F6},{botActive / N:F6}," +
                        $"{(t.HumanBuyWindows > 0 ? t.HumanTierSum / t.HumanBuyWindows : 0):F4}," +
                        $"{(t.BotBuyWindows > 0 ? t.BotTierSum / t.BotBuyWindows : 0):F4}," +
                        $"{(t.HumanBuyWindows > 0 ? (double)t.HumanHiTier / t.HumanBuyWindows : 0):F4}," +
                        $"{(t.BotBuyWindows > 0 ? (double)t.BotHiTier / t.BotBuyWindows : 0):F4}," +
                        $"{(t.HumanMoney > 0 ? t.BotMoney / t.HumanMoney : 0):F4}," +
                        $"{rows[(int)Label.UnitLo].f1:F4},{rows[(int)Label.UnitMid].f1:F4}," +
                        $"{rows[(int)Label.UnitHi].f1:F4},{rows[(int)Label.Invest].f1:F4}," +
                        $"{rows[(int)Label.Repair].f1:F4},{rows[(int)Label.Gadget].f1:F4}");
            Console.WriteLine($"  appended one row to {summaryPath}");
        }
    }
}
