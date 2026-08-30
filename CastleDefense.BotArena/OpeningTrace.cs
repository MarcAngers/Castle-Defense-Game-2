using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena;

/// <summary>
/// Reproduces Marc's live-play observation of 2026-08-18 and attributes it to a code path:
/// "in every single game the bot spawns a T4 unit as soon as it possibly can after the
/// second investment, spending 100% of its money."
///
/// The setup is deliberately his: White/nuke/reinforcements both sides (the pinned mirror),
/// no headstart, human on P1, bot on P2 -- and the human seat SAVES AND DOES NOTHING, which
/// is the behaviour he says is optimal and the condition under which he saw the spawn. That
/// matters because it rules the reactive branches out by construction: with no enemy units
/// on the field there is nothing for `inDanger` or the wiper branch to react to, so whatever
/// fires is firing unprovoked.
///
/// Logs every P2 spawn with the money it had, the money it had left, and which branch of
/// Decide() bought it (HeuristicBot.LastSpawnReason).
/// </summary>
public static class OpeningTrace
{
    private sealed class IdleOpponent : IArenaOpponent
    {
        public void Update(GameEngine engine) { }
    }

    public static void Run(string[] args)
    {
        string botKind = "search";
        int games = 1;
        double untilSeconds = 120;
        string team = "White", off = "nuke", def = "reinforcements";
        int guard = 0;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bot":     botKind = args[++i]; break;
                case "--games":   games = int.Parse(args[++i]); break;
                case "--seconds": untilSeconds = double.Parse(args[++i]); break;
                case "--team":    team = args[++i]; break;
                case "--off":     off = args[++i]; break;
                case "--def":     def = args[++i]; break;
                case "--guard":   guard = int.Parse(args[++i]); break;
            }
        }

        RolloutSearchBot.CaptureDecisionTrace = true;

        Console.WriteLine($"Opening trace: P1 idle (saves, never acts), P2 = {botKind}, "
                        + $"both {team}/{off}/{def}, no headstart, first {untilSeconds}s\n");

        for (int g = 0; g < games; g++)
        {
            var state = new GameState(Enum.Parse<TeamColour>(team, true), new Random(1234 + g));
            state.GameMode = "sp";
            var tc = Enum.Parse<TeamColour>(team, true);
            state.Player1.Side = 1; state.Player1.Team = tc;
            state.Player1.SetLoadout(new[] { off, def, GameDataManager.GetSignatureGadgetIdForTeam(tc) });
            state.Player2.Side = 2; state.Player2.Team = tc;
            state.Player2.SetLoadout(new[] { off, def, GameDataManager.GetSignatureGadgetIdForTeam(tc) });

            var engine = new GameEngine(state, seed: 9000 + g);
            var p1 = new IdleOpponent();

            // Reach through the adapter to the HeuristicBot so LastSpawnReason is readable.
            // For the search bot the prior IS a HeuristicBot, but the override decision sits
            // in RolloutSearchBot, so an unattributed spawn there means search chose it.
            HeuristicBot heuristic = null;
            RolloutSearchOpponent search = null;
            IArenaOpponent p2;
            if (botKind == "heuristic")
            {
                heuristic = new HeuristicBot(2);
                p2 = new DirectHeuristic(heuristic);
            }
            else
            {
                search = new RolloutSearchOpponent(2, 15, 300, 1, 4242 + g, true, 0.10,
                                                   earlySpendGuardMinInvest: guard);
                p2 = search;
            }

            int lastCount = 0, lastInvest = 0;
            double moneyBefore = 0;
            long lastTick = 0;

            Console.WriteLine($"--- game {g + 1} ---");
            Console.WriteLine($"{"sec",6} {"event",-26} {"$before",8} {"$after",8} {"income",7} {"inv",4} {"branch",-14}");

            while (!state.IsGameOver && state.CurrentTick < untilSeconds * 30)
            {
                int investBefore = state.Player2.InvestmentCount;
                long purchasedBefore = engine.UnitsPurchased[2];
                double spentBefore = engine.MoneySpentOnUnits[2];

                // Sampled after the tick's income accrual but before the bots act, so
                // "$before" is the money the bot actually had when it decided.
                engine.Tick();
                moneyBefore = state.Player2.Money;

                p1.Update(engine);
                p2.Update(engine);

                if (state.Player2.InvestmentCount > investBefore)
                    Console.WriteLine($"{state.CurrentTick / 30.0,6:F1} {"INVEST #" + state.Player2.InvestmentCount,-26} "
                                    + $"{moneyBefore,8:F1} {state.Player2.Money,8:F1} {state.Player2.Income,7:F2} "
                                    + $"{state.Player2.InvestmentCount,4}");

                // Count PURCHASES, not units appearing. The reinforcements gadget spawns
                // free units straight through the engine, so a unit-count delta massively
                // over-reports buying -- an earlier version of this trace read a doggo
                // stream as unit purchases when the bot had not spent a cent.
                if (engine.UnitsPurchased[2] > purchasedBefore)
                {
                    var newest = state.Units.Where(u => u.Side == 2).LastOrDefault();
                    double spent = engine.MoneySpentOnUnits[2] - spentBefore;
                    string reason = heuristic?.LastSpawnReason
                                  ?? (search != null && search.LastChosenAction >= 0 ? "SEARCH override" : "prior");
                    Console.WriteLine($"{state.CurrentTick / 30.0,6:F1} {"BUY " + newest?.DefinitionId + " T" + newest?.Tier + " $" + spent.ToString("F0"),-26} "
                                    + $"{moneyBefore,8:F1} {state.Player2.Money,8:F1} {state.Player2.Income,7:F2} "
                                    + $"{state.Player2.InvestmentCount,4} {reason,-14}");
                    if (search?.LastScores != null)
                        Console.WriteLine("         candidates: " + Describe(search));
                }
                lastTick = state.CurrentTick;
                lastCount = state.Units.Count(u => u.Side == 2);
                lastInvest = state.Player2.InvestmentCount;
            }

            (p2 as IDisposable)?.Dispose();
            Console.WriteLine($"\nAt {lastTick / 30.0:F0}s: P2 had {lastInvest} investments, {lastCount} units, "
                            + $"${state.Player2.Money:F1}, income {state.Player2.Income:F2}\n");
        }
    }

    /// <summary>Renders one search decision: every candidate's score against the prior's.</summary>
    private static string Describe(RolloutSearchOpponent s)
    {
        string Name(int a) => a switch
        {
            -1 => "prior",
            0 => "wait",
            9 => "INVEST",
            10 => "repair",
            11 => "offGadget",
            12 => "defGadget",
            13 => "sigGadget",
            100 => "MACRO-saveInvest",
            101 => "MACRO-press",
            102 => "MACRO-armageddon",
            103 => "MACRO-upgrade",
            _ => a >= 1 && a <= 8 ? $"spawnT{a}" : $"a{a}",
        };
        var parts = s.LastScores
                     .OrderByDescending(kv => kv.Value)
                     .Select(kv => $"{Name(kv.Key)}={kv.Value:F4}");
        return $"prior={s.LastPriorScore:F4} | " + string.Join("  ", parts)
             + $" | chose {Name(s.LastChosenAction)}";
    }

    private sealed class DirectHeuristic : IArenaOpponent
    {
        private readonly HeuristicBot _bot;
        public DirectHeuristic(HeuristicBot bot) => _bot = bot;
        public void Update(GameEngine engine) => _bot.Update(engine);
    }
}
