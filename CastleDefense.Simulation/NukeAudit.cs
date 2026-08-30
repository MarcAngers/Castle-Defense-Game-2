using CastleDefense.Engine;
using CastleDefense.Engine.Gadgets;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// Board state at every offensive-gadget cast (action 11) in a recorded game.
    ///
    /// Built 2026-08-19 to check Marc's report that the singleplayer bot nukes its own
    /// army, including with no enemy units on the board at all. For each cast it prints
    /// what the ENGINE's auto-targeter would pick (GameEngine.UseGadget's position == -1
    /// branch, which is the path search's raw action 11 takes) and how many units of each
    /// side that blast would actually catch.
    ///
    /// UPGRADED FOR v3 REPLAYS, 2026-08-21. v3 records the actual gadget target, the actual
    /// start loadout, the actual map and the actual engine seed, so a v3 game reconstructs
    /// FAITHFULLY all the way to the end -- the "treat everything after cast #1 as a
    /// plausible continuation" caveat below applies only to v2 files now.
    ///
    /// That upgrade also turned this tool into a CODE-PATH DISCRIMINATOR, which is what it
    /// was really needed for. Two different things in this codebase cast an offensive gadget:
    ///
    ///   * HeuristicBot.TryUseOffenseGadget computes a target and passes it to UseGadget. All
    ///     of the friendly-fire machinery lives here -- AoeTradeOk, the no-ally-in-radius
    ///     rule, the wall exclusion, ClampProjectionToCastle.
    ///   * anything calling ApplyAction(side, 11) -- RolloutSearchBot's raw action and its
    ///     press-macro wave -- routes to UseGadget(..., -1) and the ENGINE auto-targeter,
    ///     which has no friendly-fire logic of any kind.
    ///
    /// The recorded v3 target therefore tells them apart: if it equals what the auto-targeter
    /// would have picked, the cast came through ApplyAction; if it differs, HeuristicBot
    /// aimed it. The `path` column reports that.
    ///
    /// RESULT, all 8 v3 singleplayer games, P2 = the deployed search bot (2026-08-21). Marc:
    /// "I observed the bot using the nuke on its own units on multiple occasions... we have
    /// done work previously to prevent this, but it looks like it didn't work correctly."
    ///
    ///   path   casts   casts hitting own   own units hit   enemy units hit
    ///   AUTO      14                  12              78                42
    ///   heur      50                   0               0               285
    ///
    /// THE FRIENDLY-FIRE WORK IS NOT BROKEN -- IT IS BYPASSED. HeuristicBot's 50 casts hit
    /// zero friendlies and 285 enemies. Every own-goal in the corpus, all 12, came through
    /// the auto-targeter. And the two clean AUTO casts are the two where the bot had NO units
    /// on the board at all: 12 for 12 whenever there was anything of its own to hit.
    ///
    /// It is not an occasional miss, because the aim rule is ATTRACTED to the bot's own army.
    /// For side 2 it picks `frontmost enemy + 300`, which in a fight is the point where the
    /// two armies are colliding; and with no enemies at all it falls back to the enemy castle,
    /// clamped to 300 -- exactly where a winning bot's siege is standing. EE51FF t2521 is that
    /// case in its purest form: 0 enemy units on the board, 13 own, 7 of them caught.
    ///
    /// The instrument is trustworthy here: 7 of the 8 v3 rebuilds hit the recorded winner on
    /// the recorded tick (see the oracle at the end of AuditOne). 0C7A5B is the exception and
    /// has not been chased.
    ///
    /// v2 CAVEAT (unchanged, and it matters for reading v2 output). `.replay` v2 stores only
    /// the discrete action id per tick, never the gadget target position, so a rebuilt v2
    /// game re-aims every cast with the auto-targeter -- and its trajectory diverges from the
    /// real game from the first cast onward.
    ///
    /// Usage: --nuke-audit &lt;replay file or dir&gt; [--side 2] [--all]
    /// </summary>
    public static class NukeAudit
    {
        public static void Run(string path, string[] args)
        {
            int side = 2;
            bool all = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--side" && i + 1 < args.Length) side = int.Parse(args[++i]);
                if (args[i] == "--all") all = true;
            }

            var files = Directory.Exists(path)
                ? Directory.GetFiles(path, "*.replay").OrderBy(f => f).ToArray()
                : new[] { path };

            foreach (var f in files)
            {
                var rf = ReplayFile.Read(f);
                var acts = side == 1 ? rf.A1 : rf.A2;
                if (!all && !acts.Any(b => b == 11)) continue;
                AuditOne(rf, side);
            }
        }

        private static void AuditOne(ReplayFile rf, int side)
        {
            Console.WriteLine($"\n=== {rf.GameId}  P1 {rf.P1Team}/{rf.P1Off}  P2 {rf.P2Team}/{rf.P2Off}  "
                            + $"winner={rf.Winner}  ticks={rf.TickCount} ===");

            var (state, engine) = rf.BuildStart();

            // THE REPLAY HEADER STORES THE **END-OF-GAME** LOADOUT. GameRecorder.Save runs at
            // game over and WriteReplay serialises `p1.OffensiveGadget?.Id` from the live
            // PlayerState at that moment, so a gadget that upgraded mid-game is recorded at
            // its FINAL tier. ReplayFile.BuildStart then equips that tier from tick 0, which
            // for FC1462 meant starting with nuke_3 ($4000, radius 600) in a game that
            // actually began with nuke ($20, radius 300) -- every cast then failed the money
            // check and the whole reconstruction diverged. Gadget tiers are earned by in-game
            // XP and this game has startingTick 0 (no headstart), so the true starting
            // loadout is the BASE tier: strip the _2/_3 suffix.
            var meP = side == 1 ? state.Player1 : state.Player2;
            var enP = side == 1 ? state.Player2 : state.Player1;
            static string Base(string id) => string.IsNullOrEmpty(id) ? id : id.Split('_')[0];
            // v3 files carry the real START loadout and BuildStart has already equipped it;
            // overriding it with the base-tier guess would throw away better information.
            if (!rf.HasV3)
            {
                state.Player1.SetLoadout(new[] { Base(rf.P1Off), Base(rf.P1Def), Base(rf.P1Sig) });
                state.Player2.SetLoadout(new[] { Base(rf.P2Off), Base(rf.P2Def), Base(rf.P2Sig) });
            }
            Console.WriteLine($"    startingTick={rf.StartingTick} (timeSkip {rf.StartingTick / 900.0:F0}), "
                            + $"startMoney P1={rf.P1StartMoney:F0} P2={rf.P2StartMoney:F0}");
            Console.WriteLine(rf.HasV3
                ? $"    v3 file: exact map/seed/start-loadout/gadget targets. start offence {meP.OffensiveGadget?.Id}"
                : $"    v2 file: header loadout (END of game): {rf.P2Off} -- reconstructing from base tier");
            var def = meP.OffensiveGadget;
            Console.WriteLine($"    start gadget: {def.Id}  radius={def.Radius}  baseValue={def.BaseValue}  "
                            + $"selfCastleDmg={(int)def.BaseValue / 2}  delay={def.Delay}  cost={def.Cost}");
            Console.WriteLine();
            Console.WriteLine("    tick   sec  gadget   cost   money    myHP    enHP  enemU  ownU     tgt  autoTgt  path   own/enemy   NOW aim:own/enemy");
            Console.WriteLine("    " + new string('-', 132));

            int casts = 0;
            for (int i = 0; i < rf.TickCount; i++)
            {
                byte a1 = rf.A1[i], a2 = rf.A2[i];

                if ((side == 1 ? a1 : a2) == 11)
                {
                    var me = side == 1 ? state.Player1 : state.Player2;
                    // Re-read: the equipped gadget changes tier as XP accrues.
                    def = me.OffensiveGadget;
                    bool onCd = me.GadgetCooldowns.TryGetValue(def.Id, out long cd) && cd > 0;
                    bool affordable = me.Money >= def.Cost;
                    var enemies = state.Units.Where(u => u.Side != side).ToList();
                    var mine = state.Units.Where(u => u.Side == side).ToList();

                    // LEGACY mirror of GameEngine.UseGadget's auto-target branch as it stood
                    // UP TO 2026-08-21. Deliberately kept verbatim rather than re-pointed at
                    // the new GadgetTargeting: every recording in the corpus was played under
                    // THIS rule, so it is the only thing the recorded target can be compared
                    // against to tell the two code paths apart. Re-pointing it would silently
                    // reclassify history.
                    int pos;
                    if (enemies.Count > 0)
                        pos = side == 1
                            ? (int)enemies.OrderBy(e => e.Position).First().Position - 300
                            : (int)enemies.OrderByDescending(e => e.Position).First().Position + 300;
                    else
                        pos = side == 1 ? GameEngine.MAP_WIDTH : 0;
                    pos = Math.Max(300, Math.Min(GameEngine.MAP_WIDTH - 300, pos));

                    // The target that was ACTUALLY used, when the file records it.
                    bool haveReal = rf.GadgetTargets.TryGetValue((i, side), out short realPos);
                    int tgt = haveReal ? realPos : pos;

                    // WHICH CODE PATH FIRED IT. ApplyAction(side, 11) hands UseGadget -1 and
                    // gets `pos`; HeuristicBot passes its own computed target. So an exact
                    // match means the cast bypassed every friendly-fire check in the bot.
                    string path = !haveReal ? "?" : (realPos == pos ? "AUTO" : "heur");

                    int ownHit = mine.Count(u => Math.Abs(u.Position - tgt) <= def.Radius);
                    int enemyHit = enemies.Count(u => Math.Abs(u.Position - tgt) <= def.Radius);

                    // COUNTERFACTUAL: what the CURRENT targeter would do on this exact board.
                    // For an AUTO cast this is the whole verification of the 2026-08-21 fix --
                    // same recorded boards, no new games needed. `refuse` means it declines to
                    // cast at all, which for these gadgets is the correct answer more often
                    // than not.
                    int? newAim = GadgetTargeting.AutoTarget(engine, side, def);
                    string newCol;
                    if (!newAim.HasValue) newCol = "refuse";
                    else
                    {
                        int np = Math.Max(300, Math.Min(GameEngine.MAP_WIDTH - 300, newAim.Value));
                        int no = mine.Count(u => Math.Abs(u.Position - np) <= def.Radius);
                        int ne = enemies.Count(u => Math.Abs(u.Position - np) <= def.Radius);
                        newCol = $"{np}:{no}/{ne}";
                    }

                    var enemyP = side == 1 ? state.Player2 : state.Player1;
                    string flag = !affordable ? "  (UNAFFORDABLE - would fail)"
                                : onCd ? "  (ON COOLDOWN - would fail)"
                                : enemies.Count == 0 ? "  <-- NO ENEMY UNITS" : "";
                    Console.WriteLine(
                        $"    {state.CurrentTick,5}  {state.CurrentTick / 30,4}  {def.Id,-7} {def.Cost,5}  "
                        + $"{me.Money,6:F0}  {me.CastleHealth,6}  {enemyP.CastleHealth,6}  "
                        + $"{enemies.Count,5}  {mine.Count,4}  {tgt,6}  {pos,7}  {path,-5}  {ownHit,4} /{enemyHit,3}   {newCol,-14}{flag}");

                    if (affordable && !onCd && ownHit > 0 && enemyHit == 0)
                        Console.WriteLine($"           ^^ PURE OWN-GOAL: {ownHit} own unit(s) in blast, zero enemy. "
                                        + $"Cost ${def.Cost}, self-castle damage {(int)def.BaseValue / 2}.");

                    // The 15 ticks before the cast, so a press-macro wave is visible as what
                    // it is: a run of spawns on consecutive ticks with 11/12/13 on the tail.
                    // A cast that is the tail of a QUEUE was not decided at this tick at all,
                    // which is why asking "what would search choose here" answers nothing.
                    var window = new List<string>();
                    for (int k = Math.Max(0, i - 14); k <= i; k++)
                    {
                        byte a = side == 1 ? rf.A1[k] : rf.A2[k];
                        if (a != 0) window.Add($"t{k}:{a}");
                    }
                    Console.WriteLine("           preceding 15 ticks of P" + side + " actions: "
                                    + (window.Count > 0 ? string.Join(" ", window) : "(none)"));
                    casts++;
                }

                if (a1 != 0) rf.ApplyRecorded(engine, 1, i, a1);
                if (a2 != 0) rf.ApplyRecorded(engine, 2, i, a2);
                engine.Tick();
                if (state.IsGameOver) break;
            }
            Console.WriteLine($"\n    {casts} offensive-gadget casts by P{side}");

            // ORACLE. On a v3 file the rebuild has the real map, the real engine seed, the
            // real start loadout and the real gadget aim, so it should land on the SAME
            // winner at the SAME tick as the recorded game. If it does, everything above is
            // exact rather than "a plausible continuation" -- and if it ever stops doing so,
            // that is the signal that some new piece of state is going unrecorded. Same
            // discipline as --divergence's `--bot replay` check.
            if (rf.HasV3)
            {
                bool ok = state.IsGameOver && state.WinnerSide == rf.Winner
                          && state.CurrentTick == rf.TickCount;
                Console.WriteLine($"    reconstruction oracle: {(ok ? "EXACT" : "DIVERGED")} -- "
                                + $"rebuilt winner={(state.IsGameOver ? state.WinnerSide : -1)} @ tick {state.CurrentTick}, "
                                + $"recorded winner={rf.Winner} @ tick {rf.TickCount}");
            }
        }
    }
}
