using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// REPLAY DIVERGENCE: puts the bot in the HUMAN's seat and records where it would
    /// have played differently.
    ///
    /// WHY THIS AND NOT SELF-PLAY. Every benchmark this project has is the bot against
    /// another bot, so it only ever samples positions the bot walks itself into. Marc
    /// beats it ~90% of the time, which means his games contain a whole region of state
    /// space the bot never generates and therefore never gets measured on. This walks his
    /// recorded trajectories and asks, at every decision point, "what would you have done
    /// here?" — then keeps following HIS action, so the trajectory stays human.
    ///
    /// WHAT IT MEASURES AND WHAT IT DOES NOT. This is POLICY DIVERGENCE, not error. It
    /// says where the two disagree; it cannot say who was right. Every row is a hypothesis.
    /// Settling one needs counterfactual rollouts from the divergent state, which is far
    /// more expensive and only worth doing on whatever this flags.
    ///
    /// HOW THE BOT'S CHOICE IS OBSERVED. Not by reading an action id — HeuristicBot can
    /// spawn several units in one Decide(), and RolloutSearchBot commits through macros,
    /// so a single "action" undersells both. Instead the engine is CLONED, the shadow bot
    /// acts on the clone, and the clone is DIFFED against the parent: money spent, units
    /// bought by tier, invest taken, repair, gadget cast. That works for any bot without
    /// modifying it, and it captures multi-action decisions correctly. The clone is then
    /// discarded.
    ///
    /// The shadow bot instance PERSISTS across the game so its internal state accumulates
    /// (HeuristicBot carries HP-drain history, observed enemy tiers, spend allowances).
    /// It is updated only at decision points, which for RolloutSearchBot at interval 15 is
    /// exactly how it runs live. HeuristicBot natively decides every 5 ticks, so as a
    /// shadow it sees a sparser HP history than it would in a real game — a known caveat,
    /// and the same one that applies to SearchBot's own prior.
    ///
    /// Usage: --divergence &lt;replayDir&gt; &lt;outCsv&gt; [--bot search|heuristic] [--interval N]
    /// </summary>
    public static class Divergence
    {
        private static string ReadStr(BinaryReader r)
        {
            int len = r.ReadByte();
            return len > 0 ? Encoding.UTF8.GetString(r.ReadBytes(len)) : "";
        }

        /// <summary>Everything one side committed to during a decision, however many
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
        }

        /// <summary>
        /// Diffs a post-action clone against the pre-action parent. Units are matched by
        /// side and counted by tier; everything else is a scalar delta.
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

        public static void Run(string replayDir, string outCsv, string[] args)
        {
            string botKind = "search";
            int interval = 15;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--bot" && i + 1 < args.Length) botKind = args[++i];
                else if (args[i] == "--interval" && i + 1 < args.Length) interval = int.Parse(args[++i]);
            }

            if (!Directory.Exists(replayDir))
            {
                Console.Error.WriteLine($"[divergence] replay directory not found: {replayDir}");
                return;
            }
            var files = Directory.GetFiles(replayDir, "*.replay").OrderBy(f => f).ToArray();
            Console.WriteLine($"[divergence] {files.Length} replays, shadow bot = {botKind}, decision every {interval} ticks");
            Console.WriteLine($"[divergence] the shadow plays SIDE 1 (the human's seat); the trajectory follows the human\n");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outCsv))!);
            using var sw = new StreamWriter(outCsv, false, Encoding.UTF8);
            sw.WriteLine("game_id,tick,winner,p1_team,p2_team,income,money,invest_count,hp_pct,enemy_hp_pct," +
                         "enemy_units,enemy_top_tier," +
                         "h_money,h_units,h_top_tier,h_invest,h_repair,h_gadget," +
                         "b_money,b_units,b_top_tier,b_invest,b_repair,b_gadget");

            int done = 0, skipped = 0;
            foreach (var f in files)
            {
                try { One(f, sw, botKind, interval); done++; }
                catch (Exception ex) { skipped++; Console.Error.WriteLine($"[divergence] skip {Path.GetFileName(f)}: {ex.Message}"); }
                if (done % 20 == 0 && done > 0) Console.WriteLine($"  ... {done}/{files.Length}");
            }
            Console.WriteLine($"\n[divergence] {done} replays processed, {skipped} skipped -> {outCsv}");
        }

        private static void One(string path, StreamWriter sw, string botKind, int interval)
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream, Encoding.UTF8);

            var magic = r.ReadBytes(4);
            if (magic[0] != 'C' || magic[1] != 'D' || magic[2] != 'R' || magic[3] != 'P')
                throw new InvalidDataException("not a CDRP replay");

            byte version = r.ReadByte();
            string gameId = Encoding.ASCII.GetString(r.ReadBytes(6));
            r.ReadInt64();          // timestamp
            ReadStr(r);             // game_version
            string p1Team = ReadStr(r), p1Off = ReadStr(r), p1Def = ReadStr(r), p1Sig = ReadStr(r);
            string p2Team = ReadStr(r), p2Off = ReadStr(r), p2Def = ReadStr(r), p2Sig = ReadStr(r);
            byte winner = r.ReadByte();

            long startingTick = 0;
            double p1StartMoney = 0, p2StartMoney = 0;
            if (version >= 2)
            {
                startingTick = r.ReadInt64();
                p1StartMoney = r.ReadDouble();
                p2StartMoney = r.ReadDouble();
            }
            else
            {
                // v1 replays need the time-machine state inferred, which lives in Program.
                // Rather than duplicate that, skip them — they are the oldest recordings
                // and predate the bot this analysis is about.
                throw new InvalidDataException("v1 replay (pre-time-machine-header), skipped");
            }

            uint tickCount = r.ReadUInt32();

            int timeSkip = (int)(startingTick / (30 * 30));
            var state = new GameState();
            state.Player1 = new PlayerState(timeSkip);
            state.Player2 = new PlayerState(timeSkip);
            state.Player1.Side = 1;
            state.Player2.Side = 2;
            state.Player1.Money = p1StartMoney;
            state.Player2.Money = p2StartMoney;
            state.CurrentTick = startingTick;
            state.Player1.Team = Enum.Parse<TeamColour>(p1Team, ignoreCase: true);
            state.Player2.Team = Enum.Parse<TeamColour>(p2Team, ignoreCase: true);
            var l1 = new[] { p1Off, p1Def, p1Sig }.Where(s => !string.IsNullOrEmpty(s)).ToArray();
            var l2 = new[] { p2Off, p2Def, p2Sig }.Where(s => !string.IsNullOrEmpty(s)).ToArray();
            if (l1.Length == 3) state.Player1.SetLoadout(l1);
            if (l2.Length == 3) state.Player2.SetLoadout(l2);

            var engine = new GameEngine(state);

            // The shadow sits in SIDE 1 — the human's seat.
            HeuristicBot shadowHeur = null;
            RolloutSearchBot shadowSearch = null;
            if (botKind == "heuristic") shadowHeur = new HeuristicBot(1);
            else shadowSearch = new RolloutSearchBot(1, interval, 300, 1, seed: 12345,
                                                     usePrior: true, overrideMargin: 0.10);

            var humanWindow = Spend.New();
            long nextDecision = startingTick;

            for (uint t = 0; t < tickCount && !state.IsGameOver; t++)
            {
                byte a1 = r.ReadByte();
                byte a2 = r.ReadByte();

                // ── Ask the shadow what it would do, BEFORE the human acts ────────
                if (state.CurrentTick >= nextDecision)
                {
                    nextDecision = state.CurrentTick + interval;

                    var snapshot = CloneStateOnly(state);
                    var clone = engine.Clone(rngSeed: 777);
                    if (shadowHeur != null) shadowHeur.Update(clone);
                    else shadowSearch.Update(clone);
                    var bot = Diff(snapshot, clone._state, 1);

                    var me = state.Player1;
                    var opp = state.Player2;
                    int enemyUnits = state.Units.Count(u => u.Side == 2);
                    int enemyTop = state.Units.Where(u => u.Side == 2).Select(u => u.Tier).DefaultIfEmpty(0).Max();

                    sw.WriteLine($"{gameId},{state.CurrentTick},{winner},{p1Team},{p2Team}," +
                                 $"{me.Income:F1},{me.Money:F0},{me.InvestmentCount}," +
                                 $"{(double)me.CastleHealth / Math.Max(1, me.CastleMaxHealth):F3}," +
                                 $"{(double)opp.CastleHealth / Math.Max(1, opp.CastleMaxHealth):F3}," +
                                 $"{enemyUnits},{enemyTop}," +
                                 $"{humanWindow.Money:F0},{humanWindow.TotalUnits},{humanWindow.TopTier}," +
                                 $"{humanWindow.Invests},{humanWindow.Repairs},{humanWindow.Gadgets}," +
                                 $"{bot.Money:F0},{bot.TotalUnits},{bot.TopTier}," +
                                 $"{bot.Invests},{bot.Repairs},{bot.Gadgets}");

                    humanWindow = Spend.New();
                }

                // ── Advance on the HUMAN's real actions ───────────────────────────
                var pre = CloneStateOnly(state);
                engine.ApplyAction(1, a1);
                engine.ApplyAction(2, a2);
                var post = Diff(pre, state, 1);
                humanWindow.Money += post.Money;
                for (int k = 1; k <= 8; k++) humanWindow.UnitsByTier[k] += post.UnitsByTier[k];
                humanWindow.Invests += post.Invests;
                humanWindow.Repairs += post.Repairs;
                humanWindow.Gadgets += post.Gadgets;

                engine.Tick();
            }
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
    }
}
