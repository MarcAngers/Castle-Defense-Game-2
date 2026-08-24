using System.Security.Cryptography;
using System.Text;
using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    /// <summary>
    /// Deterministic fingerprint of HeuristicBot's play, for proving a refactor changed nothing.
    ///
    /// EXISTS BECAUSE THE OBVIOUS BENCHMARKS CANNOT DO THIS. counter-eval is not reproducible
    /// (the same binary gives different rows run to run) and counter-matrix is byte-identical
    /// but sweeps 128x128 cells, which is minutes of wall clock before it says anything. A
    /// refactor guard has to be exact AND fast or it will not get run.
    ///
    /// Everything here is seeded from the game index alone: the map roll, the loadouts and the
    /// engine stream. Two runs of the same binary agree exactly; two builds that play the same
    /// way agree exactly.
    /// </summary>
    public static class BotChecksum
    {
        public static void Run(string[] args)
        {
            int games = 24;
            bool defenceOnly = false;
            bool p1Only = false;
            int trace = -1;
            float engageDps = -1f;
            double wiperCd = -1;
            bool noCoverage = false;
            bool repairFix = false;
            bool hazardFix = false;
            string loadout = null;   // e.g. White,nuke,reinforcements -- pins BOTH sides
            string dump = null;      // per-tick CSV of the traced game   // P1 defensive, P2 the shipped attacking bot -- the head-to-head
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--games" && i + 1 < args.Length) games = int.Parse(args[++i]);
                if (args[i] == "--defence-only") defenceOnly = true;
                if (args[i] == "--p1-defence-only") { p1Only = true; defenceOnly = true; }
                if (args[i] == "--trace" && i + 1 < args.Length) trace = int.Parse(args[++i]);
                if (args[i] == "--engage-dps" && i + 1 < args.Length) engageDps = float.Parse(args[++i]);
                if (args[i] == "--wiper-cd" && i + 1 < args.Length) wiperCd = double.Parse(args[++i]);
                if (args[i] == "--no-coverage") noCoverage = true;
                // P1 plays the flagship PLUS the repair fixes; P2 stays the flagship, so the
                // difference is attributable to repair alone.
                if (args[i] == "--p1-repair-fix") { repairFix = true; p1Only = true; }
                if (args[i] == "--p1-hazard-fix") { hazardFix = true; p1Only = true; }
                if (args[i] == "--loadout" && i + 1 < args.Length) loadout = args[++i];
                if (args[i] == "--dump" && i + 1 < args.Length) dump = args[++i];
            }

            var settings = hazardFix ? HeuristicBotSettings.RepairFixPlusHazardProfile
                : repairFix ? HeuristicBotSettings.RepairFixProfile
                : !defenceOnly ? null
                : (engageDps >= 0f || wiperCd >= 0 || noCoverage)
                    ? new HeuristicBotSettings
                      {
                          DefenceOnly = true,
                          MinBlockEffectiveness = engageDps >= 0f
                              ? engageDps : HeuristicBotSettings.Default.MinBlockEffectiveness,
                          WiperMinIntervalSeconds = wiperCd >= 0
                              ? wiperCd : HeuristicBotSettings.DefenceOnlyProfile.WiperMinIntervalSeconds,
                          WiperCountsFieldCoverage = !noCoverage,
                      }
                    : HeuristicBotSettings.DefenceOnlyProfile;
            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            var offense = new[] { "nuke", "firebomb", "snipe", "freeze" };
            var defense = new[] { "heal", "reinforcements", "speed", "wall" };

            var sb = new StringBuilder();
            Console.WriteLine($"BOT CHECKSUM -- {games} seeded self-play games, "
                            + $"{(defenceOnly ? "DEFENCE-ONLY" : "shipped")} settings\n");
            Console.WriteLine($"{"game",-6}{"map",-9}{"winner",-8}{"ticks",8}{"p1hp",9}{"p2hp",9}{"p1inv",7}{"p2inv",7}{"p1units",9}{"p1$",9}{"p2$",9}");

            StreamWriter dumpw = null;
            if (dump != null)
            {
                dumpw = new StreamWriter(dump);
                dumpw.WriteLine("tick,hp,maxhp,money,inv,own,enemy,enemy_dps,enemy_swings,"
                    + "enemy_value,choice,t1,t2,t3,t4,t5,t6,t7,t8,"
                    + "wipe_veto,w_radius,w_reach,w_valrad,w_valreach,w_spread,p2money,p2inv,p1income,p2income,p1spent,p2spent,"
                    // The trade curve: value each side has LOST cumulatively, plus what each
                    // side is holding in gadgets. "When did we stop winning the trades" is not
                    // answerable without these two running totals.
                    + "own_lost,foe_lost,own_val,foe_val,p1def,p2def,p1off,p2off,"
                    + "o1,o2,o3,o4,o5,o6,o7,o8,p2hp,p2maxhp,ownpos,foepos,"
                    // Cumulative own arrivals split by HOW they arrived: b* = the bot
                    // PAID for it, f* = a gadget spawned it free. The field histogram
                    // (o*) cannot separate these, and the whole question is which.
                    + "b1,b2,b3,b4,b5,b6,b7,b8,f1,f2,f3,f4,f5,f6,f7,f8");
            }

            for (int g = 0; g < games; g++)
            {
                var rng = new Random(g);
                var map = teams[rng.Next(teams.Length)];
                var state = new GameState(map, new Random(g));
                state.Player1 = new PlayerState();
                state.Player2 = new PlayerState();
                string[] pin = loadout?.Split(',');
                for (int side = 1; side <= 2; side++)
                {
                    var p = side == 1 ? state.Player1 : state.Player2;
                    var t = pin != null ? Enum.Parse<TeamColour>(pin[0], true) : teams[rng.Next(teams.Length)];
                    p.Side = side;
                    p.Team = t;
                    p.SetLoadout(pin != null
                        ? new[] { pin[1], pin[2], GameDataManager.GetSignatureGadgetIdForTeam(t) }
                        : new[] { offense[rng.Next(offense.Length)],
                                  defense[rng.Next(defense.Length)],
                                  GameDataManager.GetSignatureGadgetIdForTeam(t) });
                }

                var engine = new GameEngine(state, null, g);
                var p1 = new HeuristicBot(1, settings);
                var p2 = new HeuristicBot(2, p1Only ? null : settings);
                int p1Spawns = 0;
                int spawnsThisSecond = 0;
                var reasons = new Dictionary<string,int>();
                var choices = new Dictionary<string,int>();
                // Honest kill accounting: every distinct enemy unit that appeared, and whether
                // it is still alive. The bot's own WIPE tally double-counts, because at a short
                // cooldown consecutive purchases each price the SAME pile before the first has
                // landed -- at cd 0 it claims to have destroyed 1.28x everything the opponent
                // ever bought, which is impossible. This cannot inflate: a unit is counted once.
                var seenFoe = new Dictionary<Guid, double>();
                var seenOwn = new Dictionary<Guid, double>();
                double ownLost = 0, foeLost = 0;   // running, so the dump can chart the trade
                // Arrivals split by provenance. A unit that appears while engine.Tick() runs came
                // from a scheduled gadget effect (reinforcements) and was free; one that appears
                // while p1.Update() runs was bought. Nothing else spawns our units.
                var bought = new int[9];
                var freeIn = new int[9];
                var ownIds = new HashSet<Guid>();

                while (!state.IsGameOver)
                {
                    engine.Tick();
                    foreach (var u in state.Units)
                        if (u.Side == 1 && ownIds.Add(u.InstanceId) && u.Tier >= 1 && u.Tier <= 8)
                            freeIn[u.Tier]++;
                    int before = state.Units.Count(u => u.Side == 1);
                    string beforeChoice = p1.LastDefenceChoice;
                    p1.Update(engine);
                    foreach (var u in state.Units)
                        if (u.Side == 1 && ownIds.Add(u.InstanceId) && u.Tier >= 1 && u.Tier <= 8)
                            bought[u.Tier]++;
                    if (!string.IsNullOrEmpty(p1.LastDefenceChoice) && p1.PendingActionCount == 0)
                    {
                        string c = p1.LastDefenceChoice;
                        choices[c] = choices.TryGetValue(c, out var cc) ? cc + 1 : 1;
                    }
                    _ = beforeChoice;
                    if (dumpw != null && g == (trace < 0 ? 0 : trace) && state.CurrentTick % 3 == 0)
                    {
                        var foe = state.Units.Where(u => u.Side == 2).ToList();
                        var tm = CastleDefense.Engine.Bot.ThreatModel.Build(engine, 1, foe, state.Player1.CastleHealth);
                        var tiers = new int[9];
                        var ot = new int[9];
                        foreach (var u in state.Units)
                            if (u.Side == 1 && u.Tier >= 1 && u.Tier <= 8) ot[u.Tier]++;
                        double val = 0;
                        foreach (var u in foe)
                        {
                            if (u.Tier >= 1 && u.Tier <= 8) tiers[u.Tier]++;
                            var rd = GameDataManager.Teams.SelectMany(t2 => t2.Roster).FirstOrDefault(r2 => r2.Id == u.DefinitionId);
                            if (rd != null) val += rd.Cost;
                        }
                        dumpw.WriteLine($"{state.CurrentTick},{state.Player1.CastleHealth},{state.Player1.CastleMaxHealth},"
                            + $"{state.Player1.Money:F0},{state.Player1.InvestmentCount},"
                            + $"{state.Units.Count(u => u.Side == 1)},{foe.Count},{tm.UnblockedDps:F0},{tm.SwingRate:F1},"
                            + $"{val:F0},{p1.LastDefenceChoice},"
                            + $"{tiers[1]},{tiers[2]},{tiers[3]},{tiers[4]},{tiers[5]},{tiers[6]},{tiers[7]},{tiers[8]},"
                            + $"{p1.LastWipeVeto},{p1.LastWipeInRadius},{p1.LastWipeInReach},"
                            + $"{p1.LastWipeValRadius:F0},{p1.LastWipeValReach:F0},{p1.LastWipeSpread:F0},"
                            + $"{state.Player2.Money:F0},{state.Player2.InvestmentCount},"
                            + $"{state.Player1.Income:F0},{state.Player2.Income:F0},"
                            + $"{engine.MoneySpentOnUnits[1]:F0},{engine.MoneySpentOnUnits[2]:F0},"
                            + $"{ownLost:F0},{foeLost:F0},{seenOwn.Values.Sum():F0},{seenFoe.Values.Sum():F0},"
                            + $"{state.Player1.DefensiveGadget?.Id},{state.Player2.DefensiveGadget?.Id},"
                            + $"{state.Player1.OffensiveGadget?.Id},{state.Player2.OffensiveGadget?.Id},"
                            + $"{ot[1]},{ot[2]},{ot[3]},{ot[4]},{ot[5]},{ot[6]},{ot[7]},{ot[8]},"
                            + $"{state.Player2.CastleHealth},{state.Player2.CastleMaxHealth},"
                            // Mean position of each army: where the fighting line actually sits.
                            // A defence-only bot whose line is deep in enemy territory is not
                            // defending, whatever its decision arm says it chose.
                            + $"{(state.Units.Where(u => u.Side == 1).Select(u => (double)u.Position).DefaultIfEmpty(0).Average()):F0},"
                            + $"{(state.Units.Where(u => u.Side == 2).Select(u => (double)u.Position).DefaultIfEmpty(0).Average()):F0},"
                            + $"{bought[1]},{bought[2]},{bought[3]},{bought[4]},{bought[5]},{bought[6]},{bought[7]},{bought[8]},"
                            + $"{freeIn[1]},{freeIn[2]},{freeIn[3]},{freeIn[4]},{freeIn[5]},{freeIn[6]},{freeIn[7]},{freeIn[8]}");
                    }
                    if (g == trace && state.CurrentTick % 30 == 0)
                        Console.WriteLine($"  t={state.CurrentTick/30,4}s hp={state.Player1.CastleHealth,7} "
                            + $"inv={state.Player1.InvestmentCount} $={state.Player1.Money,8:F0} "
                            + $"bare={p1.LastBareSurvival,6:F1}s tgt={p1.LastDefenceTarget,5:F1}s "
                            + $"need={p1.LastRequiredRate,5:F2}/s cred={p1.LastBlockCredit,4:F1} "
                            + $"$/hp={p1.LastDollarsPerHp,6:F3} {p1.LastDefenceChoice,-6} spawned={spawnsThisSecond,2}/s "
                            + $"own={state.Units.Count(u => u.Side == 1),3} {p1.LastThreatDebug}");
                    if (state.CurrentTick % 30 == 0) spawnsThisSecond = 0;
                    p2.Update(engine);
                    int after = state.Units.Count(u => u.Side == 1);
                    foreach (var u in state.Units)
                    {
                        var book = u.Side == 2 ? seenFoe : seenOwn;
                        if (!book.ContainsKey(u.InstanceId))
                            book[u.InstanceId] = CastleDefense.Engine.Gadgets.GadgetTargeting.UnitCost(engine, u);
                    }
                    {
                        var live = new HashSet<Guid>(state.Units.Select(u => u.InstanceId));
                        ownLost = seenOwn.Where(kv => !live.Contains(kv.Key)).Sum(kv => kv.Value);
                        foeLost = seenFoe.Where(kv => !live.Contains(kv.Key)).Sum(kv => kv.Value);
                    }
                    if (after > before)
                    {
                        p1Spawns += after - before;
                        spawnsThisSecond += after - before;
                        string why = p1.LastSpawnReason ?? "?";
                        reasons[why] = reasons.TryGetValue(why, out var c) ? c + 1 : 1;
                    }
                }

                string row = $"{g,-6}{map,-9}{state.WinnerSide,-8}{state.CurrentTick,8}"
                           + $"{state.Player1.CastleHealth,9}{state.Player2.CastleHealth,9}"
                           + $"{state.Player1.InvestmentCount,7}{state.Player2.InvestmentCount,7}{p1Spawns,9}";
                // Hash BEHAVIOUR only, never the printed row. Adding a display column must not
                // look like a behaviour change -- it did exactly that once already.
                sb.Append(g).Append(',').Append(state.WinnerSide).Append(',').Append(state.CurrentTick)
                  .Append(',').Append(state.Player1.CastleHealth).Append(',').Append(state.Player2.CastleHealth)
                  .Append(',').Append(state.Player1.InvestmentCount).Append(',').Append(state.Player2.InvestmentCount)
                  .Append(',').Append(p1Spawns).Append('\n');
                row += $"{engine.MoneySpentOnUnits[1],9:F0}{engine.MoneySpentOnUnits[2],9:F0}";
                row += $"  FOELOST={foeLost:F0} FOESEEN={seenFoe.Values.Sum():F0} OWNSEEN={seenOwn.Values.Sum():F0}";
                // Gadget casts per side. reinforcements_3 buys 5 tier-7 units ($10,330 of
                // White army) for $1,440 on a 10s cooldown -- a 7.2x multiplier no unit
                // purchase can match -- so "did each side actually fire its defensive
                // gadget" is a first-order economic question, not a detail.
                row += $"  REP p1={p1.ActionCounts[10]} p2={p2.ActionCounts[10]}";
                row += $"  HZB={p1.HazardBlackoutDecisions} T1={state.Player1.Team} T2={state.Player2.Team}";
                row += $"  GAD p1 off={p1.ActionCounts[11]} def={p1.ActionCounts[12]} sig={p1.ActionCounts[13]}"
                     + $" | p2 off={p2.ActionCounts[11]} def={p2.ActionCounts[12]} sig={p2.ActionCounts[13]}";
                // The CAST COUNT hides the tier, and the tier is the whole story: the same
                // 28 casts are 5 tier-1 units each on the base gadget and 5 tier-7 on tier 3.
                row += $"  DEFGAD p1={state.Player1.DefensiveGadget?.Id} p2={state.Player2.DefensiveGadget?.Id}";
                row += $"  WIPE n={p1.WipeCount} reach={p1.WipeValueReached:F0} cred={p1.WipeValueCredited:F0} paid={p1.WipeSpend:F0} "
                    + $"altkill={p1.WipeBestAltKill:F0} altcost={p1.WipeBestAltCost:F0} altdiff={p1.WipeBestAltCount}";
                Console.WriteLine(row + "  " + string.Join(" ", reasons.OrderByDescending(k => k.Value).Select(k => k.Key + "=" + k.Value))
                                + "  | " + string.Join(" ", choices.OrderByDescending(k => k.Value).Select(k => k.Key + "=" + k.Value)));
            }

            dumpw?.Flush(); dumpw?.Dispose();

            using var md5 = MD5.Create();
            string hash = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
            Console.WriteLine($"\nCHECKSUM {hash}");
        }
    }
}
