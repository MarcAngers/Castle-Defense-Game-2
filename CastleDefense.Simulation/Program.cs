using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CastleDefense.Simulation
{
    // ── Binary protocol helpers ────────────────────────────────────────────────
    // Reset  (C#→Py): [1392B P1State][14B P1Mask][1B nameLen][NB name]
    // Step   (C#→Py): [1392B P1State][14B P1Mask][4B reward][1B done][4B winner]
    // Action (Py→C#): [1B action][4B denseWeight]
    static class Proto
    {
        const int StateFloats = 348;
        const int StateBytes  = StateFloats * 4;
        const int MaskBytes   = 14;

        static readonly byte[] _stepBuf = new byte[StateBytes + MaskBytes + 4 + 1 + 4];

        public static void SendReset(NetworkStream s, float[] state, int[] mask, string opponentName)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(opponentName ?? "");
            int nameLen = Math.Min(nameBytes.Length, 255);
            byte[] buf = new byte[StateBytes + MaskBytes + 1 + nameLen];
            int pos = 0;
            Buffer.BlockCopy(state, 0, buf, pos, StateBytes); pos += StateBytes;
            for (int i = 0; i < MaskBytes; i++) buf[pos++] = (byte)mask[i];
            buf[pos++] = (byte)nameLen;
            Buffer.BlockCopy(nameBytes, 0, buf, pos, nameLen);
            s.Write(buf, 0, buf.Length);
        }

        public static void SendStep(NetworkStream s, float[] state, int[] mask, float reward, bool isDone, int winnerSide)
        {
            int pos = 0;
            Buffer.BlockCopy(state, 0, _stepBuf, pos, StateBytes); pos += StateBytes;
            for (int i = 0; i < MaskBytes; i++) _stepBuf[pos++] = (byte)mask[i];
            BitConverter.TryWriteBytes(new Span<byte>(_stepBuf, pos, 4), reward); pos += 4;
            _stepBuf[pos++] = isDone ? (byte)1 : (byte)0;
            BitConverter.TryWriteBytes(new Span<byte>(_stepBuf, pos, 4), winnerSide);
            s.Write(_stepBuf, 0, _stepBuf.Length);
        }

        static void ReadExact(NetworkStream s, byte[] buf, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int n = s.Read(buf, offset, count - offset);
                if (n == 0) throw new IOException("Python disconnected");
                offset += n;
            }
        }

        static readonly byte[] _actionBuf = new byte[5];
        public static (int action, float denseWeight) ReadAction(NetworkStream s)
        {
            ReadExact(s, _actionBuf, 5);
            return (_actionBuf[0], BitConverter.ToSingle(_actionBuf, 1));
        }
    }

    public class StatTracker
    {
        public int TotalMatches = 0;
        public int TotalWins = 0;
        public int First100Wins = 0;
        public Queue<bool> RecentWins = new Queue<bool>();

        public void AddResult(bool aiWon)
        {
            TotalMatches++;
            if (aiWon) TotalWins++;

            // Tracks the baseline for the first 100 matches of THIS specific category
            if (TotalMatches <= 100 && aiWon) First100Wins++;

            RecentWins.Enqueue(aiWon);
            if (RecentWins.Count > 100) RecentWins.Dequeue();
        }

        public double RecentWinrate => RecentWins.Count == 0 ? 0 : (double)RecentWins.Count(w => w) / RecentWins.Count * 100;
        public double TotalWinrate => TotalMatches == 0 ? 0 : (double)TotalWins / TotalMatches * 100;
        public double BaselineWinrate => TotalMatches == 0 ? 0 : TotalMatches >= 100
            ? (First100Wins / 100.0 * 100)
            : (First100Wins / (double)TotalMatches * 100);
    }

    public record TrackerData(int Matches, int Wins, int First100Wins, int RecentWins, int RecentTotal);

    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Initializing ML Environment Server...");
            GameDataManager.Initialize();

            // --- NEW: DYNAMIC PORT ASSIGNMENT ---
            int port = 5000;
            if (args.Length > 0 && int.TryParse(args[0], out int parsedPort))
            {
                port = parsedPort;
            }

            // Set the window title so we don't get confused!
            Console.Title = $"Castle Defense AI Arena - Port {port}";

            TcpListener server = new TcpListener(IPAddress.Parse("127.0.0.1"), port);
            server.Start();
            Console.WriteLine($"Listening for Python AI on 127.0.0.1:{port}...");

            // 2. Pause and wait for Python to connect
            using TcpClient client = server.AcceptTcpClient();
            Console.WriteLine("Python AI Connected! Building the arena...");

            using NetworkStream stream = client.GetStream();

            int matchCount = 0;
            int maxTicks = 18000;

            int totalAIWins = 0;
            int first100AIWins = 0;
            Queue<bool> recentAIWins = new Queue<bool>();

            RandomBot randBot = new RandomBot();
            AntiSpamBot antiSpamBot = new AntiSpamBot();

            // Load league model ONNX opponents from league_models/ subdirectory
            var leagueModels = new List<(string name, AIBrain brain)>();
            string leagueDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "league_models");
            if (Directory.Exists(leagueDir))
            {
                foreach (var onnxFile in Directory.GetFiles(leagueDir, "*.onnx")
                                                   .Where(f => !f.EndsWith(".data")))
                {
                    string modelName = Path.GetFileNameWithoutExtension(onnxFile);
                    try
                    {
                        leagueModels.Add((modelName, new AIBrain(onnxFile)));
                        Console.WriteLine($"[League] Loaded opponent: {modelName}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARNING] Could not load {modelName}: {ex.Message}");
                    }
                }
            }
            Console.WriteLine($"[League] {leagueModels.Count} opponent model(s) ready.");

            // The Global Tracker (replaces your old separate integers)
            StatTracker globalTracker = new StatTracker();

            // Categorized Trackers
            Dictionary<string, StatTracker> opponentTrackers = new Dictionary<string, StatTracker>();
            Dictionary<int, StatTracker> timeSkipTrackers = new Dictionary<int, StatTracker>();

            // --- THE INFINITE TRAINING LOOP ---
            while (true)
            {
                matchCount++;
                Console.WriteLine($"\n--- STARTING MATCH {matchCount} ---");

                // 1. Reset the Board
                var state = new GameState();

                // Skip a random amount of time at the start of the game
                string upgradeString = "";
                Random rand = new Random();
                int timeSkip = Math.Max(rand.Next(-8, 9), 0);
                state.Player1 = new PlayerState(timeSkip);
                state.Player2 = new PlayerState(timeSkip);

                // Start 20% of games with some savings in the bank (enough to invest)
                if (rand.Next(5) == 0)
                {
                    state.Player1.Money = state.Player1.InvestmentPrice + state.Player1.Income;
                    state.Player2.Money = state.Player2.InvestmentPrice + state.Player2.Income;
                }

                state.CurrentTick = 30 * 30 * timeSkip;

                if (timeSkip > 3)
                    upgradeString = "_2";
                if (timeSkip > 5)
                    upgradeString = "_3";

                state.Player1.Side = 1;
                state.Player1.Team = GameDataManager.GetRandomTeam();
                state.Player1.SetLoadout(new string[] { GameDataManager.GetRandomOGadgetId() + upgradeString, GameDataManager.GetRandomDGadgetId() + upgradeString, GameDataManager.GetSignatureGadgetIdForTeam(state.Player1.Team) + upgradeString });

                state.Player2.Side = 2;
                state.Player2.Team = GameDataManager.GetRandomTeam();
                state.Player2.SetLoadout(new string[] { GameDataManager.GetRandomOGadgetId() + upgradeString, GameDataManager.GetRandomDGadgetId() + upgradeString, GameDataManager.GetSignatureGadgetIdForTeam(state.Player2.Team) + upgradeString });

                var engine = new GameEngine(state);

                // Randomly assign opponents during training
                int botSelection = rand.Next(11);
                int spamTier = Math.Max(Math.Min(timeSkip + rand.Next(-2, 3), 8), 1);
                SpamBot bot = new SpamBot(spamTier);

                // Select opponent — bots 0-5 are hardcoded, 6-10 use a league ONNX model
                AIBrain selectedLeague = null;
                string opponentName = "Random Dummy";

                if (botSelection == 0) opponentName = "Random Dummy";
                else if (botSelection == 1) opponentName = "Anti-Spam Bot";
                else if (botSelection >= 2 && botSelection <= 5) opponentName = $"Spam Bot T{spamTier}";
                else if (leagueModels.Count > 0)
                {
                    var chosen = leagueModels[rand.Next(leagueModels.Count)];
                    selectedLeague = chosen.brain;
                    opponentName = chosen.name;
                }

                // 3. The Match Loop
                try
                {
                    // 2. Send the initial starting state to Python (includes opponent name for tracking)
                    Proto.SendReset(stream, state.GetStateVector(1), state.GetActionMask(1), opponentName);

                    int framesToSkip = 9;

                    // 4. The Match Loop
                    while (!state.IsGameOver && state.CurrentTick < maxTicks)
                    {
                        var (p1Action, denseWeight) = Proto.ReadAction(stream);

                        float cumulativeP1Reward = 0f;
                        float cumulativeP2Reward = 0f;
                        StepResult finalResult = null;

                        // --- THE FRAME SKIP LOOP ---
                        for (int i = 0; i < framesToSkip; i++)
                        {
                            if (state.IsGameOver || state.CurrentTick >= maxTicks) break;

                            // AI acts on the first tick of each frame skip; waits on the rest
                            int currentP1Action = (i == 0) ? p1Action : 0;
                            int currentP2Action = 0;

                            // Bots act every tick; league models act once per frame skip (same cadence as AI)
                            if (botSelection == 0)
                                currentP2Action = randBot.GetAction();
                            else if (botSelection == 1)
                                currentP2Action = antiSpamBot.GetAction(state.CurrentTick, state.Player1.Team);
                            else if (botSelection >= 2 && botSelection <= 5)
                                currentP2Action = bot.GetAction();
                            else if (selectedLeague != null && i == 0)
                                currentP2Action = selectedLeague.GetBestAction(state.GetStateVector(2), state.GetActionMask(2));

                            StepResult tickResult = engine.Step(currentP1Action, currentP2Action, denseWeight);

                            // Add up the running tally of rewards
                            cumulativeP1Reward += tickResult.P1Reward;
                            cumulativeP2Reward += tickResult.P2Reward;

                            // Keep overwriting finalResult so we always have the most recent GameState array
                            finalResult = tickResult;
                        }

                        // --- TIE-BREAKER LOGIC ---
                        if (state.CurrentTick >= maxTicks && !state.IsGameOver)
                        {
                            state.IsGameOver = true;
                            finalResult.IsDone = true;

                            // Who has the most health?
                            if (state.Player1.CastleHealth > state.Player2.CastleHealth)
                            {
                                cumulativeP1Reward += 50f;
                                cumulativeP2Reward -= 50f;
                            }
                            else if (state.Player2.CastleHealth > state.Player1.CastleHealth)
                            {
                                cumulativeP1Reward -= 50f;
                                cumulativeP2Reward += 50f;
                            }
                        }

                        Proto.SendStep(stream,
                            finalResult.P1State, finalResult.P1ActionMask,
                            cumulativeP1Reward, finalResult.IsDone, finalResult.WinnerSide);
                    }
                }
                catch (Exception ex)
                {
                    // 1. Did Python actually just close? (Socket exceptions)
                    if (ex is System.IO.IOException || ex is System.Net.Sockets.SocketException)
                    {
                        Console.WriteLine("\n[NETWORKING] Python disconnected naturally. Shutting down arena.");

                        // --- THE FINAL REPORT CARD ---
                        var log = new System.Text.StringBuilder();
                        log.AppendLine($"=== Arena Port {port} | Completed: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

                        void Log(string line)
                        {
                            Console.WriteLine(line);
                            log.AppendLine(line);
                        }

                        Console.ForegroundColor = ConsoleColor.Green;
                        Log("\n===========================================================");
                        Log("               FINAL TRAINING RUN STATISTICS               ");
                        Log("===========================================================");

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Log($"[GLOBAL] Matches: {globalTracker.TotalMatches} | Total WR: {globalTracker.TotalWinrate:0.0}% | Baseline: {globalTracker.BaselineWinrate:0.0}%");

                        // Print Opponents (Alphabetical)
                        Log("\n--- BY OPPONENT ---");
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        foreach (var kvp in opponentTrackers.OrderBy(x => x.Key))
                        {
                            var oppName = kvp.Key;
                            var finalOppStats = kvp.Value;
                            Log($"[{oppName.PadRight(17)}] Matches: {finalOppStats.TotalMatches.ToString().PadRight(5)} | Total WR: {finalOppStats.TotalWinrate,5:0.0}% | Baseline: {finalOppStats.BaselineWinrate,5:0.0}% | Final 100: {finalOppStats.RecentWinrate,5:0.0}%");
                        }

                        // Print Time Skips (Numerical Order)
                        Log("\n--- BY TIME SKIP ---");
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        foreach (var kvp in timeSkipTrackers.OrderBy(x => x.Key))
                        {
                            var skip = kvp.Key;
                            var finalTimeStats = kvp.Value;
                            Log($"[SKIP {skip.ToString().PadRight(2)}] Matches: {finalTimeStats.TotalMatches.ToString().PadRight(5)} | Total WR: {finalTimeStats.TotalWinrate,5:0.0}% | Baseline: {finalTimeStats.BaselineWinrate,5:0.0}% | Final 100: {finalTimeStats.RecentWinrate,5:0.0}%");
                        }

                        Console.ForegroundColor = ConsoleColor.Green;
                        Log("===========================================================\n");
                        Console.ResetColor();

                        System.IO.File.WriteAllText($"training_stats_{port}.txt", log.ToString());

                        TrackerData Snap(StatTracker t) => new TrackerData(
                            t.TotalMatches, t.TotalWins, t.First100Wins,
                            t.RecentWins.Count(w => w), t.RecentWins.Count);

                        var jsonData = new
                        {
                            port,
                            global = Snap(globalTracker),
                            opponents = opponentTrackers.ToDictionary(kvp => kvp.Key, kvp => Snap(kvp.Value)),
                            timeSkips = timeSkipTrackers.ToDictionary(kvp => kvp.Key.ToString(), kvp => Snap(kvp.Value))
                        };
                        System.IO.File.WriteAllText($"training_stats_{port}.json",
                            JsonSerializer.Serialize(jsonData, new JsonSerializerOptions { WriteIndented = true }));
                        break;
                    }

                    // 2. NOPE! The Game Engine crashed! 
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[FATAL ENGINE CRASH] {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                    Console.ResetColor();

                    // 3. Save the error to a text file so it survives the crash!
                    System.IO.File.WriteAllText("engine_crash_log.txt", ex.ToString());

                    break; // Break the infinite loop
                }

                Console.WriteLine($"Match {matchCount} finished in {state.CurrentTick} ticks.");
                Console.WriteLine($"P1 HP: {state.Player1.CastleHealth} | P2 HP: {state.Player2.CastleHealth}");

                // The AI is always Player 1 in this training setup
                bool aiWon = false;

                if (state.WinnerSide == 1)
                {
                    aiWon = true; // Clean knockout!
                }
                else if (state.WinnerSide == 0 && state.CurrentTick >= maxTicks)
                {
                    // Survived to Sudden Death, check the Tie-Breaker
                    if (state.Player1.CastleHealth > state.Player2.CastleHealth)
                    {
                        aiWon = true;
                    }
                }

                // --- CATEGORIZE THE MATCH ---
                // 2. Ensure the trackers exist in the dictionaries
                if (!opponentTrackers.ContainsKey(opponentName))
                opponentTrackers[opponentName] = new StatTracker();

                if (!timeSkipTrackers.ContainsKey(timeSkip))
                    timeSkipTrackers[timeSkip] = new StatTracker();

                // --- NEW: UPDATE ALL TRACKERS ---
                globalTracker.AddResult(aiWon);
                opponentTrackers[opponentName].AddResult(aiWon);
                timeSkipTrackers[timeSkip].AddResult(aiWon);

                // --- NEW: CALCULATE AND PRINT STATS ---
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[GLOBAL] Recent: {globalTracker.RecentWinrate:0.0}% | Total: {globalTracker.TotalWinrate:0.0}% | Baseline: {globalTracker.BaselineWinrate:0.0}%");

                // Print the specific opponent we just fought
                var oppStats = opponentTrackers[opponentName];
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[VS {opponentName.ToUpper()}] Recent: {oppStats.RecentWinrate:0.0}% | Total: {oppStats.TotalWinrate:0.0}%");

                // Print the specific time skip we just used
                var timeStats = timeSkipTrackers[timeSkip];
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[TIME SKIP {timeSkip}] Recent: {timeStats.RecentWinrate:0.0}% | Total: {timeStats.TotalWinrate:0.0}%\n");

                Console.ResetColor();
            }
        }
    }
}