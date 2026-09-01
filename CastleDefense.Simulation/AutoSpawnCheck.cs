using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// DOES THE AUTO-SPAWNER MATCH ITS DESIGN TABLE?
    ///
    /// The auto-spawner is specified by a table of 19 rows (cost, units/s, tier order) that
    /// lives outside the code. Three things in the implementation can silently drift from
    /// it, and all three are invisible in normal play:
    ///
    ///  1. COST. The prices are walked off the shared economy curve at a different scale
    ///     from investing (start 2.5, step 0.25, multiplier x*4+7), with the top three rungs
    ///     hardcoded. An arithmetic slip shows up as a price nobody notices is wrong.
    ///
    ///  2. RATE. A rate of 4 units/s does not divide the 30 Hz tick rate, so the spawn
    ///     cadence runs off a fractional accumulator. If that drifts, a level delivers a
    ///     rate the table never specified -- and being off by one unit per second at level 8
    ///     is a bigger balance change than several of the upgrades themselves.
    ///
    ///  3. TIER ORDER. The cycle must repeat exactly, in order, and restart on upgrade.
    ///
    /// This measures all three against the table transcribed below, which is the design
    /// document rather than a copy of the implementation -- the point is for the two to be
    /// written down independently, so a change to one fails against the other.
    ///
    /// Run: dotnet run --project CastleDefense.Simulation -- --auto-spawn-check
    /// </summary>
    public static class AutoSpawnCheck
    {
        // The design table, transcribed from the spec. Deliberately NOT read from
        // PlayerState -- a check that sources its expectations from the code under test
        // passes no matter what that code says.
        private static readonly (int Level, double Cost, int PerSecond, int[] Tiers)[] Table =
        {
            (1,     102, 1, new[] { 1 }),
            (2,     128, 2, new[] { 1, 1 }),
            (3,     161, 2, new[] { 2, 1 }),
            (4,     205, 2, new[] { 2, 2 }),
            (5,     264, 3, new[] { 3, 2, 1 }),
            (6,     344, 3, new[] { 3, 2, 2 }),
            (7,     454, 3, new[] { 3, 3, 2 }),
            (8,     608, 4, new[] { 4, 2, 1, 1 }),
            (9,     828, 4, new[] { 4, 3, 2, 1 }),
            (10,   1146, 4, new[] { 4, 4, 1, 1 }),
            (11,   1617, 4, new[] { 4, 4, 3, 2 }),
            (12,   2324, 5, new[] { 4, 4, 3, 3, 2 }),
            (13,   3408, 5, new[] { 4, 4, 4, 3, 2 }),
            (14,   5107, 5, new[] { 5, 3, 3, 2, 1 }),
            (15,   7827, 5, new[] { 5, 4, 3, 2, 2 }),
            (16,  12283, 6, new[] { 5, 4, 4, 2, 2, 2 }),
            (17,  43614, 6, new[] { 6, 5, 4, 3, 2, 1 }),
            (18, 100000, 6, new[] { 7, 6, 6, 5, 5, 4 }),
            (19, 100000, 6, new[] { 8, 7, 7, 6, 6, 5 }),
        };

        public static void Run(string[] args)
        {
            int failures = 0;

            Console.WriteLine("=== AUTO-SPAWNER CHECK ===");
            Console.WriteLine();

            // ---- 1. PRICES -------------------------------------------------------------
            //
            // The table was produced in a spreadsheet whose rounding was ROUND, while every
            // price in the game goes through WholeDollars, which is Math.Ceiling. Some rows
            // therefore sit exactly one dollar apart by design. Those are reported as a
            // NOTE, not a failure; anything larger is a real defect.
            Console.WriteLine("PRICES (engine vs design table)");
            foreach (var row in Table)
            {
                double got = PlayerState.AutoSpawnPriceFor(row.Level);
                double delta = got - row.Cost;
                if (Math.Abs(delta) < 0.5)
                    Console.WriteLine($"  L{row.Level,-2} {got,8:0}  ok");
                else if (Math.Abs(delta) <= 1.0)
                    Console.WriteLine($"  L{row.Level,-2} {got,8:0}  NOTE table says {row.Cost} (ceiling vs round)");
                else
                {
                    Console.WriteLine($"  L{row.Level,-2} {got,8:0}  FAIL table says {row.Cost}, delta {delta:+0.##;-0.##}");
                    failures++;
                }
            }
            Console.WriteLine();

            // ---- 2. RATE AND TIER ORDER, measured by running the engine -----------------
            //
            // Not by inspecting the table a second time: the accumulator, the cycle wrap and
            // the roster lookup are the parts that can be wrong, and only a real tick loop
            // exercises them. One clean engine per level.
            Console.WriteLine("RATE AND TIER ORDER (30s of engine ticks per level)");
            const int seconds = 30;

            // THE OPENING SQUAD IS TURNED OFF FOR THE DURATION. It spawns free tier-1 units
            // on ticks 1, 31, 61... which INTERLEAVE with the auto-spawner rather than
            // preceding it, so they cannot be skipped by dropping a fixed number of leading
            // entries -- that was the first version of this check and it produced confident
            // nonsense. Restored in the finally below so the flag cannot leak into whatever
            // runs next in the same process.
            int savedSquad = GameEngine.OpeningSquadSize;
            GameEngine.OpeningSquadSize = 0;
            try
            {
            foreach (var row in Table)
            {
                var engine = new GameEngine(new GameState());
                var p1 = engine._state.Player1;

                // Drive the level directly rather than buying it: this measures the spawn
                // machinery, and routing through money would make the test depend on the
                // price table it is separately checking.
                p1.AutoSpawnLevel = row.Level;
                p1.AutoSpawnAccumulator = 0;
                p1.AutoSpawnCycleIndex = 0;

                // AN UNKILLABLE PAIR OF CASTLES. At the top levels one side is handed a free
                // tier-8 stream and the other has nothing, so the game reaches game-over
                // well inside the window -- and Tick() returns early once it does, which
                // silently truncates the sample and reads as a low spawn rate. That was the
                // second wrong version of this check.
                foreach (var p in new[] { engine._state.Player1, engine._state.Player2 })
                {
                    p.CastleMaxHealth = int.MaxValue / 4;
                    p.CastleHealth = int.MaxValue / 4;
                }

                var spawnedTiers = new List<int>();
                var seen = new HashSet<Guid>();

                for (int t = 0; t < seconds * GameEngine.TICKS_PER_SECOND; t++)
                {
                    engine.Tick();
                    // Identity, not list position: units die and are REMOVED from
                    // _state.Units mid-tick, so a "what is new since index N" scan reads
                    // survivors as fresh spawns. New units are appended, so walking the list
                    // in order and taking the unseen ones recovers true spawn order.
                    foreach (var unit in engine._state.Units)
                    {
                        if (!seen.Add(unit.InstanceId)) continue;
                        if (unit.Side == 1) spawnedTiers.Add(unit.Tier);
                    }
                }

                if (engine._state.IsGameOver)
                {
                    Console.WriteLine($"  L{row.Level,-2} FAIL game ended inside the measurement window");
                    failures++;
                    continue;
                }

                var fromAuto = spawnedTiers;
                double measuredRate = (double)fromAuto.Count / seconds;
                bool rateOk = Math.Abs(measuredRate - row.PerSecond) < 0.05;

                // The observed stream must be the design cycle repeated.
                bool orderOk = true;
                for (int i = 0; i < fromAuto.Count; i++)
                {
                    if (fromAuto[i] != row.Tiers[i % row.Tiers.Length]) { orderOk = false; break; }
                }

                string cycle = string.Join(",", row.Tiers);
                string status = rateOk && orderOk ? "ok" : "FAIL";
                if (!rateOk || !orderOk) failures++;

                Console.WriteLine($"  L{row.Level,-2} rate {measuredRate,5:0.00}/s (want {row.PerSecond})  cycle [{cycle}]  {status}"
                    + (orderOk ? "" : $"  got [{string.Join(",", fromAuto.Take(row.Tiers.Length * 2))}]"));
            }
            }
            finally { GameEngine.OpeningSquadSize = savedSquad; }
            Console.WriteLine();

            // ---- 3. UPGRADE RESTARTS THE CYCLE -----------------------------------------
            {
                var p = new PlayerState();
                p.AutoSpawnLevel = 5;
                p.AutoSpawnCycleIndex = 2;      // mid-cycle
                p.ApplyAutoSpawnStep();
                bool ok = p.AutoSpawnLevel == 6 && p.AutoSpawnCycleIndex == 0;
                Console.WriteLine($"UPGRADE RESTARTS CYCLE: level {p.AutoSpawnLevel}, index {p.AutoSpawnCycleIndex}  {(ok ? "ok" : "FAIL")}");
                if (!ok) failures++;
            }

            // ---- 4. THE LADDER TERMINATES ----------------------------------------------
            {
                var p = new PlayerState();
                for (int i = 0; i < 40; i++) p.ApplyAutoSpawnStep();
                bool ok = p.AutoSpawnLevel == PlayerState.MaxAutoSpawnLevel;
                Console.WriteLine($"LADDER CAPS AT MAX: level {p.AutoSpawnLevel} (want {PlayerState.MaxAutoSpawnLevel})  {(ok ? "ok" : "FAIL")}");
                if (!ok) failures++;
            }

            // ---- 5. THE ACTION MASK IS UNCHANGED ---------------------------------------
            //
            // The whole reason action 14 is not in the mask: every trained model and every
            // pinned benchmark depends on the mask being 14 wide. If this ever fails, the
            // models are invalidated, and that must be a deliberate decision rather than a
            // surprise discovered later.
            {
                var state = new GameState();
                // GetActionMask dereferences all three gadget slots, and a bare GameState
                // leaves them null -- only the hub assigns a loadout. Any three real
                // definitions will do; the mask WIDTH is what is under test.
                foreach (var p in new[] { state.Player1, state.Player2 })
                {
                    p.OffensiveGadget = GameDataManager.Gadgets[0];
                    p.DefensiveGadget = GameDataManager.Gadgets[1];
                    p.SignatureGadget = GameDataManager.Gadgets[2];
                }
                int[] mask = state.GetActionMask(1);
                bool ok = mask.Length == 14;
                Console.WriteLine($"ACTION MASK WIDTH: {mask.Length} (want 14, auto-spawn deliberately excluded)  {(ok ? "ok" : "FAIL")}");
                if (!ok) failures++;
            }

            // ---- 6. NOTHING NON-FINITE REACHES SERIALISABLE STATE ----------------------
            //
            // A non-finite double in game state is a LIVE-GAME CRASH, not a cosmetic bug:
            // System.Text.Json throws "\.NET number values such as positive and negative
            // infinity cannot be written as valid JSON", SignalR surfaces it inside its
            // per-connection write pipeline, and that one client is aborted while the server
            // keeps simulating. The player sees a freeze and a failed rejoin. This has
            // happened before -- wall_3 wrote float.PositiveInfinity into Unit.AttackCooldown
            // (commit 29d64bfe) -- and the rule adopted then was that nothing should be able
            // to write a non-finite value into serialisable state.
            //
            // AutoSpawnPrice is the auto-spawner's exposure: AutoSpawnPriceFor returns
            // PositiveInfinity for "there is no such level", and the top of the ladder asks
            // it for level 20.
            {
                var p = new PlayerState();
                bool ok = true;
                for (int lvl = 0; lvl <= PlayerState.MaxAutoSpawnLevel + 2; lvl++)
                {
                    if (double.IsNaN(p.AutoSpawnPrice) || double.IsInfinity(p.AutoSpawnPrice))
                    {
                        Console.WriteLine($"NON-FINITE PRICE at level {p.AutoSpawnLevel}: {p.AutoSpawnPrice}  FAIL");
                        ok = false;
                        break;
                    }
                    p.ApplyAutoSpawnStep();
                }

                // Prove it end-to-end through the real serialiser rather than trusting the
                // IsInfinity check: this is the exact call SignalR makes.
                string json = null;
                string threw = null;
                try
                {
                    json = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        p.AutoSpawnLevel,
                        p.AutoSpawnPrice,
                        p.Money,
                        p.Income,
                        p.InvestmentPrice,
                        p.RepairPrice,
                    });
                }
                catch (Exception ex) { threw = ex.GetType().Name + ": " + ex.Message; ok = false; }

                Console.WriteLine($"SERIALISES AT MAX LEVEL: {(threw == null ? json : threw)}  {(ok ? "ok" : "FAIL")}");
                if (!ok) failures++;
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        }
    }
}
