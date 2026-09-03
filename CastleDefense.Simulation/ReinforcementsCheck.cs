using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Gadgets;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// DOES REINFORCEMENTS SPEND THE BUDGET THE DESIGN GIVES IT?
    ///
    /// Since the 2026-09-03 rebalance the gadget no longer spawns a flat five units of a
    /// fixed tier. Its CSV BaseValue is an EFFICIENCY MULTIPLIER, the cast gets
    /// ceil(Cost * BaseValue) of roster value to spend, and the squad is whatever a greedy
    /// most-expensive-first spend of that budget buys -- sent LOWEST tier first.
    ///
    /// Four things can drift silently, and none of them is visible in normal play:
    ///
    ///  1. THE BUDGET. It is the product of two CSV columns and a ceiling. Level 1 is
    ///     $12 x 1.33 = $15.96, which must be $16 and not $15 -- and if BaseValue is ever
    ///     read back as a TIER by new code, the budget silently becomes a tier index.
    ///
    ///  2. THE COMPOSITION. Greedy from the top with a leftover rule. Getting the leftover
    ///     wrong is the quiet one: a level-1 cast on a team whose tier 1 costs $4 has $0-3
    ///     stranded, and discarding it means paying $12 for nothing on some teams and not
    ///     others.
    ///
    ///  3. THE ORDER. Lowest tier FIRST is the balance content of the change -- the chumps
    ///     screen for the expensive units instead of arriving behind them. It reverses with
    ///     one misplaced line and looks fine on a screenshot.
    ///
    ///  4. THE COST. They must arrive FREE and must not eat unit charges, which is the
    ///     ignoreCost path in SpawnUnit.
    ///
    /// Sections 1 and 2 measure against the table transcribed below, which is the design
    /// document rather than a copy of the implementation -- the point is that the two are
    /// written down independently, so a change to one fails against the other. Sections 3
    /// and 4 run the real tick loop.
    ///
    /// Run: dotnet run --project CastleDefense.Simulation -- --reinforcements-check
    /// </summary>
    public static class ReinforcementsCheck
    {
        // The design table. Cost and multiplier are Marc's; the budget is HIS arithmetic,
        // not Math.Ceiling's, which is the whole point of writing it out separately.
        private static readonly (string Id, int Cost, double Multiplier, int Budget)[] Table =
        {
            ("reinforcements",     12, 1.33, 16),
            ("reinforcements_2",  180, 1.5,  270),
            ("reinforcements_3", 2000, 3.0,  6000),
        };

        // White at level 3, worked by hand against the MOVE-ON-ONE-EARLY rule (each tier
        // stops while one more copy would still fit, and hands that budget down):
        //
        //   $6,000  legg   $2,066  floor 2 -> take 1   ->  $3,934 left
        //           bread  $  338  floor 11 -> take 10 ->  $  554 left
        //           alpacco $  81  floor 6 -> take 5   ->  $  149 left
        //           ringo  $   18  floor 8 -> take 7   ->  $   23 left
        //           squirt $    9  floor 2 -> take 1   ->  $   14 left
        //           catto  $    4  floor 3 -> take 2   ->  $    6 left
        //           doggo  $    3  cheapest, take 2    ->  $    0 left
        //
        // 28 units for exactly $6,000, against 12 under the old take-all-you-can-afford rule.
        // Written most-expensive-first, so the check reverses it to get the SPAWN order.
        private static readonly int[] WhiteLevel3Composition =
        {
            7,
            6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
            5, 5, 5, 5, 5,
            4, 4, 4, 4, 4, 4, 4,
            3,
            2, 2,
            1, 1,
        };

        public static void Run(string[] args)
        {
            int failures = 0;
            void Check(string label, bool ok, string detail = null)
            {
                Console.WriteLine($"  {label,-56} {(ok ? "ok" : "FAIL")}{(detail == null ? "" : "  " + detail)}");
                if (!ok) failures++;
            }

            Console.WriteLine("=== REINFORCEMENTS CHECK ===");
            Console.WriteLine();

            // ---- 1. THE CSV SAYS WHAT THE DESIGN SAYS ----------------------------------
            Console.WriteLine("CSV AND BUDGET");
            foreach (var row in Table)
            {
                var def = GameDataManager.Gadgets.Find(g => g.Id == row.Id);
                if (def == null) { Check($"{row.Id} exists", false); continue; }

                Check($"{row.Id} cost ${def.Cost}", def.Cost == row.Cost, $"design ${row.Cost}");
                Check($"{row.Id} multiplier x{def.BaseValue}",
                      Math.Abs(def.BaseValue - row.Multiplier) < 0.0005, $"design x{row.Multiplier}");
                int budget = ReinforcementsEffect.BudgetFor(def);
                Check($"{row.Id} budget ${budget}", budget == row.Budget, $"design ${row.Budget}");
            }
            Console.WriteLine();

            // ---- 2. MARC'S WORKED EXAMPLE ----------------------------------------------
            //
            // The one cell of the matrix that was computed by hand. If the greedy disagrees
            // with it, every other cell is suspect however self-consistent it looks.
            Console.WriteLine("WHITE, LEVEL 3 (the worked example)");
            {
                var def = GameDataManager.Gadgets.Find(g => g.Id == "reinforcements_3");
                var team = GameDataManager.Teams.Find(t => t.Color == TeamColour.White);
                var squad = ReinforcementsEffect.BuildSquad(def, team);

                var want = WhiteLevel3Composition.Reverse().ToArray();   // spawn order
                Check($"squad is exactly the worked {want.Length} units, lowest tier first",
                      squad.Select(u => u.Tier).SequenceEqual(want),
                      $"got [{string.Join(",", squad.Select(u => u.Tier))}] want [{string.Join(",", want)}]");
                Check("spends the budget exactly", squad.Sum(u => u.Cost) == 6000,
                      $"${squad.Sum(u => u.Cost)}");
            }
            Console.WriteLine();

            // ---- 3. EVERY TEAM AT EVERY LEVEL ------------------------------------------
            //
            // Properties rather than a transcribed table, because a per-team table would be
            // 24 cells of arithmetic nobody would ever re-derive -- which is the rot
            // CLAUDE.md's measurement-pitfalls section is about. These four hold for any
            // roster and any budget, so they survive a rebalance of either.
            Console.WriteLine("ALL TEAMS x ALL LEVELS (invariants)");
            foreach (var row in Table)
            {
                var def = GameDataManager.Gadgets.Find(g => g.Id == row.Id);
                foreach (var team in GameDataManager.Teams)
                {
                    var squad = ReinforcementsEffect.BuildSquad(def, team);
                    int cheapest = team.Roster.Where(u => u.Cost > 0).Min(u => u.Cost);
                    int value = squad.Sum(u => u.Cost);
                    bool nonEmpty = squad.Count > 0;

                    // Ascending in PRICE: the screen walks out ahead of the muscle.
                    bool ordered = true;
                    for (int i = 1; i < squad.Count; i++)
                        if (squad[i].Cost < squad[i - 1].Cost) ordered = false;

                    // Nothing is left on the table. Either the budget is spent to within
                    // less than the cheapest unit, or the leftover rule overspent by exactly
                    // one of those -- there is no third case.
                    bool spent = value <= row.Budget
                        ? row.Budget - value < cheapest
                        : value - row.Budget < cheapest;

                    // MOVE-ON-ONE-EARLY, checked as an observable property rather than by
                    // re-running the algorithm: walk the roster top down, and after each unit
                    // type is done with, at most ONE more of it must have been affordable.
                    // The cheapest type is exempt -- it buys outright, so the bound there is
                    // its own price, and the leftover rule may then overspend by one more.
                    bool greedy = true;
                    long left = row.Budget;
                    var desc = team.Roster.Where(u => u.Cost > 0)
                                          .OrderByDescending(u => u.Cost).ThenByDescending(u => u.Tier)
                                          .ToList();
                    for (int i = 0; i < desc.Count; i++)
                    {
                        var u = desc[i];
                        left -= (long)squad.Count(x => x.Id == u.Id) * u.Cost;
                        long bound = (i == desc.Count - 1) ? u.Cost : 2L * u.Cost;
                        // At the bottom the leftover unit can push `left` negative; that is
                        // the documented one-tier-1 overspend, not a greedy violation.
                        if (left >= bound) greedy = false;
                    }

                    bool ok = nonEmpty && ordered && spent && greedy;
                    Check($"{team.Color,-7} L{def.Level}  {squad.Count,2} units  ${value,5}", ok,
                          ok ? null : $"empty={!nonEmpty} order={ordered} spent={spent} greedy={greedy}");
                }
            }
            Console.WriteLine();

            // ---- 4. THE LIVE ENGINE ----------------------------------------------------
            //
            // Sections 1-3 test the composer. This casts the gadget in a real game and
            // watches what walks on: the cadence, the order, and that it is free.
            Console.WriteLine("LIVE CAST (reinforcements_3, White, seat 1)");
            int savedSquad = GameEngine.OpeningSquadSize;
            GameEngine.OpeningSquadSize = 0;   // its free tier-1s would mix into the stream
            try
            {
                var state = new GameState();
                state.Player1.Team = TeamColour.White;
                state.Player2.Team = TeamColour.White;
                string sig = GameDataManager.GetSignatureGadgetIdForTeam(TeamColour.White);
                state.Player1.SetLoadout(new[] { "nuke", "reinforcements_3", sig });
                state.Player2.SetLoadout(new[] { "nuke", "reinforcements_3", sig });

                // THE CASTLES ARE MADE INDESTRUCTIBLE FOR THE DURATION, and that is not
                // padding. A level-3 squad is 28 units at 15 ticks apart -- 14 seconds -- and
                // the head of it razes an undefended enemy castle long before the tail spawns.
                // The first run of this check after the cadence went 10 -> 15 ticks reported
                // "24 units arrived" for exactly that reason: the game ended at tick ~350 and
                // the remaining PendingEffects never fired.
                state.Player1.CastleHealth = state.Player1.CastleMaxHealth = 100_000_000;
                state.Player2.CastleHealth = state.Player2.CastleMaxHealth = 100_000_000;

                var engine = new GameEngine(state, null, 12345);
                var p = state.Player1;
                p.Money = 100000;
                double before = p.Money;

                var def = GameDataManager.Gadgets.Find(g => g.Id == "reinforcements_3");
                var expected = ReinforcementsEffect.BuildSquad(def,
                    GameDataManager.Teams.Find(t => t.Color == TeamColour.White));

                // Position 0 rather than -1: aim is irrelevant to an untargeted gadget, and a
                // -1 can be REFUSED outright by the auto-targeter (see UseGadget's note).
                bool cast = engine.UseGadget(1, "reinforcements_3", 0);
                Check("cast accepted", cast);
                double afterCast = p.Money;

                // Watch the stream land: the tick each new unit first appears on, in order.
                var seen = new HashSet<Guid>();
                var arrivals = new List<(long Tick, int Tier)>();
                long start = state.CurrentTick;
                int window = expected.Count * ReinforcementsEffect.SpawnIntervalTicks + 60;
                for (int i = 0; i < window; i++)
                {
                    if (state.IsGameOver) break;   // never expected; the castles are 100M HP
                    engine.Tick();
                    foreach (var u in state.Units)
                        if (u.Side == 1 && seen.Add(u.InstanceId))
                            arrivals.Add((state.CurrentTick - start, u.Tier));
                }

                Check($"{expected.Count} units arrived", arrivals.Count == expected.Count,
                      $"got {arrivals.Count}");
                Check("arrival order is lowest tier first",
                      arrivals.Select(a => a.Tier).SequenceEqual(expected.Select(u => u.Tier)),
                      $"got [{string.Join(",", arrivals.Select(a => a.Tier))}]");

                // The cadence: consecutive arrivals exactly SpawnIntervalTicks apart. Measured
                // as a DIFFERENCE so it does not also depend on which tick the effect first
                // fires on.
                bool cadence = arrivals.Count > 1;
                for (int i = 1; i < arrivals.Count; i++)
                    if (arrivals[i].Tick - arrivals[i - 1].Tick != ReinforcementsEffect.SpawnIntervalTicks)
                        cadence = false;
                Check($"one unit every {ReinforcementsEffect.SpawnIntervalTicks} ticks", cadence,
                      string.Join(",", arrivals.Select(a => a.Tick)));

                // FREE. The cast costs the gadget's price and nothing else -- the units take
                // SpawnUnit's ignoreCost path, so no roster price, no charge and no entry in
                // the purchase counters.
                Check($"the cast cost exactly ${def.Cost}",
                      Math.Abs((before - afterCast) - def.Cost) < 0.001, $"spent ${before - afterCast}");
                Check("no unit charge was spent", p.UnitCharges.Count == 0,
                      $"{p.UnitCharges.Count} entries");
                Check("no unit purchase was counted", engine.UnitsPurchased[1] == 0,
                      $"{engine.UnitsPurchased[1]}");
            }
            finally
            {
                GameEngine.OpeningSquadSize = savedSquad;
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
            if (failures > 0) Environment.ExitCode = 1;
        }
    }
}
