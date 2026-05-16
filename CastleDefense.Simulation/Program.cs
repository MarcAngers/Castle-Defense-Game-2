using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CastleDefense.Simulation
{
    // ── Self-play batch protocol ────────────────────────────────────────────────
    // C# collects N_STEPS ticks using its own ONNX model for P1, then ships the
    // entire experience batch to Python for a GPU PPO update.
    //
    // Batch (C#→Py):
    //   [4B int32:  num_steps]
    //   [num_steps × 1413B]:
    //     [1392B obs f32][14B mask i8][1B action u8][4B reward f32][1B ep_start u8][1B winner u8]
    //   [1407B final state]: [1392B obs f32][14B mask i8][1B done u8]
    //   [2B uint16: num_episodes]
    //   per episode: [1B name_len][name bytes][1B winner_side]
    //
    // Ack (Py→C#): [1B model_version]  — C# reloads ONNX when version changes

    static class BatchProto
    {
        public const int STATE_FLOATS = 348;
        public const int STATE_BYTES  = STATE_FLOATS * 4;   // 1392
        public const int MASK_BYTES   = 14;
        public const int STEP_BYTES   = STATE_BYTES + MASK_BYTES + 1 + 4 + 1 + 1; // 1413
        public const int FINAL_BYTES  = STATE_BYTES + MASK_BYTES + 1;             // 1407

        public static void SendBatch(
            NetworkStream s, int nSteps,
            float[][] obs, int[][] mask, int[] action, float[] reward, bool[] epStart, int[] winner,
            float[] finalObs, int[] finalMask, bool finalDone,
            List<(string name, int winner)> episodes)
        {
            // Pre-calculate episode bytes
            var epNameBytes = episodes.Select(e => {
                var b = Encoding.UTF8.GetBytes(e.name ?? "Unknown");
                return b.Length > 255 ? b[..255] : b;
            }).ToArray();

            int epDataBytes = epNameBytes.Sum(b => 1 + b.Length + 1);
            int totalSize   = 4 + nSteps * STEP_BYTES + FINAL_BYTES + 2 + epDataBytes;
            byte[] buf      = new byte[totalSize];
            int p = 0;

            BitConverter.TryWriteBytes(new Span<byte>(buf, p, 4), nSteps); p += 4;

            for (int i = 0; i < nSteps; i++)
            {
                Buffer.BlockCopy(obs[i], 0, buf, p, STATE_BYTES); p += STATE_BYTES;
                for (int j = 0; j < MASK_BYTES; j++) buf[p++] = (byte)mask[i][j];
                buf[p++] = (byte)action[i];
                BitConverter.TryWriteBytes(new Span<byte>(buf, p, 4), reward[i]); p += 4;
                buf[p++] = epStart[i] ? (byte)1 : (byte)0;
                buf[p++] = (byte)winner[i];
            }

            Buffer.BlockCopy(finalObs, 0, buf, p, STATE_BYTES); p += STATE_BYTES;
            for (int j = 0; j < MASK_BYTES; j++) buf[p++] = (byte)finalMask[j];
            buf[p++] = finalDone ? (byte)1 : (byte)0;

            BitConverter.TryWriteBytes(new Span<byte>(buf, p, 2), (ushort)episodes.Count); p += 2;
            for (int i = 0; i < episodes.Count; i++)
            {
                buf[p++] = (byte)epNameBytes[i].Length;
                Buffer.BlockCopy(epNameBytes[i], 0, buf, p, epNameBytes[i].Length);
                p += epNameBytes[i].Length;
                buf[p++] = (byte)episodes[i].winner;
            }

            s.Write(buf, 0, p);
        }

        public static byte ReadAck(NetworkStream s)
        {
            int b = s.ReadByte();
            if (b < 0) throw new IOException("Python disconnected");
            return (byte)b;
        }
    }

    public class StatTracker
    {
        public int TotalMatches = 0;
        public int TotalWins    = 0;
        public int First100Wins = 0;
        public Queue<bool> RecentWins = new Queue<bool>();

        public void AddResult(bool aiWon)
        {
            TotalMatches++;
            if (aiWon) TotalWins++;
            if (TotalMatches <= 100 && aiWon) First100Wins++;
            RecentWins.Enqueue(aiWon);
            if (RecentWins.Count > 100) RecentWins.Dequeue();
        }

        public double RecentWinrate  => RecentWins.Count == 0 ? 0 : (double)RecentWins.Count(w => w) / RecentWins.Count * 100;
        public double TotalWinrate   => TotalMatches == 0 ? 0 : (double)TotalWins / TotalMatches * 100;
        public double BaselineWinrate => TotalMatches == 0 ? 0 : TotalMatches >= 100
            ? (First100Wins / 100.0 * 100)
            : (First100Wins / (double)TotalMatches * 100);
    }

    public record TrackerData(int Matches, int Wins, int First100Wins, int RecentWins, int RecentTotal);

    class Program
    {
        const int    N_STEPS             = 8192;
        const int    MAX_TICKS           = 18000;
        const string TRAINING_MODEL_PATH = "current_model.onnx";

        static readonly Random _rand = new Random();

        static void Main(string[] args)
        {
            Console.WriteLine("Initializing ML Environment Server...");
            GameDataManager.Initialize();

            int port = 5000;
            if (args.Length > 0 && int.TryParse(args[0], out int parsedPort)) port = parsedPort;
            Console.Title = $"Castle Defense AI Arena - Port {port}";

            // Training brain for P1 inference (null = random until Python exports first model)
            AIBrain trainingBrain      = null;
            byte    loadedModelVersion = 255; // force reload check on first ack
            TryLoadTrainingBrain(ref trainingBrain);

            // League opponents for P2
            var leagueModels = new List<(string name, AIBrain brain)>();
            string leagueDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "league_models");
            if (Directory.Exists(leagueDir))
            {
                foreach (var f in Directory.GetFiles(leagueDir, "*.onnx").Where(x => !x.EndsWith(".data")))
                {
                    try
                    {
                        leagueModels.Add((Path.GetFileNameWithoutExtension(f), new AIBrain(f)));
                        Console.WriteLine($"[League] Loaded: {Path.GetFileNameWithoutExtension(f)}");
                    }
                    catch (Exception ex) { Console.WriteLine($"[League] Skip: {ex.Message}"); }
                }
            }
            Console.WriteLine($"[League] {leagueModels.Count} opponent(s) ready.");

            var globalTracker    = new StatTracker();
            var opponentTrackers = new Dictionary<string, StatTracker>();
            var timeSkipTrackers = new Dictionary<int, StatTracker>();

            var server = new TcpListener(IPAddress.Parse("127.0.0.1"), port);
            server.Start();
            Console.WriteLine($"Listening on 127.0.0.1:{port}...");
            using var client = server.AcceptTcpClient();
            Console.WriteLine("Python connected! Starting self-play collection...");
            using var stream = client.GetStream();

            // Batch buffers (reused each batch)
            float[][] batchObs    = new float[N_STEPS][];
            int[][]   batchMask   = new int[N_STEPS][];
            int[]     batchAction = new int[N_STEPS];
            float[]   batchRew    = new float[N_STEPS];
            bool[]    batchEpS    = new bool[N_STEPS];
            int[]     batchWin    = new int[N_STEPS];
            var batchEpisodes     = new List<(string name, int winner)>();

            int  batchPos    = 0;
            bool nextEpStart = true;

            // Current game state (spans multiple episodes, even across batch boundaries)
            GameState  state       = null;
            GameEngine engine      = null;
            string     oppName     = "Random Dummy";
            int        botSel      = 0;
            int        timeSkip    = 0;
            int        spamTier    = 1;
            SpamBot    spamBot     = null;
            AIBrain    leagueBrain = null;
            var        randBot     = new RandomBot();
            var        antiBot     = new AntiSpamBot();

            while (true) // ── OUTER BATCH LOOP ──
            {
                // Start new episode if needed
                if (state == null || state.IsGameOver || state.CurrentTick >= MAX_TICKS)
                {
                    if (state != null) // record completed episode
                    {
                        int epWinner = state.WinnerSide;
                        if (epWinner == 0 && state.CurrentTick >= MAX_TICKS)
                            epWinner = state.Player1.CastleHealth >= state.Player2.CastleHealth ? 1 : 2;

                        batchEpisodes.Add((oppName, epWinner));
                        bool aiWon = epWinner == 1;

                        if (!opponentTrackers.ContainsKey(oppName)) opponentTrackers[oppName] = new StatTracker();
                        if (!timeSkipTrackers.ContainsKey(timeSkip)) timeSkipTrackers[timeSkip] = new StatTracker();
                        globalTracker.AddResult(aiWon);
                        opponentTrackers[oppName].AddResult(aiWon);
                        timeSkipTrackers[timeSkip].AddResult(aiWon);

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write($"[GLOBAL] {globalTracker.TotalMatches} games | Recent: {globalTracker.RecentWinrate:0.0}% | Total: {globalTracker.TotalWinrate:0.0}%  ");
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[vs {oppName}] {opponentTrackers[oppName].RecentWinrate:0.0}%");
                        Console.ResetColor();
                    }

                    // Setup new game
                    state    = new GameState();
                    timeSkip = Math.Max(_rand.Next(-8, 9), 0);
                    string upg = timeSkip > 5 ? "_3" : timeSkip > 3 ? "_2" : "";

                    state.Player1 = new PlayerState(timeSkip);
                    state.Player2 = new PlayerState(timeSkip);
                    if (_rand.Next(5) == 0)
                    {
                        state.Player1.Money = state.Player1.InvestmentPrice + state.Player1.Income;
                        state.Player2.Money = state.Player2.InvestmentPrice + state.Player2.Income;
                    }
                    state.CurrentTick = 30 * 30 * timeSkip;
                    state.Player1.Side = 1;
                    state.Player1.Team = GameDataManager.GetRandomTeam();
                    state.Player1.SetLoadout(new[] {
                        GameDataManager.GetRandomOGadgetId() + upg,
                        GameDataManager.GetRandomDGadgetId() + upg,
                        GameDataManager.GetSignatureGadgetIdForTeam(state.Player1.Team) + upg });
                    state.Player2.Side = 2;
                    state.Player2.Team = GameDataManager.GetRandomTeam();
                    state.Player2.SetLoadout(new[] {
                        GameDataManager.GetRandomOGadgetId() + upg,
                        GameDataManager.GetRandomDGadgetId() + upg,
                        GameDataManager.GetSignatureGadgetIdForTeam(state.Player2.Team) + upg });

                    engine  = new GameEngine(state);
                    botSel  = _rand.Next(11);
                    spamTier = Math.Max(Math.Min(timeSkip + _rand.Next(-2, 3), 8), 1);
                    spamBot  = new SpamBot(spamTier);
                    leagueBrain = null;
                    oppName = "Random Dummy";

                    if (botSel == 1) oppName = "Anti-Spam Bot";
                    else if (botSel >= 2 && botSel <= 5) oppName = $"Spam Bot T{spamTier}";
                    else if (botSel > 5 && leagueModels.Count > 0)
                    {
                        var chosen = leagueModels[_rand.Next(leagueModels.Count)];
                        leagueBrain = chosen.brain;
                        oppName     = chosen.name;
                    }

                    nextEpStart = true;
                }

                // ── STEP COLLECTION LOOP ──
                while (batchPos < N_STEPS && !state.IsGameOver && state.CurrentTick < MAX_TICKS)
                {
                    float[] p1Obs  = state.GetStateVector(1);
                    int[]   p1Mask = state.GetActionMask(1);
                    int p1Action   = trainingBrain != null
                        ? trainingBrain.GetBestAction(p1Obs, p1Mask)
                        : GetRandomValidAction(p1Mask);

                    float      cumRew   = 0f;
                    StepResult lastTick = null;

                    for (int fi = 0; fi < 9; fi++)
                    {
                        if (state.IsGameOver || state.CurrentTick >= MAX_TICKS) break;

                        int p2Action = 0;
                        if      (botSel == 0)                      p2Action = randBot.GetAction();
                        else if (botSel == 1)                      p2Action = antiBot.GetAction(state.CurrentTick, state.Player1.Team);
                        else if (botSel >= 2 && botSel <= 5)       p2Action = spamBot.GetAction();
                        else if (leagueBrain != null && fi == 0)   p2Action = leagueBrain.GetBestAction(state.GetStateVector(2), state.GetActionMask(2));

                        var tick = engine.Step(fi == 0 ? p1Action : 0, p2Action, 0f);
                        cumRew  += tick.P1Reward;
                        lastTick = tick;
                    }

                    // Overtime tie-break
                    if (state.CurrentTick >= MAX_TICKS && !state.IsGameOver)
                    {
                        state.IsGameOver  = true;
                        lastTick.IsDone   = true;
                        if      (state.Player1.CastleHealth > state.Player2.CastleHealth) cumRew += 50f;
                        else if (state.Player2.CastleHealth > state.Player1.CastleHealth) cumRew -= 50f;
                    }

                    batchObs[batchPos]    = p1Obs;
                    batchMask[batchPos]   = p1Mask;
                    batchAction[batchPos] = p1Action;
                    batchRew[batchPos]    = cumRew;
                    batchEpS[batchPos]    = nextEpStart;
                    batchWin[batchPos]    = lastTick.IsDone ? lastTick.WinnerSide : 0;
                    nextEpStart           = lastTick.IsDone;
                    batchPos++;
                }

                // ── SEND BATCH WHEN FULL ──
                if (batchPos >= N_STEPS)
                {
                    float[] finalObs  = state.GetStateVector(1);
                    int[]   finalMask = state.GetActionMask(1);
                    bool    finalDone = state.IsGameOver;

                    try
                    {
                        BatchProto.SendBatch(stream, N_STEPS,
                            batchObs, batchMask, batchAction, batchRew, batchEpS, batchWin,
                            finalObs, finalMask, finalDone, batchEpisodes);

                        byte ackVersion = BatchProto.ReadAck(stream);
                        if (ackVersion != loadedModelVersion)
                        {
                            TryLoadTrainingBrain(ref trainingBrain);
                            loadedModelVersion = ackVersion;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is SocketException)
                    {
                        Console.WriteLine("\n[NET] Python disconnected. Writing final stats...");
                        PrintFinalStats(port, globalTracker, opponentTrackers, timeSkipTrackers);
                        return;
                    }

                    batchPos = 0;
                    batchEpisodes.Clear();
                }
            }
        }

        static void TryLoadTrainingBrain(ref AIBrain brain)
        {
            if (!File.Exists(TRAINING_MODEL_PATH)) return;
            try
            {
                brain?.Dispose();
                brain = new AIBrain(TRAINING_MODEL_PATH);
                Console.WriteLine($"[Model] Reloaded {TRAINING_MODEL_PATH}");
            }
            catch (Exception ex) { Console.WriteLine($"[Model] Reload failed (will retry): {ex.Message}"); }
        }

        static int GetRandomValidAction(int[] mask)
        {
            var valid = Enumerable.Range(0, mask.Length).Where(i => mask[i] != 0).ToList();
            return valid.Count > 0 ? valid[_rand.Next(valid.Count)] : 0;
        }

        static void PrintFinalStats(int port, StatTracker global,
            Dictionary<string, StatTracker> opp, Dictionary<int, StatTracker> ts)
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Arena Port {port} | Completed: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            void Log(string line) { Console.WriteLine(line); log.AppendLine(line); }

            Console.ForegroundColor = ConsoleColor.Green;
            Log("\n===========================================================");
            Log("               FINAL TRAINING RUN STATISTICS               ");
            Log("===========================================================");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Log($"[GLOBAL] Matches: {global.TotalMatches} | Total WR: {global.TotalWinrate:0.0}% | Baseline: {global.BaselineWinrate:0.0}%");

            Log("\n--- BY OPPONENT ---");
            Console.ForegroundColor = ConsoleColor.Yellow;
            foreach (var kvp in opp.OrderBy(x => x.Key))
                Log($"[{kvp.Key.PadRight(17)}] {kvp.Value.TotalMatches,5} games | Total: {kvp.Value.TotalWinrate,5:0.0}% | Final 100: {kvp.Value.RecentWinrate,5:0.0}%");

            Log("\n--- BY TIME SKIP ---");
            Console.ForegroundColor = ConsoleColor.Magenta;
            foreach (var kvp in ts.OrderBy(x => x.Key))
                Log($"[SKIP {kvp.Key,-2}] {kvp.Value.TotalMatches,5} games | Total: {kvp.Value.TotalWinrate,5:0.0}% | Final 100: {kvp.Value.RecentWinrate,5:0.0}%");

            Console.ForegroundColor = ConsoleColor.Green;
            Log("===========================================================\n");
            Console.ResetColor();

            File.WriteAllText($"training_stats_{port}.txt", log.ToString());
            TrackerData Snap(StatTracker t) => new TrackerData(
                t.TotalMatches, t.TotalWins, t.First100Wins,
                t.RecentWins.Count(w => w), t.RecentWins.Count);
            var json = new
            {
                port,
                global    = Snap(global),
                opponents = opp.ToDictionary(k => k.Key, k => Snap(k.Value)),
                timeSkips = ts.ToDictionary(k => k.Key.ToString(), k => Snap(k.Value))
            };
            File.WriteAllText($"training_stats_{port}.json",
                JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
