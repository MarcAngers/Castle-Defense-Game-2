using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
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
            double botAutoLevel = 0, botOffensiveDecisions = 0;
            double raceHeld = 0, raceHopeless = 0, chipperMatch = 0, raceDeficit = 0;
            double spWiper = 0, spReactive = 0, spAttack = 0, spChip = 0;
            var wiperTiers = new long[9];
            var reactTiers = new long[9]; var atkTiers = new long[9];
            var midByDom = new long[9];
            double midCharge = 0, midAny = 0, midMatched = 0;
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
                botAutoLevel += state.Player2.AutoSpawnLevel;
                raceHeld += bot.RaceHeldPurchases;
                raceHopeless += bot.RaceHopelessDecisions;
                chipperMatch += bot.ChipperMatchDecisions;
                raceDeficit += bot.LastRaceDeficitSeconds;
                spWiper += bot.SpendWiper; spReactive += bot.SpendReactive;
                spAttack += bot.SpendAttack; spChip += bot.SpendChipBlock;
                midCharge += bot.MidBuyViaChargeFallback;
                midAny += bot.MidBuyViaAnyAffordable;
                midMatched += bot.MidBuyViaMatchedOrOutclass;
                for (int ti = 1; ti <= 8; ti++)
                {
                    wiperTiers[ti] += bot.WiperTierCounts[ti];
                    reactTiers[ti] += bot.ReactiveTierCounts[ti];
                    atkTiers[ti] += bot.AttackTierCounts[ti];
                    midByDom[ti] += bot.MidBuyByDominantTier[ti];
                }
                botOffensiveDecisions += bot.OffensiveSpendDecisions;
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
            Console.WriteLine($"  UNIT SPEND BY REASON (per game): wiper {spWiper / n,8:N0}  " +
                              $"reactive {spReactive / n,8:N0}  attack {spAttack / n,8:N0}  " +
                              $"chip-block {spChip / n,7:N0}");
            var wt = new List<string>();
            for (int ti = 1; ti <= 8; ti++) if (wiperTiers[ti] > 0) wt.Add($"T{ti} x{wiperTiers[ti] / n:F1}");
            Console.WriteLine($"    wiper picks by tier: {string.Join("  ", wt)}");
            foreach (var (label, arr, roster) in new[]
                     { ("reactive", reactTiers, (object)null), ("attack", atkTiers, (object)null) })
            {
                var parts = new List<string>();
                double cash = 0;
                for (int ti = 1; ti <= 8; ti++)
                    if (arr[ti] > 0) parts.Add($"T{ti} x{arr[ti] / n:F1}");
                Console.WriteLine($"    {label,-8} picks by tier: {string.Join("  ", parts)}");
            }

            var md = new List<string>();
            for (int ti = 0; ti <= 8; ti++) if (midByDom[ti] > 0) md.Add($"dom{ti} x{midByDom[ti] / n:F1}");
            Console.WriteLine($"    reactive T5+ buys, by DOMINANT ENEMY TIER at the time: {string.Join("  ", md)}");
            Console.WriteLine($"    reactive T5+ buys, by ROUTE: matched/outclass {midMatched / n,5:F1}  " +
                              $"charge-fallback {midCharge / n,5:F1}  any-affordable {midAny / n,5:F1}");

            Console.WriteLine($"  ECONOMY TRACKER (per game): offensive purchases HELD by the race gate " +
                              $"{raceHeld / n,7:F0}, hopeless-zone decisions {raceHopeless / n,6:F0}, " +
                              $"single-chipper matches {chipperMatch / n,5:F0}");
            Console.WriteLine($"    final race deficit (ours minus theirs, seconds to ARMAGEDDON) " +
                              $"{raceDeficit / n,8:F1}   negative = bot ahead");
            Console.WriteLine($"    auto-spawner level reached {botAutoLevel / n,5:F1}   " +
                              $"non-reactive attack decisions {botOffensiveDecisions / n,8:F0}");

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

        /// <summary>
        /// Replays BOTH recorded action streams into a fresh engine and reports where each
        /// side's money went. Marc's ask, 2026-09-02: "how much the bot spent on units, and
        /// how many upgrades that could have bought for the Autospawner."
        ///
        /// This reconstructs the ACTUAL game rather than sampling variations, so unlike the
        /// gauntlet it uses ApplyRecorded -- the recorded gadget target is the right one here,
        /// because the object is to reproduce what happened, not to re-aim in a new game.
        /// </summary>
        public static void Spend(string[] args, string recordingsDir)
        {
            string target = args.Length > 1 ? args[1] : null;
            if (target == null) { Console.WriteLine("usage: --replay-spend <gameId|path>"); return; }
            string path = File.Exists(target) ? target
                        : Path.Combine(recordingsDir, "singleplayer", target + ".replay");
            if (!File.Exists(path)) { Console.WriteLine($"No replay at {path}"); return; }

            var rf = ReplayFile.Read(path);
            var (state, engine) = rf.BuildStart();
            long startTick = state.CurrentTick;

            var gadget = new double[3];
            engine.OnGadgetCast += (side, gadgetId, pos) =>
            {
                var gd = engine.GetGadgetDefinition(gadgetId);
                if (gd != null && side >= 1 && side <= 2) gadget[side] += gd.Cost;
            };

            while (!state.IsGameOver && state.CurrentTick < GameEngine.MAX_TICKS)
            {
                engine.Tick();
                long i = state.CurrentTick - startTick;
                if (i < 0 || i >= rf.A1.Length) continue;
                if (rf.A1[i] != 0) rf.ApplyRecorded(engine, 1, (int)state.CurrentTick, rf.A1[i]);
                if (rf.A2[i] != 0) rf.ApplyRecorded(engine, 2, (int)state.CurrentTick, rf.A2[i]);
            }

            Console.WriteLine($"=== SPEND BREAKDOWN -- {rf.GameId} ===");
            Console.WriteLine($"  reconstructed {(state.CurrentTick - startTick) / 30.0:F0}s, " +
                              $"winner P{state.WinnerSide} (recorded: {rf.TickCount / 30.0:F0}s, winner P{rf.Winner})");
            if (state.WinnerSide != rf.Winner)
                Console.WriteLine("  NOTE: reconstruction diverged from the recorded outcome -- read the numbers as indicative.");
            Console.WriteLine();
            Console.WriteLine("                        P1 (human)     P2 (bot)");
            var p1 = state.Player1; var p2 = state.Player2;
            Console.WriteLine($"    units          {engine.MoneySpentOnUnits[1],14:N0} {engine.MoneySpentOnUnits[2],13:N0}");
            Console.WriteLine($"    gadgets        {gadget[1],14:N0} {gadget[2],13:N0}");
            Console.WriteLine($"    repairs        {LadderCost(p1.RepairCount, true),14:N0} {LadderCost(p2.RepairCount, true),13:N0}");
            Console.WriteLine($"    investments    {LadderCost(p1.InvestmentCount, false),14:N0} {LadderCost(p2.InvestmentCount, false),13:N0}");
            // ARMAGEDDON IS NOT IN THE INVESTMENT LADDER. Buying it does NOT run
            // ApplyInvestmentStep -- InvestmentCount stays at 8 and ArmageddonUsed is set
            // instead -- so LadderCost(InvestmentCount) misses it entirely. In 73DBD4 that
            // silently omitted $121,221 from the human's column and made the bot look like it
            // had out-earned him 2.5x when the real ratio is 1.15x.
            Console.WriteLine($"    ARMAGEDDON     {(p1.ArmageddonUsed ? 121221 : 0),14:N0} {(p2.ArmageddonUsed ? 121221 : 0),13:N0}");
            Console.WriteLine($"    auto-spawner   {AutoCost(p1.AutoSpawnLevel),14:N0} {AutoCost(p2.AutoSpawnLevel),13:N0}   (levels {p1.AutoSpawnLevel} / {p2.AutoSpawnLevel})");
            Console.WriteLine($"    unspent        {p1.Money,14:N0} {p2.Money,13:N0}");
            Console.WriteLine($"    units bought   {engine.UnitsPurchased[1],14:N0} {engine.UnitsPurchased[2],13:N0}");

            // Which tiers each side reached for. Recorded actions include attempts that
            // failed (ApplyAction stamps LastAction before the purchase is validated), so read
            // these as INTENT; MoneySpentOnUnits above is the exact spend.
            Console.WriteLine();
            Console.WriteLine("  UNIT-BUY INTENT BY TIER (recorded actions 1-8)");
            for (int side = 1; side <= 2; side++)
            {
                var stream = side == 1 ? rf.A1 : rf.A2;
                var tiers = new int[9];
                foreach (var a in stream) if (a >= 1 && a <= 8) tiers[a]++;
                var roster = GameDataManager.Teams.Find(x => x.Color ==
                    (side == 1 ? state.Player1.Team : state.Player2.Team))?.Roster;
                var parts = new List<string>();
                for (int ti = 1; ti <= 8; ti++)
                    if (tiers[ti] > 0)
                    {
                        string nm = roster != null && ti <= roster.Count ? roster[ti - 1].Id : "?";
                        int cost = roster != null && ti <= roster.Count ? roster[ti - 1].Cost : 0;
                        parts.Add($"T{ti}({nm} ${cost}) x{tiers[ti]}");
                    }
                Console.WriteLine($"    P{side}: {string.Join("  ", parts)}");
            }

            Console.WriteLine();
            Console.WriteLine("  WHAT THE BOT'S UNIT SPEND WOULD HAVE BOUGHT INSTEAD");
            double botUnits = engine.MoneySpentOnUnits[2];
            int reach = p2.AutoSpawnLevel;
            while (reach < PlayerState.MaxAutoSpawnLevel
                   && AutoCost(reach + 1) - AutoCost(p2.AutoSpawnLevel) <= botUnits) reach++;
            Console.WriteLine($"    unit spend ${botUnits:N0} on top of level {p2.AutoSpawnLevel} " +
                              $"reaches AUTO-SPAWNER LEVEL {reach} " +
                              $"(${AutoCost(reach) - AutoCost(p2.AutoSpawnLevel):N0} of it)");
            Console.WriteLine($"    that is {PlayerState.AutoSpawnUnitsPerSecond(reach)} free units/sec of tiers " +
                              $"[{string.Join(",", PlayerState.AutoSpawnCycle(reach))}] " +
                              $"instead of {PlayerState.AutoSpawnUnitsPerSecond(p2.AutoSpawnLevel)}/sec " +
                              $"[{string.Join(",", PlayerState.AutoSpawnCycle(p2.AutoSpawnLevel))}]");
            Console.WriteLine($"    ARMAGEDDON costs 121,221; the bot ended on ${p2.Money:N0}.");
        }

        /// <summary>
        /// Reconstructs a recorded game and prints WHEN each gadget upgrade landed for each
        /// side, with the loser's progress toward the same upgrade at that moment. Written for
        /// Marc's 0240D8 hypothesis: that he won the instant he reached reinforcements_3 while
        /// the bot, one cast away from the same upgrade, spent on units instead.
        ///
        /// Gadget XP is a flat 100 per cast for every gadget (see BOT_MECHANICS.md), so
        /// "casts remaining" is exact arithmetic, not an estimate.
        /// </summary>
        public static void Timeline(string[] args, string recordingsDir)
        {
            string target = args.Length > 1 ? args[1] : null;
            if (target == null) { Console.WriteLine("usage: --replay-timeline <gameId|path>"); return; }
            string path = File.Exists(target) ? target
                        : Path.Combine(recordingsDir, "singleplayer", target + ".replay");
            if (!File.Exists(path)) { Console.WriteLine($"No replay at {path}"); return; }

            var rf = ReplayFile.Read(path);
            var (state, engine) = rf.BuildStart();
            long start = state.CurrentTick;

            var events = new List<string>();
            var casts = new int[3];
            engine.OnGadgetUpgraded += (side, def) =>
            {
                double secs = (state.CurrentTick - start) / 30.0;
                var them = side == 1 ? state.Player2 : state.Player1;
                string fam = def.Id.Split('_')[0];
                them.GadgetXp.TryGetValue(fam, out int oppXp);
                // What the OTHER side still needed for the same family, in casts.
                var oppDef = fam == (them.OffensiveGadget?.Id.Split('_')[0]) ? them.OffensiveGadget
                           : fam == (them.DefensiveGadget?.Id.Split('_')[0]) ? them.DefensiveGadget
                           : fam == (them.SignatureGadget?.Id.Split('_')[0]) ? them.SignatureGadget : null;
                string oppNote = oppDef == null ? "" :
                    $"  |  P{(side == 1 ? 2 : 1)} on {oppDef.Id}, xp {oppXp}/{oppDef.UpgradeCost}" +
                    $" = {Math.Max(0, (int)Math.Ceiling((oppDef.UpgradeCost - oppXp) / 100.0))} casts away," +
                    $" ${them.Money:N0} in hand";
                events.Add($"  {secs,6:F0}s  P{side} -> {def.Id,-20}{oppNote}");
            };
            engine.OnGadgetCast += (side, id, pos) => { if (side >= 1 && side <= 2) casts[side]++; };

            while (!state.IsGameOver && state.CurrentTick < GameEngine.MAX_TICKS)
            {
                engine.Tick();
                long i = state.CurrentTick - start;
                if (i < 0 || i >= rf.A1.Length) continue;
                if (rf.A1[i] != 0) rf.ApplyRecorded(engine, 1, (int)state.CurrentTick, rf.A1[i]);
                if (rf.A2[i] != 0) rf.ApplyRecorded(engine, 2, (int)state.CurrentTick, rf.A2[i]);
            }

            Console.WriteLine($"=== UPGRADE TIMELINE -- {rf.GameId} ===");
            Console.WriteLine($"  reconstructed {(state.CurrentTick - start) / 30.0:F0}s, winner P{state.WinnerSide}" +
                              $" (recorded {rf.TickCount / 30.0:F0}s, winner P{rf.Winner})");
            if (state.WinnerSide != rf.Winner)
                Console.WriteLine("  NOTE: reconstruction diverged -- read as indicative.");
            Console.WriteLine($"  total gadget casts: P1 {casts[1]}, P2 {casts[2]}");
            Console.WriteLine();
            foreach (var e in events) Console.WriteLine(e);
        }

        /// <summary>
        /// Dumps the per-second economy of a recorded game to CSV: both sides' money, income,
        /// investment count, castle HP, plus the cumulative spend split by category. Written so
        /// Marc can see the shape of a game rather than only its totals.
        /// </summary>
        public static void Economy(string[] args, string recordingsDir)
        {
            string target = args.Length > 1 ? args[1] : null;
            string outPath = null;
            for (int i = 1; i < args.Length; i++)
                if (args[i] == "--csv" && i + 1 < args.Length) outPath = args[++i];
            if (target == null) { Console.WriteLine("usage: --replay-economy <gameId> [--csv path]"); return; }
            string path = File.Exists(target) ? target
                        : Path.Combine(recordingsDir, "singleplayer", target + ".replay");
            if (!File.Exists(path)) { Console.WriteLine($"No replay at {path}"); return; }
            outPath ??= $"economy_{Path.GetFileNameWithoutExtension(path)}.csv";

            var rf = ReplayFile.Read(path);
            var (state, engine) = rf.BuildStart();
            long start = state.CurrentTick;

            var gadget = new double[3];
            engine.OnGadgetCast += (side, id, pos) =>
            {
                var gd = engine.GetGadgetDefinition(id);
                if (gd != null && side >= 1 && side <= 2) gadget[side] += gd.Cost;
            };
            var upgrades = new List<string>();
            engine.OnGadgetUpgraded += (side, def) =>
                upgrades.Add($"{(state.CurrentTick - start) / 30.0:F0},{side},{def.Id}");

            using var w = new StreamWriter(outPath);
            w.WriteLine("second,p1_money,p1_income,p1_invest,p1_hp_pct,p1_repairs,p1_units_spent,p1_gadget_spent," +
                        "p2_money,p2_income,p2_invest,p2_hp_pct,p2_repairs,p2_units_spent,p2_gadget_spent");

            while (!state.IsGameOver && state.CurrentTick < GameEngine.MAX_TICKS)
            {
                engine.Tick();
                long i = state.CurrentTick - start;
                if (i >= 0 && i < rf.A1.Length)
                {
                    if (rf.A1[i] != 0) rf.ApplyRecorded(engine, 1, (int)state.CurrentTick, rf.A1[i]);
                    if (rf.A2[i] != 0) rf.ApplyRecorded(engine, 2, (int)state.CurrentTick, rf.A2[i]);
                }
                if (i % 30 != 0) continue;
                var a = state.Player1; var b = state.Player2;
                w.WriteLine($"{i / 30},{a.Money:F0},{a.Income:F0},{a.InvestmentCount},{Pct(a):F1}," +
                            $"{LadderCost(a.RepairCount, true):F0},{engine.MoneySpentOnUnits[1]:F0},{gadget[1]:F0}," +
                            $"{b.Money:F0},{b.Income:F0},{b.InvestmentCount},{Pct(b):F1}," +
                            $"{LadderCost(b.RepairCount, true):F0},{engine.MoneySpentOnUnits[2]:F0},{gadget[2]:F0}");
            }
            Console.WriteLine($"wrote {outPath}  ({(state.CurrentTick - start) / 30}s, winner P{state.WinnerSide})");
            Console.WriteLine("upgrades (second,side,gadget):");
            foreach (var u in upgrades) Console.WriteLine("  " + u);
        }

        /// <summary>
        /// Prints every expensive unit purchase in a recorded game with the decision context
        /// around it -- money, enemy board, and each gate in the wiper's own test. Written for
        /// Marc's 73DBD4 question: why did the bot buy a $23,000 tier 8?
        ///
        /// Recorded actions are attempts, and the action id alone says nothing about which
        /// branch chose it, so this reconstructs the state at that tick and re-evaluates the
        /// gates rather than inferring them.
        /// </summary>
        public static void Why(string[] args, string recordingsDir)
        {
            string target = args.Length > 1 ? args[1] : null;
            double minCost = 500;
            for (int i = 1; i < args.Length; i++)
                if (args[i] == "--min" && i + 1 < args.Length) minCost = double.Parse(args[++i]);
            if (target == null) { Console.WriteLine("usage: --replay-why <gameId> [--min cost]"); return; }
            string path = File.Exists(target) ? target
                        : Path.Combine(recordingsDir, "singleplayer", target + ".replay");
            if (!File.Exists(path)) { Console.WriteLine($"No replay at {path}"); return; }

            var rf = ReplayFile.Read(path);
            var (state, engine) = rf.BuildStart();
            long start = state.CurrentTick;
            var botTeam = GameDataManager.Teams.Find(t => t.Color == state.Player2.Team);
            int botCastle = GameEngine.MAP_WIDTH - 200;

            Console.WriteLine($"=== EXPENSIVE BOT PURCHASES -- {rf.GameId} (>= ${minCost:N0}) ===");
            Console.WriteLine("The bot is P2. WaveWipeRadius is 500px from its own wall.");

            var oppEcon = new CastleDefense.Engine.Bot.OpponentEconomy(1);
            while (!state.IsGameOver && state.CurrentTick < GameEngine.MAX_TICKS)
            {
                engine.Tick();
                long i = state.CurrentTick - start;
                if (i < 0 || i >= rf.A1.Length) continue;
                if (rf.A1[i] != 0) rf.ApplyRecorded(engine, 1, (int)state.CurrentTick, rf.A1[i]);

                oppEcon.Update(engine);

                byte a = rf.A2[i];
                if (a >= 1 && a <= 8 && botTeam != null && a - 1 < botTeam.Roster.Count)
                {
                    var def = botTeam.Roster[a - 1];
                    if (def.Cost >= minCost)
                    {
                        var me = state.Player2;
                        // The enemy board, as the wiper's own tests measure it.
                        double committedVal = 0; int committedN = 0; double toughest = 0;
                        string toughestId = "-";
                        foreach (var u in state.Units)
                        {
                            if (u.Side == 2) continue;
                            if (Math.Abs(u.Position - botCastle) > 500) continue;
                            committedN++;
                            committedVal += CastleDefense.Engine.Gadgets.GadgetTargeting.UnitCost(engine, u);
                            double ehp = u.CurrentHealth + u.CurrentShield;
                            if (ehp > toughest) { toughest = ehp; toughestId = u.DefinitionId; }
                        }
                        double topCost = botTeam.Roster.Where(x => x.Cost > 0)
                            .Select(x => (double)x.Cost).DefaultIfEmpty(1).Max();
                        // Cheapest unit that one-shots the toughest committed enemy.
                        var oneShot = botTeam.Roster.Where(d => d.Cost > 0 && d.Damage >= toughest)
                            .OrderBy(d => d.Cost).FirstOrDefault();

                        Console.WriteLine($"{i / 30,5}s  BOUGHT {def.Id} (T{def.Tier}) ${def.Cost:N0}");
                        Console.WriteLine($"        bot money ${me.Money:N0}  income ${me.Income:N0}/s  " +
                                          $"invest {me.InvestmentCount}  hp {100.0 * me.CastleHealth / Math.Max(1, me.CastleMaxHealth):F0}%");
                        Console.WriteLine($"        enemy committed within 500px: {committedN} units, " +
                                          $"value ${committedVal:N0}, toughest {toughestId} @ {toughest:N0} ehp");
                        // --- what the OFFENSIVE branch was choosing between ---------
                        float enemyHit = 0;
                        int domTier = 0; double domDmg = 0; var tierDmg = new Dictionary<int,double>();
                        foreach (var u in state.Units)
                        {
                            if (u.Side == 2) continue;
                            if (u.Damage > enemyHit) enemyHit = u.Damage;
                            tierDmg.TryGetValue(u.Tier, out double d);
                            tierDmg[u.Tier] = d + u.Damage;
                        }
                        foreach (var kv in tierDmg) if (kv.Value > domDmg) { domDmg = kv.Value; domTier = kv.Key; }
                        Console.WriteLine($"        enemy field: {state.Units.Count(x => x.Side == 1)} units, " +
                                          $"dominant tier T{domTier}, biggest single hit {enemyHit:N0}");
                        foreach (var d in botTeam.Roster.Where(x => x.Tier >= domTier && x.Cost > 0).OrderBy(x => x.Tier))
                        {
                            double sc = CastleDefense.Engine.Bot.HeuristicBot.DiagScore(d, false, enemyHit, true);
                            Console.WriteLine($"          T{d.Tier} {d.Id,-12} ${d.Cost,7:N0}  hp {d.MaxHealth,7:N0}  " +
                                              $"dmg {d.Damage,6:N0} x {d.AttackSpeed:F2}/s  affordable {(d.Cost <= me.Money ? "yes" : "NO ")}  " +
                                              $"score {sc,12:N0}");
                        }

                        float mineSec = CastleDefense.Engine.Bot.HeuristicBot.DiagSecondsToArmageddon(me);
                        var oppSnap = oppEcon.Snapshot();
                        float theirSec = CastleDefense.Engine.Bot.HeuristicBot.DiagSecondsToArmageddon(oppSnap);
                        var afterBuy = me.Clone(); afterBuy.Money -= def.Cost;
                        float afterSec = CastleDefense.Engine.Bot.HeuristicBot.DiagSecondsToArmageddon(afterBuy);
                        Console.WriteLine($"        RACE GATE: bot {mineSec:F0}s to ARMAGEDDON, modelled human {theirSec:F0}s " +
                                          $"(tracker: money ${oppSnap.Money:N0}, income ${oppSnap.Income:N0}/s, invest {oppSnap.InvestmentCount}) " +
                                          $"-> offence {(mineSec <= theirSec ? "ALLOWED" : "HELD")}");
                        Console.WriteLine($"        AFTER PAYING ${def.Cost:N0}: bot {afterSec:F0}s vs human {theirSec:F0}s " +
                                          $"-> {(afterSec <= theirSec ? "still ahead" : $"BEHIND by {afterSec - theirSec:F0}s")} " +
                                          $"(the purchase cost {afterSec - mineSec:F0}s of race position)");

                        Console.WriteLine($"        WIPER gates: cheapest one-shot = " +
                                          $"{(oneShot?.Id ?? "none")} ${oneShot?.Cost ?? 0:N0}   " +
                                          $"budget = 0.35 x ${committedVal:N0} = ${committedVal * 0.35:N0}   " +
                                          $"-> {(oneShot != null && oneShot.Cost <= committedVal * 0.35 ? "PASSES" : "FAILS")}");
                        Console.WriteLine($"        RICH MODE: money >= 3 x topCost (${topCost * 3:N0})? " +
                                          $"{(me.Money >= topCost * 3 ? "YES -> switches to RawPower ranking" : "no")}");
                        Console.WriteLine();
                    }
                }
                if (a != 0) rf.ApplyRecorded(engine, 2, (int)state.CurrentTick, a);
            }
        }

        private static double AutoCost(int level)
        {
            double t = 0;
            for (int i = 1; i <= level; i++)
            {
                double c = PlayerState.AutoSpawnPriceFor(i);
                if (double.IsFinite(c)) t += c;
            }
            return t;
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
