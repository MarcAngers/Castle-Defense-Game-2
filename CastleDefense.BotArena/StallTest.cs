using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    /// <summary>
    /// Measures the CHUMP-BLOCK: how much longer a defender survives an advancing high-tier
    /// force when it feeds cheap tier-1 bodies in front of it, and what that costs.
    ///
    /// The scenario is deliberately sterile rather than a real game. The attacker gets a
    /// fixed force -- N copies of one high tier, spawned --force-gap ticks apart, optionally
    /// with a lower-tier ESCORT streamed in behind them to break the blocking line -- and
    /// nothing else, ever. The defender either does nothing (the control) or spawns one
    /// tier-1 unit every N ticks and nothing else. No gadgets, no investing, no repairs,
    /// both castles pinned at the same HP and both economies set far above anything either
    /// side could spend. What comes out is the isolated value of the blocking mechanic, in
    /// seconds of delay and in dollars per second bought.
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
    ///  - ClampToContact skips FRIENDLY units, so members of a force do not queue behind
    ///    each other -- they overlap at the same contact point and all swing at the line.
    ///    That is why force size multiplies the rate the blocking line is eaten at.
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
            public int ForceSize;
            public int EscortTier;        // 0 = no escort
            public string EscortUnit = "";
            public string BlockerTeam = "";
            public string BlockerUnit = "";
            public int Interval;          // 0 = control (defender does nothing)
            public int AttackerSeat;      // 1 or 2
            public string Outcome = "";
            public long Ticks;
            public double Seconds => Ticks / (double)GameEngine.TICKS_PER_SECOND;
            public bool BeyondGameLimit;

            // defender side
            public int BlockersSpawned;
            public int BlockersLost;
            public double BlockerSpend;
            public int PeakBlockers;
            public double SpendAtForceDeath;

            // attacker side
            public int EscortsSpawned;
            public double AttackerSpend;  // list price of everything the attacker put on the field
            public double ForceSpend;     // list price of the high-tier units alone
            public int BigUnitsAlive;
            public long ForceDiedTick = -1;

            public float DeepestProgress; // 0 = spawn point, 1 = defender's wall
            public long FirstCastleHitTick = -1;
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
            // The chump stream does two things at once: it blocks, and -- once the attacking
            // force is dead -- it walks on and razes the attacker's castle. Shielding the
            // attacker's castle isolates the first from the second, so "how long did the
            // defender survive" is a measurement of blocking alone. Run it both ways.
            bool   protectAtk = Arg(args, "--protect-attacker", "true") == "true";
            int    forceGap   = int.Parse(Arg(args, "--force-gap", "30"));
            int    escortGap  = int.Parse(Arg(args, "--escort-gap", "30"));
            var    forces     = IntList(Arg(args, "--forces", "1"));
            var    escorts    = IntList(Arg(args, "--escorts", "0"));   // 0 = no escort
            var    intervals  = IntList(Arg(args, "--intervals", "3"));
            var    tiers      = IntList(Arg(args, "--tiers", "5,6,7,8"));

            var teams = ParseTeams(teamArg);
            var seats = seatArg == "both" ? new[] { 1, 2 } : new[] { int.Parse(seatArg) };

            Console.WriteLine("STALL TEST -- a high-tier attacking force vs a stream of tier-1 chumps");
            Console.WriteLine($"  castle HP      : {hp} both sides");
            Console.WriteLine($"  income         : ${income:F0}/s both sides (spawns are never money-limited)");
            Console.WriteLine($"  attacker teams : {string.Join(",", teams)}");
            Console.WriteLine($"  blocker        : {blockerArg}");
            Console.WriteLine($"  tiers          : {string.Join(",", tiers)}");
            Console.WriteLine($"  force sizes    : {string.Join(",", forces)} (spawned {forceGap / 30.0:F2}s apart)");
            Console.WriteLine($"  escorts        : {string.Join(",", escorts.Select(e => e == 0 ? "none" : $"T{e}"))}" +
                              $" (one every {escortGap / 30.0:F2}s while any high-tier unit lives)");
            Console.WriteLine($"  chump periods  : {string.Join(",", intervals)} ticks  " +
                              $"({string.Join(",", intervals.Select(i => $"{30.0 / i:F2}/s"))})");
            Console.WriteLine($"  attacker seat  : {string.Join(",", seats)}");
            Console.WriteLine($"  attacker castle: {(protectAtk ? "INVULNERABLE (isolates blocking from the counter-attack)" : "normal (chumps can win the game outright)")}");
            Console.WriteLine($"  horizon        : {horizon} ticks ({horizon / 30.0:F0}s); the real game ends at {GameTimeLimit / 30}s");
            Console.WriteLine();

            var results = new List<Result>();
            int nBlockerTeams = blockerArg == "all" ? AllTeams().Length : 1;
            int total = teams.Length * nBlockerTeams * tiers.Length * seats.Length
                      * forces.Length * escorts.Length * (1 + intervals.Length);
            int done = 0;

            foreach (var atkTeam in teams)
            {
                var blockerTeams = blockerArg == "mirror"
                    ? new[] { atkTeam }
                    : blockerArg == "all" ? AllTeams() : new[] { Enum.Parse<TeamColour>(blockerArg, true) };

                foreach (var blkTeam in blockerTeams)
                foreach (var tier in tiers)
                foreach (var force in forces)
                foreach (var escort in escorts)
                foreach (var seat in seats)
                {
                    // force 0 with no escort is nobody attacking at all -- skip rather than
                    // burn the horizon on an empty board.
                    if (force == 0 && escort == 0) { done += 1 + intervals.Length; continue; }
                    var spec = new Spec(atkTeam, blkTeam, tier, force, forceGap, escort, escortGap,
                                        seat, hp, income, horizon, seed, protectAtk);
                    // interval 0 is the control arm: defender does absolutely nothing.
                    results.Add(RunOne(spec, 0));
                    foreach (var interval in intervals)
                        results.Add(RunOne(spec, interval));
                    done += 1 + intervals.Length;
                    if (total > 200) Console.Write($"\r  {done}/{total} runs...");
                }
            }
            if (total > 200) Console.WriteLine();

            PrintTables(results, intervals);
            if (csvPath != "") WriteCsv(results, csvPath);
        }

        private readonly record struct Spec(
            TeamColour AtkTeam, TeamColour BlkTeam, int Tier, int Force, int ForceGap,
            int Escort, int EscortGap, int AttackerSeat, int Hp, double Income,
            long Horizon, int Seed, bool ProtectAttackerCastle);

        private static Result RunOne(Spec s, int interval)
        {
            int attackerSeat = s.AttackerSeat;
            int defenderSeat = attackerSeat == 1 ? 2 : 1;

            var state = new GameState(TeamColour.White, new Random(s.Seed));
            state.Player1 = new PlayerState();
            state.Player2 = new PlayerState();

            var attacker = attackerSeat == 1 ? state.Player1 : state.Player2;
            var defender = attackerSeat == 1 ? state.Player2 : state.Player1;

            Configure(attacker, attackerSeat, s.AtkTeam, s.Hp, s.Income);
            Configure(defender, defenderSeat, s.BlkTeam, s.Hp, s.Income);
            attacker.IsInvulnerable = s.ProtectAttackerCastle;
            // The engine expires invulnerability in ProcessStatuses once CurrentTick passes
            // InvulnerableUntilTick, so a bare flag lasts exactly one tick. This is the only
            // way to make it permanent without touching the engine.
            attacker.InvulnerableUntilTick = long.MaxValue;

            var engine = new GameEngine(state, null, s.Seed);

            var atkDef = Roster(s.AtkTeam)[s.Tier - 1];
            var escDef = s.Escort > 0 ? Roster(s.AtkTeam)[s.Escort - 1] : null;
            var blkDef = Roster(s.BlkTeam)[0];

            var res = new Result
            {
                AttackerTeam = s.AtkTeam.ToString(), Tier = s.Tier, AttackerUnit = atkDef.Id,
                ForceSize = s.Force, EscortTier = s.Escort, EscortUnit = escDef?.Id ?? "-",
                BlockerTeam = s.BlkTeam.ToString(), BlockerUnit = blkDef.Id,
                Interval = interval, AttackerSeat = attackerSeat,
            };

            // Force members are spawned with ignoreCost so the schedule is exactly the one
            // asked for rather than one the attacker's bank happens to allow -- five tier 8s
            // is up to $115,000 and would otherwise trickle in over 23 seconds at $5,000/s.
            // Their list price is accounted separately so the cost comparison still holds.
            var bigIds = new HashSet<Guid>();
            int spawnedBig = 0;

            int defenderHpBefore = defender.CastleHealth;
            // Force 0 is the escort-only reference arm: there are no high-tier units to
            // escort, so the escort stream IS the attack and must not switch itself off.
            bool escortOnly = s.Force == 0;
            bool forceAlive = true;

            while (state.CurrentTick < s.Horizon)
            {
                // --- attacker's schedule ---
                if (spawnedBig < s.Force && state.CurrentTick == (long)spawnedBig * s.ForceGap)
                {
                    if (engine.SpawnUnit(attackerSeat, atkDef.Id, ignoreCost: true))
                    {
                        bigIds.Add(state.Units[^1].InstanceId);
                        res.ForceSpend += atkDef.Cost;
                        res.AttackerSpend += atkDef.Cost;
                        spawnedBig++;
                    }
                }
                // The escort exists to walk the high-tier units in, so it stops when there is
                // nothing left to escort. Without that rule an escort stream never ends and
                // every run burns the full horizon.
                if (escDef != null && forceAlive && state.CurrentTick % s.EscortGap == 0)
                {
                    if (engine.SpawnUnit(attackerSeat, escDef.Id, ignoreCost: true))
                    {
                        res.EscortsSpawned++;
                        res.AttackerSpend += escDef.Cost;
                    }
                }

                // --- defender's schedule ---
                if (interval > 0 && forceAlive && state.CurrentTick % interval == 0)
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

                // One pass over the board per tick -- membership tests against the live unit
                // list would be O(n) each and the piles here reach thousands.
                int liveBlockers = 0, liveBig = 0, liveAttackerUnits = 0;
                float deepest = res.DeepestProgress;
                foreach (var u in state.Units)
                {
                    if (u.Side == defenderSeat) { liveBlockers++; continue; }
                    liveAttackerUnits++;
                    if (!bigIds.Contains(u.InstanceId)) continue;
                    liveBig++;
                    float progress = 1f - engine.GetDistanceToEnemyCastle(u) / LaneLength;
                    if (progress > deepest) deepest = progress;
                }
                res.DeepestProgress = deepest;
                res.PeakBlockers = Math.Max(res.PeakBlockers, liveBlockers);
                res.BlockersLost = res.BlockersSpawned - liveBlockers;
                res.BigUnitsAlive = liveBig;

                if (!escortOnly && forceAlive && liveBig == 0 && spawnedBig == s.Force)
                {
                    forceAlive = false;
                    res.ForceDiedTick = state.CurrentTick;
                    res.SpendAtForceDeath = res.BlockerSpend;
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
                if (!s.ProtectAttackerCastle && attacker.CastleHealth <= 0)
                {
                    res.Ticks = state.CurrentTick;
                    res.Outcome = "defender_won";   // chumps killed the force, then razed its castle
                    return res;
                }

                // With the attacker's castle shielded and NOTHING left on the attacker's side
                // -- force dead and escorts stopped with it -- the answer is settled: nothing
                // can ever touch the defender's castle again. Stopping here is not just a
                // shortcut, it is what keeps an unbounded chump pile from grinding an
                // invulnerable wall for the whole horizon, quadratically.
                if (s.ProtectAttackerCastle && !forceAlive && liveAttackerUnits == 0)
                {
                    res.Ticks = state.CurrentTick;
                    res.Outcome = "force_destroyed";
                    return res;
                }
            }

            res.Ticks = state.CurrentTick;
            res.Outcome = forceAlive ? "survived_horizon" : "force_destroyed_horizon";
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

        private static int[] IntList(string arg) =>
            arg.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();

        private static string Arg(string[] args, string name, string fallback)
        {
            int i = Array.IndexOf(args, name);
            return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : fallback;
        }

        // ---------------------------------------------------------------- reporting

        private static void WriteCsv(List<Result> results, string path)
        {
            using var w = new StreamWriter(path);
            w.WriteLine("attacker_team,tier,attacker_unit,force_size,escort_tier,escort_unit," +
                        "blocker_team,blocker_unit,interval_ticks,attacker_seat,outcome,ticks,seconds," +
                        "beyond_game_limit,blockers_spawned,blockers_lost,blocker_spend,peak_blockers," +
                        "spend_at_force_death,escorts_spawned,attacker_spend,force_spend," +
                        "big_units_alive,force_died_tick,deepest_progress,first_castle_hit_tick");
            foreach (var r in results)
                w.WriteLine($"{r.AttackerTeam},{r.Tier},{r.AttackerUnit},{r.ForceSize},{r.EscortTier},{r.EscortUnit}," +
                            $"{r.BlockerTeam},{r.BlockerUnit},{r.Interval},{r.AttackerSeat},{r.Outcome},{r.Ticks},{r.Seconds:F2}," +
                            $"{r.BeyondGameLimit},{r.BlockersSpawned},{r.BlockersLost},{r.BlockerSpend:F0},{r.PeakBlockers}," +
                            $"{r.SpendAtForceDeath:F0},{r.EscortsSpawned},{r.AttackerSpend:F0},{r.ForceSpend:F0}," +
                            $"{r.BigUnitsAlive},{r.ForceDiedTick},{r.DeepestProgress:F4},{r.FirstCastleHitTick}");
            Console.WriteLine($"\nWrote {results.Count} rows to {path}");
        }

        private static void PrintTables(List<Result> all, int[] intervals)
        {
            Func<Result, (int, string, int, int, int, string)> key =
                r => (r.AttackerSeat, r.AttackerTeam, r.Tier, r.ForceSize, r.EscortTier, r.BlockerTeam);
            var ctrl = all.Where(r => r.Interval == 0).ToDictionary(key);
            int firstSeat = all[0].AttackerSeat;

            foreach (var combo in all.Select(r => (r.ForceSize, r.EscortTier)).Distinct()
                                     .OrderBy(x => x.EscortTier).ThenBy(x => x.ForceSize))
            {
                string esc = combo.EscortTier == 0 ? "no escort" : $"+ T{combo.EscortTier} escort every second";
                Console.WriteLine($"\n################ FORCE: {combo.ForceSize}x high-tier, {esc} ################");
                foreach (var interval in intervals)
                {
                    Console.WriteLine($"\n--- chumps every {interval} ticks ({30.0 / interval:F2}/s) ---");
                    Console.WriteLine($"{"team",-8} {"T",-2} {"attacker",-14} " +
                                      $"{"no defence",11} {"with chumps",12} {"delay",10} " +
                                      $"{"atk cost",9} {"def cost",9} {"ratio",7} {"reach",6} {"outcome",-24}");
                    foreach (var r in all.Where(r => r.Interval == interval && r.ForceSize == combo.ForceSize
                                                  && r.EscortTier == combo.EscortTier && r.AttackerSeat == firstSeat)
                                         .OrderBy(r => r.AttackerTeam).ThenBy(r => r.Tier))
                    {
                        var c = ctrl[key(r)];
                        double delay = r.Seconds - c.Seconds;
                        double defCost = r.ForceDiedTick >= 0 ? r.SpendAtForceDeath : r.BlockerSpend;
                        string delayStr = r.Outcome == "castle_destroyed" ? $"{delay,9:F1}s" : $"{"inf",10}";
                        string ratio = r.AttackerSpend > 0 ? $"{defCost / r.AttackerSpend,6:F2}x" : "-";
                        Console.WriteLine($"{r.AttackerTeam,-8} {r.Tier,-2} {r.AttackerUnit,-14} " +
                                          $"{Fmt(c),11} {Fmt(r),12} {delayStr} " +
                                          $"{r.AttackerSpend,9:F0} {defCost,9:F0} {ratio,7} " +
                                          $"{r.DeepestProgress,5:P0} {r.Outcome,-24}");
                    }
                }
            }
            Console.WriteLine();
        }

        private static string Fmt(Result r) => r.Outcome switch
        {
            "castle_destroyed" => $"{r.Seconds:F1}s",
            "defender_won"     => "DEF WINS",
            "force_destroyed"  => "NEVER",
            "force_destroyed_horizon" => "NEVER",
            _                  => $">{r.Seconds:F0}s",
        };
    }
}
