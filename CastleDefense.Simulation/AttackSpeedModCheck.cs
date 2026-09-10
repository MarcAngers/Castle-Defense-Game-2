using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// DOES A SPEED BUFF / SLOW ACTUALLY CHANGE HOW OFTEN A UNIT SWINGS?
    ///
    /// Added 2026-09-03. The roster's AttackSpeed is DERIVED from move speed (the Doggo
    /// Formula, k * Speed / Damage), so a status that changes movement without changing the
    /// swing rate contradicts the formula the stat comes from. `speedMod` now scales both.
    ///
    /// Counted by ATTACKS LANDED over a fixed window rather than by reading AttackCooldown,
    /// because the cooldown is a live timer and the thing that matters is the observable
    /// rate. A wall is included on purpose: AttackSpeed 0 times any multiplier is still 0,
    /// and 1000f/0 is the float.PositiveInfinity that has twice made game state
    /// unserialisable and dropped live games.
    ///
    /// Run: dotnet run --project CastleDefense.Simulation -- --attack-speed-mod-check
    /// </summary>
    public static class AttackSpeedModCheck
    {
        /// <summary>
        /// Attacks a lone unit lands on an indestructible castle over <paramref name="ticks"/>,
        /// with an optional Slow/Speed status pinned on for the whole window.
        ///
        /// Castle damage is the cleanest counter available: one swing is one
        /// pendingCastleDamage entry, so total damage / damage-per-swing IS the swing count,
        /// with no combat targeting or unit deaths in the way.
        /// </summary>
        private static double SwingsPerSecond(string unitId, string statusName, float statusValue, int ticks)
        {
            int savedSquad = GameEngine.OpeningSquadSize;
            GameEngine.OpeningSquadSize = 0;
            try
            {
                var state = new GameState(TeamColour.Blue, new Random(7));   // no map HP/dmg effect
                state.Player1.Team = TeamColour.White;
                state.Player2.Team = TeamColour.White;
                string sig = GameDataManager.GetSignatureGadgetIdForTeam(TeamColour.White);
                state.Player1.SetLoadout(new[] { "nuke", "heal", sig });
                state.Player2.SetLoadout(new[] { "nuke", "heal", sig });
                state.Player2.CastleHealth = state.Player2.CastleMaxHealth = 1_000_000_000;

                var engine = new GameEngine(state, null, 4242);

                // Right up against the enemy castle wall so it attacks from tick 1 and never
                // spends the window walking.
                engine.SpawnUnit(1, unitId, true, GameEngine.P2_CASTLE_WALL - 60);
                var unit = state.Units.First(u => u.Side == 1);

                double startHp = state.Player2.CastleHealth;
                for (int i = 0; i < ticks; i++)
                {
                    // Refreshed every tick so the window is a clean steady state rather than
                    // a mix of buffed and unbuffed ticks as a duration expires.
                    if (statusName != null)
                    {
                        unit.Statuses.RemoveAll(st => st.Name == statusName);
                        unit.Statuses.Add(new ActiveStatus(statusName, state.CurrentTick + 60, statusValue));
                    }
                    engine.Tick();
                }

                var def = GameDataManager.Teams.SelectMany(t => t.Roster).First(u => u.Id == unitId);
                double perSwing = def.Damage * (def.AttackType == AttackType.Siege ? 2 : 1);
                double swings = (startHp - state.Player2.CastleHealth) / perSwing;
                return swings / (ticks / (double)GameEngine.TICKS_PER_SECOND);
            }
            finally { GameEngine.OpeningSquadSize = savedSquad; }
        }

        /// <summary>
        /// The rate a unit with this attack speed can ACTUALLY achieve.
        ///
        /// AttackCooldown is set in milliseconds and decremented once per tick, so a unit
        /// swings every ceil(cooldownMs / msPerTick) TICKS -- never on a fraction of one. A
        /// x10 buff on a 0.83/s unit therefore yields 7.5/s, not 8.33/s, because 120ms rounds
        /// up to 4 ticks. Modelling that here rather than widening the tolerance is what keeps
        /// the check able to catch a real regression: a loose tolerance would have accepted
        /// the multiplier being dropped on the floor entirely at the small end.
        /// </summary>
        private static double QuantisedRate(double aps, int windowTicks)
        {
            if (aps <= 0) return 0;
            double msPerTick = 1000.0 / GameEngine.TICKS_PER_SECOND;
            int period = Math.Max(1, (int)Math.Ceiling((1000.0 / aps) / msPerTick));
            long swings = (windowTicks - 1) / period + 1;      // it swings on the first tick too
            return swings / (windowTicks / (double)GameEngine.TICKS_PER_SECOND);
        }

        public static void Run(string[] args)
        {
            int failures = 0;
            void Check(string label, bool ok, string detail = null)
            {
                Console.WriteLine($"  {label,-54} {(ok ? "ok" : "FAIL")}{(detail == null ? "" : "  " + detail)}");
                if (!ok) failures++;
            }

            Console.WriteLine("=== ATTACK-SPEED MODIFIER CHECK ===");
            Console.WriteLine();

            // A tier-6 sits comfortably between the two clamps, so its baseline is the
            // formula's real output and both directions have room to move.
            var white = GameDataManager.Teams.First(t => t.Color == TeamColour.White);
            string mid = white.Roster.First(u => u.Tier == 6).Id;
            const int Ticks = 900;   // 30s, long enough that rounding on one swing is noise

            float baseApsRaw = white.Roster.First(u => u.Tier == 6).AttackSpeed;
            double baseline = SwingsPerSecond(mid, null, 1f, Ticks);
            Console.WriteLine($"BASELINE: {mid} AttackSpeed {baseApsRaw:F3}/s -> swings {baseline:F2}/s");
            Console.WriteLine($"(rates are TICK-QUANTISED: the cooldown only decrements once per tick,");
            Console.WriteLine($" so the achievable rate is 30 / ceil(cooldownTicks), not the raw value)");
            Console.WriteLine();

            Console.WriteLine("THE MULTIPLIER CARRIES THROUGH");
            foreach (var (name, value, label) in new[]
            {
                ("Speed", 1.5f, "speed lvl 1"),
                ("Speed", 2.0f, "speed lvl 2"),
                ("Speed", 10f,  "speed lvl 3"),
                ("Slow",  0.5f, "goo"),
                ("Slow",  0.25f,"freeze trail"),
            })
            {
                double got = SwingsPerSecond(mid, name, value, Ticks);
                double want = QuantisedRate(baseApsRaw * value, Ticks);
                bool ok = Math.Abs(got - want) < 0.005;
                Check($"{label,-14} x{value,-5} -> {got,6:F2}/s", ok, $"expected {want:F2}/s");
            }
            Console.WriteLine();

            // The direction is the part a player would notice, so assert it separately from
            // the arithmetic: buffed must be faster than base, slowed must be slower.
            Console.WriteLine("DIRECTION");
            {
                double fast = SwingsPerSecond(mid, "Speed", 1.5f, Ticks);
                double slow = SwingsPerSecond(mid, "Slow", 0.5f, Ticks);
                Check("a Speed buff swings FASTER than baseline", fast > baseline * 1.2,
                      $"{fast:F2} vs {baseline:F2}");
                Check("a Slow swings SLOWER than baseline", slow < baseline * 0.8,
                      $"{slow:F2} vs {baseline:F2}");
            }
            Console.WriteLine();

            // A wall has AttackSpeed 0. 0 x anything is still 0, and the guard must keep it
            // out of the 1000f/x division -- non-finite state is the documented crash.
            Console.WriteLine("A WALL STILL CANNOT ATTACK, AND STILL SERIALISES");
            {
                int savedSquad = GameEngine.OpeningSquadSize;
                GameEngine.OpeningSquadSize = 0;
                try
                {
                    var state = new GameState(TeamColour.Blue, new Random(7));
                    state.Player1.Team = state.Player2.Team = TeamColour.White;
                    string sig = GameDataManager.GetSignatureGadgetIdForTeam(TeamColour.White);
                    state.Player1.SetLoadout(new[] { "nuke", "wall", sig });
                    state.Player2.SetLoadout(new[] { "nuke", "wall", sig });
                    var engine = new GameEngine(state, null, 99);
                    engine.SpawnUnit(1, "wall_3", true, GameEngine.P2_CASTLE_WALL - 60);
                    var wall = state.Units.First(u => u.Side == 1);

                    double hp0 = state.Player2.CastleHealth;
                    for (int i = 0; i < 120; i++)
                    {
                        wall.Statuses.RemoveAll(st => st.Name == "Speed");
                        wall.Statuses.Add(new ActiveStatus("Speed", state.CurrentTick + 60, 10f));
                        engine.Tick();
                    }
                    Check("a x10-buffed wall deals no castle damage",
                          Math.Abs(state.Player2.CastleHealth - hp0) < 0.001);
                    Check("its AttackCooldown is finite", float.IsFinite(wall.AttackCooldown),
                          $"{wall.AttackCooldown}");
                    string json = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        wall.AttackCooldown, wall.CurrentSpeed, wall.Position,
                    });
                    Check("and the unit still serialises", json.Length > 0, json);
                }
                finally { GameEngine.OpeningSquadSize = savedSquad; }
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
            if (failures > 0) Environment.ExitCode = 1;
        }
    }
}
