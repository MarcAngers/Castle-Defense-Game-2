using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    /// <summary>
    /// A trustworthy, reproducible benchmark ladder.
    ///
    /// WHY THIS EXISTS: a 2026-07-28 audit checked two of the numbers this project had
    /// been steering by and found both wrong -- `invest-stats` was reporting free
    /// headstart investments as if the policy had earned them, and the board evaluator's
    /// weights came from an unidentifiable fit. Two out of two is a base rate, not bad
    /// luck. Before committing to any new direction the project needs one benchmark it
    /// can actually believe.
    ///
    /// FOUR PROPERTIES the older modes lacked:
    ///
    ///  1. COMMON RANDOM NUMBERS. Every contender plays the *same* pre-generated list of
    ///     game setups (teams, loadouts, timeSkip, bonus cash) against the same opponents.
    ///     Comparisons are paired, which removes almost all setup variance -- the dominant
    ///     noise source. Independent random sampling per contender, as the old modes did,
    ///     needs several times the games for the same resolution.
    ///
    ///  2. SIDE-SWAPPED. Every setup is played twice, once with the contender as P1 and
    ///     once as P2. The campaign log spent two sessions chasing a real P1/P2 seat bias;
    ///     this makes the ladder immune to whatever remains of it by construction.
    ///
    ///  3. EARNED INVESTS, NOT RAW. Starting InvestmentCount is recorded per game and
    ///     subtracted. Under `headstart`, PlayerState(timeSkip) grants E[timeSkip] = 2.118
    ///     free investments to BOTH sides before either acts; reporting the end-of-game
    ///     count (as invest-stats did) reports mostly the time machine.
    ///
    ///  4. CONFIDENCE INTERVALS. Win rates carry a Wilson 95% interval. A bare percentage
    ///     invites reading noise as signal, which is exactly what happened with the
    ///     "2.76 vs 2.80 confirms it's real" reading.
    ///
    /// DETERMINISM: as of the 2026-07-28 seeding pass this should be fully reproducible.
    /// Three gameplay-affecting `new Random()` sites were replaced with a seeded stream —
    /// GameEngine.Rng (unit y-position on spawn), MeteorEffect (stagger + spread), and the
    /// GameState map/shadow-map roll. Each spec carries its own EngineSeed, so two
    /// contenders playing the same spec get the same random stream and any difference in
    /// outcome is attributable to their decisions.
    ///
    /// VERIFY THIS RATHER THAN TRUSTING IT: run the same seed twice and diff the CSVs.
    /// They should be byte-identical. If they are not, an unseeded random source is still
    /// in the loop and the confidence intervals below are optimistic. This is also the
    /// regression test for the upcoming data-based-effects refactor — same seed before
    /// and after should produce the same CSV.
    ///
    /// Usage:
    ///   ladder [games] [--seed N] [--headstart|--nostart|--both] [--csv path]
    ///          [--only substr] [--offense id] [--defense id]
    ///
    /// --offense / --defense pin that loadout slot on BOTH sides, overwriting the drawn
    /// value after spec generation so the rng stream is unaffected. Use it to measure a
    /// change that touches a single gadget family: a gadget is 1 of 4 draws, so on a normal
    /// run such a change is diluted into ~25% of games and comes back byte-identical on
    /// most rungs — unmeasurable rather than neutral. Pinned and unpinned runs on the same
    /// seed differ only in the pinned slot.
    /// </summary>
    public static class Ladder
    {
        // One fully-specified game setup. Generated once from the seed, replayed by
        // every contender so comparisons are paired.
        private sealed class GameSpec
        {
            public int TimeSkip;
            public bool BonusMoney;
            public TeamColour TeamA, TeamB;
            public TeamColour Map;
            public string OffA, DefA, OffB, DefB;
            // Per-game engine seed, drawn from the ladder seed. Two contenders playing
            // the same spec get the same engine stream, so any difference in outcome is
            // attributable to their decisions rather than to luck.
            public int EngineSeed;
        }

        private sealed class Record
        {
            public int Wins, Losses, Draws;
            public double EarnedInvests, OppEarnedInvests, Ticks;
            public int Games;

            // ── PREDICTED-SIGNATURE DIAGNOSTICS (added 2026-09-01) ────────────────────
            // Win rate alone cannot tell a change that worked from a change that worked
            // for the wrong reason, and this project's whole failure mode is fitting to a
            // number nobody re-derived. BOT_BACKLOG.md therefore requires every hypothesis
            // to name what should move AND what must not; these are the quantities those
            // predictions are written in.
            //
            // UnitsBought is the engine's own purchase counter, which excludes ignoreCost
            // spawns -- so it measures DECISIONS THAT LANDED, not bodies on the field. That
            // distinction is the point: a spawn refused for want of a charge increments
            // nothing anywhere, which is exactly how the charge blind spot stayed invisible.
            // EndMoney is the companion signal -- money that piled up because purchases
            // silently failed.
            public double UnitsBought, EndMoney, EndHpPct;

            public double WinRate => Games > 0 ? (double)Wins / Games : 0;
        }

        private static readonly string[] OffenseOptions = { "nuke", "firebomb", "snipe", "freeze" };
        private static readonly string[] DefenseOptions = { "heal", "reinforcements", "speed", "wall" };

        /// <summary>
        /// Wilson score interval — correct near 0 and 1, unlike the normal approximation,
        /// which matters here because several matchups sit at 0-25% win rate.
        /// </summary>
        public static (double lo, double hi) WilsonInterval(int wins, int n, double z = 1.96)
        {
            if (n == 0) return (0, 0);
            double p = (double)wins / n;
            double d = 1 + z * z / n;
            double centre = (p + z * z / (2.0 * n)) / d;
            double half = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / d;
            return (Math.Max(0, centre - half), Math.Min(1, centre + half));
        }

        private static List<GameSpec> BuildSpecs(int count, int seed, bool headstart)
        {
            var rng = new Random(seed);
            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            var specs = new List<GameSpec>(count);
            for (int i = 0; i < count; i++)
            {
                int ts = headstart ? Math.Max(rng.Next(-8, 9), 0) : 0;
                specs.Add(new GameSpec
                {
                    TimeSkip = ts,
                    // Mirrors CreateGame's 1-in-5 starting-cash coin flip, but drawn from
                    // OUR seeded rng so the setup is reproducible.
                    BonusMoney = ts > 0 && rng.Next(5) == 0,
                    TeamA = teams[rng.Next(teams.Length)],
                    TeamB = teams[rng.Next(teams.Length)],
                    Map = teams[rng.Next(teams.Length)],
                    OffA = OffenseOptions[rng.Next(OffenseOptions.Length)],
                    DefA = DefenseOptions[rng.Next(DefenseOptions.Length)],
                    OffB = OffenseOptions[rng.Next(OffenseOptions.Length)],
                    DefB = DefenseOptions[rng.Next(DefenseOptions.Length)],
                    EngineSeed = rng.Next(),
                });
            }
            return specs;
        }

        private static void ApplyLoadout(PlayerState p, int side, TeamColour team,
                                         string offense, string defense, int timeSkip)
        {
            string upg = timeSkip > 5 ? "_3" : timeSkip > 3 ? "_2" : "";
            p.Side = side;
            p.Team = team;
            p.SetLoadout(new[]
            {
                offense + upg,
                defense + upg,
                GameDataManager.GetSignatureGadgetIdForTeam(team) + upg,
            });
        }

        /// <summary>
        /// Builds a game from a spec. `contenderIsP1` decides which side of the spec the
        /// contender occupies, so the same spec can be played both ways.
        /// </summary>
        private static (GameState state, GameEngine engine) CreateFromSpec(GameSpec s, bool contenderIsP1)
        {
            // Seeded GameState ctor: the map roll (shadow-map selection) is real
            // gameplay-affecting randomness and has to be pinned too, or two contenders
            // playing "the same" spec can end up on different maps.
            var state = new GameState(s.Map, new Random(s.EngineSeed));
            state.Player1 = new PlayerState(s.TimeSkip);
            state.Player2 = new PlayerState(s.TimeSkip);

            var (t1, o1, d1) = contenderIsP1 ? (s.TeamA, s.OffA, s.DefA) : (s.TeamB, s.OffB, s.DefB);
            var (t2, o2, d2) = contenderIsP1 ? (s.TeamB, s.OffB, s.DefB) : (s.TeamA, s.OffA, s.DefA);
            ApplyLoadout(state.Player1, 1, t1, o1, d1, s.TimeSkip);
            ApplyLoadout(state.Player2, 2, t2, o2, d2, s.TimeSkip);

            if (s.BonusMoney)
            {
                state.Player1.Money = state.Player1.InvestmentPrice + state.Player1.Income;
                state.Player2.Money = state.Player2.InvestmentPrice + state.Player2.Income;
            }

            state.CurrentTick = 30 * 30 * s.TimeSkip;
            return (state, new GameEngine(state, null, s.EngineSeed));
        }

        private static Record PlayMatchup(
            Func<int, IArenaOpponent> makeContender,
            Func<int, IArenaOpponent> makeOpponent,
            List<GameSpec> specs)
        {
            var rec = new Record();
            foreach (var spec in specs)
            {
                // Each spec played twice, sides swapped — removes any residual seat bias.
                for (int pass = 0; pass < 2; pass++)
                {
                    bool contenderIsP1 = pass == 0;
                    int cSide = contenderIsP1 ? 1 : 2;
                    var (state, engine) = CreateFromSpec(spec, contenderIsP1);

                    var contender = makeContender(cSide);
                    var opponent = makeOpponent(contenderIsP1 ? 2 : 1);

                    var cPlayer = contenderIsP1 ? state.Player1 : state.Player2;
                    var oPlayer = contenderIsP1 ? state.Player2 : state.Player1;
                    int cStartInvests = cPlayer.InvestmentCount;
                    int oStartInvests = oPlayer.InvestmentCount;
                    long startTick = state.CurrentTick;

                    while (!state.IsGameOver)
                    {
                        engine.Tick();
                        // Update in board order so neither side gets a systematic
                        // within-tick advantage from the harness itself.
                        if (contenderIsP1) { contender.Update(engine); opponent.Update(engine); }
                        else               { opponent.Update(engine); contender.Update(engine); }
                    }

                    if (state.WinnerSide == cSide) rec.Wins++;
                    else if (state.WinnerSide == 0) rec.Draws++;
                    else rec.Losses++;

                    rec.EarnedInvests += cPlayer.InvestmentCount - cStartInvests;
                    rec.OppEarnedInvests += oPlayer.InvestmentCount - oStartInvests;
                    rec.Ticks += state.CurrentTick - startTick;
                    rec.UnitsBought += engine.UnitsPurchased[cSide];
                    rec.EndMoney += cPlayer.Money;
                    rec.EndHpPct += cPlayer.CastleMaxHealth > 0
                        ? 100.0 * cPlayer.CastleHealth / cPlayer.CastleMaxHealth : 0;
                    rec.Games++;

                    (contender as IDisposable)?.Dispose();
                    (opponent as IDisposable)?.Dispose();
                }
            }
            return rec;
        }

        public static void Run(string[] args, Func<string?> findLeagueModelsDir)
        {
            int games = 100;
            int seed = 12345;
            string mode = "both";
            string csvPath = "ladder_results.csv";
            string? only = null;
            string? pinOffense = null;
            string? pinDefense = null;
            // Adds a second HeuristicBot contender running a named HeuristicBotSettings
            // profile, so a candidate flag and the committed reference play the SAME
            // pre-generated specs inside ONE run. That matters more than usual here: the
            // mirror rung moves 3-5 points on seed alone (flag (9)'s own evidence -- the
            // reference scored 44.83/47.67 nostart and 46.00/50.83 headstart across two
            // seeds), so comparing two SEPARATE runs risks attributing seed noise to the
            // flag. Paired inside one run, both contenders face identical setups.
            // Repeatable: several candidate profiles and the committed reference all play
            // the SAME specs inside ONE run, which is what makes both the win-rate deltas
            // and the throughput ratio paired rather than cross-run.
            var variants = new List<string>();

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i]); break;
                    case "--csv" when i + 1 < args.Length: csvPath = args[++i]; break;
                    case "--only" when i + 1 < args.Length: only = args[++i]; break;
                    case "--variant" when i + 1 < args.Length: variants.Add(args[++i]); break;
                    // Loadout pins. Applied AFTER BuildSpecs so the rng stream is untouched
                    // and a pinned run stays directly comparable to an unpinned one on the
                    // same seed -- everything except the pinned slot is the same game.
                    //
                    // WHY THIS EXISTS: several candidate changes touch ONE gadget family
                    // (the firebomb friendly-fire fix, the freeze/blackhole early-stall
                    // change). A gadget is 1 of 4 draws, so on a normal ladder those
                    // changes are diluted into ~25% of games and come back byte-identical
                    // on most rungs -- unmeasurable rather than neutral. Pinning the slot
                    // makes the affected games 100% of the sample instead.
                    case "--offense" when i + 1 < args.Length: pinOffense = args[++i]; break;
                    case "--defense" when i + 1 < args.Length: pinDefense = args[++i]; break;
                    case "--headstart": mode = "headstart"; break;
                    case "--nostart": mode = "nostart"; break;
                    case "--both": mode = "both"; break;
                    default:
                        if (int.TryParse(args[i], out var g)) games = g;
                        break;
                }
            }

            // ── Ladder opponents: fixed, deterministic, never changes between runs ──
            // Deliberately excludes league models: the point is a STABLE yardstick, and a
            // league folder whose contents drift makes runs incomparable over time. That
            // drift already caused a real problem (see the 2026-07-28 "League pool bloat"
            // entry in the campaign log).
            // RusherBaseline is deliberately absent: it buys "cheapest affordable", which
            // for every roster is the tier-1 unit, making it a duplicate of Tier1Spam.
            // The first ladder run showed the two producing byte-identical results, so
            // including both just doubles the cost of that rung for no information.
            var ladder = new List<(string name, Func<int, IArenaOpponent> make)>
            {
                ("DoNothing",     side => new DoNothingBaseline()),
                ("Tier1Spam",     side => new TierSpamBaseline(side, 1)),
                ("Tier4Spam",     side => new TierSpamBaseline(side, 4)),
                ("Investor",      side => new InvestorBaseline(side)),
                ("BalancedHuman", side => new BalancedHumanBaseline(side)),
                // The only rung not derived from HeuristicBot or from a spam pattern: a
                // behaviour clone fitted to Marc's recorded games. Added because every
                // other rung, HeuristicBot most of all, is inside the family that
                // RolloutSearchBot uses as its own prior AND rollout policy — so the
                // ladder could reward better self-simulation and read it as strength.
                ("HumanClone",    side => new HumanCloneBaseline(side)),
                ("HeuristicBot",  side => new HeuristicBotAdapter(side)),
            };

            // ── Contenders: HeuristicBot as the reference, plus every ONNX checkpoint ──
            var contenders = new List<(string name, Func<int, IArenaOpponent> make)>
            {
                ("HeuristicBot", side => new HeuristicBotAdapter(side)),
                // Probe A's candidate rollout policy, at four commitment levels. Present as
                // contenders because the probe's premise is that this is STRONGER than
                // HeuristicBot; if the rows below do not beat the HeuristicBot row, the
                // probe is measuring nothing and must not be run. 1.00 is the mildest form
                // (invest on sight, otherwise play normally); 0.25 stops attacking a quarter
                // of the way to the next investment.
                ("SavingHeuristic@1.00", side => new SavingHeuristicBaseline(side, 1.00)),
                ("SavingHeuristic@0.70", side => new SavingHeuristicBaseline(side, 0.70)),
                ("SavingHeuristic@0.50", side => new SavingHeuristicBaseline(side, 0.50)),
                ("SavingHeuristic@0.25", side => new SavingHeuristicBaseline(side, 0.25)),
            };

            foreach (var variant in variants)
            {
                var fld = typeof(HeuristicBotSettings).GetField(variant,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (fld == null)
                {
                    Console.WriteLine($"[ladder] No HeuristicBotSettings profile named '{variant}'. Available:");
                    foreach (var f in typeof(HeuristicBotSettings).GetFields(
                                 System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                        Console.WriteLine("    " + f.Name);
                    return;
                }
                var profile = (HeuristicBotSettings)fld.GetValue(null)!;
                contenders.Add(($"HeuristicBot+{variant}", side => new HeuristicBotAdapter(side, profile)));
                Console.WriteLine($"[ladder] variant contender: HeuristicBot+{variant}");
            }

            var dir = findLeagueModelsDir();
            if (dir != null)
            {
                foreach (var f in Directory.GetFiles(dir, "*.onnx").OrderBy(x => x))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    string path = f;
                    contenders.Add((name, side => new AIModelOpponent(side, path)));
                }
            }
            else
            {
                Console.WriteLine("[ladder] No league_models folder found — running baselines only.");
            }

            if (only != null)
                contenders = contenders
                    .Where(c => c.name.Contains(only, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            var modes = mode == "both" ? new[] { "nostart", "headstart" } : new[] { mode };

            using var csv = new StreamWriter(csvPath);
            csv.WriteLine("mode,seed,contender,opponent,games,wins,losses,draws," +
                          "win_rate,ci_lo,ci_hi,earned_invests,opp_earned_invests,avg_ticks," +
                          "elapsed_s,ticks_per_s,units_per_s,end_money,end_hp_pct");

            foreach (var m in modes)
            {
                bool headstart = m == "headstart";
                var specs = BuildSpecs(games, seed, headstart);

                // Overwrite the drawn loadout slot(s) AFTER generation -- see the --offense
                // case above. BuildSpecs has already consumed its rng draws, so pinning
                // changes only which gadget is equipped, never which teams/maps/timeSkips
                // the run plays.
                if (pinOffense != null || pinDefense != null)
                {
                    foreach (var s in specs)
                    {
                        if (pinOffense != null) { s.OffA = pinOffense; s.OffB = pinOffense; }
                        if (pinDefense != null) { s.DefA = pinDefense; s.DefB = pinDefense; }
                    }
                    Console.WriteLine($"  [pinned loadout] offense={pinOffense ?? "(drawn)"}  " +
                                      $"defense={pinDefense ?? "(drawn)"}");
                }

                Console.WriteLine();
                Console.WriteLine(new string('=', 100));
                Console.WriteLine($"  LADDER [{m}]  seed={seed}  {games} setups x 2 sides = " +
                                  $"{games * 2} games per matchup");
                if (headstart)
                    Console.WriteLine($"  (headstart grants both sides E[timeSkip]=2.118 free invests; " +
                                      $"invests below are EARNED only)");
                Console.WriteLine(new string('=', 100));

                // UNTIMED WARM-UP. Without it the first contender pays JIT for the whole
                // engine and every later contender looks faster: an n=20 smoke run reported
                // 260k / 345k / 377k ticks/s in run order for three contenders whose real
                // costs are within a few percent. Throughput is a headline number of this
                // run, so the warm-up is not optional.
                var warmSpecs = specs.Take(Math.Min(4, specs.Count)).ToList();
                foreach (var (_, wMake) in contenders)
                    foreach (var (_, oMake) in ladder)
                        PlayMatchup(wMake, oMake, warmSpecs);

                foreach (var (cName, cMake) in contenders)
                {
                    Console.WriteLine($"\n  {cName}");
                    Console.WriteLine("    opponent                       win rate  earned inv   opp inv    avg s  un/s     idle$  hp%");
                    Console.WriteLine("    " + new string('-', 94));

                    int totWins = 0, totGames = 0;
                    // WALL CLOCK PER CONTENDER. Both contenders play the SAME pre-generated
                    // specs against the same opponents inside one run on one machine, so the
                    // ratio of these totals is a paired throughput comparison -- the only
                    // honest way to price a change that adds per-decision work. Reported as
                    // simulated ticks/sec because contenders whose games run to different
                    // lengths would make raw seconds incomparable.
                    double contenderSeconds = 0;
                    double contenderTicks = 0;
                    foreach (var (oName, oMake) in ladder)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var r = PlayMatchup(cMake, oMake, specs);
                        sw.Stop();
                        double rungSeconds = sw.Elapsed.TotalSeconds;
                        contenderSeconds += rungSeconds;
                        contenderTicks += r.Ticks;
                        var (lo, hi) = WilsonInterval(r.Wins, r.Games);
                        totWins += r.Wins;
                        totGames += r.Games;

                        Console.WriteLine(
                            $"    {oName,-16} {r.WinRate,7:P1} [{lo,6:P1},{hi,6:P1}]  " +
                            $"{r.EarnedInvests / r.Games,10:F2} {r.OppEarnedInvests / r.Games,8:F2}  " +
                            $"{r.Ticks / r.Games / 30.0,7:F1} " +
                            $"{(r.Ticks > 0 ? r.UnitsBought / (r.Ticks / 30.0) : 0),6:F2} " +
                            $"{r.EndMoney / r.Games,9:N0} {r.EndHpPct / r.Games,6:F1}");

                        csv.WriteLine($"{m},{seed},{cName},{oName},{r.Games},{r.Wins},{r.Losses}," +
                                      $"{r.Draws},{r.WinRate:F4},{lo:F4},{hi:F4}," +
                                      $"{r.EarnedInvests / r.Games:F4}," +
                                      $"{r.OppEarnedInvests / r.Games:F4}," +
                                      $"{r.Ticks / r.Games:F1}," +
                                      $"{rungSeconds:F2}," +
                                      $"{(rungSeconds > 0 ? r.Ticks / rungSeconds : 0):F0}," +
                                      $"{(r.Ticks > 0 ? r.UnitsBought / (r.Ticks / 30.0) : 0):F3}," +
                                      $"{r.EndMoney / r.Games:F0}," +
                                      $"{r.EndHpPct / r.Games:F1}");
                        csv.Flush();
                    }

                    var (alo, ahi) = WilsonInterval(totWins, totGames);
                    Console.WriteLine("    " + new string('-', 94));
                    Console.WriteLine($"    {"OVERALL",-16} {(double)totWins / Math.Max(totGames, 1),7:P1} " +
                                      $"[{alo,6:P1},{ahi,6:P1}]   ({totGames} games)");
                    Console.WriteLine($"    {"THROUGHPUT",-16} {contenderSeconds,7:F1}s  " +
                                      $"{(contenderSeconds > 0 ? contenderTicks / contenderSeconds : 0),12:N0} ticks/s");
                }
            }

            Console.WriteLine($"\n[ladder] Wrote {csvPath}");
            Console.WriteLine("[ladder] Same --seed reproduces the same setups, so two runs are " +
                              "directly comparable.");
        }
    }
}
