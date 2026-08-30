using System.Text;
using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    /// <summary>
    /// Proves GameEngine.Clone() is correct, rather than assuming it.
    ///
    /// Cloning fails in ways that are silent by nature — a shared object reference
    /// produces a subtly wrong rollout, not an exception — so it needs a test that
    /// actively looks for sharing. This project has already been bitten by exactly that:
    /// a shallow Unit copy once let a shadow bot's hypothetical gadget casts attach real
    /// status effects to the live game, and it went unnoticed for a long time.
    ///
    /// Three properties, checked on real mid-game positions:
    ///
    ///   COMPLETENESS  a fresh clone fingerprints identically to its parent. Catches any
    ///                 field that failed to copy.
    ///   ISOLATION     advancing the clone leaves the parent bit-identical. Catches any
    ///                 field that is shared instead of copied — the dangerous case.
    ///   DETERMINISM   two clones taken from the same position with the same seed and
    ///                 advanced identically end up identical. Catches unseeded randomness
    ///                 and hidden order-dependence, which would make search results noise.
    ///
    /// Usage: clone-check [games] [--seed N] [--advance ticks]
    /// </summary>
    public static class CloneCheck
    {
        /// <summary>
        /// Canonical string form of everything that can legally differ between two games.
        /// Deliberately verbose rather than a hash — when a check fails you want to see
        /// WHICH field diverged, and a hash tells you nothing.
        /// </summary>
        private static string Fingerprint(GameState s)
        {
            var sb = new StringBuilder();
            sb.Append($"tick={s.CurrentTick};over={s.IsGameOver};winner={s.WinnerSide};");
            sb.Append($"limit={s.IsTimeLimit};map={s.Map};shadow={s.ShadowMap};");

            foreach (var p in new[] { s.Player1, s.Player2 })
            {
                sb.Append($"|P{p.Side}:money={p.Money:F4},inc={p.Income:F4},invPrice={p.InvestmentPrice:F4},");
                sb.Append($"inv={p.InvestmentCount},arma={p.ArmageddonUsed},hp={p.CastleHealth},maxhp={p.CastleMaxHealth},");
                sb.Append($"repPrice={p.RepairPrice:F4},rep={p.RepairCount},invuln={p.IsInvulnerable}@{p.InvulnerableUntilTick},armaShield={p.ArmageddonShieldUntilTick},");
                sb.Append($"off={p.OffensiveGadget?.Id},def={p.DefensiveGadget?.Id},sig={p.SignatureGadget?.Id},");
                foreach (var kv in p.GadgetCooldowns.OrderBy(k => k.Key)) sb.Append($"cd[{kv.Key}]={kv.Value},");
                foreach (var kv in p.GadgetXp.OrderBy(k => k.Key)) sb.Append($"xp[{kv.Key}]={kv.Value},");
                foreach (var kv in p.UnitCharges.OrderBy(k => k.Key)) sb.Append($"ch[{kv.Key}]={kv.Value},");
                foreach (var kv in p.CooldownTimers.OrderBy(k => k.Key)) sb.Append($"ct[{kv.Key}]={kv.Value},");
            }

            // Units are compared in LIST ORDER, not sorted. The first version of this
            // sorted by InstanceId to avoid "false" ordering diffs — that was wrong twice
            // over. Units are iterated in list order by MoveAndFight, so the order is real
            // game state and a difference in it is a genuine divergence worth catching.
            // Worse, sorting by a then-nondeterministic Guid reshuffled the two sides of
            // the comparison independently, so a single differing id made unrelated units
            // line up against each other and the diff pointed at the wrong thing entirely.
            foreach (var u in s.Units)
            {
                sb.Append($"|U{u.InstanceId:N}:{u.DefinitionId},s={u.Side},pos={u.Position:F4},y={u.YPosition},");
                sb.Append($"hp={u.CurrentHealth},sh={u.CurrentShield},spd={u.CurrentSpeed:F4},cd={u.AttackCooldown:F4},");
                sb.Append($"kb={u.PendingKnockback:F4}@{u.LastKnockbackTick},awk={u.AttacksWithoutKnockback},");
                foreach (var st in u.Statuses.OrderBy(x => x.Name).ThenBy(x => x.ExpiresAtTick))
                    sb.Append($"st[{st.Name}:{st.ExpiresAtTick}:{st.Value:F4}:{st.Side}:{st.SourceGadgetId}],");
            }

            foreach (var h in s.Hazards.OrderBy(h => h.Type).ThenBy(h => h.Position).ThenBy(h => h.ExpiresAtTick))
                sb.Append($"|H{h.Type}:{h.GetType().Name},s={h.Side},pos={h.Position:F4},w={h.Width:F4},exp={h.ExpiresAtTick},src={h.SourceGadgetId};");

            return sb.ToString();
        }

        private static string FirstDifference(string a, string b)
        {
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            int from = Math.Max(0, i - 60);
            return $"      at offset {i}\n        expected ...{a.Substring(from, Math.Min(140, a.Length - from))}\n        actual   ...{b.Substring(from, Math.Min(140, b.Length - from))}";
        }

        private static void Advance(GameEngine engine, GameState state, int ticks)
        {
            // Fresh bots each time: HeuristicBot decides purely from board state and tick,
            // so identical positions produce identical play. Reusing a bot instance would
            // leak its internal timers across runs and invalidate the comparison.
            var p1 = new HeuristicBotAdapter(1);
            var p2 = new HeuristicBotAdapter(2);
            for (int t = 0; t < ticks && !state.IsGameOver; t++)
            {
                engine.Tick();
                p1.Update(engine);
                p2.Update(engine);
            }
        }

        public static void Run(string[] args)
        {
            int games = 20, seed = 4242, advance = 400;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
                else if (args[i] == "--advance" && i + 1 < args.Length) advance = int.Parse(args[++i]);
                else if (int.TryParse(args[i], out var g)) games = g;
            }

            // ── EVENT-SUBSCRIBER LEAK GUARD (added 2026-08-20) ──────────────────────
            //
            // An event is a mutable reference field, so a shallow Clone inherits its whole
            // subscriber list and every rollout raises it on the REAL game's handlers. Not
            // hypothetical: OnGadgetCast was added for replay v3 and not nulled in Clone, so
            // game 9A60C6 recorded 49,448 gadget casts for a game containing 50 -- a 357KB
            // replay and 30% of the entire gadget_uses table, all from search's rollouts.
            //
            // Delegates cannot be synthesised generically by reflection, so each event is
            // subscribed explicitly. The COUNT ASSERTION below is what makes this a real
            // guard rather than a snapshot: add a fourth event to GameEngine and this test
            // fails until someone decides whether Clone should null it.
            {
                var probeState = new GameState();
                probeState.Player1 = new PlayerState(); probeState.Player1.Side = 1;
                probeState.Player2 = new PlayerState(); probeState.Player2.Side = 2;
                probeState.Player1.Team = TeamColour.White; probeState.Player2.Team = TeamColour.White;
                probeState.Player1.SetLoadout(new[] { "nuke", "reinforcements", "cash" });
                probeState.Player2.SetLoadout(new[] { "nuke", "reinforcements", "cash" });
                var probe = new GameEngine(probeState, null, 1);

                probe.OnGadgetAnimation += (a, b, c, d) => { };
                probe.OnGadgetUpgraded  += (a, b) => { };
                probe.OnGadgetCast      += (a, b, c) => { };

                var clone = probe.Clone(2);
                int leaked = 0;
                foreach (string name in new[] { "OnGadgetAnimation", "OnGadgetUpgraded", "OnGadgetCast" })
                {
                    var f = typeof(GameEngine).GetField(name,
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (f != null && f.GetValue(clone) != null)
                    {
                        leaked++;
                        Console.WriteLine($"  [FAIL] event '{name}' SURVIVED Clone -- null it in "
                                        + "GameEngine.Clone or every rollout will raise it");
                    }
                }
                int evCount = typeof(GameEngine).GetEvents(System.Reflection.BindingFlags.Public
                                                         | System.Reflection.BindingFlags.Instance).Length;
                if (evCount != 3)
                {
                    leaked++;
                    Console.WriteLine($"  [FAIL] GameEngine now has {evCount} public events, not 3. "
                                    + "Decide whether the new one must be nulled in Clone, then update "
                                    + "this guard.");
                }
                Console.WriteLine(leaked == 0
                    ? "  [PASS] event leak       : no subscribers survive Clone (3 events checked)"
                    : $"  *** {leaked} EVENT LEAK FAILURE(S) -- recordings made now will be polluted ***");
            }

            var rng = new Random(seed);
            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            var offense = new[] { "nuke", "firebomb", "snipe", "freeze" };
            var defense = new[] { "heal", "reinforcements", "speed", "wall" };

            int completeness = 0, isolation = 0, determinism = 0, checkedGames = 0;

            Console.WriteLine($"[clone-check] {games} positions, seed={seed}, advancing clones {advance} ticks each\n");

            for (int g = 0; g < games; g++)
            {
                int gameSeed = rng.Next();
                var state = new GameState(teams[rng.Next(teams.Length)], new Random(gameSeed));
                var engine = new GameEngine(state, null, gameSeed);

                foreach (var (p, side) in new[] { (state.Player1, 1), (state.Player2, 2) })
                {
                    p.Side = side;
                    p.Team = teams[rng.Next(teams.Length)];
                    p.SetLoadout(new[]
                    {
                        offense[rng.Next(offense.Length)],
                        defense[rng.Next(defense.Length)],
                        GameDataManager.GetSignatureGadgetIdForTeam(p.Team),
                    });
                }

                // Run into the midgame so there is real material to copy — units on the
                // board, hazards burning, gadget cooldowns ticking, effects in flight.
                Advance(engine, state, 600 + rng.Next(1200));
                if (state.IsGameOver) continue;
                checkedGames++;

                string parentBefore = Fingerprint(state);
                int pendingBefore = engine.PendingEffectCount;

                // --- COMPLETENESS ---
                var cloneA = engine.Clone(rngSeed: 777);
                string cloneFresh = Fingerprint(cloneA._state);
                if (cloneFresh != parentBefore)
                {
                    completeness++;
                    Console.WriteLine($"  [FAIL completeness] game {g}: fresh clone differs from parent");
                    Console.WriteLine(FirstDifference(parentBefore, cloneFresh));
                }
                if (cloneA.PendingEffectCount != pendingBefore)
                {
                    completeness++;
                    Console.WriteLine($"  [FAIL completeness] game {g}: pending effects {pendingBefore} -> {cloneA.PendingEffectCount}");
                }

                // --- ISOLATION ---
                Advance(cloneA, cloneA._state, advance);
                string parentAfter = Fingerprint(state);
                if (parentAfter != parentBefore)
                {
                    isolation++;
                    Console.WriteLine($"  [FAIL isolation] game {g}: advancing the clone mutated the parent");
                    Console.WriteLine(FirstDifference(parentBefore, parentAfter));
                }

                // --- DETERMINISM ---
                var cloneB = engine.Clone(rngSeed: 777);
                Advance(cloneB, cloneB._state, advance);
                string a = Fingerprint(cloneA._state), b = Fingerprint(cloneB._state);
                if (a != b)
                {
                    determinism++;
                    Console.WriteLine($"  [FAIL determinism] game {g}: two same-seed clones diverged");
                    Console.WriteLine(FirstDifference(a, b));
                }
            }

            Console.WriteLine($"\n  positions checked : {checkedGames}");
            Console.WriteLine($"  completeness      : {(completeness == 0 ? "PASS" : $"FAIL ({completeness})")}");
            Console.WriteLine($"  isolation         : {(isolation == 0 ? "PASS" : $"FAIL ({isolation})")}");
            Console.WriteLine($"  determinism       : {(determinism == 0 ? "PASS" : $"FAIL ({determinism})")}");
            Console.WriteLine(completeness + isolation + determinism == 0
                ? "\n[clone-check] All checks passed — the engine is safe to clone for search."
                : "\n[clone-check] FAILURES ABOVE — do not build search on this until they are fixed.");
        }
    }
}
