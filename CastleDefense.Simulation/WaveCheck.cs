using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Gadgets;
using CastleDefense.Engine.Models;
using CastleDefense.Engine.Models.Hazards;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// DOES THE WAVE STOP WHEN ITS BUDGET RUNS OUT?
    ///
    /// Added 2026-09-03 with the knockback cap. A wave used to sweep the whole map and launch
    /// everything it touched, so its power scaled with however many units were on the field.
    /// It now carries MaxKnockbacks, spends one per DISTINCT unit launched, and collapses the
    /// moment it is spent.
    ///
    /// Four things can drift, and three of them are invisible in a normal game:
    ///
    ///  1. THE CAP ITSELF. Levels 1-2 are BaseValue/10, level 3 is a flat 1,000 that
    ///     deliberately does NOT follow that rule. A CSV rebalance of the KNOCKBACK column
    ///     silently moves two of the three.
    ///
    ///  2. DISTINCT UNITS, NOT HITS. A unit sitting inside the wave is pushed every tick;
    ///     only the first costs budget. Counting hits instead would make the cap depend on
    ///     the wave's width and speed, and a 50-unit cap would be spent on a handful of
    ///     units -- a large balance change wearing the same number.
    ///
    ///  3. IT MUST ACTUALLY STOP. The hazard has to leave GameState.Hazards, because that is
    ///     the ONLY signal the client has that the animation should fall away (see
    ///     wave-animator.js -- there is no separate message).
    ///
    ///  4. CLONE ISOLATION. WaveHazard holds the only reference-typed field on any hazard, so
    ///     a memberwise copy would let a search rollout spend the live wave's budget. That is
    ///     the trap Hazard.Clone's own doc comment warns about.
    ///
    /// Run: dotnet run --project CastleDefense.Simulation -- --wave-check
    /// </summary>
    public static class WaveCheck
    {
        // The design table, transcribed rather than read from WaveEffect.
        private static readonly (string Id, int Cap)[] Table =
        {
            ("wave",   50),
            ("wave_2", 100),
            ("wave_3", 1000),
        };

        public static void Run(string[] args)
        {
            int failures = 0;
            void Check(string label, bool ok, string detail = null)
            {
                Console.WriteLine($"  {label,-52} {(ok ? "ok" : "FAIL")}{(detail == null ? "" : "  " + detail)}");
                if (!ok) failures++;
            }

            Console.WriteLine("=== WAVE CHECK ===");
            Console.WriteLine();

            // ---- 1. THE CAP TABLE -------------------------------------------------------
            Console.WriteLine("CAP PER LEVEL");
            foreach (var row in Table)
            {
                var def = GameDataManager.Gadgets.Find(g => g.Id == row.Id);
                if (def == null) { Check($"{row.Id} exists", false); continue; }
                int cap = WaveEffect.CapFor(def);
                Check($"{row.Id} cap {cap} (BaseValue {def.BaseValue})", cap == row.Cap, $"design {row.Cap}");
            }
            Console.WriteLine();

            // ---- 2 & 3. A REAL WAVE OVER A REAL CROWD -----------------------------------
            //
            // Level 1 (cap 50) against 120 tier-1 units packed along the field: the wave must
            // launch exactly 50 of them and then disappear, well before HazardDuration.
            Console.WriteLine("LIVE WAVE (wave level 1, cap 50, 120 targets)");
            int savedSquad = GameEngine.OpeningSquadSize;
            GameEngine.OpeningSquadSize = 0;
            try
            {
                var def = GameDataManager.Gadgets.Find(g => g.Id == "wave");
                var state = new GameState();
                state.Player1.Team = TeamColour.Blue;
                state.Player2.Team = TeamColour.White;
                state.Player1.SetLoadout(new[] { "nuke", "heal", "wave" });
                state.Player2.SetLoadout(new[] { "nuke", "heal",
                    GameDataManager.GetSignatureGadgetIdForTeam(TeamColour.White) });

                var engine = new GameEngine(state, null, 999);
                state.Player1.Money = 100000;

                // 120 enemy tier-1 bodies, spread across the field so the wave meets them
                // over many ticks rather than all at once -- which is what makes "distinct
                // units, not hits" a meaningful thing to assert.
                var roster = GameDataManager.Teams.Find(t => t.Color == TeamColour.White).Roster;
                string chump = roster.Find(u => u.Tier == 1).Id;
                for (int i = 0; i < 120; i++)
                    engine.SpawnUnit(2, chump, true, 300 + i * 12);
                Check("120 targets on the field", state.Units.Count(u => u.Side == 2) == 120,
                      $"{state.Units.Count(u => u.Side == 2)}");

                bool cast = engine.UseGadget(1, "wave", 0);
                Check("cast accepted", cast);

                var wave = state.Hazards.OfType<WaveHazard>().FirstOrDefault();
                Check("wave hazard created", wave != null);
                Check($"its cap is {WaveEffect.CapFor(def)}", wave != null && wave.MaxKnockbacks == 50,
                      $"{wave?.MaxKnockbacks}");

                int expiresAt = wave?.ExpiresAtTick ?? 0;
                long start = state.CurrentTick;
                int endedAt = -1;
                for (int i = 0; i < def.HazardDuration + 60; i++)
                {
                    engine.Tick();
                    if (!state.Hazards.OfType<WaveHazard>().Any()) { endedAt = (int)state.CurrentTick; break; }
                }

                Check("the wave ended", endedAt >= 0, endedAt < 0 ? "still alive" : $"tick {endedAt}");
                Check("it ended EARLY, not on HazardDuration",
                      endedAt >= 0 && endedAt < expiresAt,
                      $"ended {endedAt}, HazardDuration would expire at {expiresAt}");
                Check("it launched exactly its cap, no more",
                      wave != null && wave.LaunchedCount == 50, $"{wave?.LaunchedCount}");

                // DISTINCT UNITS, NOT HITS: 50 launched out of 120 means 70 were never
                // touched. If the budget were being spent per HIT the wave would have run
                // out against a far smaller number of units.
                Check("70 targets were never reached", wave != null && wave.LaunchedCount < 120);
            }
            finally
            {
                GameEngine.OpeningSquadSize = savedSquad;
            }
            Console.WriteLine();

            // ---- 4. CLONE ISOLATION -----------------------------------------------------
            //
            // WaveHazard._launched is the only reference-typed field on any hazard. A rollout
            // that shares it would spend the live game's budget -- silently, and only in
            // games where a search bot happened to be thinking while a wave was crossing.
            Console.WriteLine("CLONE ISOLATION");
            {
                var state = new GameState();
                state.Player1.Team = TeamColour.Blue;
                state.Player2.Team = TeamColour.White;
                state.Player1.SetLoadout(new[] { "nuke", "heal", "wave" });
                state.Player2.SetLoadout(new[] { "nuke", "heal",
                    GameDataManager.GetSignatureGadgetIdForTeam(TeamColour.White) });
                var engine = new GameEngine(state, null, 4242);
                state.Player1.Money = 100000;

                var roster = GameDataManager.Teams.Find(t => t.Color == TeamColour.White).Roster;
                string chump = roster.Find(u => u.Tier == 1).Id;
                for (int i = 0; i < 30; i++) engine.SpawnUnit(2, chump, true, 200 + i * 15);
                engine.UseGadget(1, "wave", 0);

                var clone = engine.Clone(4242);
                var original = state.Hazards.OfType<WaveHazard>().First();
                var copy = clone._state.Hazards.OfType<WaveHazard>().First();

                Check("the clone got its own WaveHazard", !ReferenceEquals(original, copy));

                // Run the CLONE forward; the original's budget must not move.
                int before = original.LaunchedCount;
                for (int i = 0; i < 60; i++) clone.Tick();
                Check("advancing the clone spent none of the original's budget",
                      original.LaunchedCount == before,
                      $"original {before} -> {original.LaunchedCount}, clone {copy.LaunchedCount}");
                Check("the clone's own wave did spend budget", copy.LaunchedCount > 0,
                      $"{copy.LaunchedCount}");
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
            if (failures > 0) Environment.ExitCode = 1;
        }
    }
}
