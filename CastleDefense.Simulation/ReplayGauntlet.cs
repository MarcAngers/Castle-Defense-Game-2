using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// Plays a bot against MARC'S OWN RECORDED ACTION STREAM, replayed as a fixed opponent.
    /// Marc's idea, 2026-09-02.
    ///
    /// WHY THIS IS WORTH HAVING. Every other rung is either a spam pattern, an Investor, or
    /// HeuristicBot in a hat -- and HeuristicBot is RolloutSearchBot's own prior and rollout
    /// policy, so the ladder can reward better self-exploitation and read it as strength. The
    /// only existing exception, HumanClone, is a conditional AVERAGE of Marc and cannot open
    /// with a plan, race an economy or finish a game. This rung is a specific human game,
    /// played by the human the bot has to beat, and it costs nothing to run.
    ///
    /// GADGETS ARE RE-AIMED BY THE BOT, NOT REPLAYED (Marc's explicit requirement). v3 files
    /// DO record the cast position and ReplayFile.ApplyRecorded normally uses it, which is
    /// right for reconstructing the original game and wrong here: under different engine
    /// randomness the recorded coordinate is aimed at units that are no longer there. Actions
    /// 11/12/13 therefore go through ApplyAction, i.e. UseGadget(..., -1), which routes to
    /// GadgetTargeting.AutoTarget -- the same targeting HeuristicBot uses.
    ///
    /// SEATS ARE FIXED, NOT ALTERNATED, and that is deliberate. Everywhere else in this
    /// project alternating is mandatory because the seat bias is severe and hides in
    /// aggregates. Here the deployed configuration IS fixed-seat -- `sp` always puts the human
    /// on P1 -- so alternating would average away an asymmetry the bot really does get. The
    /// same reasoning `counter-matrix` documents. THE RESULT IS MEANINGLESS TRANSPOSED.
    ///
    /// ─── WHAT THIS DOES NOT MEASURE, and it matters ──────────────────────────────────────
    ///
    /// A recorded action stream is OFF-POLICY the moment the opponent plays differently. Marc's
    /// spawns and casts were responses to what the bot did in HIS game; against a bot that does
    /// something else they are no longer responses to anything. So this is NOT "would Marc beat
    /// this bot". It is "can the bot beat a fixed, human-shaped, economically strong action
    /// stream", which is a strictly easier opponent and a genuinely useful one.
    ///
    /// The economic half transfers well, because his investment timing is close to
    /// unconditional -- he invests when he can afford it, whatever the bot is doing. The
    /// tactical half does not. FIDELITY is therefore reported rather than assumed: how many of
    /// his recorded actions the replay could still afford to execute, and whether the replay
    /// side still reached the investment count and ARMAGEDDON that he actually reached. If
    /// those collapse, the run is measuring a crippled opponent and should be read that way.
    /// </summary>
    public static class ReplayGauntlet
    {
        public static void Run(string[] args, string recordingsDir)
        {
            string target = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
            int games = 24, seed = 4242;
            string variant = null;
            bool sweepMaps = false, race = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--games" && i + 1 < args.Length) games = int.Parse(args[++i]);
                else if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
                else if (args[i] == "--variant" && i + 1 < args.Length) variant = args[++i];
                else if (args[i] == "--maps") sweepMaps = true;
                else if (args[i] == "--race") race = true;
            }
            if (target == null) { Console.WriteLine("usage: --replay-gauntlet <gameId|path> [--games N] [--seed S] [--variant PROFILE] [--maps]"); return; }

            string path = File.Exists(target) ? target
                        : Path.Combine(recordingsDir, "singleplayer", target + ".replay");
            if (!File.Exists(path)) { Console.WriteLine($"No replay at {path}"); return; }

            var rf = ReplayFile.Read(path);
            var settings = ResolveSettings(variant);
            if (variant != null && settings == null) return;

            Console.WriteLine($"=== REPLAY GAUNTLET -- {rf.GameId} ===");
            Console.WriteLine($"  human (P1) {rf.P1Team,-7} {rf.P1StartOff}/{rf.P1StartDef}/{rf.P1StartSig}" +
                              $"   bot (P2) {rf.P2Team,-7} {rf.P2StartOff}/{rf.P2StartDef}/{rf.P2StartSig}");
            Console.WriteLine($"  recorded: {rf.TickCount} ticks ({rf.TickCount / 30.0:F0}s), winner P{rf.Winner}, " +
                              $"map {rf.Map}{(rf.ShadowMap ? " (shadow)" : "")}, v{rf.Version}");
            Console.WriteLine($"  bot profile: {variant ?? "default"}");
            Console.WriteLine($"  gadget casts are RE-AIMED by the bot's own targeting, not replayed.\n");

            var maps = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            var rng = new Random(seed);

            int botWins = 0, humanWins = 0, draws = 0;
            double botInv = 0, humanInv = 0, botHp = 0, humanHp = 0, ticks = 0;
            int humanArma = 0, botArma = 0;
            long attempted = 0, landed = 0;
            int humanWonRace = 0, botWonRace = 0;
            double botUnitSpend = 0, botRepairSpend = 0, botGadget = 0, botInvestSpend = 0, botEndMoney = 0;
            double humanArmaSecs = 0, botArmaSecs = 0;

            for (int g = 0; g < games; g++)
            {
                int engineSeed = rng.Next();
                TeamColour? mapOverride = sweepMaps ? maps[g % maps.Length] : (TeamColour?)null;
                var (state, engine) = rf.BuildStart(engineSeed, mapOverride);

                // ── RACE MODE ────────────────────────────────────────────────────────
                // The plain gauntlet turned out to REWARD RUSHING: the replayed human cannot
                // react, so the arm that killed him fastest scored 100% while reaching
                // ARMAGEDDON 0% of the time. That selects for exactly the opposite of what
                // Marc reports needing.
                //
                // Making the human's castle invulnerable removes the rush as a win condition
                // and leaves only the economy race, which is the thing being asked about. Same
                // device as the stall harness's --protect-attacker arm, and the same caveat:
                // it is an ARTIFICIAL condition. The bot's killer-instinct and disengage logic
                // both read enemy castle HP, so under this flag they never fire. Read it as
                // "who wins the ladder", never as "who wins the game".
                // InvulnerableUntilTick MUST be set too. GameEngine.ProcessStatuses clears
                // IsInvulnerable the moment CurrentTick passes that deadline, so setting the
                // flag alone is a NO-OP -- the first version of this mode did exactly that and
                // produced results byte-identical to the unprotected run, which is the only
                // reason it was caught.
                if (race)
                {
                    state.Player1.IsInvulnerable = true;
                    state.Player1.InvulnerableUntilTick = long.MaxValue;
                }

                var bot = new HeuristicBot(2, settings);
                long start = state.CurrentTick;
                long humanArmaTick = -1, botArmaTick = -1;

                // WHERE THE BOT'S MONEY GOES. Race mode showed it never reaches ARMAGEDDON
                // even when it is safe and has 600 seconds, so the question stopped being
                // "does it win the race" and became "what is it spending the race on".
                // Gadget spend is summed from the cast event because nothing else totals it.
                double botGadgetSpend = 0;
                engine.OnGadgetCast += (side, gadgetId, pos) =>
                {
                    if (side != 2) return;
                    var gd = engine.GetGadgetDefinition(gadgetId);
                    if (gd != null) botGadgetSpend += gd.Cost;
                };

                while (!state.IsGameOver && state.CurrentTick < GameEngine.MAX_TICKS)
                {
                    engine.Tick();
                    long idx = state.CurrentTick - start;

                    // The human's recorded action for this tick. Past the end of the stream he
                    // simply stops acting -- the recording ended because the game ended, and
                    // inventing behaviour past it would be fabricating a player.
                    if (idx >= 0 && idx < rf.A1.Length)
                    {
                        byte a = rf.A1[idx];
                        if (a != 0)
                        {
                            attempted++;
                            // ApplyAction, NOT ApplyRecorded -- see the class note on re-aiming.
                            if (engine.ApplyAction(1, a)) landed++;
                        }
                    }

                    bot.Update(engine);

                    if (humanArmaTick < 0 && state.Player1.ArmageddonUsed) humanArmaTick = state.CurrentTick - start;
                    if (botArmaTick < 0 && state.Player2.ArmageddonUsed) botArmaTick = state.CurrentTick - start;
                    if (race && humanArmaTick >= 0 && botArmaTick >= 0) break;
                }

                if (humanArmaTick >= 0) { humanArmaSecs += humanArmaTick / 30.0; }
                if (botArmaTick >= 0) { botArmaSecs += botArmaTick / 30.0; }
                if (humanArmaTick >= 0 && (botArmaTick < 0 || humanArmaTick < botArmaTick)) humanWonRace++;
                else if (botArmaTick >= 0) botWonRace++;

                if (state.WinnerSide == 2) botWins++;
                else if (state.WinnerSide == 1) humanWins++;
                else draws++;

                botInv += state.Player2.InvestmentCount;
                humanInv += state.Player1.InvestmentCount;
                if (state.Player1.ArmageddonUsed) humanArma++;
                if (state.Player2.ArmageddonUsed) botArma++;
                // Repairs and investments are recoverable from the counts plus the ladders,
                // walked on a throwaway PlayerState so the prices come from the engine's own
                // formulas rather than a second copy of them.
                botRepairSpend += LadderCost(state.Player2.RepairCount, isRepair: true);
                botInvestSpend += LadderCost(state.Player2.InvestmentCount, isRepair: false);
                botUnitSpend += engine.MoneySpentOnUnits[2];
                botGadget += botGadgetSpend;
                botEndMoney += state.Player2.Money;
                botHp += Pct(state.Player2);
                humanHp += Pct(state.Player1);
                ticks += state.CurrentTick - start;
            }

            double n = Math.Max(1, games);
            var (lo, hi) = Wilson(botWins, games);
            Console.WriteLine($"  BOT WIN RATE   {100.0 * botWins / n,6:F1}%  [{100 * lo:F0}%, {100 * hi:F0}%]" +
                              $"   ({botWins}W / {humanWins}L / {draws}D over {games} games)");
            Console.WriteLine($"  avg length     {ticks / n / 30.0,6:F0}s   (recorded game was {rf.TickCount / 30.0:F0}s)");
            Console.WriteLine();
            Console.WriteLine($"                     investments   ARMAGEDDON   end HP%");
            Console.WriteLine($"    replayed human  {humanInv / n,11:F2}   {100.0 * humanArma / n,8:F0}%   {humanHp / n,6:F1}");
            Console.WriteLine($"    bot             {botInv / n,11:F2}   {100.0 * botArma / n,8:F0}%   {botHp / n,6:F1}");
            Console.WriteLine();
            Console.WriteLine($"  WHERE THE BOT'S MONEY WENT (per game, averaged)");
            Console.WriteLine($"    units       {botUnitSpend / n,10:N0}");
            Console.WriteLine($"    gadgets     {botGadget / n,10:N0}");
            Console.WriteLine($"    repairs     {botRepairSpend / n,10:N0}");
            Console.WriteLine($"    investments {botInvestSpend / n,10:N0}");
            Console.WriteLine($"    unspent     {botEndMoney / n,10:N0}   <- and ARMAGEDDON costs 121,221");

            Console.WriteLine();
            Console.WriteLine($"  ARMAGEDDON RACE   human first in {humanWonRace}, bot first in {botWonRace}, " +
                              $"neither in {games - humanWonRace - botWonRace}");
            if (humanArma > 0) Console.WriteLine($"    human reached it at {humanArmaSecs / humanArma,6:F0}s on average");
            if (botArma > 0) Console.WriteLine($"    bot   reached it at {botArmaSecs / botArma,6:F0}s on average");
            if (race) Console.WriteLine("    [race mode: human castle invulnerable, so win rate above is not a result]");

            Console.WriteLine();
            Console.WriteLine($"  FIDELITY  {landed} of {attempted} recorded actions landed " +
                              $"({100.0 * landed / Math.Max(1, attempted):F1}%).");
            Console.WriteLine("    An action fails when the replayed human cannot afford it in THIS game, which is");
            Console.WriteLine("    the off-policy cost. Low fidelity means the opponent is a crippled version of");
            Console.WriteLine("    the recorded player and the win rate is correspondingly too kind to the bot.");
        }

        /// <summary>
        /// Total paid to reach <paramref name="count"/> rungs, walked on a throwaway
        /// PlayerState so every price comes from the engine's own ApplyRepairStep /
        /// ApplyInvestmentStep rather than a second copy of the curve.
        /// </summary>
        private static double LadderCost(int count, bool isRepair)
        {
            var p = new PlayerState();
            double total = 0;
            for (int i = 0; i < count; i++)
            {
                if (isRepair) { total += p.RepairPrice; p.ApplyRepairStep(); }
                else { total += p.InvestmentPrice; p.ApplyInvestmentStep(); }
            }
            return total;
        }

        private static double Pct(PlayerState p)
            => p.CastleMaxHealth > 0 ? 100.0 * p.CastleHealth / p.CastleMaxHealth : 0;

        private static HeuristicBotSettings ResolveSettings(string name)
        {
            if (name == null) return null;
            var f = typeof(HeuristicBotSettings).GetField(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (f != null) return (HeuristicBotSettings)f.GetValue(null);
            Console.WriteLine($"No HeuristicBotSettings profile named '{name}'. Available:");
            foreach (var g in typeof(HeuristicBotSettings).GetFields(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                Console.WriteLine("    " + g.Name);
            return null;
        }

        // Correct near 0 and 1, unlike the normal approximation -- this rung is expected to
        // sit at an extreme.
        private static (double lo, double hi) Wilson(int wins, int n, double z = 1.96)
        {
            if (n == 0) return (0, 0);
            double p = (double)wins / n, d = 1 + z * z / n;
            double c = (p + z * z / (2.0 * n)) / d;
            double h = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / d;
            return (Math.Max(0, c - h), Math.Min(1, c + h));
        }
    }
}
