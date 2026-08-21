using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    /// <summary>
    /// Measures the CHUMP-BLOCK: how much longer a defender survives a single advancing
    /// high-tier unit when it feeds cheap tier-1 bodies in front of it.
    ///
    /// The scenario is deliberately sterile rather than a real game. One attacker unit is
    /// spawned once and never reinforced; the defender either does nothing (the control)
    /// or spawns one tier-1 unit every N ticks and nothing else. No gadgets, no investing,
    /// no repairs, both castles pinned at the same HP and both economies set far above
    /// anything either side could spend. What comes out is the isolated value of the
    /// blocking mechanic, in seconds of delay and in dollars per second bought.
    ///
    /// WHY THE ENGINE MAKES THIS WORK AT ALL, mechanically (GameEngine.MoveAndFight):
    ///  - A unit with any enemy in range sets CurrentSpeed = 0 and attacks it. Reaching
    ///    the castle requires FindTargetsFast to come back empty, so one body in contact
    ///    stops the advance outright -- it is not a damage race, it is a hard stop.
    ///  - ClampToContact then parks the attacker at the blocker's face, so the stall
    ///    holds wherever the two lines happen to meet rather than at the wall.
    ///  - An attack hits EVERY enemy in range, not one. Blockers stacked at a single
    ///    clamped position are all struck by the same swing, which is what stops this
    ///    from being an unconditionally winning tactic.
    ///
    /// SEATS ARE RUN BOTH WAYS. CLAUDE.md's first measurement pitfall is that a bot-vs-bot
    /// number which does not balance seats is worthless, and this harness is exactly the
    /// asymmetric-by-construction setup where that would bite; --seat both is the default.
    ///
    /// Fully deterministic: the engine is seeded per run and nothing else draws randomness.
    /// </summary>
    public static class StallTest
    {
        /// <summary>Ticks of the real game (GameEngine.MAX_TICKS). Past this a live game is a timeout.</summary>
        private const long GameTimeLimit = GameEngine.MAX_TICKS;

        /// <summary>Ground both seats have to cover (see SpawnUnit's mirrored placement).</summary>
        private const float LaneLength = 1700f;

        public sealed class Result
        {
            public string AttackerTeam = "";
            public int Tier;
            public string AttackerUnit = "";
            public string BlockerTeam = "";
            public string BlockerUnit = "";
            public int Interval;          // 0 = control (defender does nothing)
            public int AttackerSeat;      // 1 or 2
            public string Outcome = "";
            public long Ticks;
            public double Seconds => Ticks / (double)GameEngine.TICKS_PER_SECOND;
            public bool BeyondGameLimit;
            public int BlockersSpawned;
            public int BlockersLost;
            public double BlockerSpend;
            public int AttackerHp;
            public int AttackerMaxHp;
            public float DeepestProgress; // 0 = spawn point, 1 = defender's wall
            public long FirstCastleHitTick = -1;
            public int PeakBlockers;
            public long AttackerDiedTick = -1;
            public double SpendAtAttackerDeath;
        }

        public static void Run(string[] args)
        {
            string teamArg    = Arg(args, "--teams", "all");
            string blockerArg = Arg(args, "--blocker", "mirror");
            string seatArg    = Arg(args, "--seat", "both");
            int    hp         = int.Parse(Arg(args, "--hp", "23000"));
            double income     = double.Parse(Arg(args, "--income", "5000"));
            long   horizon    = long.Parse(Arg(args, "--horizon", "36000"));
            int    seed       = int.Parse(Arg(args, "--seed", "12345"));
            string csvPath    = Arg(args, "--csv", "");
            // The chump stream does two things at once: it blocks, and -- once the attacker
            // is dead -- it walks on and razes the attacker's castle. Shielding the
            // attacker's castle isolates the first from the second, so "how long did the
            // defender survive" is a measurement of blocking alone. Run it both ways.
            bool   protectAtk = Arg(args, "--protect-attacker", "true") == "true";
            var    intervals  = Arg(args, "--intervals", "3")
                                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(int.Parse).ToArray();
            var    tiers      = Arg(args, "--tiers", "5,6,7,8")
                                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(int.Parse).ToArray();

            var teams = ParseTeams(teamArg);
            var seats = seatArg == "both" ? new[] { 1, 2 } : new[] { int.Parse(seatArg) };

            Console.WriteLine($"STALL TEST -- single tier-N attacker vs a stream of tier-1 chumps");
            Console.WriteLine($"  castle HP     : {hp} both sides");
            Console.WriteLine($"  income        : ${income:F0}/s both sides (spawns are never money-limited)");
            Console.WriteLine($"  attacker teams: {string.Join(",", teams)}");
            Console.WriteLine($"  blocker       : {blockerArg}");
            Console.WriteLine($"  tiers         : {string.Join(",", tiers)}");
            Console.WriteLine($"  spawn periods : {string.Join(",", intervals)} ticks  " +
                              $"({string.Join(",", intervals.Select(i => $"{30.0 / i:F1}/s"))})");
            Console.WriteLine($"  attacker seat : {string.Join(",", seats)}");
            Console.WriteLine($"  attacker castle: {(protectAtk ? "INVULNERABLE (isolates blocking from the counter-attack)" : "normal (chumps can win the game outright)")}");
            Console.WriteLine($"  horizon       : {horizon} ticks ({horizon / 30.0:F0}s); the real game ends at {GameTimeLimit / 30}s");
            Console.WriteLine();

            var results = new List<Result>();

            foreach (var atkTeam in teams)
            {
                var blockerTeams = blockerArg == "mirror"
                    ? new[] { atkTeam }
                    : blockerArg == "all" ? AllTeams() : new[] { Enum.Parse<TeamColour>(blockerArg, true) };

                foreach (var blkTeam in blockerTeams)
                foreach (var tier in tiers)
                foreach (var seat in seats)
                {
                    // interval 0 is the control arm: defender does absolutely nothing.
                    results.Add(RunOne(atkTeam, blkTeam, tier, 0, seat, hp, income, horizon, seed, protectAtk));
                    foreach (var interval in intervals)
                        results.Add(RunOne(atkTeam, blkTeam, tier, interval, seat, hp, income, horizon, seed, protectAtk));
                }
            }

            PrintTables(results, intervals);

            if (csvPath != "")
            {
                using var w = new StreamWriter(csvPath);
                w.WriteLine("attacker_team,tier,attacker_unit,blocker_team,blocker_unit,interval_ticks," +
                            "attacker_seat,outcome,ticks,seconds,beyond_game_limit,blockers_spawned," +
                            "blockers_lost,blocker_spend,attacker_hp,attacker_max_hp,deepest_progress," +
                            "first_castle_hit_tick,peak_blockers,attacker_died_tick,spend_at_attacker_death");
                foreach (var r in results)
                    w.WriteLine($"{r.AttackerTeam},{r.Tier},{r.AttackerUnit},{r.BlockerTeam},{r.BlockerUnit},{r.Interval}," +
                                $"{r.AttackerSeat},{r.Outcome},{r.Ticks},{r.Seconds:F2},{r.BeyondGameLimit},{r.BlockersSpawned}," +
                                $"{r.BlockersLost},{r.BlockerSpend:F0},{r.AttackerHp},{r.AttackerMaxHp},{r.DeepestProgress:F4}," +
                                $"{r.FirstCastleHitTick},{r.PeakBlockers},{r.AttackerDiedTick},{r.SpendAtAttackerDeath:F0}");
                Console.WriteLine($"\nWrote {results.Count} rows to {csvPath}");
            }
        }

        private static Result RunOne(TeamColour atkTeam, TeamColour blkTeam, int tier, int interval,
                                     int attackerSeat, int hp, double income, long horizon, int seed,
                                     bool protectAttackerCastle)
        {
            int defenderSeat = attackerSeat == 1 ? 2 : 1;

            var state = new GameState(TeamColour.White, new Random(seed));
            state.Player1 = new PlayerState();
            state.Player2 = new PlayerState();

            var attacker = attackerSeat == 1 ? state.Player1 : state.Player2;
            var defender = attackerSeat == 1 ? state.Player2 : state.Player1;

            Configure(attacker, attackerSeat, atkTeam, hp, income);
            Configure(defender, defenderSeat, blkTeam, hp, income);
            attacker.IsInvulnerable = protectAttackerCastle;
            // The engine expires invulnerability in ProcessStatuses once CurrentTick passes
            // InvulnerableUntilTick, so a bare flag lasts exactly one tick. This is the only
            // way to make it permanent without touching the engine.
            attacker.InvulnerableUntilTick = long.MaxValue;

            var engine = new GameEngine(state, null, seed);

            var atkDef = Roster(atkTeam)[tier - 1];
            var blkDef = Roster(blkTeam)[0];

            var res = new Result
            {
                AttackerTeam = atkTeam.ToString(), Tier = tier, AttackerUnit = atkDef.Id,
                BlockerTeam = blkTeam.ToString(), BlockerUnit = blkDef.Id,
                Interval = interval, AttackerSeat = attackerSeat,
                AttackerMaxHp = atkDef.MaxHealth,
            };

            engine.SpawnUnit(attackerSeat, atkDef.Id, ignoreCost: true);
            var atkUnit = state.Units.Single();

            int defenderHpBefore = defender.CastleHealth;
            bool attackerAlive = true;

            while (state.CurrentTick < horizon)
            {
                if (interval > 0 && attackerAlive && state.CurrentTick % interval == 0)
                {
                    double before = defender.Money;
                    if (engine.SpawnUnit(defenderSeat, blkDef.Id))
                    {
                        res.BlockersSpawned++;
                        res.BlockerSpend += before - defender.Money;
                    }
                }

                engine.Tick();

                // Keep going past the real game's 600s timeout so the true time-to-kill is
                // recorded rather than censored at the limit. The flag below is what the
                // report uses to say a live game would have expired first.
                if (state.IsGameOver && state.IsTimeLimit)
                {
                    state.IsGameOver = false;
                    state.IsTimeLimit = false;
                    state.WinnerSide = 0;
                    res.BeyondGameLimit = true;
                }

                int liveBlockers = state.Units.Count(u => u.Side == defenderSeat);
                res.PeakBlockers = Math.Max(res.PeakBlockers, liveBlockers);
                res.BlockersLost = res.BlockersSpawned - liveBlockers;

                if (attackerAlive)
                {
                    if (state.Units.Contains(atkUnit) && atkUnit.CurrentHealth > 0)
                    {
                        res.AttackerHp = atkUnit.CurrentHealth;
                        float progress = 1f - engine.GetDistanceToEnemyCastle(atkUnit) / LaneLength;
                        res.DeepestProgress = Math.Max(res.DeepestProgress, progress);
                    }
                    else
                    {
                        attackerAlive = false;
                        res.AttackerHp = 0;
                        res.AttackerDiedTick = state.CurrentTick;
                        res.SpendAtAttackerDeath = res.BlockerSpend;

                        // With the attacker's castle shielded the answer is already settled --
                        // the lone attacker is dead, nothing left can touch the defender's
                        // castle, so the defender survives forever. Stopping here is not just
                        // a shortcut: the run would otherwise tick out the whole horizon while
                        // an unbounded chump pile grinds an invulnerable wall, which is both
                        // meaningless and quadratically slow.
                        //
                        // In the UNPROTECTED arm it is emphatically not settled: the surviving
                        // chumps now walk on and this is where they can win the game outright.
                        // That is the whole point of that arm, so it plays on.
                        if (protectAttackerCastle)
                        {
                            res.Ticks = state.CurrentTick;
                            res.Outcome = "attacker_killed";
                            return res;
                        }
                    }
                }

                if (defender.CastleHealth < defenderHpBefore && res.FirstCastleHitTick < 0)
                    res.FirstCastleHitTick = state.CurrentTick;
                defenderHpBefore = defender.CastleHealth;

                if (defender.CastleHealth <= 0)
                {
                    res.Ticks = state.CurrentTick;
                    res.Outcome = "castle_destroyed";
                    return res;
                }

                if (attacker.CastleHealth <= 0)
                {
                    res.Ticks = state.CurrentTick;
                    res.Outcome = "defender_won";   // blockers leaked through and killed the attacker's castle
                    return res;
                }
            }

            res.Ticks = state.CurrentTick;
            res.Outcome = attackerAlive ? "survived_horizon" : "attacker_killed";
            return res;
        }

        private static void Configure(PlayerState p, int side, TeamColour team, int hp, double income)
        {
            p.Side = side;
            p.Team = team;
            // Loadout is never used -- no gadget is ever fired -- but PlayerState fields are
            // non-nullable in several engine paths, so give it a real one.
            p.SetLoadout(new[] { "nuke", "wall", GameDataManager.GetSignatureGadgetIdForTeam(team) });
            p.CastleHealth = hp;
            p.CastleMaxHealth = hp;
            p.Income = income;
            p.Money = income;   // one second of income banked; T1 chumps cost $1-$4
        }

        private static List<UnitDefinition> Roster(TeamColour t) =>
            GameDataManager.Teams.First(x => x.Color == t).Roster;

        private static TeamColour[] AllTeams() => (TeamColour[])Enum.GetValues(typeof(TeamColour));

        private static TeamColour[] ParseTeams(string arg) =>
            arg == "all" ? AllTeams()
                         : arg.Split(',', StringSplitOptions.RemoveEmptyEntries)
                              .Select(s => Enum.Parse<TeamColour>(s, true)).ToArray();

        private static string Arg(string[] args, string name, string fallback)
        {
            int i = Array.IndexOf(args, name);
            return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : fallback;
        }

        // ---------------------------------------------------------------- reporting

        private static void PrintTables(List<Result> all, int[] intervals)
        {
            foreach (var seat in all.Select(r => r.AttackerSeat).Distinct().OrderBy(s => s))
            {
                Console.WriteLine($"=== ATTACKER IN SEAT P{seat} ===\n");
                foreach (var interval in intervals)
                {
                    Console.WriteLine($"--- blockers every {interval} ticks ({30.0 / interval:F1}/s) ---");
                    Console.WriteLine($"{"team",-8} {"T",-2} {"attacker",-14} {"blocker",-12} " +
                                      $"{"no defence",12} {"with chumps",13} {"delay",10} {"factor",8} " +
                                      $"{"chumps",7} {"spent",8} {"$/s",8} {"reach",6} {"outcome",-18}");

                    var rows = all.Where(r => r.AttackerSeat == seat && r.Interval == interval)
                                  .OrderBy(r => r.AttackerTeam).ThenBy(r => r.Tier).ToList();
                    foreach (var r in rows)
                    {
                        var ctrl = all.First(c => c.AttackerSeat == seat && c.Interval == 0 &&
                                                  c.AttackerTeam == r.AttackerTeam && c.Tier == r.Tier &&
                                                  c.BlockerTeam == r.BlockerTeam);
                        string baseline = Fmt(ctrl);
                        string stalled  = Fmt(r);
                        double delay    = r.Seconds - ctrl.Seconds;
                        string factor   = ctrl.Seconds > 0 ? $"{r.Seconds / ctrl.Seconds,7:F2}x" : "-";
                        // Cost of the stall is charged only up to the moment the job was done:
                        // if the chumps killed the attacker outright, everything bought after
                        // that is not paying for delay.
                        double spend = r.AttackerDiedTick >= 0 ? r.SpendAtAttackerDeath : r.BlockerSpend;
                        int chumps   = r.BlockersSpawned;
                        // $/s of delay only means anything when the defender actually died;
                        // when the chumps killed the attacker the delay is unbounded and
                        // `spend` is a one-off price for removing the threat, not a rate.
                        string rate  = r.Outcome == "castle_destroyed" && delay > 0
                                     ? $"{spend / delay,7:F2}" : "-";
                        string delayStr = r.Outcome == "castle_destroyed" ? $"{delay,9:F1}s" : $"{"inf",10}";
                        string factStr  = r.Outcome == "castle_destroyed" ? factor : $"{"inf",8}";
                        Console.WriteLine($"{r.AttackerTeam,-8} {r.Tier,-2} {r.AttackerUnit,-14} {r.BlockerUnit,-12} " +
                                          $"{baseline,12} {stalled,13} {delayStr} {factStr} " +
                                          $"{chumps,7} {spend,8:F0} {rate,8} {r.DeepestProgress,5:P0} {r.Outcome,-18}");
                    }
                    Console.WriteLine();
                }
            }

            Console.WriteLine("=== SUMMARY: median stall factor by tier (both seats pooled) ===");
            Console.WriteLine($"{"tier",-5} {"interval",-9} {"n",-4} {"med no-def",11} {"med stalled",12} {"med factor",11} {"attacker killed",16}");
            foreach (var tier in all.Select(r => r.Tier).Distinct().OrderBy(t => t))
            foreach (var interval in intervals)
            {
                var rows = all.Where(r => r.Tier == tier && r.Interval == interval).ToList();
                if (rows.Count == 0) continue;
                var ctrls = rows.Select(r => all.First(c => c.AttackerSeat == r.AttackerSeat && c.Interval == 0 &&
                                                            c.AttackerTeam == r.AttackerTeam && c.Tier == r.Tier &&
                                                            c.BlockerTeam == r.BlockerTeam)).ToList();
                var factors = rows.Zip(ctrls, (r, c) => c.Seconds > 0 ? r.Seconds / c.Seconds : double.NaN)
                                  .Where(f => !double.IsNaN(f)).ToList();
                int killed = rows.Count(r => r.Outcome is "attacker_killed" or "defender_won");
                Console.WriteLine($"{tier,-5} {interval,-9} {rows.Count,-4} {Median(ctrls.Select(c => c.Seconds)),10:F1}s " +
                                  $"{Median(rows.Select(r => r.Seconds)),11:F1}s {Median(factors),10:F2}x {killed + "/" + rows.Count,16}");
            }
            Console.WriteLine();
        }

        private static string Fmt(Result r)
        {
            if (r.Outcome == "castle_destroyed") return $"{r.Seconds:F1}s";
            if (r.Outcome == "defender_won") return "DEF WINS";
            if (r.Outcome == "attacker_killed") return "NEVER";
            return $">{r.Seconds:F0}s";
        }

        private static double Median(IEnumerable<double> xs)
        {
            var v = xs.OrderBy(x => x).ToList();
            if (v.Count == 0) return double.NaN;
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0;
        }
    }
}
