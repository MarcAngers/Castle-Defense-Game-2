using CastleDefense.Engine;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// HOW FAITHFUL IS A REBUILT REPLAY? Measures it instead of assuming it either way.
    ///
    /// A .replay stores one action id per tick and NOTHING about state, so a reconstruction
    /// cannot be checked against recorded positions or money -- there aren't any. It can be
    /// checked against four things that ARE ground truth:
    ///
    ///   1. EVERY RECORDED ACTION MUST SUCCEED. The recorder only ever writes an action the
    ///      live engine accepted, so a recorded invest was affordable and a recorded gadget
    ///      was off cooldown. If ApplyAction starts returning false in the rebuild, the
    ///      rebuild's economy has drifted from the real one. This is the sharp test, and it
    ///      is what caught the end-of-game-loadout bug: with nuke_3 equipped from tick 0
    ///      every cast failed the money check.
    ///   2. FINAL GADGET TIERS must match the header, which the recorder wrote from the live
    ///      PlayerState at game over. Tiers come from cast XP, so this checks the whole
    ///      cast history, not just the end.
    ///   3. FINAL INCOME / MONEY / CASTLE HP must match the games table, captured live at
    ///      game over with no resimulation involved.
    ///   4. WINNER and DURATION must match.
    ///
    /// The known unfaithfulness is gadget AIM: .replay never stored target positions, so the
    /// rebuild re-aims every cast with GameEngine's auto-targeter. Whether that matters is an
    /// empirical question per game, which is the question this answers.
    ///
    /// RESULT FOR B0589C (2026-08-20), the first game measured this way:
    ///
    ///   CHECK 1  0 failures out of 27 P1 actions and 29 P2 actions -- PERFECT.
    ///   CHECK 2  5 of 6 final gadget tiers match; P1 defence reads reinforcements_2 against
    ///            a recorded reinforcements_3, because the rebuild ENDS EARLY and P1 never
    ///            gets the last casts.
    ///   CHECK 3  P2 (the bot) matches closely: income 19.7 vs recorded 19.7, investment 4,
    ///            castle 0%. P1 diverges: income 252.5 vs 750, investment 6 vs 7.
    ///   CHECK 4  Winner matches (P1). Duration does NOT: the rebuild ends at tick 4358
    ///            against a recorded 5670 -- the bot dies 44 SECONDS EARLY.
    ///
    /// READ CHECK 1 CAREFULLY, IT IS THE ONLY STRONG ONE. Investment counts and timings
    /// matching is NOT evidence: the recorded actions are the rebuild's INPUT, replayed at
    /// their recorded ticks, so those necessarily agree. What is real evidence is that none
    /// of them FAILED -- every recorded invest was still affordable and every recorded cast
    /// still off cooldown, which they would not have been had the economy drifted. So the
    /// ECONOMIC side of the rebuild is faithful.
    ///
    /// COMBAT IS NOT. The 44-second-early death is the whole story: re-aimed gadgets change
    /// who dies and when, and the error accumulates. The rebuild is therefore usable for
    /// economic and decision-level analysis -- why the bot stopped investing at tick 2731,
    /// what it could afford -- and NOT usable for reconstructing a specific late-game combat
    /// scenario, because the rebuild kills the bot before that scenario develops.
    ///
    /// Usage: --replay-fidelity &lt;replay&gt; [--every N]
    /// </summary>
    public static class ReplayFidelity
    {
        public static void Run(string path, string[] args)
        {
            int every = 900;   // 30s
            bool dumpCasts = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--every" && i + 1 < args.Length) every = int.Parse(args[++i]);
                // Emits the TRUE gadget casts as CSV, for repairing a gadget_uses table that
                // was polluted by the OnGadgetCast clone leak. Only meaningful on a replay
                // that reconstructs exactly -- check the four checks first.
                if (args[i] == "--dump-casts") dumpCasts = true;
            }

            var rf = ReplayFile.Read(path);
            var (state, engine) = rf.BuildStart();
            // v3 carries the real start loadout, so BuildStart is already correct. Only a v2
            // file needs the base-tier guess, because its header holds the END loadout.
            if (!rf.HasV3)
            {
                state.Player1.SetLoadout(new[] { B(rf.P1Off), B(rf.P1Def), B(rf.P1Sig) });
                state.Player2.SetLoadout(new[] { B(rf.P2Off), B(rf.P2Def), B(rf.P2Sig) });
            }

            Console.WriteLine();
            Console.WriteLine("=== REPLAY FIDELITY: " + rf.GameId + "  (format v" + rf.Version + ") ===");
            if (rf.HasV3)
                Console.WriteLine("  v3: map " + rf.Map + (rf.ShadowMap ? " (shadow)" : "") + ", engine seed "
                                + rf.EngineSeed + ", " + rf.GadgetTargets.Count + " recorded gadget targets."
                                + "  EXPECT EXACT REPRODUCTION -- any mismatch below is a real bug.");
            else
                Console.WriteLine("  v2: no map, no engine seed, no gadget targets, END-of-game loadout."
                                + "  Combat WILL drift; only the economy is trustworthy.");
            Console.WriteLine("  recorded: winner P" + rf.Winner + ", " + rf.TickCount + " ticks ("
                            + (rf.TickCount / 30.0).ToString("F0") + "s), start tick " + rf.StartingTick);
            Console.WriteLine("  header (END-of-game) loadout: P1 " + rf.P1Off + "/" + rf.P1Def + "/" + rf.P1Sig
                            + "   P2 " + rf.P2Off + "/" + rf.P2Def + "/" + rf.P2Sig);
            Console.WriteLine("  rebuilt from BASE tier; gadget tiers below must climb to match the header.");
            Console.WriteLine();
            Console.WriteLine("    tick   sec | P1 $      inc   inv rep  hp%  | P2 $      inc   inv rep  hp%  | fails");
            Console.WriteLine("    " + new string('-', 104));

            if (dumpCasts)
                engine.OnGadgetCast += (side, gadgetId, pos) =>
                    Console.WriteLine("CAST," + engine._state.CurrentTick + "," + side + "," + gadgetId);

            int fail1 = 0, fail2 = 0, act1 = 0, act2 = 0;
            int firstFailTick = -1;
            int firstFailSide = 0, firstFailAction = 0;
            var p1 = state.Player1;
            var p2 = state.Player2;

            for (int i = 0; i < rf.TickCount; i++)
            {
                if (rf.A1[i] != 0)
                {
                    act1++;
                    if (!rf.ApplyRecorded(engine, 1, i, rf.A1[i]))
                    {
                        fail1++;
                        if (firstFailTick < 0) { firstFailTick = i; firstFailSide = 1; firstFailAction = rf.A1[i]; }
                    }
                }
                if (rf.A2[i] != 0)
                {
                    act2++;
                    if (!rf.ApplyRecorded(engine, 2, i, rf.A2[i]))
                    {
                        fail2++;
                        if (firstFailTick < 0) { firstFailTick = i; firstFailSide = 2; firstFailAction = rf.A2[i]; }
                    }
                }

                if (i % every == 0 || i == rf.TickCount - 1)
                    Console.WriteLine("    " + i.ToString().PadLeft(5) + (i / 30).ToString().PadLeft(6) + " |"
                        + p1.Money.ToString("F0").PadLeft(8) + p1.Income.ToString("F1").PadLeft(8)
                        + p1.InvestmentCount.ToString().PadLeft(5) + p1.RepairCount.ToString().PadLeft(4)
                        + (100.0 * p1.CastleHealth / Math.Max(p1.CastleMaxHealth, 1)).ToString("F0").PadLeft(5)
                        + "  |" + p2.Money.ToString("F0").PadLeft(8) + p2.Income.ToString("F1").PadLeft(8)
                        + p2.InvestmentCount.ToString().PadLeft(5) + p2.RepairCount.ToString().PadLeft(4)
                        + (100.0 * p2.CastleHealth / Math.Max(p2.CastleMaxHealth, 1)).ToString("F0").PadLeft(5)
                        + "  |" + (fail1 + fail2).ToString().PadLeft(6)
                        + (state.IsGameOver ? "   <-- REBUILD ENDED EARLY (winner P" + state.WinnerSide + ")" : ""));

                if (state.IsGameOver) break;
                engine.Tick();
            }

            Console.WriteLine();
            Console.WriteLine("  CHECK 1 -- recorded actions that FAILED in the rebuild:");
            Console.WriteLine("    P1 " + fail1 + "/" + act1 + "   P2 " + fail2 + "/" + act2
                            + (fail1 + fail2 == 0 ? "   PERFECT" : ""));
            if (firstFailTick >= 0)
                Console.WriteLine("    first failure: tick " + firstFailTick + " ("
                                + (firstFailTick / 30) + "s), P" + firstFailSide
                                + " action " + firstFailAction
                                + "  -- the rebuild is untrustworthy from here on");

            Console.WriteLine();
            Console.WriteLine("  CHECK 2 -- final gadget tiers vs the recorded header:");
            Cmp("P1 offence", state.Player1.OffensiveGadget?.Id, rf.P1Off);
            Cmp("P1 defence", state.Player1.DefensiveGadget?.Id, rf.P1Def);
            Cmp("P1 signature", state.Player1.SignatureGadget?.Id, rf.P1Sig);
            Cmp("P2 offence", state.Player2.OffensiveGadget?.Id, rf.P2Off);
            Cmp("P2 defence", state.Player2.DefensiveGadget?.Id, rf.P2Def);
            Cmp("P2 signature", state.Player2.SignatureGadget?.Id, rf.P2Sig);

            Console.WriteLine();
            Console.WriteLine("  CHECK 3/4 -- final state (compare against the games table by hand):");
            Console.WriteLine("    rebuilt: winner P" + (state.IsGameOver ? state.WinnerSide.ToString() : "-none-")
                            + "  at tick " + state.CurrentTick);
            Console.WriteLine("    P1 income " + p1.Income.ToString("F1") + "  money " + p1.Money.ToString("F0")
                            + "  invest " + p1.InvestmentCount + "  hp "
                            + (100.0 * p1.CastleHealth / Math.Max(p1.CastleMaxHealth, 1)).ToString("F2") + "%");
            Console.WriteLine("    P2 income " + p2.Income.ToString("F1") + "  money " + p2.Money.ToString("F0")
                            + "  invest " + p2.InvestmentCount + "  hp "
                            + (100.0 * p2.CastleHealth / Math.Max(p2.CastleMaxHealth, 1)).ToString("F2") + "%");
        }

        private static void Cmp(string label, string got, string want)
        {
            bool ok = string.Equals(got, want, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine("    " + label.PadRight(14) + (got ?? "null").PadRight(20)
                            + " vs recorded " + (want ?? "null").PadRight(20) + (ok ? "MATCH" : "DIFFERS"));
        }

        private static string B(string id) => string.IsNullOrEmpty(id) ? id : id.Split('_')[0];
    }
}
