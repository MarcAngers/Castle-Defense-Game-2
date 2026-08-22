using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    /// <summary>
    /// Guard for <see cref="ThreatModel"/>: does the survival law, as the BOT computes it from
    /// a live board, reproduce what the stall-test sweeps actually measured?
    ///
    /// This is the difference between having implemented a formula and having implemented the
    /// finding. It rebuilds the exact scenario `stall-test` uses -- N copies of one tier spawned
    /// a second apart, 23,000 HP castle, no defence at all -- reads ThreatModel off that board,
    /// and compares its t(0) prediction against the measured control time in curve_full.csv.
    ///
    /// Run it after ANY change to ThreatModel, to the roster, or to how the engine resolves
    /// castle damage. A drift here means the bot is now defending against a game that no longer
    /// exists.
    /// </summary>
    public static class ThreatCheck
    {
        public static void Run(string[] args)
        {
            string csv = Arg(args, "--csv", "stall/curve_full.csv");
            int hp = int.Parse(Arg(args, "--hp", "23000"));

            var measured = LoadControls(csv);
            if (measured.Count == 0)
            {
                Console.WriteLine($"No control rows found in {csv} -- run the part-four sweep first.");
                return;
            }

            Console.WriteLine("THREAT MODEL CHECK -- predicted t(0) vs measured undefended survival");
            Console.WriteLine($"  scenario : N x tier-T spawned 1s apart, {hp} HP castle, no defence");
            Console.WriteLine($"  measured : {csv} (control rows, escort 0, anchor 0)");
            Console.WriteLine();
            Console.WriteLine($"{"team",-8}{"T",-3}{"force",-6}{"S",8}{"K",8}{"walk",8}{"predict",9}{"measured",10}{"error",9}");

            var errs = new List<double>();
            foreach (var team in (TeamColour[])Enum.GetValues(typeof(TeamColour)))
            {
                foreach (int tier in new[] { 5, 6, 7, 8 })
                {
                    foreach (int force in new[] { 1, 3, 5 })
                    {
                        if (!measured.TryGetValue((team.ToString(), tier, force), out double obs)) continue;

                        var m = BuildBoard(team, tier, force, hp, out var engine, out var enemies);
                        float pred = m.SurvivalSeconds(0f);
                        if (float.IsPositiveInfinity(pred)) continue;

                        double err = (pred - obs) / obs;
                        errs.Add(err);
                        Console.WriteLine($"{team,-8}{tier,-3}{"x" + force,-6}{m.SwingRate,8:F2}{m.SwingsToKill,8:F1}"
                                        + $"{m.WalkSeconds,8:F1}{pred,9:F1}{obs,10:F1}{err,8:P0}");
                    }
                }
            }

            if (errs.Count == 0) { Console.WriteLine("\nNo comparable cells."); return; }
            errs.Sort();
            double median = errs[errs.Count / 2];
            int within10 = errs.Count(e => Math.Abs(e) < 0.10);
            int within25 = errs.Count(e => Math.Abs(e) < 0.25);
            Console.WriteLine();
            Console.WriteLine($"n = {errs.Count}   median error {median:P1}   "
                            + $"|err| < 10%: {within10}/{errs.Count}   |err| < 25%: {within25}/{errs.Count}");
            Console.WriteLine(Math.Abs(median) < 0.15 && within25 >= errs.Count * 0.8
                ? "PASS -- the model the bot uses matches the games that were actually played."
                : "FAIL -- ThreatModel has drifted from the measured behaviour. Do not ship the bot on it.");
        }

        /// <summary>Rebuilds stall-test's board: force spawned 1s apart, advanced to the moment the model is read.</summary>
        private static ThreatModel BuildBoard(TeamColour team, int tier, int force, int hp,
                                              out GameEngine engine, out List<Unit> enemies)
        {
            var state = new GameState(TeamColour.White, new Random(12345));
            state.Player1 = new PlayerState();
            state.Player2 = new PlayerState();
            foreach (var (p, side) in new[] { (state.Player1, 1), (state.Player2, 2) })
            {
                p.Side = side;
                p.Team = team;
                p.SetLoadout(new[] { "nuke", "wall", GameDataManager.GetSignatureGadgetIdForTeam(team) });
                p.CastleHealth = hp;
                p.CastleMaxHealth = hp;
                p.Income = 5000;
                p.Money = 5000;
            }

            engine = new GameEngine(state, null, 12345);
            var def = GameDataManager.Teams.First(t => t.Color == team).Roster[tier - 1];

            // Spawn the whole force on the schedule stall-test used, so the model sees the same
            // board the sweep measured -- staggered spawns mean the later members are still
            // behind, which is exactly what WalkSeconds should pick up.
            for (int i = 0; i < force; i++)
            {
                engine.SpawnUnit(1, def.Id, ignoreCost: true);
                for (int t = 0; t < GameEngine.TICKS_PER_SECOND && i < force - 1; t++) engine.Tick();
            }

            enemies = state.Units.Where(u => u.Side == 1).ToList();
            return ThreatModel.Build(engine, 2, enemies, state.Player2.CastleHealth);
        }

        /// <summary>Control rows (no chumps, no anchor, no escort) keyed by team/tier/force.</summary>
        private static Dictionary<(string, int, int), double> LoadControls(string path)
        {
            var d = new Dictionary<(string, int, int), double>();
            if (!File.Exists(path)) return d;
            var lines = File.ReadAllLines(path);
            if (lines.Length < 2) return d;
            var head = lines[0].Split(',');
            int C(string n) => Array.IndexOf(head, n);
            int iTeam = C("attacker_team"), iTier = C("tier"), iForce = C("force_size");
            int iEsc = C("escort_tier"), iAnc = C("anchor_tier"), iIv = C("interval_ticks");
            int iOut = C("outcome"), iSec = C("seconds");
            if (iTeam < 0 || iSec < 0) return d;

            for (int i = 1; i < lines.Length; i++)
            {
                var c = lines[i].Split(',');
                if (c.Length <= iSec) continue;
                if (c[iIv] != "0" || c[iEsc] != "0") continue;
                if (iAnc >= 0 && c[iAnc] != "0") continue;
                if (c[iOut] != "castle_destroyed") continue;
                d[(c[iTeam], int.Parse(c[iTier]), int.Parse(c[iForce]))] = double.Parse(c[iSec]);
            }
            return d;
        }

        private static string Arg(string[] a, string n, string f)
        {
            int i = Array.IndexOf(a, n);
            return (i >= 0 && i + 1 < a.Length) ? a[i + 1] : f;
        }
    }
}
