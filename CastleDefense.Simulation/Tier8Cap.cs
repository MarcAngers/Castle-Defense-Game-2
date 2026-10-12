using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;
using CastleDefense.Engine.Replays;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// Marc, 2026-09-14, on DB3C84: the human's auto-spawner reached level 18, and the bot
    /// answered with a dozen $23,000 tier 8s. "A lot of things in this game are AoE, so a huge
    /// group of tier 8 units is about as easy to deal with as a single tier 8."
    ///
    ///   trace <id> [--variant P] [--min-tier T]
    ///       Replays the human's recorded stream and drives the LIVE bot in seat 2, then checks
    ///       the live bot reproduces its own recorded action stream. Only if it does are the
    ///       diagnostics it prints at each purchase a description of the game Marc played.
    ///
    ///   test <id> [--variants A,B,..] [--games N] [--seed S] [--maps]
    ///       Replay gauntlet: the live bot against the recorded human stream, one arm per
    ///       profile, paired engine seeds. Seed 0 of the list is the recorded seed itself.
    /// </summary>
    public static class Tier8Cap
    {
        public static void Run(string[] args, string recordingsDir)
        {
            GameDataManager.Initialize();
            string mode = args.Length > 1 ? args[1] : "";
            string id = args.Length > 2 ? args[2] : null;
            if (id == null) { Usage(); return; }
            if (mode == "corpus") { Corpus(id.Split(','), recordingsDir, args); return; }
            string path = File.Exists(id) ? id : Path.Combine(recordingsDir, "singleplayer", id + ".replay");
            if (!File.Exists(path)) { Console.WriteLine($"No replay at {path}"); return; }
            var rf = ReplayFile.Read(path);

            switch (mode)
            {
                case "trace": Trace(rf, args); break;
                case "test": Test(rf, args); break;
                default: Usage(); break;
            }
        }

        private static void Usage() =>
            Console.WriteLine("usage: --tier8-cap trace <id> [--variant P] [--min-tier T]\n" +
                              "       --tier8-cap test <id> [--variants A,B,C] [--games N] [--seed S] [--maps]");

        private static string Opt(string[] args, string name, string dflt)
        {
            int k = Array.IndexOf(args, name);
            return k >= 0 && k + 1 < args.Length ? args[k + 1] : dflt;
        }

        private static HeuristicBotSettings Resolve(string name)
        {
            var f = typeof(HeuristicBotSettings).GetField(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (f == null) Console.WriteLine($"No HeuristicBotSettings profile named '{name}'.");
            return (HeuristicBotSettings)f?.GetValue(null);
        }

        // ── trace ─────────────────────────────────────────────────────────────────────
        private static void Trace(ReplayFile rf, string[] args)
        {
            var settings = Resolve(Opt(args, "--variant", "EconomyBrakeAutoSpawnProfile"));
            if (settings == null) return;
            int minTier = int.Parse(Opt(args, "--min-tier", "8"));

            var (state, engine) = rf.BuildStart();
            long start = state.CurrentTick;
            var bot = new HeuristicBot(2, settings);
            var roster = GameDataManager.Teams.Find(t => t.Color == state.Player2.Team).Roster;
            int botCastle = GameEngine.MAP_WIDTH - 200;

            int mismatches = 0; long firstMismatch = -1;
            int p1Fail = 0;
            var lines = new List<string>();

            while (!state.IsGameOver && state.CurrentTick < GameEngine.MAX_TICKS)
            {
                // THE LIVE LOOP'S ORDER (GameHostingService): bot.Update at CurrentTick T acts
                // IMMEDIATELY, then engine.Tick -- whose ProcessActions drains the human's queued
                // click, i.e. AFTER the bot -- then RecordTick appends entry T - start. So: bot,
                // then ReplayFile.Step(p2: false), which applies the human's entry and ticks.
                long i = state.CurrentTick - start;

                // ActionCounts is bumped when Act() QUEUES, not when the action lands, so read the
                // engine's own per-tick record instead -- the same field GameRecorder writes.
                engine.ResetLastActions();
                double unitsBefore = engine.MoneySpentOnUnits[2];
                int t8Before = state.Units.Count(u => u.Side == 2 && u.Tier == 8);
                double moneyBefore = state.Player2.Money;
                bot.Update(engine);
                int did = engine.LastActionP2;

                byte rec = i >= 0 && i < rf.A2.Length ? rf.A2[i] : (byte)0;
                if (did != rec)
                {
                    if (mismatches < 8) lines.Add($"  mismatch tick {state.CurrentTick} (i={i}): live bot {did}, recorded {rec}");
                    mismatches++;
                    if (firstMismatch < 0) firstMismatch = i;
                }

                // The purchase report reads the board after the human's click has landed and
                // before the tick, which is where it always read it.
                rf.Step(engine, p2: false, after: (_, _, ok) => { if (!ok) p1Fail++; }, beforeTick: _ =>
                {
                    if (did < minTier || did > 8 || engine.MoneySpentOnUnits[2] <= unitsBefore) return;
                    var me = state.Player2;
                    var enemies = state.Units.Where(u => u.Side == 1).ToList();
                    int near700 = enemies.Count(u => Math.Abs(u.Position - botCastle) <= 700);
                    int near500 = enemies.Count(u => Math.Abs(u.Position - botCastle) <= 500);
                    var tiers = enemies.GroupBy(u => u.Tier).OrderByDescending(g => g.Key)
                                       .Select(g => $"T{g.Key}x{g.Count()}");
                    double nearestEnemy = enemies.Count == 0 ? double.NaN : enemies.Min(u => Math.Abs(u.Position - botCastle));
                    lines.Add($"{i / 30.0,6:F1}s  BUY {roster[did - 1].Id} (T{did}) ${roster[did - 1].Cost:N0}  " +
                              $"reason={bot.LastSpawnReason}  own T8 on field before={t8Before}");
                    lines.Add($"         money ${moneyBefore:N0} -> ${me.Money:N0}  hp {100.0 * me.CastleHealth / me.CastleMaxHealth:F0}%  " +
                              $"invest {me.InvestmentCount}  armageddon price ${me.InvestmentPrice:N0}");
                    lines.Add($"         danger={bot.LastDecisionWasDanger} survivalEmergency={bot.LastSurvivalEmergency} " +
                              $"flowUncapped={bot.LastSurvivalUncappedFlow} killerInstinct={bot.LastKillerInstinct} " +
                              $"TTD={bot.LastTimeToDeathSeconds:F1}s geoTTD={bot.LastGeoTimeToDeathSeconds:F1}s " +
                              $"timeToInvest={bot.LastTimeToInvestSeconds:F1}s committedValue(500px)=${bot.LastWaveWipeValue:N0}");
                    lines.Add($"         enemy: {enemies.Count} units [{string.Join(" ", tiers)}], {near700} within 700, " +
                              $"{near500} within 500, nearest {nearestEnemy:F0}px from our castle");
                    lines.Add($"         {bot.LastSpendDebug}");
                });
            }

            Console.WriteLine($"=== LIVE BOT TRACE -- {rf.GameId}  P1 {rf.P1Team} vs bot {rf.P2Team} ===");
            Console.WriteLine($"  rebuilt {(state.CurrentTick - start) / 30.0:F1}s winner P{state.WinnerSide}  " +
                              $"(recorded {rf.TickCount / 30.0:F1}s winner P{rf.Winner})");
            Console.WriteLine($"  bot actions differing from the recorded bot: {mismatches}" +
                              (firstMismatch >= 0 ? $" (first at {firstMismatch / 30.0:F1}s)" : " -- EXACT reproduction"));
            Console.WriteLine($"  human recorded actions refused: {p1Fail}");
            Console.WriteLine();
            foreach (var l in lines) Console.WriteLine(l);
        }

        // ── test ──────────────────────────────────────────────────────────────────────
        private sealed class Result
        {
            public int Games, BotWins, HumanWins, Draws, BotArma, HumanArma;
            public double Seconds, T8, T8Spend, T8Late, MaxT8, T7, UnitSpend, Reactive, EndMoney, BotHp, HumanHp;
            public double HumanTried, HumanLanded, CapBlocks;

            public void Add(Result r)
            {
                Games += r.Games; BotWins += r.BotWins; HumanWins += r.HumanWins; Draws += r.Draws;
                BotArma += r.BotArma; HumanArma += r.HumanArma; Seconds += r.Seconds; T8 += r.T8;
                T8Spend += r.T8Spend; T8Late += r.T8Late; MaxT8 += r.MaxT8; T7 += r.T7; UnitSpend += r.UnitSpend;
                Reactive += r.Reactive; EndMoney += r.EndMoney; BotHp += r.BotHp; HumanHp += r.HumanHp;
                HumanTried += r.HumanTried; HumanLanded += r.HumanLanded; CapBlocks += r.CapBlocks;
            }
        }

        private static void Test(ReplayFile rf, string[] args)
        {
            var names = Opt(args, "--variants", "EconomyBrakeAutoSpawnProfile,ShipTier8Cap2,ShipTier8Cap2Hold").Split(',');
            int games = int.Parse(Opt(args, "--games", "40"));
            int seed = int.Parse(Opt(args, "--seed", "4242"));
            bool maps = args.Contains("--maps");
            var profiles = names.Select(Resolve).ToArray();
            if (profiles.Any(p => p == null)) return;

            var totals = names.Select(_ => new Result()).ToArray();
            var perArm = new Result[names.Length][];
            var rng = new Random(seed);
            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            var seeds = Enumerable.Range(0, games).Select(g => (
                es: g == 0 ? (int?)null : rng.Next(),
                map: maps && g > 0 ? teams[g % teams.Length] : (TeamColour?)null)).ToArray();

            Console.WriteLine($"=== TIER-8 CAP GAUNTLET -- {rf.GameId}  P1 {rf.P1Team} (recorded stream) vs live bot {rf.P2Team} ===");
            for (int a = 0; a < names.Length; a++)
            {
                var per = new Result[games];
                int pa = a;
                Parallel.For(0, games, g => per[g] = PlayOne(rf, profiles[pa], seeds[g].es, seeds[g].map));
                perArm[a] = per;
                foreach (var r in per) totals[a].Add(r);
                var r0 = per[0];
                Console.WriteLine($"  recorded seed  {names[a],-30} winner {(r0.BotWins == 1 ? "BOT" : r0.HumanWins == 1 ? "human" : "draw")} " +
                                  $"at {r0.Seconds:F0}s | tier 8s bought {r0.T8} (${r0.T8Spend:N0}), max {r0.MaxT8} up at once, " +
                                  $"tier 7s {r0.T7} | bot castle {r0.BotHp:F0}%, human castle {r0.HumanHp:F0}% | " +
                                  $"bot bank at end ${r0.EndMoney:N0}");
            }

            Console.WriteLine();
            Console.WriteLine($"  {games} paired games: game 0 is the recorded seed and map; the rest vary the engine seed" +
                              $"{(maps ? " and sweep all 8 maps" : "")}. Per-game averages.");
            Console.WriteLine($"  {"arm",-30} {"bot win",8} {"W-L-D",9} {"T8 buys",7} {"T8 $",8} {"T8 >300s",8} {"max T8",6} " +
                              $"{"T7 buys",7} {"unit $",8} {"reactive $",10} {"end $",8} {"bot hp",6} {"hum hp",6} {"bot/hum ARMA",12} {"len",4} {"holds",6}");
            foreach (var (name, t) in names.Zip(totals))
            {
                double n = Math.Max(1, t.Games);
                Console.WriteLine($"  {name,-30} {100.0 * t.BotWins / n,7:F1}% {$"{t.BotWins}-{t.HumanWins}-{t.Draws}",9} {t.T8 / n,7:F2} " +
                                  $"{t.T8Spend / n,8:N0} {t.T8Late / n,8:F2} {t.MaxT8 / n,6:F2} {t.T7 / n,7:F2} {t.UnitSpend / n,8:N0} " +
                                  $"{t.Reactive / n,10:N0} {t.EndMoney / n,8:N0} {t.BotHp / n,5:F0}% {t.HumanHp / n,5:F0}% " +
                                  $"{$"{t.BotArma}/{t.HumanArma}",12} {t.Seconds / n,4:F0} {t.CapBlocks / n,6:F1}");
            }

            // PAIRED FLIPS against the first arm: the same seed and map in both, so a flip is the change.
            Console.WriteLine();
            for (int a = 1; a < names.Length; a++)
            {
                int saved = 0, lost = 0, touched = 0;
                for (int g = 0; g < games; g++)
                {
                    var b = perArm[0][g]; var v = perArm[a][g];
                    if (b.MaxT8 > 2) touched++;
                    if (b.BotWins == 0 && v.BotWins == 1) saved++;
                    if (b.BotWins == 1 && v.BotWins == 0) lost++;
                }
                Console.WriteLine($"  {names[a],-30} vs {names[0]}: bot win flipped loss->win {saved}, win->loss {lost}; " +
                                  $"baseline exceeded 2 tier 8s in {touched} of {games} games " +
                                  $"(win rate there: baseline {Rate(perArm[0], games, g => perArm[0][g].MaxT8 > 2)}, " +
                                  $"this arm {Rate(perArm[a], games, g => perArm[0][g].MaxT8 > 2)})");
            }
        }

        /// <summary>
        /// The same paired comparison over many of Marc's games. Pass ids the caller has already
        /// filtered to real results (no disconnect/abandoned) -- this reads no database.
        /// --machine-only keeps games where the human pressed the auto-spawner at least once.
        /// </summary>
        private static void Corpus(string[] ids, string recordingsDir, string[] args)
        {
            var names = Opt(args, "--variants", "EconomyBrakeAutoSpawnProfile").Split(',');
            int games = int.Parse(Opt(args, "--games", "8"));
            bool machineOnly = args.Contains("--machine-only");
            var profiles = names.Select(Resolve).ToArray();
            if (profiles.Any(p => p == null)) return;

            var files = new List<ReplayFile>();
            foreach (var id in ids)
            {
                string path = Path.Combine(recordingsDir, "singleplayer", id + ".replay");
                if (!File.Exists(path)) { Console.WriteLine($"  {id}: no replay, skipped"); continue; }
                ReplayFile rf;
                try { rf = ReplayFile.Read(path); } catch (Exception e) { Console.WriteLine($"  {id}: {e.Message}"); continue; }
                if (machineOnly && !rf.A1.Contains((byte)14)) continue;
                files.Add(rf);
            }
            Console.WriteLine($"=== CORPUS -- {files.Count} games{(machineOnly ? " with an auto-spawner press" : "")}, " +
                              $"{games} seeds each (game 0 = recorded seed and map, the rest sweep maps) ===");

            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            var totals = names.Select(_ => new Result()).ToArray();
            var humanWinsPerGame = names.Select(_ => new int[files.Count]).ToArray();
            for (int a = 0; a < names.Length; a++)
            {
                var per = new Result[files.Count * games];
                int pa = a;
                Parallel.For(0, per.Length, k =>
                {
                    int f = k / games, g = k % games;
                    var rng = new Random(4242 + g);
                    int? es = g == 0 ? null : rng.Next();
                    TeamColour? map = g == 0 ? null : teams[g % teams.Length];
                    per[k] = PlayOne(files[f], profiles[pa], es, map);
                });
                for (int k = 0; k < per.Length; k++)
                {
                    totals[a].Add(per[k]);
                    humanWinsPerGame[a][k / games] += per[k].HumanWins;
                }
            }

            Console.WriteLine($"  {"arm",-30} {"HUMAN win",9} {"unit $",8} {"reactive $",10} {"T8 buys",7} {"end $",8} {"bot ARMA",8} {"len",4}");
            for (int a = 0; a < names.Length; a++)
            {
                var t = totals[a]; double n = Math.Max(1, t.Games);
                Console.WriteLine($"  {names[a],-30} {100.0 * t.HumanWins / n,8:F1}% {t.UnitSpend / n,8:N0} {t.Reactive / n,10:N0} " +
                                  $"{t.T8 / n,7:F2} {t.EndMoney / n,8:N0} {100.0 * t.BotArma / n,7:F1}% {t.Seconds / n,4:F0}");
            }
            for (int a = 1; a < names.Length; a++)
            {
                int better = 0, worse = 0;
                for (int f = 0; f < files.Count; f++)
                {
                    if (humanWinsPerGame[a][f] < humanWinsPerGame[0][f]) better++;
                    else if (humanWinsPerGame[a][f] > humanWinsPerGame[0][f]) worse++;
                }
                Console.WriteLine($"  {names[a]}: bot does better on {better} games, worse on {worse}, same on {files.Count - better - worse}");
            }
        }

        private static string Rate(Result[] rs, int games, Func<int, bool> where)
        {
            var idx = Enumerable.Range(0, games).Where(where).ToList();
            if (idx.Count == 0) return "n/a";
            return $"{100.0 * idx.Count(g => rs[g].BotWins == 1) / idx.Count:F1}% of {idx.Count}";
        }

        private static Result PlayOne(ReplayFile rf, HeuristicBotSettings settings, int? engineSeed, TeamColour? map)
        {
            var (state, engine) = rf.BuildStart(engineSeed, map);
            long start = state.CurrentTick;
            var bot = new HeuristicBot(2, settings);
            var r = new Result { Games = 1 };
            bool recorded = engineSeed == null && map == null;

            while (!state.IsGameOver && state.CurrentTick < GameEngine.MAX_TICKS)
            {
                // Live-loop order, as verified by `trace`: bot, then the human's queued click, then Tick.
                long i = state.CurrentTick - start;
                engine.ResetLastActions();
                double spent = engine.MoneySpentOnUnits[2];
                bot.Update(engine);
                double paid = engine.MoneySpentOnUnits[2] - spent;
                if (paid > 0 && engine.LastActionP2 == 8)
                {
                    r.T8++; r.T8Spend += paid;
                    if (i >= 300 * 30) r.T8Late++;
                }
                else if (paid > 0 && engine.LastActionP2 == 7) r.T7++;

                // On the recorded seed the board is the real one, so the recorded aim is valid;
                // elsewhere the gadget is re-aimed, as the replay gauntlet does.
                rf.Step(engine, p2: false, reaim: !recorded,
                        after: (_, _, ok) => { r.HumanTried++; if (ok) r.HumanLanded++; });

                int onField = 0;
                foreach (var u in state.Units) if (u.Side == 2 && u.Tier == 8) onField++;
                if (onField > r.MaxT8) r.MaxT8 = onField;
            }

            var p1 = state.Player1; var p2 = state.Player2;
            if (state.WinnerSide == 2) r.BotWins = 1; else if (state.WinnerSide == 1) r.HumanWins = 1; else r.Draws = 1;
            r.Seconds = (state.CurrentTick - start) / 30.0;
            r.UnitSpend = engine.MoneySpentOnUnits[2];
            r.Reactive = bot.SpendReactive;
            r.EndMoney = p2.Money;
            r.BotHp = 100.0 * p2.CastleHealth / Math.Max(1, p2.CastleMaxHealth);
            r.HumanHp = 100.0 * p1.CastleHealth / Math.Max(1, p1.CastleMaxHealth);
            r.BotArma = p2.ArmageddonUsed ? 1 : 0;
            r.HumanArma = p1.ArmageddonUsed ? 1 : 0;
            r.CapBlocks = bot.Tier8CapBlocks + bot.RaceReactiveHolds;   // flag 28 hold / flag 30 hold
            return r;
        }
    }
}
