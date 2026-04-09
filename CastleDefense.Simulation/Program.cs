using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
// Make sure you include your Data namespace!

namespace CastleDefense.Simulation
{
    public class ActionPayload
    {
        public int P1Action { get; set; }
        public int P2Action { get; set; }

        public float DenseRewardWeight { get; set; }
        public string OpponentName { get; set; }
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

    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Initializing ML Environment Server...");
            GameDataManager.Initialize();

            // 1. Start the TCP Server on port 5000
            int port = 5000;
            TcpListener server = new TcpListener(IPAddress.Parse("127.0.0.1"), port);
            server.Start();
            Console.WriteLine($"Listening for Python AI on 127.0.0.1:{port}...");

            // 2. Pause and wait for Python to connect
            using TcpClient client = server.AcceptTcpClient();
            Console.WriteLine("Python AI Connected! Building the arena...");

            // Setup the data streams
            using NetworkStream stream = client.GetStream();
            using StreamReader reader = new StreamReader(stream);
            using StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };

            int matchCount = 0;
            int maxTicks = 18000;

            int totalAIWins = 0;
            int first100AIWins = 0;
            Queue<bool> recentAIWins = new Queue<bool>();

            RandomBot randBot = new RandomBot();
            AntiSpamBot antiSpamBot = new AntiSpamBot();

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
                int timeSkip = Math.Max(rand.Next(-4, 9), 0);
                state.Player1 = new PlayerState(timeSkip);
                state.Player2 = new PlayerState(timeSkip);
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

                // Randomly assign spam bot opponents during training
                int botSelection = rand.Next(11);
                int spamTier = Math.Max(Math.Min(timeSkip + rand.Next(-2, 3), 8), 1);
                SpamBot bot = new SpamBot(spamTier);

                // 1. Identify the opponent
                string opponentName = "Unknown";

                if (botSelection == 0) opponentName = "Random Dummy";
                else if (botSelection == 1) opponentName = "Anti-Spam Bot";
                else if (botSelection >= 2 && botSelection <= 5)
                {
                    opponentName = $"Spam Bot T{spamTier}";
                }

                // 3. The Match Loop
                try
                {
                    // 2. Send the initial starting state to Python
                    var initialResult = new StepResult
                    {
                        P1State = state.GetStateVector(1),
                        P2State = state.GetStateVector(2),
                        P1ActionMask = state.GetActionMask(1),
                        P2ActionMask = state.GetActionMask(2),
                        P1Reward = 0,
                        P2Reward = 0,
                        IsDone = false
                    };
                    writer.WriteLine(JsonSerializer.Serialize(initialResult));

                    int framesToSkip = 3;

                    // 4. The Match Loop
                    while (!state.IsGameOver && state.CurrentTick < maxTicks)
                    {
                        string message = reader.ReadLine();
                        if (string.IsNullOrEmpty(message)) break;

                        var actions = JsonSerializer.Deserialize<ActionPayload>(message);

                        if (botSelection > 5 && !string.IsNullOrEmpty(actions.OpponentName))
                        {
                            opponentName = actions.OpponentName;
                        }

                        float cumulativeP1Reward = 0f;
                        float cumulativeP2Reward = 0f;
                        StepResult finalResult = null;

                        // --- THE FRAME SKIP LOOP ---
                        for (int i = 0; i < framesToSkip; i++)
                        {
                            if (state.IsGameOver || state.CurrentTick >= maxTicks) break;

                            // Execute the AI's action ONLY on the first tick. 
                            // For the next 14 ticks, force them to "Wait"
                            int currentP1Action = (i == 0) ? actions.P1Action : 0;
                            int currentP2Action = (i == 0) ? actions.P2Action : 0;

                            // Playing hardcoded bot opponent
                            if (botSelection == 0)
                            {
                                currentP2Action = randBot.GetAction();
                            }
                            else if (botSelection == 1)
                            {
                                currentP2Action = antiSpamBot.GetAction(state.CurrentTick, state.Player1.Team);
                            }
                            else if (botSelection >= 2 && botSelection <= 5)
                            {
                                currentP2Action = bot.GetAction();
                            }
                            // Otherwise, play against python model

                            StepResult tickResult = engine.Step(currentP1Action, currentP2Action, actions.DenseRewardWeight);

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

                        // Inject the summed-up rewards into the final result object before sending to Python
                        finalResult.P1Reward = cumulativeP1Reward;
                        finalResult.P2Reward = cumulativeP2Reward;

                        writer.WriteLine(JsonSerializer.Serialize(finalResult));
                    }
                }
                catch (Exception ex)
                {
                    // 1. Did Python actually just close? (Socket exceptions)
                    if (ex is System.IO.IOException || ex is System.Net.Sockets.SocketException)
                    {
                        Console.WriteLine("\n[NETWORKING] Python disconnected naturally. Shutting down arena.");

                        // --- NEW: THE FINAL REPORT CARD ---
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("\n===========================================================");
                        Console.WriteLine("               FINAL TRAINING RUN STATISTICS               ");
                        Console.WriteLine("===========================================================");

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"[GLOBAL] Matches: {globalTracker.TotalMatches} | Total WR: {globalTracker.TotalWinrate:0.0}% | Baseline: {globalTracker.BaselineWinrate:0.0}%");

                        // Print Opponents (Alphabetical)
                        Console.WriteLine("\n--- BY OPPONENT ---");
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        foreach (var kvp in opponentTrackers.OrderBy(x => x.Key))
                        {
                            var oppName = kvp.Key;
                            var finalOppStats = kvp.Value;
                            // PadRight and formatting alignments ensure columns line up perfectly in the console
                            Console.WriteLine($"[{oppName.PadRight(17)}] Matches: {finalOppStats.TotalMatches.ToString().PadRight(5)} | Total WR: {finalOppStats.TotalWinrate,5:0.0}% | Baseline: {finalOppStats.BaselineWinrate,5:0.0}% | Final 100: {finalOppStats.RecentWinrate,5:0.0}%");
                        }

                        // Print Time Skips (Numerical Order)
                        Console.WriteLine("\n--- BY TIME SKIP ---");
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        foreach (var kvp in timeSkipTrackers.OrderBy(x => x.Key))
                        {
                            var skip = kvp.Key;
                            var finalTimeStats = kvp.Value;
                            Console.WriteLine($"[SKIP {skip.ToString().PadRight(2)}] Matches: {finalTimeStats.TotalMatches.ToString().PadRight(5)} | Total WR: {finalTimeStats.TotalWinrate,5:0.0}% | Baseline: {finalTimeStats.BaselineWinrate,5:0.0}% | Final 100: {finalTimeStats.RecentWinrate,5:0.0}%");
                        }

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("===========================================================\n");
                        Console.ResetColor();
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