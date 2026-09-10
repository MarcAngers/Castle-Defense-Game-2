using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Gadgets;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// DOES SNIPE HIT THE UNITS THE PLAYER MEANT?
    ///
    /// Added 2026-09-03 with the generous auto-target; extended 2026-09-08 when snipe became a
    /// BURST weapon (2 shots at level 2, 10 at level 3) and the wall rules were rewritten.
    ///
    /// This is a pure selection rule with no visible feedback: a wrong pick looks identical to
    /// a right one until the wrong unit fails to die. So it gets asserted directly, and the
    /// firing CADENCE is asserted through a real tick loop rather than by reading constants.
    ///
    /// Run: dotnet run --project CastleDefense.Simulation -- --snipe-check
    /// </summary>
    public static class SnipeCheck
    {
        public static void Run(string[] args)
        {
            int failures = 0;
            void Check(string label, bool ok, string detail = null)
            {
                Console.WriteLine($"  {label,-60} {(ok ? "ok" : "FAIL")}{(detail == null ? "" : "  " + detail)}");
                if (!ok) failures++;
            }

            Console.WriteLine("=== SNIPE CHECK ===");
            Console.WriteLine($"AutoTargetRadius = {SnipeEffect.AutoTargetRadius}, " +
                              $"DirectClickPrecision = {SnipeEffect.DirectClickPrecision}");
            foreach (var id in new[] { "snipe", "snipe_2", "snipe_3" })
            {
                var d = GameDataManager.Gadgets.Find(g => g.Id == id);
                Console.WriteLine($"  {id,-8} shots {SnipeEffect.ShotsFor(d),3}   " +
                                  $"interval {SnipeEffect.ShotIntervalFor(d),3} ticks   " +
                                  $"delay {d.Delay,3}   dmg {d.BaseValue:N0}");
            }
            Console.WriteLine();

            int savedSquad = GameEngine.OpeningSquadSize;
            GameEngine.OpeningSquadSize = 0;
            try
            {
                // A White line: the tier 7 is the prize, screened by chumps standing closer to
                // wherever the player is realistically going to click.
                var state = new GameState();
                state.Player1.Team = TeamColour.White;
                state.Player2.Team = TeamColour.White;
                string sig = GameDataManager.GetSignatureGadgetIdForTeam(TeamColour.White);
                state.Player1.SetLoadout(new[] { "snipe", "heal", sig });
                state.Player2.SetLoadout(new[] { "snipe", "heal", sig });
                var engine = new GameEngine(state, null, 4242);
                state.Player1.Money = 500000;

                var roster = GameDataManager.Teams.Find(t => t.Color == TeamColour.White).Roster;
                string chump = roster.Find(u => u.Tier == 1).Id;   // doggo,  12 HP
                string legg = roster.Find(u => u.Tier == 7).Id;    // eggo, 4690 HP

                // The prize at 1000, chumps packed around it from 850 to 1150.
                engine.SpawnUnit(2, legg, true, 1000);
                for (int x = 850; x <= 1150; x += 30)
                    if (x != 1000) engine.SpawnUnit(2, chump, true, x);

                var enemies = state.Units.Where(u => u.Side != 1).ToList();
                var prize = enemies.First(u => u.DefinitionId == legg);
                Console.WriteLine($"FIELD: 1 x {legg} ({prize.MaxHealth} HP) at 1000, " +
                                  $"{enemies.Count - 1} x {chump} packed 850-1150");
                Console.WriteLine();

                // ---- SHOT 0 STILL FINDS THE PRIZE ---------------------------------------
                Console.WriteLine("A CLICK NEAR THE PRIZE FINDS IT");
                foreach (int click in new[] { 1000, 985, 940, 880, 1120, 1180, 1270 })
                {
                    var hit = SnipeEffect.SelectTarget(enemies, click);
                    bool ok = hit != null && hit.InstanceId == prize.InstanceId;
                    Check($"click at {click} (off by {Math.Abs(click - 1000)}) hits the tier 7", ok,
                          ok ? null : $"hit {hit?.DefinitionId} at {hit?.Position:F0}");
                }
                Console.WriteLine();

                Console.WriteLine("BUT THE RADIUS IS A REAL LIMIT");
                {
                    int far = 1000 + SnipeEffect.AutoTargetRadius + 60;
                    var hit = SnipeEffect.SelectTarget(enemies, far);
                    Check($"click at {far} (beyond the radius) does NOT hit it",
                          hit != null && hit.InstanceId != prize.InstanceId,
                          $"hit {hit?.DefinitionId} at {hit?.Position:F0}");
                    Check("...but still hits SOMETHING rather than fizzling", hit != null);
                }
                Console.WriteLine();

                // ---- THE BURST -----------------------------------------------------------
                //
                // The whole point of the 2026-09-08 change: shot 0 is the aim, the rest are
                // the biggest things on the field, and they are all DIFFERENT units.
                Console.WriteLine("THE BURST PICKS DISTINCT TARGETS, BIGGEST FIRST");
                {
                    // Give it a spread of real targets so "biggest first" is observable.
                    for (int t = 2; t <= 6; t++)
                    {
                        var d = roster.Find(u => u.Tier == t);
                        engine.SpawnUnit(2, d.Id, true, 400 + t * 40);
                    }
                    var field = state.Units.Where(u => u.Side != 1).ToList();

                    var five = SnipeEffect.SelectTargets(field, 1000, 5);
                    Check("5 shots select 5 targets", five.Count == 5, $"{five.Count}");
                    Check("all distinct", five.Select(u => u.InstanceId).Distinct().Count() == five.Count);
                    Check("shot 0 is the aimed tier 7", five[0].InstanceId == prize.InstanceId,
                          five[0].DefinitionId);
                    var follow = five.Skip(1).ToList();
                    Check("follow-ups are in descending MaxHealth",
                          follow.Zip(follow.Skip(1), (a, b) => a.MaxHealth >= b.MaxHealth).All(x => x),
                          string.Join(" > ", follow.Select(u => $"{u.DefinitionId}:{u.MaxHealth}")));

                    // More shots than there are enemies: it must stop, not repeat or crash.
                    var lots = SnipeEffect.SelectTargets(field, 1000, field.Count + 25);
                    Check("more shots than enemies stops at the enemy count",
                          lots.Count == field.Count, $"{lots.Count} of {field.Count}");
                    Check("...and still never repeats a unit",
                          lots.Select(u => u.InstanceId).Distinct().Count() == lots.Count);

                    Check("one shot returns exactly one target",
                          SnipeEffect.SelectTargets(field, 1000, 1).Count == 1);
                    Check("an empty field returns nothing rather than throwing",
                          SnipeEffect.SelectTargets(new List<Unit>(), 1000, 10).Count == 0);
                }
                Console.WriteLine();

                // ---- WALLS ---------------------------------------------------------------
                Console.WriteLine("WALLS");
                {
                    var ws = new GameState();
                    ws.Player1.Team = ws.Player2.Team = TeamColour.White;
                    ws.Player1.SetLoadout(new[] { "snipe", "wall", sig });
                    ws.Player2.SetLoadout(new[] { "snipe", "wall", sig });
                    var we = new GameEngine(ws, null, 99);
                    ws.Player2.Money = 500000;

                    // wall_3 SPECIFICALLY: 48,000 HP and 450 wide, an order of magnitude above
                    // the tier 7 on health. A weaker wall would let these pass even if the
                    // exclusion were deleted, since the tier 7 would out-rank it anyway.
                    we.SpawnUnit(2, "wall_3", true, 1000);
                    we.SpawnUnit(2, legg, true, 1400);
                    for (int x = 1300; x <= 1500; x += 50)
                        if (x != 1400) we.SpawnUnit(2, chump, true, x);

                    var wf = ws.Units.Where(u => u.Side != 1).ToList();
                    var theWall = wf.First(GadgetTargeting.IsWall);
                    var theLegg = wf.First(u => u.DefinitionId == legg);
                    Console.WriteLine($"  wall_3 at 1000 ({theWall.MaxHealth} HP, {theWall.Width} wide), " +
                                      $"tier 7 at 1400 ({theLegg.MaxHealth} HP)");

                    Check("a click ON the wall takes the wall",
                          SnipeEffect.SelectTarget(wf, 1000)?.InstanceId == theWall.InstanceId);
                    Check("a click inside the wall's edge still takes the wall",
                          SnipeEffect.SelectTarget(wf, 1000 + theWall.Width / 2 - 10)?.InstanceId == theWall.InstanceId);
                    Check("a click OUTSIDE the wall does NOT take it (the tier 7 wins)",
                          SnipeEffect.SelectTarget(wf, 1400)?.InstanceId == theLegg.InstanceId);

                    // The carve-out: a real unit standing on the wall's footprint beats it.
                    we.SpawnUnit(2, legg, true, 1050);
                    var wf2 = ws.Units.Where(u => u.Side != 1).ToList();
                    var onWall = wf2.First(u => u.DefinitionId == legg && Math.Abs(u.Position - 1050) < 1);
                    Check("a unit ON the wall's footprint beats the wall when clicked",
                          SnipeEffect.SelectTarget(wf2, 1050)?.InstanceId == onWall.InstanceId,
                          SnipeEffect.SelectTarget(wf2, 1050)?.DefinitionId);

                    // THE HEADLINE RULE: follow-ups must never spend themselves on scenery.
                    var burst = SnipeEffect.SelectTargets(wf2, 1000, 12);
                    Check("shot 0 may be the wall (clicked)", burst[0].InstanceId == theWall.InstanceId);
                    Check("NO follow-up shot targets a wall",
                          burst.Skip(1).All(u => !GadgetTargeting.IsWall(u)),
                          string.Join(",", burst.Skip(1).Select(u => u.DefinitionId)));

                    // ...and when the player did NOT click the wall, it is out of the burst
                    // entirely rather than merely demoted.
                    var burst2 = SnipeEffect.SelectTargets(wf2, 1400, 12);
                    Check("a burst aimed elsewhere contains no wall at all",
                          burst2.All(u => !GadgetTargeting.IsWall(u)));

                    // A lone wall is still snipeable -- excluding walls outright would make it
                    // immune, which is a legitimate play denied.
                    var alone = wf2.Where(GadgetTargeting.IsWall).ToList();
                    Check("a wall with nothing else in range IS hit",
                          SnipeEffect.SelectTarget(alone, 1000)?.InstanceId == theWall.InstanceId);
                }
                Console.WriteLine();

                // ---- CADENCE, THROUGH A REAL TICK LOOP -----------------------------------
                //
                // Asserted by watching units die rather than by reading Delay and interval
                // back out of the CSV, which would only prove the CSV equals itself.
                Console.WriteLine("CADENCE (real tick loop, level 3)");
                {
                    var cs = new GameState();
                    cs.Player1.Team = cs.Player2.Team = TeamColour.White;
                    cs.Player1.SetLoadout(new[] { "snipe_3", "heal", sig });
                    cs.Player2.SetLoadout(new[] { "snipe_3", "heal", sig });
                    var ce = new GameEngine(cs, null, 7);
                    cs.Player1.Money = 500000;
                    // BOTH castles, and that is not belt-and-braces. The targets are side-2
                    // units walking at OUR castle: if they raze it the game ends, Tick stops
                    // advancing, and the remaining scheduled shots never fire -- which reads
                    // as "the burst did not work" when the burst was fine.
                    cs.Player1.CastleHealth = cs.Player1.CastleMaxHealth = 1_000_000_000;
                    cs.Player2.CastleHealth = cs.Player2.CastleMaxHealth = 1_000_000_000;

                    var def3 = GameDataManager.Gadgets.Find(g => g.Id == "snipe_3");
                    int shots = SnipeEffect.ShotsFor(def3), interval = SnipeEffect.ShotIntervalFor(def3);

                    // One target per shot, all identical and far from the castle so nothing
                    // else can kill them.
                    for (int i = 0; i < shots; i++) ce.SpawnUnit(2, legg, true, 900 + i * 12);
                    int before = cs.Units.Count(u => u.Side == 2);

                    var plan = SnipeEffect.SelectTargets(
                        cs.Units.Where(u => u.Side != 1).ToList(), 900, shots);
                    Check($"the firing solution has {shots} targets", plan.Count == shots,
                          $"{plan.Count} from {before} enemies");
                    Check("the cast is accepted", ce.UseGadget(1, "snipe_3", 900));

                    var deathTick = new List<int>();
                    int alive = before;
                    for (int t = 0; t < def3.Delay + shots * interval + 90; t++)
                    {
                        ce.Tick();
                        int now = cs.Units.Count(u => u.Side == 2);
                        for (int k = 0; k < alive - now; k++) deathTick.Add(t + 1);
                        alive = now;
                    }

                    Check($"all {shots} shots landed a kill", deathTick.Count == shots,
                          $"{deathTick.Count} kills from {before} targets");
                    if (deathTick.Count == shots)
                    {
                        var gaps = deathTick.Zip(deathTick.Skip(1), (a, b) => b - a).ToList();
                        Check($"shots are {interval} ticks apart",
                              gaps.All(g => g == interval), string.Join(",", gaps));
                        Check($"the first lands at the CSV delay ({def3.Delay})",
                              Math.Abs(deathTick[0] - def3.Delay) <= 1, $"tick {deathTick[0]}");
                    }
                }
                Console.WriteLine();

                // ---- THE BOT PATH IS UNCHANGED IN SHAPE ----------------------------------
                Console.WriteLine("BOT CASTS STILL RESOLVE");
                {
                    var def = GameDataManager.Gadgets.Find(g => g.Id == "snipe");
                    int? aim = GadgetTargeting.AutoTarget(engine, 1, def);
                    Check("AutoTarget returns an aim point", aim.HasValue, $"{aim}");
                    if (aim.HasValue)
                        Check("that aim point selects a real unit",
                              SnipeEffect.SelectTarget(enemies, aim.Value) != null);

                    double moneyBefore = state.Player1.Money;
                    bool cast = engine.UseGadget(1, "snipe", -1);
                    Check("a -1 (auto-aimed) cast is accepted", cast,
                          $"spent ${moneyBefore - state.Player1.Money}");
                }
            }
            finally
            {
                GameEngine.OpeningSquadSize = savedSquad;
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
            if (failures > 0) Environment.ExitCode = 1;
        }
    }
}
