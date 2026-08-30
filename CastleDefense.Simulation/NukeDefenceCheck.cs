using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// DOES HeuristicBot SURVIVE AN ENEMY NUKE IT COULD HAVE SURVIVED?
    ///
    /// Guard for the incoming-nuke repair added 2026-08-20 (Marc: "there's a check so the
    /// bot doesn't kill itself with its own Nuke, but not for the enemy's"). Runs the
    /// scenario in its purest form -- the one the bot's danger model structurally cannot
    /// see -- and runs it twice, with the fix on and with HeuristicBotSettings
    /// .IncomingNukeRepairOff, so the number means something against its own absence.
    ///
    /// THE POSITION: an EMPTY BOARD. No units anywhere, so every rate-based danger signal
    /// in HeuristicBot reads perfectly safe: observed drain 0, projected drain 0,
    /// time-to-death infinite, inDanger false, survivalEmergency false. The defender sits
    /// above RepairHpThreshold, so the ordinary repair rule does not fire either. Its
    /// castle HP is nonetheless below the blast already in flight.
    ///
    /// Money is deliberately generous. That is half the test: with nothing else to spend on
    /// and an investment affordable, the pre-fix bot's HIGHEST-priority action is the invest
    /// early-exit, so it returns from every one of the ~9 decisions inside the 48-tick
    /// window having bought economy it is about to be deleted with.
    ///
    /// The level-1 row is expected to be a NO-OP for the fix and is kept as the honest
    /// negative case: a level-1 blast is 100, and no castle can be both above 60% HP and
    /// under 100 HP, so lethal level-1 nukes only exist in states the ordinary repair
    /// threshold already covers.
    ///
    /// Usage: --nuke-defence-check
    /// </summary>
    public static class NukeDefenceCheck
    {
        public static void Run(string[] args)
        {
            Console.WriteLine();
            Console.WriteLine("=== INCOMING-NUKE DEFENCE ===");
            Console.WriteLine("  Empty board, defender = HeuristicBot (P1), attacker (P2) casts once at tick 0.");
            Console.WriteLine();
            Console.WriteLine("  level    blast   repairs  start HP    fix   survived  end HP    repairs bought");
            Console.WriteLine("  -----------------------------------------------------------------------------");

            // (gadget id, repair steps to pre-apply, castle HP to start at). HP is chosen
            // lethal-but-recoverable and ABOVE RepairHpThreshold (0.60) wherever the
            // arithmetic allows it, so the ordinary repair rule cannot claim the save.
            var cases = new (string Id, int Repairs, int Hp)[]
            {
                ("nuke",    0,    90),   // blast 100  -- 4.5% HP, ordinary rule covers it
                ("nuke_2",  0,  1400),   // blast 1500 -- 70% HP
                ("nuke_3",  1, 11000),   // blast 12000 -- 91.7% HP
            };

            int failures = 0;
            foreach (var c in cases)
            {
                foreach (bool fixOn in new[] { false, true })
                {
                    var settings = fixOn ? null : HeuristicBotSettings.IncomingNukeRepairOff;
                    var r = RunOne(c.Id, c.Repairs, c.Hp, settings);
                    Console.WriteLine($"  {c.Id,-7} {r.Blast,7} {c.Repairs,8} {c.Hp,9}   {(fixOn ? "on " : "off"),4}"
                                    + $"   {(r.Survived ? "YES" : "no "),8}  {r.EndHp,7}    {r.RepairsBought,3}");
                    if (fixOn && !r.Survived) failures++;
                }
                Console.WriteLine();
            }

            Console.WriteLine(failures == 0
                ? "  PASS -- every survivable blast was survived with the fix on."
                : $"  FAIL -- {failures} survivable blast(s) still killed the bot.");
        }

        private static (int Blast, bool Survived, int EndHp, int RepairsBought) RunOne(
            string nukeId, int repairSteps, int startHp, HeuristicBotSettings settings)
        {
            // Fixed map and engine seed: nothing here depends on the roll, and a probe that
            // moves between runs is not a guard.
            var state = new GameState(TeamColour.Blue, new Random(12345));
            var engine = new GameEngine(state, seed: 12345);

            state.Player1.Team = TeamColour.White;
            state.Player2.Team = TeamColour.White;
            // Defender's own offense is freeze, not nuke: the point is the ENEMY's blast,
            // and leaving a nuke in its own loadout would let the suicide guard confound it.
            state.Player1.SetLoadout(new[] { "freeze", "wall", "rage" });
            state.Player2.SetLoadout(new[] { nukeId, "wall", "rage" });

            for (int i = 0; i < repairSteps; i++) state.Player1.ApplyRepairStep();
            state.Player1.CastleHealth = startHp;
            state.Player1.Money = 5000;

            // The attacker eats its own blast too. Full health means the engine's
            // 1-shot prevention floors it at 1 rather than handing us a draw.
            state.Player2.Money = 100000;
            state.Player2.CastleHealth = state.Player2.CastleMaxHealth;

            var def = GameDataManager.Gadgets.Find(g => g.Id == nukeId);
            int blast = CastleDefense.Engine.Gadgets.NukeEffect.CastleBlastFor(def);

            var bot = new HeuristicBot(1, settings);
            int repairsBefore = state.Player1.RepairCount;

            if (!engine.UseGadget(2, nukeId, 200))
                throw new Exception("attacker could not cast " + nukeId);

            // 48-tick delay; run well past it so the detonation definitely lands.
            for (int t = 0; t < 90 && !state.IsGameOver; t++)
            {
                bot.Update(engine);
                engine.Tick();
            }

            bool survived = !(state.IsGameOver && state.WinnerSide == 2)
                            && state.Player1.CastleHealth > 0;
            return (blast, survived, state.Player1.CastleHealth,
                    state.Player1.RepairCount - repairsBefore);
        }
    }
}
