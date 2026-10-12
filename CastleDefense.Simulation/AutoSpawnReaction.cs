using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;
using CastleDefense.Engine.Replays;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// Does a human buying one or two auto-spawner levels provoke an outsized, sustained
    /// spending reaction from HeuristicBot? Marc's report, 2026-09-10: "I will spend $100 on
    /// the auto-spawner and instead of responding with its own auto-spawner the bot will
    /// spend hundreds of dollars on defensive units and gadgets."
    ///
    /// Three modes, from weakest to strongest evidence:
    ///
    ///   recorded  <ids...>     Reconstructs real games (both recorded streams) and reports
    ///                          what the bot spent around Marc's auto-spawner purchases.
    ///                          Ground truth, but confounded: the game changes for reasons
    ///                          other than the machine.
    ///   gauntlet  <id>         CAUSAL. Plays the live bot against Marc's recorded stream
    ///                          twice per seed -- as recorded, and with every auto-spawner
    ///                          purchase (action 14) deleted. Same engine seed in both arms,
    ///                          so the difference is the machine and nothing else.
    ///   synthetic              ISOLATED. The human is a script that invests and does
    ///                          nothing else, except buy auto-spawner levels 0/1/2 at a set
    ///                          time. Nothing else on the board can explain a reaction.
    ///
    /// Every dollar either side spends is attributed by DELTA around the call that spent it
    /// (MoneySpentOnUnits, RepairCount, InvestmentCount, AutoSpawnLevel, ArmageddonUsed, and
    /// OnGadgetCast by slot), so the ledger is exact rather than inferred from action ids --
    /// a recorded action id is an attempt, not a purchase.
    /// </summary>
    public static class AutoSpawnReaction
    {
        // ── the ledger ─────────────────────────────────────────────────────────────────
        private const int UNITS = 0, OFF = 1, DEF = 2, SIG = 3, REPAIR = 4, INVEST = 5, AUTO = 6,
                          ARMA = 7, REACTIVE = 8, WIPER = 9, ATTACK = 10, CHIP = 11, N = 12;
        private static readonly string[] Names =
            { "units", "offGadget", "defGadget", "sigGadget", "repair", "invest", "autoSpawn", "ARMAGEDDON",
              "  u:reactive", "  u:wiper", "  u:attack", "  u:chip" };

        /// <summary>"Reaction" spend: everything that is not economy. Units, all gadgets, repairs.</summary>
        private static double Reaction(double[] l) => l[UNITS] + l[OFF] + l[DEF] + l[SIG] + l[REPAIR];

        private sealed class Snap
        {
            public double Units; public int Repairs, Invests, Auto; public bool Arma;
            public double RepairPrice, InvestPrice, AutoPrice;
            public double Reactive, Wiper, Attack, Chip;
        }

        private static Snap Take(GameEngine e, int side, HeuristicBot bot = null)
        {
            var p = side == 1 ? e._state.Player1 : e._state.Player2;
            return new Snap
            {
                Units = e.MoneySpentOnUnits[side], Repairs = p.RepairCount, Invests = p.InvestmentCount,
                Auto = p.AutoSpawnLevel, Arma = p.ArmageddonUsed, RepairPrice = p.RepairPrice,
                InvestPrice = p.InvestmentPrice, AutoPrice = p.AutoSpawnPrice,
                Reactive = bot?.SpendReactive ?? 0, Wiper = bot?.SpendWiper ?? 0,
                Attack = bot?.SpendAttack ?? 0, Chip = bot?.SpendChipBlock ?? 0,
            };
        }

        private static void Charge(double[] l, Snap a, Snap b)
        {
            l[UNITS] += b.Units - a.Units;
            if (b.Repairs > a.Repairs) l[REPAIR] += a.RepairPrice;
            if (b.Invests > a.Invests) l[INVEST] += a.InvestPrice;
            if (b.Arma && !a.Arma) l[ARMA] += a.InvestPrice;
            if (b.Auto > a.Auto) l[AUTO] += a.AutoPrice;
            l[REACTIVE] += b.Reactive - a.Reactive; l[WIPER] += b.Wiper - a.Wiper;
            l[ATTACK] += b.Attack - a.Attack; l[CHIP] += b.Chip - a.Chip;
        }

        private static int SlotOf(PlayerState p, string gadgetId)
        {
            string fam = gadgetId.Split('_')[0];
            if (p.OffensiveGadget != null && p.OffensiveGadget.Id.Split('_')[0] == fam) return OFF;
            if (p.SignatureGadget != null && p.SignatureGadget.Id.Split('_')[0] == fam) return SIG;
            return DEF;
        }

        private static void WireGadgets(GameEngine e, double[][] ledgers, int[][] casts = null)
        {
            e.OnGadgetCast += (side, gid, pos) =>
            {
                if (side < 1 || side > 2) return;
                var gd = e.GetGadgetDefinition(gid);
                var p = side == 1 ? e._state.Player1 : e._state.Player2;
                int slot = SlotOf(p, gid);
                if (gd != null) ledgers[side][slot] += gd.Cost;
                if (casts != null) casts[side][slot]++;
            };
        }

        public static void Run(string[] args, string recordingsDir)
        {
            GameDataManager.Initialize();
            // A binary built outside the repo (e.g. `-o` elsewhere, to dodge the MSB3027 lock
            // while the game server is running) cannot find the recordings by walking up.
            int ri = Array.IndexOf(args, "--recordings");
            if (ri >= 0 && ri + 1 < args.Length)
            {
                recordingsDir = args[ri + 1];
                args = args.Where((_, k) => k != ri && k != ri + 1).ToArray();
            }
            string mode = args.Length > 1 ? args[1] : "";
            switch (mode)
            {
                case "recorded": Recorded(args, recordingsDir); break;
                case "gauntlet": Gauntlet(args, recordingsDir); break;
                case "synthetic": Synthetic(args); break;
                case "duel": Duel(args); break;
                case "profile-diff":
                {
                    // Profiles are spelled out field by field (init-only properties cannot be
                    // copied), so "the candidate is the shipped profile plus one flag" is a claim
                    // that has to be checked, not assumed.
                    var a = ResolveSettings(args.Length > 2 ? args[2] : "EconomyBrakeAutoSpawnProfile");
                    var b = ResolveSettings(args.Length > 3 ? args[3] : "ShipMatchAutoSpawn");
                    if (a == null || b == null) break;
                    int diffs = 0;
                    foreach (var p in typeof(HeuristicBotSettings).GetProperties())
                    {
                        var va = p.GetValue(a); var vb = p.GetValue(b);
                        if (!Equals(va, vb)) { Console.WriteLine($"  {p.Name,-40} {va,-12} -> {vb}"); diffs++; }
                    }
                    Console.WriteLine($"{diffs} field(s) differ.");
                    break;
                }
                default:
                    Console.WriteLine("usage: --autospawn-reaction recorded <id...>\n" +
                                      "       --autospawn-reaction gauntlet <id> [--games N] [--seed S] [--variant P] [--window SEC]\n" +
                                      "       --autospawn-reaction synthetic [--games N] [--levels 0,1,2] [--at SEC] [--variant P]\n" +
                                      "                                      [--human Team,off,def,sig] [--bot Team,off,def,sig] [--window SEC] [--trace]");
                    break;
            }
        }

        // ── 1. RECORDED ────────────────────────────────────────────────────────────────
        private static void Recorded(string[] args, string recordingsDir)
        {
            int window = 60;
            var ids = new List<string>();
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--window" && i + 1 < args.Length) window = int.Parse(args[++i]);
                else ids.Add(args[i]);
            }

            Console.WriteLine($"=== RECORDED GAMES: bot spend around the human's auto-spawner purchases (window {window}s) ===");
            Console.WriteLine("reaction = units + all three gadgets + repairs. income = what the bot EARNED in the same window.\n");
            foreach (var id in ids)
            {
                string path = Path.Combine(recordingsDir, "singleplayer", id + ".replay");
                if (!File.Exists(path)) { Console.WriteLine($"{id}: no replay"); continue; }
                var rf = ReplayFile.Read(path);
                var (state, engine) = rf.BuildStart();
                long start = state.CurrentTick;
                var led = new[] { new double[N], new double[N], new double[N] };
                WireGadgets(engine, led);

                var cum = new List<double[]>();      // bot cumulative ledger per second
                var earned = new List<double>();     // bot cumulative income per second
                var p1Units = new List<int>();       // P1 units within 700 of the bot castle, per second
                double botEarned = 0;
                var buys = new List<(double sec, int level, double price)>();
                int[] failed = new int[3], tried = new int[3];

                while (!state.IsGameOver && state.CurrentTick < GameEngine.MAX_TICKS)
                {
                    // ReplayFile.Step owns the order; the hooks run before the tick, so
                    // CurrentTick in them is the tick the action was recorded at.
                    Snap s0 = default;
                    rf.Step(engine,
                        before: (side, a) => { s0 = Take(engine, side); tried[side]++; },
                        after: (side, a, ok) =>
                        {
                            if (!ok) failed[side]++;
                            var s1 = Take(engine, side);
                            Charge(led[side], s0, s1);
                            if (side == 1 && s1.Auto > s0.Auto) buys.Add(((state.CurrentTick - start) / 30.0, s1.Auto, s0.AutoPrice));
                        });
                    long i = state.CurrentTick - start;
                    if (state.CurrentTick % 30 == 0) botEarned += state.Player2.Income;
                    if (i % 30 == 0)
                    {
                        cum.Add((double[])led[2].Clone());
                        earned.Add(botEarned);
                        p1Units.Add(state.Units.Count(u => u.Side == 1 && Math.Abs(u.Position - 1800) < 700));
                    }
                }

                Console.WriteLine($"--- {rf.GameId}  P1 {rf.P1Team} {rf.P1StartOff}/{rf.P1StartDef}/{rf.P1StartSig} vs bot {rf.P2Team}  " +
                                  $"{(state.CurrentTick - start) / 30.0:F0}s winner P{state.WinnerSide} (recorded P{rf.Winner})  " +
                                  $"fidelity P1 {tried[1] - failed[1]}/{tried[1]} P2 {tried[2] - failed[2]}/{tried[2]}");
                Console.WriteLine($"    human auto-spawner: {(buys.Count == 0 ? "never bought" : string.Join(", ", buys.Select(b => $"L{b.level} @{b.sec:F0}s ${b.price:N0}")))}");
                Console.WriteLine($"    human total: units ${led[1][UNITS]:N0}  gadgets ${led[1][OFF] + led[1][DEF] + led[1][SIG]:N0}  " +
                                  $"repairs ${led[1][REPAIR]:N0}  auto ${led[1][AUTO]:N0}");
                Console.WriteLine($"    bot   total: units ${led[2][UNITS]:N0}  off ${led[2][OFF]:N0}  def ${led[2][DEF]:N0}  sig ${led[2][SIG]:N0}  " +
                                  $"repairs ${led[2][REPAIR]:N0}  invest ${led[2][INVEST]:N0}  auto ${led[2][AUTO]:N0}  ARMA ${led[2][ARMA]:N0}");
                if (buys.Count > 0)
                {
                    int t0 = (int)buys[0].sec;
                    PrintWindow("before first buy", cum, earned, p1Units, Math.Max(0, t0 - window), t0);
                    PrintWindow("after  first buy", cum, earned, p1Units, t0, Math.Min(cum.Count - 1, t0 + window));
                }
                // Always print a fixed early-game window too, so games with and without a
                // machine can be compared at the same phase.
                PrintWindow("0-90s", cum, earned, p1Units, 0, Math.Min(cum.Count - 1, 90));
                PrintWindow("90-180s", cum, earned, p1Units, Math.Min(cum.Count - 1, 90), Math.Min(cum.Count - 1, 180));
                Console.WriteLine();
            }
        }

        private static void PrintWindow(string label, List<double[]> cum, List<double> earned, List<int> near, int a, int b)
        {
            if (b <= a || b >= cum.Count) return;
            var d = new double[N];
            for (int k = 0; k < N; k++) d[k] = cum[b][k] - cum[a][k];
            double inc = earned[b] - earned[a];
            double avgNear = near.Skip(a).Take(b - a).DefaultIfEmpty(0).Average();
            Console.WriteLine($"    [{label,-16} {a,3}-{b,3}s] bot reaction ${Reaction(d),7:N0} of ${inc,7:N0} earned ({100 * Reaction(d) / Math.Max(1, inc),5:F0}%)  " +
                              $"units {d[UNITS],6:N0} off {d[OFF],5:N0} def {d[DEF],5:N0} sig {d[SIG],5:N0} rep {d[REPAIR],5:N0}  " +
                              $"invest {d[INVEST],6:N0}   human units near bot castle {avgNear:F1}");
        }

        // ── 2. GAUNTLET (causal) ───────────────────────────────────────────────────────
        private sealed class ArmResult
        {
            public double[] Led = new double[N];
            public double[] Win = new double[N];       // spend inside the reaction window
            public double WinEarned, Earned;
            public double DangerTicks, Ticks, WinDangerTicks, WinTicks;
            public double BotMoney, BotWins, HumanWins, BotInvest, HumanInvest, HumanAuto, BotAuto, BotHp, HumanHp;
            public double[] InvestAt = new double[10];  // seconds to reach rung k (sum), and count
            public int[] InvestAtN = new int[10];
            public long Tried, Landed;
            public int[] Casts = new int[4];
            public int Games;
            // Why each bot gadget cast was allowed, keyed "slot family: reason".
            public SortedDictionary<string, double[]> CastWhy = new SortedDictionary<string, double[]>();
            public double[] HumanAt = new double[10];
            public double BotArmaAt; public int BotArmaN, CastleLost;
            public int[] HumanAtN = new int[10];
            // FRIENDLY FIRE from the bot's OWN firebomb (ARMAGEDDON's random firebombs excluded).
            public double FfUnits, FfValue, FfDamage, FfDeaths, FfDeathValue, EnemyBurnDamage, FirebombCasts;
            public double FfOnField, FfPaidAfter, FfFreeAfter;
            public double BotGadgetLevelOff;   // offensive gadget level reached (XP proxy)
            public double BlockerLevels, FireHeld;   // how often flags 26 / 27 actually fired
            public double LossToArma, LossOther, BotArmaGames, BotCastleHpLost;   // HOW the bot lost
        }

        /// <summary>
        /// Per-game instrumentation that does not touch the bot: re-derives, AT THE MOMENT OF
        /// EACH CAST, which of the bot's justifications held. The offensive value test is
        /// `enemyValue >= cost || Income >= 50` (TargetValueJustified), and the drain cap is
        /// lifted by `TTD < DrainCapTtdSeconds` or `enemyValue >= cost` -- so a cast that
        /// passed ONLY on the income carve-out is one that no target justified.
        /// Enemy value is priced at ROSTER cost, exactly as the bot prices it, which is the
        /// point: a free auto-spawned unit is valued as if the opponent had paid for it.
        /// </summary>
        private sealed class Probe
        {
            private readonly GameEngine _e; private readonly ArmResult _r; private readonly int _bs;
            private readonly double[] _humanAt = new double[10];
            // Every bot unit's spawn tick and whether it was PAID (appeared during the bot's own
            // Update) or FREE (appeared during engine.Tick: reinforcements, auto-spawner, squad).
            private readonly Dictionary<Guid, (long tick, bool paid)> _spawn = new Dictionary<Guid, (long, bool)>();
            private readonly HashSet<Guid> _ignited = new HashSet<Guid>();
            private HashSet<Guid> _burningNow = new HashSet<Guid>();
            private HashSet<Guid> _beforeBot = new HashSet<Guid>();
            private readonly Dictionary<Guid, double> _cost = new Dictionary<Guid, double>();
            private long _lastFirebombCast = long.MinValue;
            public Probe(GameEngine e, HeuristicBot bot, ArmResult r, int botSide = 2)
            {
                _e = e; _r = r; _bs = botSide; Bot = bot;
                for (int k = 0; k < 10; k++) _humanAt[k] = -1;
                e.OnGadgetCast += (side, gid, pos) =>
                {
                    if (side != _bs) return;
                    var gd = e.GetGadgetDefinition(gid);
                    if (gd == null) return;
                    var me = _bs == 1 ? e._state.Player1 : e._state.Player2;
                    string fam = gid.Split('_')[0];
                    if (fam == "firebomb" && !me.ArmageddonUsed) { _lastFirebombCast = e._state.CurrentTick; r.FirebombCasts++; }
                    int slot = SlotOf(me, gid);
                    string why;
                    if (slot == OFF)
                    {
                        var enemies = e._state.Units.Where(u => u.Side != _bs).ToList();
                        double val = CastleDefense.Engine.Gadgets.GadgetTargeting.ValueNear(e, enemies, pos, Math.Max(150, gd.Radius));
                        why = val >= gd.Cost ? "value >= cost"
                            : me.Income >= 50 ? "ONLY the Income>=50 carve-out"
                            : "neither (?)";
                        if (bot.LastTimeToDeathSeconds < 5) why += ", TTD<5";
                    }
                    else why = bot.LastDecisionWasDanger ? "in danger" : "not in danger";
                    string key = $"{(slot == OFF ? "off" : slot == DEF ? "def" : "sig")} {fam,-14} {why}";
                    if (!r.CastWhy.TryGetValue(key, out var acc)) r.CastWhy[key] = acc = new double[2];
                    acc[0]++; acc[1] += gd.Cost;
                };
            }
            public void BeforeBot()
            {
                _beforeBot.Clear();
                foreach (var u in _e._state.Units) if (u.Side == _bs) _beforeBot.Add(u.InstanceId);
            }
            public void AfterBot()
            {
                long t = _e._state.CurrentTick;
                foreach (var u in _e._state.Units)
                    if (u.Side == _bs && !_beforeBot.Contains(u.InstanceId) && !_spawn.ContainsKey(u.InstanceId))
                        _spawn[u.InstanceId] = (t, !u.DefinitionId.StartsWith("wall"));
            }
            public void AfterTick()
            {
                var st = _e._state;
                long t = st.CurrentTick;
                var me = _bs == 1 ? st.Player1 : st.Player2;
                foreach (var u in st.Units)
                    if (u.Side == _bs && !_spawn.ContainsKey(u.InstanceId)) _spawn[u.InstanceId] = (t, false);
                // Burn is applied by ProcessStatuses, which runs only on ticks divisible by 5.
                if (t % 5 != 0 || me.ArmageddonUsed) return;
                var now = new HashSet<Guid>();
                foreach (var u in st.Units)
                {
                    foreach (var s in u.Statuses)
                    {
                        if (s.Name != "Burn" || s.Side != _bs || s.SourceGadgetId == null
                            || !s.SourceGadgetId.StartsWith("firebomb")) continue;
                        if (u.Side != _bs) { _r.EnemyBurnDamage += s.Value; continue; }
                        _r.FfDamage += s.Value;
                        now.Add(u.InstanceId);
                        if (_ignited.Add(u.InstanceId))
                        {
                            double c = CastleDefense.Engine.Gadgets.GadgetTargeting.UnitCost(_e, u);
                            _cost[u.InstanceId] = c;
                            _r.FfUnits++; _r.FfValue += c;
                            (long tick, bool paid) sp = _spawn.TryGetValue(u.InstanceId, out var v) ? v : (t, false);
                            if (sp.tick < _lastFirebombCast) _r.FfOnField++;
                            else if (sp.paid) _r.FfPaidAfter++;
                            else _r.FfFreeAfter++;
                        }
                    }
                }
                // A unit that was burning from our own fire last pass and is gone now died
                // while burning -- combat may share the blame, so read this as an upper bound.
                var alive = new HashSet<Guid>(st.Units.Select(x => x.InstanceId));
                foreach (var id in _burningNow)
                    if (!alive.Contains(id)) { _r.FfDeaths++; _r.FfDeathValue += _cost.TryGetValue(id, out var c) ? c : 0; }
                _burningNow = now;
            }
            public void Tick(long i)
            {
                var human = _bs == 1 ? _e._state.Player2 : _e._state.Player1;
                int inv = human.InvestmentCount;
                if (inv < 10 && _humanAt[inv] < 0) _humanAt[inv] = i / 30.0;
            }
            public HeuristicBot Bot;
            public void Finish()
            {
                if (Bot != null) { _r.BlockerLevels += Bot.BlockerAutoSpawnLevelsBought; _r.FireHeld += Bot.FireHeldSpawns; }
                for (int k = 0; k < 10; k++) if (_humanAt[k] >= 0) { _r.HumanAt[k] += _humanAt[k]; _r.HumanAtN[k]++; }
                var me = _bs == 1 ? _e._state.Player1 : _e._state.Player2;
                _r.BotGadgetLevelOff += me.OffensiveGadget?.Level ?? 0;
            }
        }

        private static void Gauntlet(string[] args, string recordingsDir)
        {
            string id = args.Length > 2 ? args[2] : null;
            int games = 40, seed = 4242, window = 60;
            string variant = "EconomyBrakeAutoSpawnProfile";
            for (int i = 3; i < args.Length; i++)
            {
                if (args[i] == "--games" && i + 1 < args.Length) games = int.Parse(args[++i]);
                else if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
                else if (args[i] == "--variant" && i + 1 < args.Length) variant = args[++i];
                else if (args[i] == "--window" && i + 1 < args.Length) window = int.Parse(args[++i]);
            }
            var settings = ResolveSettings(variant);
            if (settings == null) return;
            string path = Path.Combine(recordingsDir, "singleplayer", id + ".replay");
            if (!File.Exists(path)) { Console.WriteLine($"No replay at {path}"); return; }
            var rf = ReplayFile.Read(path);
            int keep = 2;
            bool maps = false;
            for (int i = 3; i < args.Length; i++)
            {
                if (args[i] == "--keep" && i + 1 < args.Length) keep = int.Parse(args[++i]);
                else if (args[i] == "--maps") maps = true;
            }

            // B deletes every press; C keeps only the first `keep` -- "a level or two".
            var stripped = (byte[])rf.A1.Clone();
            var firstK = (byte[])rf.A1.Clone();
            int t0 = -1, nAuto = 0;
            for (int i = 0; i < stripped.Length; i++)
                if (stripped[i] == 14)
                {
                    if (t0 < 0) t0 = i;
                    stripped[i] = 0;
                    if (++nAuto > keep) firstK[i] = 0;
                }
            if (t0 < 0) { Console.WriteLine($"{id}: the human never pressed the auto-spawner; nothing to strip."); return; }

            Console.WriteLine($"=== AUTO-SPAWNER COUNTERFACTUAL GAUNTLET -- {rf.GameId} ===");
            Console.WriteLine($"  human P1 {rf.P1Team} {rf.P1StartOff}/{rf.P1StartDef}/{rf.P1StartSig}   bot P2 {rf.P2Team} " +
                              $"{rf.P2StartOff}/{rf.P2StartDef}/{rf.P2StartSig}   profile {variant}");
            Console.WriteLine($"  recorded stream has {nAuto} auto-spawner presses, first at {t0 / 30.0:F0}s. " +
                              $"B deletes them all, C keeps only the first {keep}; the rest of the stream is untouched.");
            Console.WriteLine($"  {games} paired seeds{(maps ? ", map swept over all 8 (the engine seed alone barely varies a game)" : "")}.\n");

            var armA = new ArmResult(); var armB = new ArmResult(); var armC = new ArmResult();
            var rng = new Random(seed);
            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            for (int g = 0; g < games; g++)
            {
                int es = rng.Next();
                TeamColour? map = maps ? teams[g % teams.Length] : null;
                PlayGauntlet(rf, rf.A1, es, map, settings, t0, t0 + window * 30, armA);
                PlayGauntlet(rf, stripped, es, map, settings, t0, t0 + window * 30, armB);
                PlayGauntlet(rf, firstK, es, map, settings, t0, t0 + window * 30, armC);
            }
            Report(new[] { ("A: as recorded", armA), ($"C: first {keep} only", armC), ("B: machine deleted", armB) },
                   $"window = {t0 / 30.0:F0}s .. {t0 / 30.0 + window:F0}s (first auto-spawner press onward)");
        }

        private static void PlayGauntlet(ReplayFile rf, byte[] stream, int engineSeed, TeamColour? map,
                                         HeuristicBotSettings settings, long winStart, long winEnd, ArmResult r)
        {
            var (state, engine) = rf.BuildStart(engineSeed, map);
            long start = state.CurrentTick;
            var led = new[] { new double[N], new double[N], new double[N] };
            var casts = new[] { new int[4], new int[4], new int[4] };
            WireGadgets(engine, led, casts);
            var bot = new HeuristicBot(2, settings);
            var probe = new Probe(engine, bot, r);
            double[] winStartLed = null; double earned = 0, winEarnedStart = 0;
            var investAt = new double[10];
            for (int k = 0; k < 10; k++) investAt[k] = -1;

            var arm = rf.WithActions(stream, rf.A2);
            while (!state.IsGameOver && state.CurrentTick < GameEngine.MAX_TICKS)
            {
                // The live loop's order (ReplayFile.Step): the bot decides at tick d, then the
                // human's click for d lands, then the tick. This used to Tick first and apply
                // the human BEFORE the bot, which showed the bot a click it could not have seen.
                long d = state.CurrentTick - start;
                var b0 = Take(engine, 2, bot);
                probe.BeforeBot();
                bot.Update(engine);
                probe.AfterBot();
                Charge(led[2], b0, Take(engine, 2, bot));

                Snap s0 = default;
                arm.Step(engine, p2: false, reaim: true,
                         before: (_, _) => s0 = Take(engine, 1),
                         after: (_, _, ok) => { r.Tried++; if (ok) r.Landed++; Charge(led[1], s0, Take(engine, 1)); });
                probe.AfterTick();

                long i = state.CurrentTick - start;
                if (state.CurrentTick % 30 == 0) earned += state.Player2.Income;
                if (i == winStart) { winStartLed = (double[])led[2].Clone(); winEarnedStart = earned; }
                if (i == winEnd && winStartLed != null)
                {
                    for (int k = 0; k < N; k++) r.Win[k] += led[2][k] - winStartLed[k];
                    r.WinEarned += earned - winEarnedStart;
                }

                r.Ticks++; if (bot.LastDecisionWasDanger) r.DangerTicks++;
                if (d >= winStart && d < winEnd) { r.WinTicks++; if (bot.LastDecisionWasDanger) r.WinDangerTicks++; }
                int inv = state.Player2.InvestmentCount;
                if (inv < 10 && investAt[inv] < 0) investAt[inv] = d / 30.0;
                probe.Tick(d);
            }

            for (int k = 0; k < N; k++) r.Led[k] += led[2][k];
            r.Earned += earned;
            if (state.WinnerSide == 2) r.BotWins++; else if (state.WinnerSide == 1) r.HumanWins++;
            r.BotInvest += state.Player2.InvestmentCount; r.HumanInvest += state.Player1.InvestmentCount;
            r.BotAuto += state.Player2.AutoSpawnLevel; r.HumanAuto += state.Player1.AutoSpawnLevel;
            r.BotHp += Pct(state.Player2); r.HumanHp += Pct(state.Player1); r.BotMoney += state.Player2.Money;
            for (int k = 0; k < 10; k++) if (investAt[k] >= 0) { r.InvestAt[k] += investAt[k]; r.InvestAtN[k]++; }
            for (int k = 1; k <= 3; k++) r.Casts[k] += casts[2][k];
            probe.Finish();
            r.Games++;
        }

        private static void Report((string label, ArmResult r)[] arms, string windowNote)
        {
            string H(string s) => s.PadLeft(20);
            Console.WriteLine("  per game, averaged                  " + string.Join("", arms.Select(a => H(a.label))));
            void Row(string name, Func<ArmResult, string> f) =>
                Console.WriteLine($"  {name,-34}" + string.Join("", arms.Select(a => H(f(a.r)))));
            Row("bot win rate", r => $"{100 * r.BotWins / r.Games:F1}%");
            Row("human win rate", r => $"{100 * r.HumanWins / r.Games:F1}%");
            Row("human auto-spawner level", r => $"{r.HumanAuto / r.Games:F2}");
            Row("human fidelity", r => $"{100.0 * r.Landed / Math.Max(1, r.Tried):F1}%");
            Row("bot end HP%", r => $"{r.BotHp / r.Games:F1}");
            Row("bot investments", r => $"{r.BotInvest / r.Games:F2}");
            Row("bot money in hand at end", r => $"${r.BotMoney / r.Games:N0}");
            Row("bot auto-spawner level", r => $"{r.BotAuto / r.Games:F2}");
            Row("bot in DANGER mode (time share)", r => $"{100 * r.DangerTicks / Math.Max(1, r.Ticks):F1}%");
            if (arms.Any(a => a.r.LossToArma + a.r.LossOther > 0))
            {
                Row("losses: opponent ARMAGEDDON", r => $"{100 * r.LossToArma / r.Games:F1}%");
                Row("losses: anything else", r => $"{100 * r.LossOther / r.Games:F1}%");
                Row("bot bought ARMAGEDDON", r => $"{100 * r.BotArmaGames / r.Games:F1}%");
            }
            if (arms.Any(a => a.r.BotArmaN > 0 || a.r.CastleLost > 0))
            {
                Row("bot reached ARMAGEDDON", r => $"{100.0 * r.BotArmaN / r.Games:F0}%");
                Row("  ...at (mean)", r => r.BotArmaN == 0 ? "-" : $"{r.BotArmaAt / r.BotArmaN:F1}s");
                Row("bot castle LOST to the stream", r => $"{100.0 * r.CastleLost / r.Games:F0}%");
                Row("human ARMAGEDDON-ready (rung8+48.5s)", r => r.HumanAtN[8] == 0 ? "-"
                    : $"{r.HumanAt[8] / r.HumanAtN[8] + 121221 / 2500.0:F1}s");
            }
            Console.WriteLine();
            Console.WriteLine("  WHOLE GAME, bot spend by category");
            for (int k = 0; k < N; k++) Row(Names[k], r => $"${r.Led[k] / r.Games:N0}");
            Row("REACTION (units+gadgets+repair)", r => $"${Reaction(r.Led) / r.Games:N0}");
            Row("income earned", r => $"${r.Earned / r.Games:N0}");
            Row("gadget casts off/def/sig", r => $"{r.Casts[1] / (double)r.Games:F1}/{r.Casts[2] / (double)r.Games:F1}/{r.Casts[3] / (double)r.Games:F1}");
            Console.WriteLine();
            Console.WriteLine($"  REACTION WINDOW -- {windowNote}");
            for (int k = 0; k < N; k++) Row(Names[k], r => $"${r.Win[k] / r.Games:N0}");
            Row("REACTION (units+gadgets+repair)", r => $"${Reaction(r.Win) / r.Games:N0}");
            Row("income earned in window", r => $"${r.WinEarned / r.Games:N0}");
            Row("reaction as % of income", r => $"{100 * Reaction(r.Win) / Math.Max(1, r.WinEarned):F0}%");
            Row("DANGER mode share in window", r => $"{100 * r.WinDangerTicks / Math.Max(1, r.WinTicks):F1}%");
            Console.WriteLine();
            Console.WriteLine("  seconds to reach investment rung k (mean over games that reached it; n in brackets)");
            for (int k = 1; k <= 8; k++)
                Row($"rung {k}", r => r.InvestAtN[k] == 0 ? "-" : $"{r.InvestAt[k] / r.InvestAtN[k]:F0}s ({r.InvestAtN[k]})");
            Console.WriteLine();
            Console.WriteLine("  ...and the HUMAN's rung times (what the machine cost them in the race)");
            for (int k = 1; k <= 8; k++)
                Row($"human rung {k}", r => r.HumanAtN[k] == 0 ? "-" : $"{r.HumanAt[k] / r.HumanAtN[k]:F0}s ({r.HumanAtN[k]})");
            Console.WriteLine();
            Console.WriteLine("  FRIENDLY FIRE -- the bot's OWN firebomb burning its OWN units (per game; ARMAGEDDON excluded)");
            Row("firebomb casts", r => $"{r.FirebombCasts / r.Games:F1}");
            Row("own units ignited", r => $"{r.FfUnits / r.Games:F1}");
            Row("  ...on field at the cast", r => $"{r.FfOnField / r.Games:F1}");
            Row("  ...PAID for after the cast", r => $"{r.FfPaidAfter / r.Games:F1}");
            Row("  ...FREE, spawned after the cast", r => $"{r.FfFreeAfter / r.Games:F1}");
            Row("roster value ignited", r => $"${r.FfValue / r.Games:N0}");
            Row("own burn damage", r => $"{r.FfDamage / r.Games:N0}");
            Row("enemy burn damage (same fires)", r => $"{r.EnemyBurnDamage / r.Games:N0}");
            Row("died while burning (upper bnd)", r => $"{r.FfDeaths / r.Games:F1} / ${r.FfDeathValue / r.Games:N0}");
            Row("offensive gadget level at end", r => $"{r.BotGadgetLevelOff / r.Games:F2}");
            Row("spawns/casts held by own fire", r => $"{r.FireHeld / r.Games:F1}");
            Row("machine levels bought as blocker", r => $"{r.BlockerLevels / r.Games:F2}");
            Console.WriteLine();
            Console.WriteLine("  WHY EACH BOT GADGET CAST WAS ALLOWED (casts / $ per game)");
            var keys = arms.SelectMany(a => a.r.CastWhy.Keys).Distinct().OrderBy(k => k).ToList();
            foreach (var key in keys)
                Row(key.Length > 34 ? key.Substring(0, 34) : key,
                    r => r.CastWhy.TryGetValue(key, out var v) ? $"{v[0] / r.Games:F1} / ${v[1] / r.Games:N0}" : "-");
        }

        // ── 3. SYNTHETIC (isolated) ────────────────────────────────────────────────────
        private static void Synthetic(string[] args)
        {
            int games = 60, seed = 777, window = 60;
            double at = 30;
            var levels = new List<int> { 0, 1, 2 };
            string variant = "EconomyBrakeAutoSpawnProfile";
            string[] human = { "Blue", "freeze", "reinforcements", "wave" };
            string[] botL = { "Yellow", "firebomb", "reinforcements", "divine" };
            bool trace = false, allTeams = false, protect = true;
            int horizon = 420;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--games" && i + 1 < args.Length) games = int.Parse(args[++i]);
                else if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
                else if (args[i] == "--at" && i + 1 < args.Length) at = double.Parse(args[++i]);
                else if (args[i] == "--window" && i + 1 < args.Length) window = int.Parse(args[++i]);
                else if (args[i] == "--variant" && i + 1 < args.Length) variant = args[++i];
                else if (args[i] == "--levels" && i + 1 < args.Length) levels = args[++i].Split(',').Select(int.Parse).ToList();
                else if (args[i] == "--human" && i + 1 < args.Length) human = args[++i].Split(',');
                else if (args[i] == "--bot" && i + 1 < args.Length) botL = args[++i].Split(',');
                else if (args[i] == "--trace") trace = true;
                else if (args[i] == "--all-teams") allTeams = true;
                else if (args[i] == "--no-protect") protect = false;
                else if (args[i] == "--horizon" && i + 1 < args.Length) horizon = int.Parse(args[++i]);
            }
            var settings = ResolveSettings(variant);
            if (settings == null) return;

            Console.WriteLine("=== SYNTHETIC: a human that invests ASAP and does NOTHING else, except buy auto-spawner levels ===");
            Console.WriteLine($"  human P1 {string.Join("/", human)}{(allTeams ? " (team swept over all 8)" : "")}   " +
                              $"bot P2 {string.Join("/", botL)}   profile {variant}");
            Console.WriteLine($"  machine bought at >= {at:F0}s (as soon as affordable after that)   {games} paired seeds per arm");
            // WITHOUT PROTECTION THE CONTROL ARM IS MEANINGLESS: a human who buys nothing dies to
            // the bot's free opening squad, so the no-machine games end in about a minute and
            // every whole-game figure compares a 60s game with a 240s one. Same device as the
            // replay gauntlet's --race, and the same caveat -- win rate is not a result here.
            Console.WriteLine($"  human castle {(protect ? "INVULNERABLE (win rate is not a result)" : "vulnerable")}; " +
                              $"human stops at rung 8 and never buys ARMAGEDDON;\n" +
                              $"  a game ends when the BOT buys ARMAGEDDON, its castle falls, or at {horizon}s\n");

            var arms = levels.Select(L => ($"auto L{L}", new ArmResult())).ToArray();
            var rng = new Random(seed);
            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            for (int g = 0; g < games; g++)
            {
                int es = rng.Next();
                var map = teams[rng.Next(teams.Length)];
                var h = (string[])human.Clone();
                if (allTeams) h[0] = teams[g % teams.Length].ToString();
                for (int a = 0; a < levels.Count; a++)
                    PlaySynthetic(h, botL, map, es, settings, levels[a], (long)(at * 30), window * 30, arms[a].Item2,
                                  trace && g == 0, protect, horizon * 30L);
            }
            Report(arms, $"{at:F0}s .. {at + window:F0}s (machine purchase onward)");
        }

        private static void PlaySynthetic(string[] human, string[] botL, TeamColour map, int seed,
                                          HeuristicBotSettings settings, int level, long atTick, long windowTicks,
                                          ArmResult r, bool trace, bool protect, long horizonTicks)
        {
            var state = new GameState(map, new Random(seed)) { Map = map };
            state.Player1 = new PlayerState { Side = 1, Team = Enum.Parse<TeamColour>(human[0], true) };
            state.Player2 = new PlayerState { Side = 2, Team = Enum.Parse<TeamColour>(botL[0], true) };
            state.Player1.SetLoadout(human.Skip(1).ToArray());
            state.Player2.SetLoadout(botL.Skip(1).ToArray());
            if (protect)
            {
                // Both fields, or ProcessStatuses clears the flag on the first tick -- see
                // ReplayGauntlet's race mode, where exactly that produced a silent no-op.
                state.Player1.IsInvulnerable = true;
                state.Player1.InvulnerableUntilTick = long.MaxValue;
            }
            var engine = new GameEngine(state, null, seed);
            var led = new[] { new double[N], new double[N], new double[N] };
            var casts = new[] { new int[4], new int[4], new int[4] };
            WireGadgets(engine, led, casts);
            var bot = new HeuristicBot(2, settings);
            var probe = new Probe(engine, bot, r);
            double[] winStartLed = null; double earned = 0, winEarnedStart = 0;
            var investAt = new double[10];
            for (int k = 0; k < 10; k++) investAt[k] = -1;
            var p1 = state.Player1;
            if (trace) Console.WriteLine($"  --- trace, auto L{level} ---");

            while (!state.IsGameOver && state.CurrentTick < Math.Min(GameEngine.MAX_TICKS, horizonTicks))
            {
                engine.Tick();
                probe.AfterTick();
                long i = state.CurrentTick;
                if (i % 30 == 0) earned += state.Player2.Income;
                if (i == atTick) { winStartLed = (double[])led[2].Clone(); winEarnedStart = earned; }
                if (i == atTick + windowTicks && winStartLed != null)
                {
                    for (int k = 0; k < N; k++) r.Win[k] += led[2][k] - winStartLed[k];
                    r.WinEarned += earned - winEarnedStart;
                }

                // THE HUMAN SCRIPT. One action per tick at most, like a real player.
                // The machine has priority once its time comes, so every arm buys its
                // levels at the same moment rather than whenever an invest happens to leave
                // enough change.
                var s0 = Take(engine, 1);
                if (i >= atTick && p1.AutoSpawnLevel < level)
                {
                    if (p1.Money >= p1.AutoSpawnPrice) { r.Tried++; if (engine.UpgradeAutoSpawn(1)) r.Landed++; }
                }
                // STOPS AT RUNG 8 AND NEVER BUYS ARMAGEDDON. The first version invested
                // "ASAP" and so bought ARMAGEDDON too -- whose shield rewrites
                // InvulnerableUntilTick and whose nukes hit BOTH castles -- so every
                // late-game figure (win rate, repairs, "castle destroyed") was measuring an
                // ARMAGEDDON race rather than the stream. The game now ends when the BOT buys
                // ARMAGEDDON, and the time it takes is the race metric.
                else if (p1.InvestmentCount < PlayerState.ArmageddonInvestmentCount
                         && p1.Money >= p1.InvestmentPrice)
                {
                    r.Tried++; if (engine.Invest(1)) r.Landed++;
                }
                Charge(led[1], s0, Take(engine, 1));

                var b0 = Take(engine, 2, bot);
                probe.BeforeBot();
                bot.Update(engine);
                probe.AfterBot();
                Charge(led[2], b0, Take(engine, 2, bot));

                r.Ticks++; if (bot.LastDecisionWasDanger) r.DangerTicks++;
                if (i >= atTick && i < atTick + windowTicks) { r.WinTicks++; if (bot.LastDecisionWasDanger) r.WinDangerTicks++; }
                int inv = state.Player2.InvestmentCount;
                if (inv < 10 && investAt[inv] < 0) investAt[inv] = i / 30.0;
                probe.Tick(i);

                if (state.Player2.ArmageddonUsed)
                {
                    r.BotArmaAt += i / 30.0; r.BotArmaN++;
                    break;
                }

                if (trace && i % 150 == 0 && i <= atTick + windowTicks + 300)
                {
                    int near = state.Units.Count(u => u.Side == 1 && Math.Abs(u.Position - 1800) < 700);
                    int mine = state.Units.Count(u => u.Side == 2);
                    Console.WriteLine($"    {i / 30,4}s  human L{p1.AutoSpawnLevel} inv{p1.InvestmentCount} | bot ${state.Player2.Money,7:N0} " +
                                      $"inv{state.Player2.InvestmentCount} next ${state.Player2.InvestmentPrice:N0}  danger={(bot.LastDecisionWasDanger ? "Y" : "n")} " +
                                      $"ttd {Fmt(bot.LastTimeToDeathSeconds)} tti {Fmt(bot.LastTimeToInvestSeconds)}  " +
                                      $"enemies<700 {near,2} own units {mine,3}  spent units ${led[2][UNITS],6:N0} " +
                                      $"gadgets ${led[2][OFF] + led[2][DEF] + led[2][SIG],6:N0} rep ${led[2][REPAIR],5:N0}");
                }
            }

            for (int k = 0; k < N; k++) r.Led[k] += led[2][k];
            r.Earned += earned;
            if (state.WinnerSide == 2) r.BotWins++; else if (state.WinnerSide == 1) r.HumanWins++;
            r.BotInvest += state.Player2.InvestmentCount; r.HumanInvest += p1.InvestmentCount;
            r.BotAuto += state.Player2.AutoSpawnLevel; r.HumanAuto += p1.AutoSpawnLevel;
            r.BotHp += Pct(state.Player2); r.HumanHp += Pct(p1); r.BotMoney += state.Player2.Money;
            if (state.IsGameOver && state.WinnerSide == 1) r.CastleLost++;
            for (int k = 0; k < 10; k++) if (investAt[k] >= 0) { r.InvestAt[k] += investAt[k]; r.InvestAtN[k]++; }
            for (int k = 1; k <= 3; k++) r.Casts[k] += casts[2][k];
            probe.Finish();
            r.Games++;
        }

        // ── 4. DUEL (bot vs bot, seat-alternated) ─────────────────────────────────────
        /// <summary>
        /// Profile A (probed) vs profile B, random teams/maps/loadouts drawn from a seeded rng,
        /// every setup played from both seats -- the seat bias is severe (CLAUDE.md), so an
        /// unalternated number here would be worthless. `--offense firebomb` pins A's offensive
        /// slot so friendly fire is measured at 100% exposure rather than diluted into the ~25%
        /// of games that draw it (the same reason Ladder.cs grew --offense).
        /// </summary>
        private static void Duel(string[] args)
        {
            int games = 200, seed = 4242;
            string a = "EconomyBrakeAutoSpawnProfile", b = "EconomyBrakeAutoSpawnProfile", pinOff = null;
            var others = new List<string>();
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--games" && i + 1 < args.Length) games = int.Parse(args[++i]);
                else if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
                else if (args[i] == "--a" && i + 1 < args.Length) others.Add(args[++i]);
                else if (args[i] == "--b" && i + 1 < args.Length) b = args[++i];
                else if (args[i] == "--offense" && i + 1 < args.Length) pinOff = args[++i];
            }
            if (others.Count == 0) others.Add(a);
            bool chipper = b == "Chipper";
            var sb = chipper ? null : ResolveSettings(b);
            if (!chipper && sb == null) return;
            string[] offs = { "nuke", "freeze", "firebomb", "snipe" };
            string[] defs = { "reinforcements", "heal", "wall", "speed" };
            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            var rng = new Random(seed);
            var specs = new List<(TeamColour ta, TeamColour tb, TeamColour map, string oa, string da, string ob, string db, int es)>();
            for (int g = 0; g < games; g++)
                specs.Add((teams[rng.Next(8)], teams[rng.Next(8)], teams[rng.Next(8)],
                           offs[rng.Next(4)], defs[rng.Next(4)], offs[rng.Next(4)], defs[rng.Next(4)], rng.Next()));

            Console.WriteLine($"=== DUEL: A (probed) vs B = {b}, {games} setups x 2 seats, seed {seed}" +
                              $"{(pinOff != null ? $", A's offense pinned to {pinOff}" : "")} ===\n");
            var arms = new List<(string, ArmResult)>();
            foreach (var name in others)
            {
                var sa = ResolveSettings(name);
                if (sa == null) return;
                var r = new ArmResult();
                foreach (var sp in specs)
                    for (int seat = 1; seat <= 2; seat++)
                        PlayDuel(sp.ta, sp.tb, sp.map, pinOff ?? sp.oa, sp.da, sp.ob, sp.db, sp.es, seat, sa, sb, r);
                arms.Add((name.Length > 19 ? name.Substring(0, 19) : name, r));
            }
            Console.WriteLine("  (\"bot\" rows are A; \"human\" rows are B)");
            Report(arms.ToArray(), "n/a (no window in duel mode)");
        }

        private static void PlayDuel(TeamColour ta, TeamColour tb, TeamColour map, string oa, string da,
                                     string ob, string db, int seed, int aSide,
                                     HeuristicBotSettings sa, HeuristicBotSettings sb, ArmResult r)
        {
            var state = new GameState(map, new Random(seed)) { Map = map };
            var pa = new PlayerState { Side = aSide, Team = ta };
            var pb = new PlayerState { Side = 3 - aSide, Team = tb };
            pa.SetLoadout(new[] { oa, da, GameDataManager.GetSignatureGadgetIdForTeam(ta) });
            pb.SetLoadout(new[] { ob, db, GameDataManager.GetSignatureGadgetIdForTeam(tb) });
            state.Player1 = aSide == 1 ? pa : pb;
            state.Player2 = aSide == 1 ? pb : pa;
            var engine = new GameEngine(state, null, seed);
            var led = new[] { new double[N], new double[N], new double[N] };
            var casts = new[] { new int[4], new int[4], new int[4] };
            WireGadgets(engine, led, casts);
            var botA = new HeuristicBot(aSide, sa);
            var botB = sb == null ? null : new HeuristicBot(3 - aSide, sb);
            long chipNext = 0;
            var probe = new Probe(engine, botA, r, aSide);
            var investAt = new double[10];
            for (int k = 0; k < 10; k++) investAt[k] = -1;
            double earned = 0;

            while (!state.IsGameOver && state.CurrentTick < GameEngine.MAX_TICKS)
            {
                engine.Tick();
                probe.AfterTick();
                long i = state.CurrentTick;
                if (i % 30 == 0) earned += pa.Income;
                // Board order, as Ladder.cs does, so the harness adds no seat advantage.
                for (int side = 1; side <= 2; side++)
                {
                    if (side == aSide)
                    {
                        var s0 = Take(engine, aSide, botA);
                        probe.BeforeBot();
                        botA.Update(engine);
                        probe.AfterBot();
                        Charge(led[aSide], s0, Take(engine, aSide, botA));
                    }
                    else if (botB != null) botB.Update(engine);
                    else if (i >= chipNext)
                    {
                        // ChipperBaseline (BotArena/Baselines.cs), transcribed: invest first,
                        // then keep EXACTLY ONE cheapest charged unit alive, never more.
                        chipNext = i + 5;
                        if (pb.Money >= pb.InvestmentPrice && engine.Invest(3 - aSide)) { }
                        else if (!state.Units.Any(u => u.Side == 3 - aSide))
                        {
                            var roster = GameDataManager.Teams.FirstOrDefault(t => t.Color == pb.Team)?.Roster;
                            var cheapest = roster?.Where(d => d.Cost > 0 && d.Cost <= pb.Money && pb.HasUnitCharge(d.Id))
                                                  .OrderBy(d => d.Cost).FirstOrDefault();
                            if (cheapest != null) engine.SpawnUnit(3 - aSide, cheapest.Id);
                        }
                    }
                }
                r.Ticks++; if (botA.LastDecisionWasDanger) r.DangerTicks++;
                if (pa.InvestmentCount < 10 && investAt[pa.InvestmentCount] < 0) investAt[pa.InvestmentCount] = i / 30.0;
                probe.Tick(i);
            }
            for (int k = 0; k < N; k++) r.Led[k] += led[aSide][k];
            r.Earned += earned;
            if (state.WinnerSide == aSide) r.BotWins++; else if (state.WinnerSide == 3 - aSide) r.HumanWins++;
            if (state.WinnerSide == 3 - aSide) { if (pb.ArmageddonUsed) r.LossToArma++; else r.LossOther++; }
            if (pa.ArmageddonUsed) r.BotArmaGames++;
            r.BotInvest += pa.InvestmentCount; r.HumanInvest += pb.InvestmentCount;
            r.BotAuto += pa.AutoSpawnLevel; r.HumanAuto += pb.AutoSpawnLevel;
            r.BotHp += Pct(pa); r.HumanHp += Pct(pb); r.BotMoney += pa.Money;
            for (int k = 0; k < 10; k++) if (investAt[k] >= 0) { r.InvestAt[k] += investAt[k]; r.InvestAtN[k]++; }
            for (int k = 1; k <= 3; k++) r.Casts[k] += casts[aSide][k];
            probe.Finish();
            r.Games++;
        }

        private static string Fmt(float s) => s >= 9999 ? "  inf" : $"{s,5:F0}";

        private static double Pct(PlayerState p)
            => p.CastleMaxHealth > 0 ? 100.0 * p.CastleHealth / p.CastleMaxHealth : 0;

        private static HeuristicBotSettings ResolveSettings(string name)
        {
            var f = typeof(HeuristicBotSettings).GetField(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (f != null) return (HeuristicBotSettings)f.GetValue(null);
            Console.WriteLine($"No HeuristicBotSettings profile named '{name}'.");
            return null;
        }
    }
}
