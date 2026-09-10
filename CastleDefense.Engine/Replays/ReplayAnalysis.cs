using System;
using System.Collections.Generic;
using System.Linq;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Replays
{
    /// <summary>
    /// REBUILDS A RECORDED GAME AND REPORTS ITS ECONOMY -- the single implementation behind
    /// both `--replay-economy` and the /analysis web pages.
    ///
    /// WHY IT LIVES HERE. Two callers now walk a replay and sample the same numbers, and a
    /// second sampler is exactly the shape of code this project has been burned by: the
    /// webpage and the CLI would quietly disagree about the same game and there would be no
    /// symptom until someone compared them by hand. One class, two front-ends.
    ///
    /// ── WHAT A RECONSTRUCTION IS, AND WHAT IT IS NOT ─────────────────────────────────────
    ///
    /// A .replay stores one action byte per tick and NO STATE. Every number here is
    /// therefore re-simulated, not recorded, and it is only as good as the rules the rebuild
    /// runs under. <see cref="Fidelity"/> is the instrument that says whether to believe it,
    /// and it is computed on every run rather than offered as an option:
    ///
    ///   THE RECORDER ONLY EVER WRITES AN ACTION THE LIVE ENGINE ACCEPTED. So a recorded
    ///   invest was affordable and a recorded cast was off cooldown. The moment ApplyAction
    ///   starts returning false, the rebuild's state has drifted from the real game's and
    ///   nothing after that point means anything.
    ///
    /// That test is not theoretical. Game 1F3C07 was played with freeze's UpgradeCost at 700
    /// and rebuilt against 500: the rebuild upgraded two casts early, PlayerState.AddGadgetXp
    /// stamped freeze_2's 560-tick cooldown at the upgrade, and a recorded cast 389 ticks
    /// later was refused 171 ticks inside it. The rebuild ended 70 seconds before the real
    /// game. From v4 the replay carries a fingerprint of the master CSVs so this is a banner
    /// rather than a mystery -- see <see cref="BalanceStatus"/>.
    /// </summary>
    public static class ReplayAnalysis
    {
        /// <summary>One sampled instant, once per <see cref="Options.SampleEverySeconds"/>.</summary>
        public sealed class Sample
        {
            public int Second;
            public long Tick;
            public double[] Money = new double[3];       // indexed 1/2, 0 unused
            public double[] Income = new double[3];
            public int[] Invest = new int[3];
            public double[] HpPct = new double[3];
            public double[] RepairSpend = new double[3];
            public double[] UnitSpend = new double[3];
            public double[] GadgetSpend = new double[3];
            public int[] AutoSpawnLevel = new int[3];
            public int[] UnitsAlive = new int[3];
        }

        /// <summary>
        /// A moment when the economy lead changed hands.
        ///
        /// THE LEAD IS HIERARCHICAL, which is Marc's definition and not a statistical one:
        /// being ahead on INCOME is being ahead economically, full stop, because income is a
        /// rung on a ladder and a rung is worth more than any amount of cash that has not
        /// bought one. Only when both sides sit on the same rung does money held decide.
        /// </summary>
        public sealed class Highlight
        {
            public int Second;
            public long Tick;
            public int Leader;                 // 1 or 2
            public int PreviousLeader;         // 0 = nobody led yet
            public string Basis;               // "income" or "money"
            public string Headline;
            public double[] Money = new double[3];
            public double[] Income = new double[3];
            public int[] Invest = new int[3];
            /// <summary>What each side actually DID in the window before the swing. Read off
            /// the recorded action stream, so this part is ground truth rather than inference.</summary>
            public List<string> P1Actions = new();
            public List<string> P2Actions = new();
            /// <summary>Populated for singleplayer games only. See <see cref="BotRationale"/>.</summary>
            public BotRationale.Rationale BotThinking;
        }

        /// <summary>
        /// Whether the rebuild reproduced the recorded game. See the class comment: the only
        /// strong test is that no recorded action FAILED.
        /// </summary>
        public sealed class FidelityResult
        {
            public int[] Attempted = new int[3];
            public int[] Failed = new int[3];
            public long FirstFailureTick = -1;
            public int FirstFailureSide;
            public byte FirstFailureAction;
            public bool Perfect => Failed[1] == 0 && Failed[2] == 0;
            /// <summary>Second past which nothing should be believed; -1 when perfect.</summary>
            public int TrustedUntilSecond = -1;
        }

        /// <summary>Does the rebuild's balance data match what the game was PLAYED under?</summary>
        public enum BalanceStatus
        {
            /// <summary>Pre-v4 replay: it recorded no fingerprint, so this cannot be known.</summary>
            Unknown,
            Match,
            GadgetsChanged,
            RosterChanged,
            BothChanged
        }

        public sealed class Options
        {
            public int SampleEverySeconds = 1;
            /// <summary>A new money leader must clear the old one by this fraction before it
            /// counts. Without it two banks a few dollars apart trade the lead every second
            /// and the highlight list becomes noise rather than signal.</summary>
            public double MoneyMarginFraction = 0.05;
            /// <summary>...and by at least this many dollars, which is what stops the opening
            /// seconds (both sides on $12) from emitting a swing per tick.</summary>
            public double MoneyMarginAbsolute = 40;
            /// <summary>A new leader must hold the lead this long before the swing is emitted.</summary>
            public int HoldSeconds = 4;
            /// <summary>How far back to look when listing what caused a swing.</summary>
            public int ActionWindowSeconds = 12;
            /// <summary>Settings profile for the bot rationale; null = no rationale.</summary>
            public HeuristicBotSettings BotSettings;
            /// <summary>Which seat the bot occupied (2 for singleplayer), 0 for none.</summary>
            public int BotSide;
        }

        public sealed class Result
        {
            public string GameId;
            public byte FormatVersion;
            public TeamColour Map;
            public bool ShadowMap;
            public int RecordedWinner;
            public long RecordedTicks;
            public int RebuiltWinner;
            public long RebuiltTicks;
            public List<Sample> Samples = new();
            public List<Highlight> Highlights = new();
            public FidelityResult Fidelity = new();
            public BalanceStatus Balance;
            public string RecordedGadgetsHash = "", RecordedRosterHash = "";
            public string CurrentGadgetsHash = "", CurrentRosterHash = "";
            /// <summary>(second, side, gadget id) for every mid-game gadget upgrade.</summary>
            public List<(int second, int side, string gadgetId)> Upgrades = new();
            public bool[] Armageddon = new bool[3];
            public int[] ArmageddonSecond = { -1, -1, -1 };
            public double[] FinalMoney = new double[3];
            public double[] FinalIncome = new double[3];
        }

        public static Result Run(ReplayFile rf, Options opt = null)
        {
            opt ??= new Options();
            var res = new Result
            {
                GameId = rf.GameId,
                FormatVersion = rf.Version,
                Map = rf.Map,
                ShadowMap = rf.ShadowMap,
                RecordedWinner = rf.Winner,
                RecordedTicks = rf.TickCount,
                RecordedGadgetsHash = rf.GadgetsHash,
                RecordedRosterHash = rf.RosterHash,
                CurrentGadgetsHash = GameDataManager.GadgetsFileHash,
                CurrentRosterHash = GameDataManager.RosterFileHash,
                Balance = Compare(rf)
            };

            var (state, engine) = rf.BuildStart();
            long start = state.CurrentTick;
            int sampleTicks = Math.Max(1, opt.SampleEverySeconds * GameEngine.TICKS_PER_SECOND);

            // Gadget spend is not tracked by the engine (MoneySpentOnUnits covers units
            // only), so total it off the cast event, which fires once per successful cast
            // with the resolved position.
            var gadgetSpend = new double[3];
            engine.OnGadgetCast += (side, id, pos) =>
            {
                var gd = engine.GetGadgetDefinition(id);
                if (gd != null && side >= 1 && side <= 2) gadgetSpend[side] += gd.Cost;
            };
            engine.OnGadgetUpgraded += (side, def) =>
                res.Upgrades.Add(((int)((state.CurrentTick - start) / GameEngine.TICKS_PER_SECOND), side, def.Id));

            // ARMAGEDDON is an invest action taken at the cap, so watch the flag rather than
            // the action byte -- see the note on ReplayFile.P1Armageddon.
            bool[] armaSeen = new bool[3];

            var rationale = opt.BotSettings != null && opt.BotSide is 1 or 2
                          ? new BotRationale(opt.BotSide, opt.BotSettings) : null;
            var pendingStates = new Dictionary<long, BotRationale.Snapshot>();

            while (!state.IsGameOver && state.CurrentTick < GameEngine.MAX_TICKS)
            {
                engine.Tick();
                long i = state.CurrentTick - start;

                if (i >= 0 && i < rf.A1.Length)
                {
                    if (rf.A1[i] != 0) Apply(rf, engine, 1, state, i, res.Fidelity);
                    if (rf.A2[i] != 0) Apply(rf, engine, 2, state, i, res.Fidelity);
                }

                for (int side = 1; side <= 2; side++)
                {
                    var p = side == 1 ? state.Player1 : state.Player2;
                    if (p.ArmageddonUsed && !armaSeen[side])
                    {
                        armaSeen[side] = true;
                        res.Armageddon[side] = true;
                        res.ArmageddonSecond[side] = (int)(i / GameEngine.TICKS_PER_SECOND);
                    }
                }

                if (i % sampleTicks != 0) continue;
                var s = Sampled(state, engine, gadgetSpend, (int)(i / GameEngine.TICKS_PER_SECOND), state.CurrentTick);
                res.Samples.Add(s);
                // The bot rationale needs the BOARD, which a Sample deliberately does not
                // carry (it is serialised to the browser once per second). Snapshot only the
                // extra bits, and only while a rationale was asked for.
                if (rationale != null) pendingStates[s.Tick] = rationale.Capture(state, engine);
            }

            res.RebuiltWinner = state.WinnerSide;
            res.RebuiltTicks = state.CurrentTick - start;
            for (int side = 1; side <= 2; side++)
            {
                var p = side == 1 ? state.Player1 : state.Player2;
                res.FinalMoney[side] = p.Money;
                res.FinalIncome[side] = p.Income;
            }
            if (!res.Fidelity.Perfect && res.Fidelity.FirstFailureTick >= 0)
                res.Fidelity.TrustedUntilSecond =
                    (int)((res.Fidelity.FirstFailureTick - start) / GameEngine.TICKS_PER_SECOND);

            res.Highlights = FindHighlights(res.Samples, opt);
            Describe(res.Highlights, rf, res, opt);
            if (rationale != null)
                foreach (var h in res.Highlights)
                    if (pendingStates.TryGetValue(h.Tick, out var snap))
                        h.BotThinking = rationale.Explain(snap, h);

            return res;
        }

        private static void Apply(ReplayFile rf, GameEngine engine, int side, GameState state,
                                  long i, FidelityResult fid)
        {
            byte a = side == 1 ? rf.A1[i] : rf.A2[i];
            fid.Attempted[side]++;
            bool ok = rf.ApplyRecorded(engine, side, (int)state.CurrentTick, a);
            if (ok) return;
            fid.Failed[side]++;
            if (fid.FirstFailureTick < 0)
            {
                fid.FirstFailureTick = state.CurrentTick;
                fid.FirstFailureSide = side;
                fid.FirstFailureAction = a;
            }
        }

        private static Sample Sampled(GameState state, GameEngine engine, double[] gadgetSpend,
                                      int second, long tick)
        {
            var s = new Sample { Second = second, Tick = tick };
            for (int side = 1; side <= 2; side++)
            {
                var p = side == 1 ? state.Player1 : state.Player2;
                s.Money[side] = p.Money;
                s.Income[side] = p.Income;
                s.Invest[side] = p.InvestmentCount;
                s.HpPct[side] = p.CastleMaxHealth > 0 ? 100.0 * p.CastleHealth / p.CastleMaxHealth : 0;
                s.RepairSpend[side] = LadderCost(p.RepairCount, isRepair: true);
                s.UnitSpend[side] = engine.MoneySpentOnUnits[side];
                s.GadgetSpend[side] = gadgetSpend[side];
                s.AutoSpawnLevel[side] = p.AutoSpawnLevel;
            }
            foreach (var u in state.Units)
                if (u.Side is 1 or 2) s.UnitsAlive[u.Side]++;
            return s;
        }

        /// <summary>
        /// What a given number of rungs COST in total. Neither ladder stores its own spend,
        /// so it is re-walked from a fresh PlayerState, which is also the only way to price
        /// them that cannot drift from the live formula.
        /// </summary>
        public static double LadderCost(int count, bool isRepair)
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

        private static BalanceStatus Compare(ReplayFile rf)
        {
            if (!rf.HasV4 || string.IsNullOrEmpty(rf.GadgetsHash)) return BalanceStatus.Unknown;
            bool g = rf.GadgetsHash != GameDataManager.GadgetsFileHash;
            bool r = !string.IsNullOrEmpty(rf.RosterHash) && rf.RosterHash != GameDataManager.RosterFileHash;
            return (g, r) switch
            {
                (false, false) => BalanceStatus.Match,
                (true, false) => BalanceStatus.GadgetsChanged,
                (false, true) => BalanceStatus.RosterChanged,
                _ => BalanceStatus.BothChanged
            };
        }

        // ── Highlights ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Who is ahead economically at this instant, under the hierarchical rule. Returns 0
        /// when the two are close enough that no one is meaningfully ahead, which the caller
        /// reads as "unchanged" rather than as a swing.
        /// </summary>
        private static int LeaderAt(Sample s, Options opt)
        {
            if (Math.Abs(s.Income[1] - s.Income[2]) > 1e-6)
                return s.Income[1] > s.Income[2] ? 1 : 2;

            double hi = Math.Max(s.Money[1], s.Money[2]);
            double gap = Math.Abs(s.Money[1] - s.Money[2]);
            if (gap < Math.Max(opt.MoneyMarginAbsolute, hi * opt.MoneyMarginFraction)) return 0;
            return s.Money[1] > s.Money[2] ? 1 : 2;
        }

        private static List<Highlight> FindHighlights(List<Sample> samples, Options opt)
        {
            var outp = new List<Highlight>();
            int current = 0;          // the lead as last CONFIRMED
            int candidate = 0;
            int heldFor = 0;

            for (int idx = 0; idx < samples.Count; idx++)
            {
                var s = samples[idx];
                int lead = LeaderAt(s, opt);
                if (lead == 0 || lead == current) { candidate = 0; heldFor = 0; continue; }

                if (lead == candidate) heldFor++;
                else { candidate = lead; heldFor = 1; }

                if (heldFor < opt.HoldSeconds) continue;

                // Confirmed. Attribute the swing to where it STARTED, not to where it was
                // confirmed, or every highlight reads HoldSeconds late.
                var at = samples[Math.Max(0, idx - (opt.HoldSeconds - 1))];
                var h = new Highlight
                {
                    Second = at.Second,
                    Tick = at.Tick,
                    Leader = lead,
                    PreviousLeader = current,
                    Basis = Math.Abs(at.Income[1] - at.Income[2]) > 1e-6 ? "income" : "money"
                };
                Array.Copy(at.Money, h.Money, 3);
                Array.Copy(at.Income, h.Income, 3);
                Array.Copy(at.Invest, h.Invest, 3);
                outp.Add(h);
                current = lead;
                candidate = 0;
                heldFor = 0;
            }
            return outp;
        }

        /// <summary>
        /// Fills in the human-readable half of each highlight: the headline, and what each
        /// side actually did in the window leading up to it. The actions are read off the
        /// recorded stream rather than inferred, so that part is exact.
        /// </summary>
        private static void Describe(List<Highlight> hs, ReplayFile rf, Result res, Options opt)
        {
            foreach (var h in hs)
            {
                int endTick = (int)h.Tick;
                int startTick = Math.Max(0, endTick - opt.ActionWindowSeconds * GameEngine.TICKS_PER_SECOND);
                long baseTick = rf.StartingTick;
                // INCLUSIVE OF endTick. The sample is taken at that tick and the recorded
                // action for it is applied in the same pass, so an exclusive bound drops the
                // one action most likely to have CAUSED the swing -- which it did: game
                // 12B538's swing at 0:04 is the invest at tick 120, and the window read
                // t < 120 and reported "P2 did nothing".
                for (long t = startTick - baseTick; t <= endTick - baseTick && t < rf.A1.Length; t++)
                {
                    if (t < 0) continue;
                    if (rf.A1[t] != 0) h.P1Actions.Add(ActionName(rf.A1[t], 1, rf, t + baseTick));
                    if (rf.A2[t] != 0) h.P2Actions.Add(ActionName(rf.A2[t], 2, rf, t + baseTick));
                }
                h.P1Actions = Condense(h.P1Actions);
                h.P2Actions = Condense(h.P2Actions);

                string who = h.Leader == 1 ? "P1" : "P2";
                string lost = h.PreviousLeader == 0 ? null : (h.PreviousLeader == 1 ? "P1" : "P2");
                if (h.Basis == "income")
                {
                    h.Headline = lost == null
                        ? $"{who} takes the economy lead — first to income rung {Math.Max(h.Invest[1], h.Invest[2])} (${Math.Max(h.Income[1], h.Income[2]):N0}/s)"
                        : $"{who} takes the economy lead from {lost} — up an income rung, ${Math.Max(h.Income[1], h.Income[2]):N0}/s against ${Math.Min(h.Income[1], h.Income[2]):N0}/s";
                }
                else
                {
                    double mine = h.Money[h.Leader], theirs = h.Money[h.Leader == 1 ? 2 : 1];
                    h.Headline = lost == null
                        ? $"{who} takes the economy lead — level on income, ${mine:N0} banked against ${theirs:N0}"
                        : $"{who} takes the economy lead from {lost} — same income rung, but ${mine:N0} banked against ${theirs:N0}";
                }

                // A SWING CAUSED BY ARMAGEDDON IS NOT AN ECONOMIC EVENT, and reading it as
                // one inverts it. The buyer's bank drops to nothing, so the other side
                // "takes the economy lead" on the very tick it starts losing the game. Say
                // which it was.
                for (int side = 1; side <= 2; side++)
                {
                    int at = res.ArmageddonSecond[side];
                    if (at < 0 || at > h.Second || at < h.Second - opt.ActionWindowSeconds) continue;
                    h.Headline += $" — but P{side} has just spent everything on ARMAGEDDON";
                }
            }
        }

        private static List<string> Condense(List<string> raw)
        {
            var outp = new List<string>();
            foreach (var g in raw.GroupBy(x => x).OrderByDescending(g => g.Count()))
                outp.Add(g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key);
            return outp;
        }

        private static string ActionName(byte a, int side, ReplayFile rf, long tick)
        {
            switch (a)
            {
                case 9: return "invest";
                case 10: return "repair";
                case 14: return "auto-spawner upgrade";
                case >= 1 and <= 8: return $"spawn tier {a}";
                case >= 11 and <= 13:
                    var p = side == 1
                        ? new[] { rf.P1StartOff, rf.P1StartDef, rf.P1StartSig }
                        : new[] { rf.P2StartOff, rf.P2StartDef, rf.P2StartSig };
                    string fam = p[a - 11];
                    if (string.IsNullOrEmpty(fam)) fam = a == 11 ? "offence" : a == 12 ? "defence" : "signature";
                    return $"cast {fam.Split('_')[0]}";
                default: return $"action {a}";
            }
        }
    }
}
