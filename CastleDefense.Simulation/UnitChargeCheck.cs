using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// DO UNIT CHARGES BEHAVE THE WAY THE RULE SAYS?
    ///
    /// Five charges per buyable unit, one back per second, and running out puts that unit on
    /// cooldown until the next charge lands. The rule is small; what makes it worth a guard
    /// is that it is enforced in THREE places that can disagree, and two of the three are
    /// invisible when they drift:
    ///
    ///   - GameEngine.SpawnUnit refuses the purchase and spends the charge.
    ///   - GameEngine.TickCooldowns regenerates it.
    ///   - GameState.GetActionMask decides whether a policy may even pick the action.
    ///
    /// A mask that disagrees with SpawnUnit is the dangerous one: the bot picks a unit it
    /// cannot buy, the purchase silently fails, and the decision is wasted with nothing in
    /// any log to say so.
    ///
    /// Also guards the two things that must NOT consume a charge -- free spawns and the
    /// engine's rollout clones.
    ///
    /// Run: dotnet run --project CastleDefense.Simulation -- --unit-charge-check
    /// </summary>
    public static class UnitChargeCheck
    {
        public static void Run(string[] args)
        {
            int failures = 0;
            void Check(string label, bool ok, string detail = null)
            {
                Console.WriteLine($"  {label,-52} {(ok ? "ok" : "FAIL")}{(detail == null ? "" : "  " + detail)}");
                if (!ok) failures++;
            }

            Console.WriteLine("=== UNIT CHARGE CHECK ===");
            Console.WriteLine();

            // The opening squad would spend the first tick spawning free units; off for the
            // duration so the counts below are purely what this check buys.
            int savedSquad = GameEngine.OpeningSquadSize;
            GameEngine.OpeningSquadSize = 0;
            try
            {
                // ---- A GAME STARTS FULL --------------------------------------------------
                Console.WriteLine("STARTING STATE");
                {
                    var engine = new GameEngine(new GameState());
                    var p = engine._state.Player1;
                    var roster = GameDataManager.Teams.Find(t => t.Color == p.Team)?.Roster;
                    bool allFull = roster != null && roster.Count > 0
                                && roster.All(u => p.GetUnitCharges(u.Id) == PlayerState.UnitMaxCharges);
                    Check($"every roster unit starts at {PlayerState.UnitMaxCharges}", allFull);
                    // The dictionary should be EMPTY at this point: absent means full, and
                    // seeding it would be dead weight on every clone and every wire frame.
                    Check("charge dictionary starts empty (absent == full)", p.UnitCharges.Count == 0,
                          $"count={p.UnitCharges.Count}");
                }
                Console.WriteLine();

                // ---- SPENDING AND REFUSAL ------------------------------------------------
                Console.WriteLine("SPENDING");
                string unitId;
                {
                    var engine = new GameEngine(new GameState());
                    var p = engine._state.Player1;
                    var roster = GameDataManager.Teams.Find(t => t.Color == p.Team).Roster;
                    unitId = roster[0].Id;
                    p.Money = 1_000_000;   // money must never be the binding constraint here

                    int bought = 0;
                    for (int i = 0; i < PlayerState.UnitMaxCharges + 3; i++)
                        if (engine.SpawnUnit(1, unitId)) bought++;

                    Check($"exactly {PlayerState.UnitMaxCharges} buys succeed back-to-back",
                          bought == PlayerState.UnitMaxCharges, $"got {bought}");
                    Check("charges are then 0", p.GetUnitCharges(unitId) == 0,
                          $"got {p.GetUnitCharges(unitId)}");
                    Check("a refused buy costs no money", Math.Abs(p.Money - (1_000_000 - PlayerState.UnitMaxCharges * roster[0].Cost)) < 0.001,
                          $"money={p.Money:0.##}");

                    // THE MASK MUST AGREE WITH THE ENGINE.
                    p.OffensiveGadget ??= GameDataManager.Gadgets[0];
                    p.DefensiveGadget ??= GameDataManager.Gadgets[1];
                    p.SignatureGadget ??= GameDataManager.Gadgets[2];
                    int[] mask = engine._state.GetActionMask(1);
                    Check("action mask closes that tier at 0 charges", mask[roster[0].Tier] == 0);
                }
                Console.WriteLine();

                // ---- REGENERATION --------------------------------------------------------
                Console.WriteLine($"REGENERATION (1 charge / {PlayerState.UnitChargeRegenMs}ms)");
                {
                    var engine = new GameEngine(new GameState());
                    var p = engine._state.Player1;
                    var roster = GameDataManager.Teams.Find(t => t.Color == p.Team).Roster;
                    string id = roster[0].Id;
                    p.Money = 1_000_000;
                    for (int i = 0; i < PlayerState.UnitMaxCharges; i++) engine.SpawnUnit(1, id);

                    // Sample once per second for long enough to refill and then some.
                    var observed = new List<int>();
                    for (int sec = 1; sec <= PlayerState.UnitMaxCharges + 2; sec++)
                    {
                        for (int t = 0; t < GameEngine.TICKS_PER_SECOND; t++) engine.Tick();
                        observed.Add(p.GetUnitCharges(id));
                    }

                    var want = new List<int>();
                    for (int sec = 1; sec <= PlayerState.UnitMaxCharges + 2; sec++)
                        want.Add(Math.Min(sec, PlayerState.UnitMaxCharges));

                    Check("charges per second after draining to zero",
                          observed.SequenceEqual(want),
                          $"got [{string.Join(",", observed)}] want [{string.Join(",", want)}]");

                    // Once full the timer must stop, or the entry is sent to the client and
                    // ticked forever for nothing.
                    p.CooldownTimers.TryGetValue(id, out long left);
                    Check("regen timer stops once full", left <= 0, $"timer={left}");
                }
                Console.WriteLine();

                // ---- FREE SPAWNS ARE FREE ------------------------------------------------
                Console.WriteLine("FREE SPAWNS DO NOT CONSUME CHARGES");
                {
                    var engine = new GameEngine(new GameState());
                    var p = engine._state.Player1;
                    var roster = GameDataManager.Teams.Find(t => t.Color == p.Team).Roster;
                    string id = roster[0].Id;

                    for (int i = 0; i < 20; i++) engine.SpawnUnit(1, id, ignoreCost: true);
                    Check("20 ignoreCost spawns leave charges full",
                          p.GetUnitCharges(id) == PlayerState.UnitMaxCharges,
                          $"got {p.GetUnitCharges(id)}");
                    Check("and write no charge entry", p.UnitCharges.Count == 0,
                          $"count={p.UnitCharges.Count}");
                }
                Console.WriteLine();

                // ---- CLONE ISOLATION ------------------------------------------------------
                //
                // Search runs ~231x rollouts off Clone(). If the charge dictionary were
                // shared, a speculative purchase inside a rollout would spend the LIVE
                // game's charge. PlayerState.Clone copies it, and this is the assertion that
                // says so out loud.
                Console.WriteLine("ROLLOUT CLONES DO NOT SPEND THE LIVE GAME'S CHARGES");
                {
                    var engine = new GameEngine(new GameState());
                    var p = engine._state.Player1;
                    var roster = GameDataManager.Teams.Find(t => t.Color == p.Team).Roster;
                    string id = roster[0].Id;
                    p.Money = 1_000_000;

                    var clone = engine.Clone();
                    var cp = clone._state.Player1;
                    cp.Money = 1_000_000;
                    for (int i = 0; i < PlayerState.UnitMaxCharges; i++) clone.SpawnUnit(1, id);

                    Check("clone drained to 0", cp.GetUnitCharges(id) == 0, $"got {cp.GetUnitCharges(id)}");
                    Check("original still full", p.GetUnitCharges(id) == PlayerState.UnitMaxCharges,
                          $"got {p.GetUnitCharges(id)}");
                }
            }
            finally { GameEngine.OpeningSquadSize = savedSquad; }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        }
    }
}
