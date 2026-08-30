using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// DOES A SIDE EVER TAKE MORE THAN ONE ACTION IN A SINGLE TICK?
    ///
    /// The recorder stores exactly one action id per side per tick -- the live loop does
    /// ResetLastActions() -> bots act -> Tick() -> RecordTick(LastActionP1, LastActionP2) --
    /// so if a bot performs two actions in one tick, only the LAST is written to the replay.
    /// Every reconstruction would then be missing real actions, which is a divergence source
    /// that no amount of extra header data can fix.
    ///
    /// HeuristicBot.Decide() plainly can do this: it calls TryUseOffenseGadget,
    /// TryUseDefenseGadget and TryUseSignatureGadget in sequence, and repair, wave-wipe,
    /// reactive unit purchase and the fallback investment all sit in the same pass without
    /// returning. This measures how often it actually happens rather than assuming.
    ///
    /// Method: GameEngine.ActionsThisTick, incremented at the four low-level success points
    /// (spawn / invest / repair / gadget) and cleared by ResetLastActions. It deliberately
    /// does NOT use HeuristicBot.ActionCounts: since the pacing change that counter increments
    /// when an action is DECIDED, including when it is queued for a later tick, so it reports
    /// the old number forever and would hide the fix.
    ///
    /// RESULT, 20 HeuristicBot-vs-HeuristicBot games, seed 4242 (2026-08-20):
    ///
    ///   side-ticks with at least one action : 10430
    ///   side-ticks with MORE THAN ONE       :   346   (3.3% of acting ticks)
    ///   total actions taken                 : 10781
    ///   actions the recorder would DROP     :   351   (3.3% of all actions)
    ///   histogram: 2 actions x341, 3 actions x5
    ///
    /// SO THE FORMAT IS LOSSY AND THIS IS THE DIVERGENCE SOURCE. Roughly one action in
    /// thirty never reaches the replay, and each dropped one is a gadget cast or a purchase
    /// the rebuilt bot does not make -- which is why a rebuild plays a WEAKER bot and the
    /// castle falls early (0C7A5B: rebuild ends tick 4345 against a recorded 5808).
    ///
    /// A PREVIOUS EXPLANATION OF THAT DIVERGENCE WAS WRONG and is recorded here as a
    /// warning. It blamed the 28 unrecorded action-12 targets. Marc pointed out that
    /// reinforcements is untargeted, and he is right: master_gadgets marks it Targeted=0,
    /// and ReinforcementsEffect.ExecuteScheduled calls SpawnUnit while IGNORING e.Position
    /// entirely -- the Position assignment in Execute is dead data. Those casts are exactly
    /// reproducible from timing alone, so their missing targets explain nothing. (The
    /// recording-hook fix that came out of that investigation still stands on its own: the
    /// DB's gadget_uses table contains zero reinforcements/heal/wall/speed rows across all
    /// 209 games, because five effects never raise OnGadgetAnimation.)
    ///
    /// FIXED 2026-08-20 BY PACING THE BOTS, NOT BY CHANGING THE FORMAT. Marc's call, and the
    /// better one: a human gets at most one action per 33ms tick, so a bot issuing three is
    /// using input bandwidth no player has. Extra actions are now queued and played out one
    /// per tick, in order, re-validated on execution. HeuristicBot has a 5-tick decision
    /// interval against a 3-action maximum; RolloutSearchBot has 15 ticks against a 9-action
    /// CommitWave burst. Both drain before the next decision.
    ///
    /// AFTER (same 20 games, engine-level count):
    ///
    ///   side-ticks with at least one action : 13277
    ///   side-ticks with MORE THAN ONE       :     0
    ///   actions the recorder would DROP     :     0
    ///
    /// The v3 format is now LOSSLESS by construction and no v4 is needed. Note the acting-tick
    /// total moved 10430 -> 13277: pacing changes how games play out, so the before and after
    /// totals are not directly comparable -- only the multi-action count is the point.
    ///
    /// THIS IS THE REGRESSION TEST for that invariant. Any future bot that bursts actions will
    /// show up here as a non-zero drop count, and the replay format will silently start lying
    /// again if it does.
    ///
    /// Usage: --multi-action-check [games] [--seed N]
    /// </summary>
    public static class MultiActionCheck
    {
        public static void Run(string[] args)
        {
            int games = 20, seed = 4242;
            if (args.Length > 0 && int.TryParse(args[0], out var g)) games = g;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);

            var rng = new Random(seed);
            long ticksWithAny = 0, ticksWithMulti = 0, totalActions = 0, lostActions = 0;
            var multiHist = new Dictionary<int, long>();

            for (int gi = 0; gi < games; gi++)
            {
                var state = new GameState(GameDataManager.GetRandomTeam(), new Random(seed + gi));
                state.Player1 = new PlayerState(); state.Player1.Side = 1;
                state.Player2 = new PlayerState(); state.Player2.Side = 2;
                state.Player1.Team = GameDataManager.GetRandomTeam();
                state.Player2.Team = GameDataManager.GetRandomTeam();
                state.Player1.SetLoadout(new[] { "nuke", "reinforcements",
                    GameDataManager.GetSignatureGadgetIdForTeam(state.Player1.Team) });
                state.Player2.SetLoadout(new[] { "nuke", "reinforcements",
                    GameDataManager.GetSignatureGadgetIdForTeam(state.Player2.Team) });
                var engine = new GameEngine(state, null, seed + gi);

                var b1 = new HeuristicBot(1);
                var b2 = new HeuristicBot(2);

                while (!state.IsGameOver && state.CurrentTick < GameEngine.MAX_TICKS)
                {
                    // ENGINE-LEVEL count, not the bot's ActionCounts. Since the pacing
                    // change, ActionCounts increments when an action is DECIDED (and possibly
                    // queued), while what matters here is how many actually hit the engine on
                    // this tick -- which is exactly what the replay can represent.
                    engine.ResetLastActions();
                    b1.Update(engine);
                    b2.Update(engine);
                    int d1 = engine.ActionsThisTick[1];
                    int d2 = engine.ActionsThisTick[2];

                    foreach (int d in new[] { d1, d2 })
                    {
                        if (d <= 0) continue;
                        ticksWithAny++;
                        totalActions += d;
                        if (d > 1)
                        {
                            ticksWithMulti++;
                            lostActions += d - 1;      // only the LAST survives the recorder
                            multiHist[d] = (multiHist.TryGetValue(d, out var c) ? c : 0) + 1;
                        }
                    }
                    engine.Tick();
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== MULTI-ACTION TICKS, HeuristicBot both sides, " + games + " games ===");
            Console.WriteLine("  side-ticks with at least one action : " + ticksWithAny);
            Console.WriteLine("  side-ticks with MORE THAN ONE       : " + ticksWithMulti
                            + "   (" + (100.0 * ticksWithMulti / Math.Max(ticksWithAny, 1)).ToString("F1") + "% of acting ticks)");
            Console.WriteLine("  total actions taken                 : " + totalActions);
            Console.WriteLine("  actions the recorder would DROP     : " + lostActions
                            + "   (" + (100.0 * lostActions / Math.Max(totalActions, 1)).ToString("F1") + "% of all actions)");
            Console.WriteLine();
            Console.WriteLine("  actions-per-tick histogram (>1 only):");
            foreach (var kv in multiHist.OrderBy(k => k.Key))
                Console.WriteLine("    " + kv.Key + " actions in one tick : " + kv.Value + " times");
            if (lostActions == 0)
                Console.WriteLine("\n  No multi-action ticks: the one-action-per-tick format is LOSSLESS for this bot.");
            else
                Console.WriteLine("\n  *** The replay format cannot represent these games exactly. ***");
        }

        private static long Sum(long[] a)
        {
            long t = 0;
            foreach (long v in a) t += v;
            return t;
        }
    }
}
