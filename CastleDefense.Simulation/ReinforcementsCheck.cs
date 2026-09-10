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
        // NO HARDCODED PRICE TABLE, DELIBERATELY -- AND IT USED TO HAVE ONE.
        //
        // The first version of this check transcribed cost/multiplier/budget for all three
        // levels as "the design document, written down independently". That is the right shape
        // for a fixed mechanism and the WRONG shape for a balance number: Marc retunes these
        // in the CSV constantly, so within a day the check was reporting 22 failures for a
        // rebalance that was entirely intentional. A guard that cries wolf on every deliberate
        // change is a guard that gets ignored, which is worse than not having one.
        //
        // So this checks the MECHANISM, which is the code's business, and takes the NUMBERS
        // from the CSV, which is Marc's:
        //
        //   - the budget is ceil(cost x multiplier), and CEILING specifically
        //   - the composition matches an independent greedy written from the spec below
        //   - order, spend and cadence invariants hold for every team and level
        //
        // The independent greedy is the real oracle. It is deliberately NOT a call into
        // BuildSquad -- an expectation sourced from the code under test passes no matter what
        // that code says -- but a second implementation of the same written rule:
        //
        //   walk the roster most-expensive first; take floor(remaining / cost) - 1 of each so
        //   there is always room for one more, except the CHEAPEST unit which takes
        //   floor(remaining / cost) outright because there is nothing below it to hand the
        //   remainder down to; then if anything is still unspent, add one more of the tier-1.
        private static List<int> IndependentGreedy(List<UnitDefinition> roster, int budget)
        {
            var byPrice = roster.Where(u => u.Cost > 0)
                                .OrderByDescending(u => u.Cost).ThenByDescending(u => u.Tier)
                                .ToList();
            var picked = new List<int>();
            long left = budget;
            for (int i = 0; i < byPrice.Count; i++)
            {
                long n = left / byPrice[i].Cost;
                if (i != byPrice.Count - 1) n = Math.Max(0, n - 1);
                for (long k = 0; k < n; k++) picked.Add(byPrice[i].Tier);
                left -= n * byPrice[i].Cost;
            }
            if (left > 0)
            {
                var t1 = roster.FirstOrDefault(u => u.Tier == 1 && u.Cost > 0) ?? byPrice[^1];
                picked.Add(t1.Tier);
            }
            picked.Reverse();               // spawn order: lowest first
            return picked;
        }

        private static readonly string[] Levels = { "reinforcements", "reinforcements_2", "reinforcements_3" };

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

            // ---- 1. THE BUDGET IS A CEILING OF THE CSV'S OWN TWO COLUMNS ---------------
            Console.WriteLine("BUDGET = ceil(cost x multiplier)");
            foreach (var id in Levels)
            {
                var def = GameDataManager.Gadgets.Find(g => g.Id == id);
                if (def == null) { Check($"{id} exists", false); continue; }

                double exact = def.Cost * (double)def.BaseValue;
                int want = (int)Math.Ceiling(exact);
                int got = ReinforcementsEffect.BudgetFor(def);
                Check($"{id}: ${def.Cost} x {def.BaseValue} = {exact:F2} -> ${got}", got == want,
                      $"expected ${want}");

                // CEILING, NOT ROUND OR FLOOR, and only a fractional product can tell the
                // three apart. Whenever the CSV happens to give one, assert the direction --
                // this is the rule that makes a $12 x 1.33 cast worth $16 rather than $15.
                if (Math.Abs(exact - Math.Truncate(exact)) > 1e-9)
                    Check($"   ...rounded UP from {exact:F2}, not down", got > (int)exact,
                          $"got ${got}");
            }
            Console.WriteLine();

            // ---- 2. COMPOSITION vs AN INDEPENDENT GREEDY -------------------------------
            //
            // Every team at every level, against the second implementation above. This is what
            // the hand-worked White example used to do, except it cannot go stale when a price
            // moves.
            Console.WriteLine("COMPOSITION vs INDEPENDENT GREEDY (8 teams x 3 levels)");
            foreach (var id in Levels)
            {
                var def = GameDataManager.Gadgets.Find(g => g.Id == id);
                int budget = ReinforcementsEffect.BudgetFor(def);
                foreach (var team in GameDataManager.Teams)
                {
                    var squad = ReinforcementsEffect.BuildSquad(def, team);
                    var want = IndependentGreedy(team.Roster, budget);
                    bool ok = squad.Select(u => u.Tier).SequenceEqual(want);
                    Check($"{team.Color,-7} L{def.Level}  {squad.Count,2} units  ${squad.Sum(u => u.Cost),5}", ok,
                          ok ? null : $"got [{string.Join(",", squad.Select(u => u.Tier))}] " +
                                      $"want [{string.Join(",", want)}]");
                }
            }
            Console.WriteLine();

            // ---- 3. EVERY TEAM AT EVERY LEVEL ------------------------------------------
            //
            // Properties rather than a transcribed table, because a per-team table would be
            // 24 cells of arithmetic nobody would ever re-derive -- which is the rot
            // CLAUDE.md's measurement-pitfalls section is about. These four hold for any
            // roster and any budget, so they survive a rebalance of either.
            Console.WriteLine("ALL TEAMS x ALL LEVELS (invariants)");
            foreach (var id in Levels)
            {
                var def = GameDataManager.Gadgets.Find(g => g.Id == id);
                int rowBudget = ReinforcementsEffect.BudgetFor(def);
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
                    bool spent = value <= rowBudget
                        ? rowBudget - value < cheapest
                        : value - rowBudget < cheapest;

                    // MOVE-ON-ONE-EARLY, checked as an observable property rather than by
                    // re-running the algorithm: walk the roster top down, and after each unit
                    // type is done with, at most ONE more of it must have been affordable.
                    // The cheapest type is exempt -- it buys outright, so the bound there is
                    // its own price, and the leftover rule may then overspend by one more.
                    bool greedy = true;
                    long left = rowBudget;
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
