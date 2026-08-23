using CastleDefense.Engine;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// Per-tick economy trace of a RECORDED HUMAN GAME, in the same columns
    /// CastleDefense.BotArena/stall/build_anatomy.py already charts for the bot, so a human
    /// game and a bot game can be put on the same axes.
    ///
    /// FIDELITY IS NOT ASSUMED, IT IS CHECKED. A replay stores actions, not state, so this
    /// re-simulates -- and a v2 file has no map, no engine seed, no gadget targets and only
    /// the END-of-game loadout, which is enough to send the rebuild somewhere else entirely.
    /// The run therefore ends by comparing its own final income / money / castle HP against
    /// what the recorder wrote in game_records.db and printing the gap. Read that before
    /// trusting the curve; on a v3 file it should be near-exact.
    ///
    /// Economy is the most robust thing to take from a rebuild even so: the recorder only
    /// ever writes an action id AFTER the engine has accepted it (GameEngine.Invest sets
    /// LastActionP1 below the money check, SpawnUnit below the deduction), so the stream is
    /// successful actions only, and the income ladder is a function of that stream alone.
    /// </summary>
    public static class EconomyDump
    {
        public static void Run(string replayPath, string[] args)
        {
            string outPath = Arg(args, "--csv", "economy_dump.csv");
            int every = int.Parse(Arg(args, "--every", "3"));

            var rf = ReplayFile.Read(replayPath);
            Console.WriteLine($"replay {rf.GameId}  v{rf.Version}  {rf.TickCount} ticks "
                            + $"({rf.TickCount / 30.0:F0}s)  winner P{rf.Winner}");
            Console.WriteLine($"  P1 {rf.P1Team}  {(rf.HasV3 ? rf.P1StartOff : rf.P1Off)}/"
                            + $"{(rf.HasV3 ? rf.P1StartDef : rf.P1Def)}");
            Console.WriteLine($"  P2 {rf.P2Team}  {(rf.HasV3 ? rf.P2StartOff : rf.P2Off)}/"
                            + $"{(rf.HasV3 ? rf.P2StartDef : rf.P2Def)}");
            if (!rf.HasV3)
                Console.WriteLine("  WARNING: v2 file -- random map, auto-aimed gadgets, "
                                + "end-of-game loadout from tick 0. Treat the curve as indicative.");

            var (state, engine) = rf.BuildStart();
            using var w = new StreamWriter(outPath);
            w.WriteLine("tick,hp,maxhp,money,inv,own,enemy,p2hp,p2maxhp,p2money,p2inv,"
                      + "p1income,p2income,p1spent,p2spent,p1act,p2act");

            for (int i = 0; i < rf.TickCount && !state.IsGameOver; i++)
            {
                int tick = (int)state.CurrentTick;
                byte a1 = rf.A1[i], a2 = rf.A2[i];
                if (a1 != 0) rf.ApplyRecorded(engine, 1, tick, a1);
                if (a2 != 0) rf.ApplyRecorded(engine, 2, tick, a2);
                engine.Tick();

                if (i % every == 0)
                    w.WriteLine($"{state.CurrentTick},{state.Player1.CastleHealth},{state.Player1.CastleMaxHealth},"
                              + $"{state.Player1.Money:F0},{state.Player1.InvestmentCount},"
                              + $"{state.Units.Count(u => u.Side == 1)},{state.Units.Count(u => u.Side == 2)},"
                              + $"{state.Player2.CastleHealth},{state.Player2.CastleMaxHealth},"
                              + $"{state.Player2.Money:F0},{state.Player2.InvestmentCount},"
                              + $"{state.Player1.Income:F0},{state.Player2.Income:F0},"
                              + $"{engine.MoneySpentOnUnits[1]:F0},{engine.MoneySpentOnUnits[2]:F0},{a1},{a2}");
            }

            Console.WriteLine($"\nwrote {outPath}");
            Console.WriteLine($"  rebuilt final: P1 income ${state.Player1.Income:F0} money ${state.Player1.Money:F0} "
                            + $"hp {state.Player1.CastleHealth}/{state.Player1.CastleMaxHealth} inv {state.Player1.InvestmentCount}");
            Console.WriteLine($"                 P2 income ${state.Player2.Income:F0} money ${state.Player2.Money:F0} "
                            + $"hp {state.Player2.CastleHealth}/{state.Player2.CastleMaxHealth} inv {state.Player2.InvestmentCount}");
            Console.WriteLine("  compare against game_records.db for this id -- a large gap means the "
                            + "rebuild diverged and the curve is not the game that was played.");
        }

        private static string Arg(string[] a, string n, string f)
        {
            int i = Array.IndexOf(a, n);
            return (i >= 0 && i + 1 < a.Length) ? a[i + 1] : f;
        }
    }
}
