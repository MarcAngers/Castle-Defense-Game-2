using CastleDefense.Api.Data;
using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;
using CastleDefense.Engine.Models.Hazards;
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
        public const int STEP_BYTES   = STATE_BYTES + MASK_BYTES + 1 + 4 + 4 + 1 + 1; // 1417 (added eval_delta float)
        public const int FINAL_BYTES  = STATE_BYTES + MASK_BYTES + 1;                // 1407

        public static void SendBatch(
            NetworkStream s, int nSteps,
            float[][] obs, int[][] mask, int[] action, float[] reward, float[] boardEval, bool[] epStart, int[] winner,
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
                BitConverter.TryWriteBytes(new Span<byte>(buf, p, 4), reward[i]);    p += 4;
                BitConverter.TryWriteBytes(new Span<byte>(buf, p, 4), boardEval[i]); p += 4;
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
        const int N_STEPS = 8192;

        static readonly Random _rand = new Random();

        static void Main(string[] args)
        {
            Console.WriteLine("Initializing ML Environment Server...");
            GameDataManager.Initialize();

            if (args.Length >= 3 && args[0] == "--export-bc")
            {
                bool p1Only     = args.Contains("--p1-only");
                bool p1WinsOnly = args.Contains("--p1-wins-only");
                ExportBcData(args[1], args[2], p1Only, p1WinsOnly);
                return;
            }

            if (args.Length >= 3 && args[0] == "--export-eval")
            {
                ExportEvalData(args[1], args[2]);
                return;
            }

            if (args.Length >= 2 && args[0] == "--analyze-invest")
            {
                AnalyzeInvestBehavior(args[1]);
                return;
            }

            if (args.Length >= 2 && args[0] == "--analyze-actions")
            {
                AnalyzeActionDistribution(args[1], args.Contains("--mp"));
                return;
            }

            if (args.Length >= 2 && args[0] == "--trace-human")
            {
                // Optional 3rd arg filters to games whose DB opponent_type/game_mode
                // contains the given substring (e.g. "v4", "spam4", "model") -- useful
                // once a replay dir mixes spam-bot and model-opponent recordings.
                string filter = args.Length > 2 ? args[2] : null;
                TraceHumanReplays(args[1], filter);
                return;
            }

            if (args.Length >= 4 && args[0] == "--collect-calibration")
            {
                int  nGames    = int.Parse(args[1]);
                string onnxPath  = args[2];
                string outCsv    = args[3];
                CollectCalibrationData(nGames, onnxPath, outCsv);
                return;
            }

            bool timeMachine = !args.Contains("--no-time-machine");

            int port = 5000;
            if (args.Length > 0 && int.TryParse(args[0], out int parsedPort)) port = parsedPort;
            Console.Title = $"Castle Defense AI Arena - Port {port}";

            string modelPath = args.Length > 1 ? args[1] : "current_model.onnx";
            Console.WriteLine($"[Config] Time machine: {(timeMachine ? "ON" : "OFF")}");

            // League opponents — loaded once and kept in memory across all connections
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

            // Batch buffers — allocated once and reused across all connections
            float[][] batchObs       = new float[N_STEPS][];
            int[][]   batchMask      = new int[N_STEPS][];
            int[]     batchAction    = new int[N_STEPS];
            float[]   batchRew       = new float[N_STEPS];
            float[]   batchEval      = new float[N_STEPS];
            bool[]    batchEpS       = new bool[N_STEPS];
            int[]     batchWin       = new int[N_STEPS];

            var server = new TcpListener(IPAddress.Parse("127.0.0.1"), port);
            server.Start();

            while (true) // ── OUTER CONNECTION LOOP — arenas persist across Python sessions ──
            {
                Console.WriteLine($"\nListening on 127.0.0.1:{port}...");
                using var client = server.AcceptTcpClient();
                Console.WriteLine("Python connected! Starting self-play collection...");
                using var stream = client.GetStream();

                // Reload reward params fresh for each connection (GA writes these before connecting)
                var rewardParams = RewardParams.LoadFromJson($"reward_params_{port}.json");
                Console.WriteLine($"[Reward] WIN={rewardParams.WinReward} INVEST={rewardParams.InvestReward} DECAY={rewardParams.InvestDecay} COMBAT={rewardParams.CombatScale} ANTI_SPEND={rewardParams.AntiSpend} SAVINGS={rewardParams.SavingsWeight} GADGET_UP={rewardParams.GadgetUpgrade} GADGET_USE={rewardParams.GadgetUse}");

                // Reset training brain for this session
                AIBrain trainingBrain      = null;
                byte    loadedModelVersion = 255; // force reload on first ack
                TryLoadTrainingBrain(ref trainingBrain, modelPath);

                // Reset per-session stats and batch state
                var globalTracker    = new StatTracker();
                var opponentTrackers = new Dictionary<string, StatTracker>();
                var timeSkipTrackers = new Dictionary<int, StatTracker>();
                var batchEpisodes    = new List<(string name, int winner)>();

                int  batchPos    = 0;
                bool nextEpStart = true;

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

                bool disconnected = false;

                while (!disconnected) // ── BATCH LOOP ──
                {
                    // Start new episode if needed
                    if (state == null || state.IsGameOver)
                    {
                        if (state != null) // record completed episode
                        {
                            int epWinner = state.WinnerSide;
                            if (epWinner == 0 && state.IsTimeLimit)
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
                        timeSkip = timeMachine ? Math.Max(_rand.Next(-8, 9), 0) : 0;
                        string upg = timeMachine ? (timeSkip > 5 ? "_3" : timeSkip > 3 ? "_2" : "") : "";

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

                        engine  = new GameEngine(state, rewardParams);
                        botSel  = _rand.Next(16);
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
                    while (batchPos < N_STEPS && !state.IsGameOver)
                    {
                        float[] p1Obs  = state.GetStateVector(1);
                        int[]   p1Mask = state.GetActionMask(1);
                        int p1Action   = trainingBrain != null
                            ? trainingBrain.GetBestAction(p1Obs, p1Mask)
                            : GetRandomValidAction(p1Mask);

                        // Invest exploration: if the model has stopped investing, it never sees
                        // invest data and can't rediscover the behaviour no matter how large
                        // the reward signal is. Force action 9 ~5 % of the time when it's valid
                        // to inject invest experience and break the data-starvation loop.
                        const float INVEST_EXPLORE = 0.90f;
                        if (p1Mask[9] == 1 && p1Action != 9 && _rand.NextDouble() < INVEST_EXPLORE)
                            p1Action = 9;

                        float      cumRew    = 0f;
                        float      evalBefore = state.EvaluateBoard();
                        StepResult lastTick  = null;

                        for (int fi = 0; fi < 9; fi++)
                        {
                            if (state.IsGameOver) break;

                            int p2Action = 0;
                            if      (botSel == 0)                      p2Action = randBot.GetAction();
                            else if (botSel == 1)                      p2Action = antiBot.GetAction(state.CurrentTick, state.Player1.Team);
                            else if (botSel >= 2 && botSel <= 5)       p2Action = spamBot.GetAction();
                            else if (leagueBrain != null && fi == 0)   p2Action = leagueBrain.GetBestAction(state.GetStateVector(2), state.GetActionMask(2));

                            var tick = engine.Step(fi == 0 ? p1Action : 0, p2Action, 0f);
                            cumRew  += tick.P1Reward;
                            lastTick = tick;
                        }

                        // Board eval at observation time — Python computes an N-step forward
                        // return (eval[t+N] - eval[t]) for reward shaping, which correctly
                        // credits actions whose payoff is delayed (invests, unit spawns, etc.).
                        batchObs[batchPos]    = p1Obs;
                        batchMask[batchPos]   = p1Mask;
                        batchAction[batchPos] = p1Action;
                        batchRew[batchPos]    = cumRew;
                        batchEval[batchPos]   = evalBefore;
                        batchEpS[batchPos]       = nextEpStart;
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
                                batchObs, batchMask, batchAction, batchRew, batchEval, batchEpS, batchWin,
                                finalObs, finalMask, finalDone, batchEpisodes);

                            byte ackVersion = BatchProto.ReadAck(stream);
                            if (ackVersion != loadedModelVersion)
                            {
                                TryLoadTrainingBrain(ref trainingBrain, modelPath);
                                loadedModelVersion = ackVersion;
                            }
                        }
                        catch (Exception ex) when (ex is IOException || ex is SocketException)
                        {
                            Console.WriteLine("\n[NET] Python disconnected. Writing final stats...");
                            disconnected = true;
                        }

                        batchPos = 0;
                        batchEpisodes.Clear();
                    }
                }

                PrintFinalStats(port, globalTracker, opponentTrackers, timeSkipTrackers);
                trainingBrain?.Dispose();
                Console.WriteLine("[NET] Ready for next connection.");
            }
        }

        static void TryLoadTrainingBrain(ref AIBrain brain, string modelPath)
        {
            if (!File.Exists(modelPath)) return;
            try
            {
                brain?.Dispose();
                brain = new AIBrain(modelPath);
                Console.WriteLine($"[Model] Reloaded {modelPath}");
            }
            catch (Exception ex) { Console.WriteLine($"[Model] Reload failed (will retry): {ex.Message}"); }
        }

        static int GetRandomValidAction(int[] mask)
        {
            var valid = Enumerable.Range(0, mask.Length).Where(i => mask[i] != 0).ToList();
            return valid.Count > 0 ? valid[_rand.Next(valid.Count)] : 0;
        }

        // ── BC EXPORT ──────────────────────────────────────────────────────────────

        static string ReadStr(BinaryReader r)
        {
            int len = r.ReadByte();
            return len > 0 ? Encoding.UTF8.GetString(r.ReadBytes(len)) : "";
        }

        // Infer the time-machine starting state for a v1 replay by scoring candidate
        // (timeSkip, bonusMoney) pairs against the first N recorded action pairs.
        // The gadget suffix narrows candidates; we pick the pair where the most recorded
        // actions are valid in the re-simulated mask.
        static (int timeSkip, bool bonusMoney) InferV1TimeMachineState(
            string p1Team, string[] l1, string p2Team, string[] l2,
            byte[] actionBytes, int kTicks)
        {
            // Narrow candidates from gadget suffix (all three gadgets carry the same suffix)
            string suffix = l1.Length > 0 ? (l1[0].EndsWith("_3") ? "_3" : l1[0].EndsWith("_2") ? "_2" : "") : "";
            int[] candidates = suffix == "_3" ? new[] { 6, 7, 8 }
                             : suffix == "_2" ? new[] { 4, 5 }
                             : new[] { 0, 1, 2, 3 };

            int  bestTs    = candidates[0];
            bool bestBonus = false;
            int  bestScore = -1;

            foreach (int ts in candidates)
            {
                foreach (bool bonus in new[] { false, true })
                {
                    var p1 = new PlayerState(ts);
                    var p2 = new PlayerState(ts);
                    if (bonus)
                    {
                        p1.Money = p1.InvestmentPrice + p1.Income;
                        p2.Money = p2.InvestmentPrice + p2.Income;
                    }
                    p1.Side = 1; p1.Team = Enum.Parse<TeamColour>(p1Team, ignoreCase: true);
                    p2.Side = 2; p2.Team = Enum.Parse<TeamColour>(p2Team, ignoreCase: true);
                    if (l1.Length == 3) p1.SetLoadout(l1);
                    if (l2.Length == 3) p2.SetLoadout(l2);

                    var gs = new GameState();
                    gs.Player1 = p1; gs.Player2 = p2;
                    gs.CurrentTick = 30L * 30 * ts;
                    var eng = new GameEngine(gs);

                    int valid = 0;
                    for (int i = 0; i < kTicks && i * 2 + 1 < actionBytes.Length && !gs.IsGameOver; i++)
                    {
                        byte p1a = actionBytes[i * 2];
                        byte p2a = actionBytes[i * 2 + 1];
                        if (p1a > 0 && gs.GetActionMask(1)[p1a] == 1) valid++;
                        if (p2a > 0 && gs.GetActionMask(2)[p2a] == 1) valid++;
                        eng.ApplyAction(1, p1a);
                        eng.ApplyAction(2, p2a);
                        eng.Tick();
                    }

                    // Strictly greater: on tie, first candidate (lowest ts, no bonus) wins
                    if (valid > bestScore) { bestScore = valid; bestTs = ts; bestBonus = bonus; }
                }
            }

            return (bestTs, bestBonus);
        }

        // p1Only:     record only P1's perspective (skip P2 actions)
        // p1WinsOnly: skip replays where P1 did not win (winner byte != 1)
        static void ProcessReplay(string path,
            List<float[]> obs, List<int[]> masks, List<int> actions,
            bool p1Only = false, bool p1WinsOnly = false)
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream, Encoding.UTF8);

            var magic = r.ReadBytes(4);
            if (magic[0] != 'C' || magic[1] != 'D' || magic[2] != 'R' || magic[3] != 'P')
                throw new InvalidDataException("Not a CDRP replay file");

            byte   version = r.ReadByte();
            string gameId  = Encoding.ASCII.GetString(r.ReadBytes(6));
            r.ReadInt64();  // timestamp
            ReadStr(r);     // game_version (discard)

            string p1Team = ReadStr(r);
            string p1Off  = ReadStr(r), p1Def = ReadStr(r), p1Sig = ReadStr(r);
            string p2Team = ReadStr(r);
            string p2Off  = ReadStr(r), p2Def = ReadStr(r), p2Sig = ReadStr(r);
            byte   winner = r.ReadByte();

            long   startingTick = 0;
            double p1StartMoney = 0, p2StartMoney = 0;
            if (version >= 2)
            {
                startingTick = r.ReadInt64();
                p1StartMoney = r.ReadDouble();
                p2StartMoney = r.ReadDouble();
            }

            uint tickCount = r.ReadUInt32();

            if (p1WinsOnly && winner != 1)
            {
                Console.WriteLine($"[BC] {gameId}: skipped (P{winner} won, need P1 win)");
                return;
            }

            var l1 = new[] { p1Off, p1Def, p1Sig }.Where(s => !string.IsNullOrEmpty(s)).ToArray();
            var l2 = new[] { p2Off, p2Def, p2Sig }.Where(s => !string.IsNullOrEmpty(s)).ToArray();

            // v1 singleplayer replays used the time machine but didn't store the starting state.
            // Probe the first N ticks to infer which timeSkip was used, so re-simulation starts
            // from the correct economy and all recorded actions appear valid in the mask.
            // Multiplayer v1 replays never used the time machine so we skip inference for them.
            if (version == 1 && p1Only && tickCount > 0)
            {
                long   actionStart = stream.Position;
                const  int PROBE   = 100;
                byte[] probe       = r.ReadBytes(Math.Min((int)tickCount, PROBE) * 2);
                stream.Seek(actionStart, SeekOrigin.Begin);

                var (ts, bonus) = InferV1TimeMachineState(p1Team, l1, p2Team, l2, probe, PROBE);
                startingTick    = 30L * 30 * ts;
                var proto       = new PlayerState(ts);
                double amt      = bonus ? proto.InvestmentPrice + proto.Income : proto.Money;
                p1StartMoney    = amt;
                p2StartMoney    = amt;
                if (ts > 0) Console.WriteLine($"[BC] {gameId}: v1 inferred timeSkip={ts} bonus={bonus}");
            }

            int timeSkip = (int)(startingTick / (30 * 30));
            var state    = new GameState();
            state.Player1 = new PlayerState(timeSkip);
            state.Player2 = new PlayerState(timeSkip);
            state.Player1.Side = 1; state.Player1.Team = Enum.Parse<TeamColour>(p1Team, ignoreCase: true);
            state.Player2.Side = 2; state.Player2.Team = Enum.Parse<TeamColour>(p2Team, ignoreCase: true);
            state.Player1.Money = p1StartMoney;
            state.Player2.Money = p2StartMoney;
            state.CurrentTick   = startingTick;
            if (l1.Length == 3) state.Player1.SetLoadout(l1);
            if (l2.Length == 3) state.Player2.SetLoadout(l2);

            var engine   = new GameEngine(state);
            int startObs = obs.Count;

            for (uint t = 0; t < tickCount && !state.IsGameOver; t++)
            {
                byte p1Action = r.ReadByte();
                byte p2Action = r.ReadByte();

                // Always record P1 perspective for any non-wait action
                if (p1Action != 0)
                {
                    obs.Add(state.GetStateVector(1));
                    masks.Add(state.GetActionMask(1));
                    actions.Add(p1Action);
                }

                // Record P2 perspective only for human-vs-human games
                if (!p1Only && p2Action != 0)
                {
                    obs.Add(state.GetStateVector(2));
                    masks.Add(state.GetActionMask(2));
                    actions.Add(p2Action);
                }

                engine.ApplyAction(1, p1Action);
                engine.ApplyAction(2, p2Action);
                engine.Tick();
            }

            string tag = timeSkip > 0 ? $" [skip={timeSkip}]" : "";
            Console.WriteLine($"[BC] {gameId} (P{winner} won){tag}: {tickCount} ticks → {obs.Count - startObs} examples");
        }

        static void ExportBcData(string replayDir, string outputPath,
            bool p1Only = false, bool p1WinsOnly = false)
        {
            if (!Directory.Exists(replayDir))
            {
                Console.Error.WriteLine($"[BC] ERROR: replay directory not found: {replayDir}");
                Environment.Exit(1);
            }

            var replayFiles = Directory.GetFiles(replayDir, "*.replay");
            Console.WriteLine($"[BC] Found {replayFiles.Length} replay file(s) in {replayDir}" +
                              (p1Only ? " [P1 perspective only]" : "") +
                              (p1WinsOnly ? " [P1 wins only]" : ""));

            var obs     = new List<float[]>();
            var masks   = new List<int[]>();
            var actions = new List<int>();

            foreach (var f in replayFiles)
            {
                try   { ProcessReplay(f, obs, masks, actions, p1Only, p1WinsOnly); }
                catch (Exception ex) { Console.Error.WriteLine($"[BC] Skip {Path.GetFileName(f)}: {ex.Message}"); }
            }

            if (obs.Count == 0)
            {
                Console.Error.WriteLine("[BC] No training examples generated — aborting.");
                Environment.Exit(1);
            }

            Console.WriteLine($"[BC] Writing {obs.Count} training examples to {outputPath}...");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

            using var stream = new FileStream(outputPath, FileMode.Create);
            using var w = new BinaryWriter(stream);

            w.Write(new byte[] { (byte)'B', (byte)'C', (byte)'T', (byte)'R' });
            w.Write(obs.Count);
            for (int i = 0; i < obs.Count; i++)
            {
                foreach (var f in obs[i])   w.Write(f);
                foreach (var m in masks[i]) w.Write((byte)m);
                w.Write((byte)actions[i]);
            }

            long kb = stream.Length / 1024;
            Console.WriteLine($"[BC] Done — {obs.Count} examples, {kb} KB");
        }

        // ── INVEST BEHAVIOUR ANALYSIS ──────────────────────────────────────────────
        // For each replay, reconstructs the game tick-by-tick and checks whether invest
        // (action 9) was available but not taken.  Reports per-game and aggregate stats
        // for P1 and P2 separately so AI vs human behaviour can be compared.

        static void AnalyzeInvestBehavior(string replayDir)
        {
            if (!Directory.Exists(replayDir))
            {
                Console.Error.WriteLine($"[Invest] Directory not found: {replayDir}");
                Environment.Exit(1);
            }

            var files = Directory.GetFiles(replayDir, "*.replay");
            if (files.Length == 0)
            {
                Console.Error.WriteLine($"[Invest] No .replay files in {replayDir}");
                Environment.Exit(1);
            }
            Console.WriteLine($"[Invest] Analysing {files.Length} replay(s) in {replayDir}\n");

            long totalP1Avail = 0, totalP1Missed = 0;
            long totalP2Avail = 0, totalP2Missed = 0;

            foreach (var path in files.OrderBy(x => x))
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    using var r = new BinaryReader(stream, Encoding.UTF8);

                    var magic = r.ReadBytes(4);
                    if (magic[0] != 'C' || magic[1] != 'D' || magic[2] != 'R' || magic[3] != 'P')
                        throw new InvalidDataException("Not a CDRP file");

                    byte   version   = r.ReadByte();
                    string gameId    = Encoding.ASCII.GetString(r.ReadBytes(6));
                    r.ReadInt64();
                    ReadStr(r);  // game_version
                    string p1Team    = ReadStr(r);
                    string p1Off     = ReadStr(r); string p1Def = ReadStr(r); string p1Sig = ReadStr(r);
                    string p2Team    = ReadStr(r);
                    string p2Off     = ReadStr(r); string p2Def = ReadStr(r); string p2Sig = ReadStr(r);
                    byte   winner    = r.ReadByte();

                    long   startingTick = 0;
                    double p1StartMoney = 0, p2StartMoney = 0;
                    if (version >= 2)
                    {
                        startingTick = r.ReadInt64();
                        p1StartMoney = r.ReadDouble();
                        p2StartMoney = r.ReadDouble();
                    }

                    uint tickCount = r.ReadUInt32();

                    var l1 = new[] { p1Off, p1Def, p1Sig }.Where(s => !string.IsNullOrEmpty(s)).ToArray();
                    var l2 = new[] { p2Off, p2Def, p2Sig }.Where(s => !string.IsNullOrEmpty(s)).ToArray();

                    if (version == 1 && tickCount > 0)
                    {
                        long   ap    = stream.Position;
                        byte[] probe = r.ReadBytes(Math.Min((int)tickCount, 100) * 2);
                        stream.Seek(ap, SeekOrigin.Begin);
                        var (ts, bonus) = InferV1TimeMachineState(p1Team, l1, p2Team, l2, probe, 100);
                        startingTick    = 30L * 30 * ts;
                        var proto       = new PlayerState(ts);
                        double amt      = bonus ? proto.InvestmentPrice + proto.Income : proto.Money;
                        p1StartMoney    = amt; p2StartMoney = amt;
                    }

                    int timeSkip = (int)(startingTick / (30 * 30));
                    var state = new GameState();
                    state.Player1 = new PlayerState(timeSkip); state.Player2 = new PlayerState(timeSkip);
                    state.Player1.Side = 1; state.Player2.Side = 2;
                    state.Player1.Money = p1StartMoney; state.Player2.Money = p2StartMoney;
                    state.CurrentTick = startingTick;
                    state.Player1.Team = Enum.Parse<TeamColour>(p1Team, ignoreCase: true);
                    state.Player2.Team = Enum.Parse<TeamColour>(p2Team, ignoreCase: true);
                    if (l1.Length == 3) state.Player1.SetLoadout(l1);
                    if (l2.Length == 3) state.Player2.SetLoadout(l2);
                    var engine = new GameEngine(state);

                    // Count ticks where a non-wait action was taken while invest was available.
                    // "Other" = chose something other than invest (action != 9) in that situation.
                    long p1Active = 0, p1Other = 0, p2Active = 0, p2Other = 0;

                    for (uint t = 0; t < tickCount && !state.IsGameOver; t++)
                    {
                        byte p1Action = r.ReadByte();
                        byte p2Action = r.ReadByte();

                        int[] p1Mask = state.GetActionMask(1);
                        int[] p2Mask = state.GetActionMask(2);

                        if (p1Mask[9] == 1 && p1Action != 0)
                        {
                            p1Active++;
                            if (p1Action != 9) p1Other++;
                        }
                        if (p2Mask[9] == 1 && p2Action != 0)
                        {
                            p2Active++;
                            if (p2Action != 9) p2Other++;
                        }

                        engine.ApplyAction(1, p1Action);
                        engine.ApplyAction(2, p2Action);
                        engine.Tick();
                    }

                    double p1Pct = p1Active > 0 ? p1Other * 100.0 / p1Active : 0;
                    double p2Pct = p2Active > 0 ? p2Other * 100.0 / p2Active : 0;
                    Console.WriteLine($"  {gameId} (P{winner} won)  " +
                                      $"P1: {p1Other}/{p1Active} chose other ({p1Pct:F1}%)  " +
                                      $"P2: {p2Other}/{p2Active} chose other ({p2Pct:F1}%)");

                    totalP1Avail += p1Active; totalP1Missed += p1Other;
                    totalP2Avail += p2Active; totalP2Missed += p2Other;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [skip] {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("─── AGGREGATE ───────────────────────────────────────────────────");
            if (totalP1Avail > 0)
                Console.WriteLine($"  P1  active decisions w/ invest available: {totalP1Avail,6}  " +
                                  $"chose other: {totalP1Missed,6} ({totalP1Missed * 100.0 / totalP1Avail:F2}%)");
            if (totalP2Avail > 0)
                Console.WriteLine($"  P2  active decisions w/ invest available: {totalP2Avail,6}  " +
                                  $"chose other: {totalP2Missed,6} ({totalP2Missed * 100.0 / totalP2Avail:F2}%)");
        }

        // ── HUMAN DECISION TRACE ────────────────────────────────────────────────────
        // Re-simulates each replay tick-by-tick (ground truth, faithful to the
        // recorded actions) while a SEPARATE, persistent shadow HeuristicBot(side=1)
        // is queried every tick on a throwaway clone of the exact same real state --
        // it can freely Invest/Repair/SpawnUnit/UseGadget on the clone without ever
        // touching the real trajectory. Because the SAME bot instance is reused across
        // the whole game (not recreated per query), its internal decision cadence and
        // rolling HP-drain window (see HeuristicBot.EstimateTimeToDeathSeconds) build
        // up naturally from the real CastleHealth history, exactly as they would in a
        // live game -- so its TTD/TTI/danger reads are faithful, not cold-started.
        // This answers "what would the bot's own rules say to do right now, given
        // the exact situation the human is actually in" at every human decision point.
        // The .replay binary format has no opponent-identity field at all (see
        // GameRecorder's doc comment) -- "which spam tier / which named model" only ever
        // lives in game_records.db's opponent_type column, added after the fact. Once a
        // replay directory has a mix of spam-bot and model-opponent recordings, both the
        // header printout and the optional filter arg need that DB data to be usable.
        static Dictionary<string, (string gameMode, string opponentType)> LoadOpponentInfo(string replayDir)
        {
            var result = new Dictionary<string, (string, string)>();
            var candidates = new[]
            {
                Path.Combine(replayDir, "game_records.db"),
                Path.Combine(replayDir, "..", "game_records.db"),
            };
            foreach (var candidate in candidates)
            {
                string full = Path.GetFullPath(candidate);
                if (!File.Exists(full)) continue;
                try
                {
                    var db = new GameDatabase(full);
                    foreach (var g in db.GetAllGames())
                        result[g.Id] = (g.GameMode, g.OpponentType);
                    Console.WriteLine($"[TraceHuman] Loaded opponent info for {result.Count} game(s) from {full}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[TraceHuman] Could not read {full}: {ex.Message}");
                }
                break; // first candidate found wins -- don't merge across two DB files
            }
            if (result.Count == 0)
                Console.WriteLine("[TraceHuman] No game_records.db found near the replay dir -- opponent type won't be labeled (team/gadgets/actions still fully traced).");
            return result;
        }

        static void TraceHumanReplays(string replayDir, string filter = null)
        {
            if (!Directory.Exists(replayDir))
            {
                Console.Error.WriteLine($"[TraceHuman] Directory not found: {replayDir}");
                Environment.Exit(1);
            }

            var files = Directory.GetFiles(replayDir, "*.replay").OrderBy(x => x).ToArray();
            if (files.Length == 0)
            {
                Console.Error.WriteLine($"[TraceHuman] No .replay files in {replayDir}");
                Environment.Exit(1);
            }

            var opponentInfo = LoadOpponentInfo(replayDir);

            if (filter != null)
            {
                files = files.Where(f =>
                {
                    string gameId = Path.GetFileNameWithoutExtension(f);
                    if (!opponentInfo.TryGetValue(gameId, out var info)) return false;
                    return (info.opponentType?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (info.gameMode?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
                }).ToArray();
                Console.WriteLine($"[TraceHuman] {files.Length} replay(s) match filter '{filter}'");
            }

            foreach (var path in files)
            {
                try { TraceOneHumanReplay(path, opponentInfo); }
                catch (Exception ex) { Console.Error.WriteLine($"[TraceHuman] Skip {Path.GetFileName(path)}: {ex.Message}"); }
            }
        }

        static PlayerState ClonePlayerState(PlayerState src)
        {
            return new PlayerState
            {
                ConnectionId = src.ConnectionId,
                Side = src.Side,
                Team = src.Team,
                Money = src.Money,
                Income = src.Income,
                InvestmentPrice = src.InvestmentPrice,
                InvestmentCount = src.InvestmentCount,
                CastleHealth = src.CastleHealth,
                CastleMaxHealth = src.CastleMaxHealth,
                RepairPrice = src.RepairPrice,
                RepairCount = src.RepairCount,
                IsInvulnerable = src.IsInvulnerable,
                InvulnerableUntilTick = src.InvulnerableUntilTick,
                OffensiveGadget = src.OffensiveGadget,
                DefensiveGadget = src.DefensiveGadget,
                SignatureGadget = src.SignatureGadget,
                UnitCharges = new Dictionary<string, int>(src.UnitCharges),
                CooldownTimers = new Dictionary<string, long>(src.CooldownTimers),
                GadgetXp = new Dictionary<string, int>(src.GadgetXp),
                GadgetCooldowns = new Dictionary<string, long>(src.GadgetCooldowns),
            };
        }

        // Shallow-copies Units (the shadow bot only ever reads existing units and
        // appends new ones via SpawnUnit -- it never calls Tick(), so nothing mutates
        // an existing Unit in place) but deep-clones both PlayerStates, since Invest/
        // Repair/SpawnUnit/UseGadget mutate PlayerState fields directly and must never
        // leak back into the real trajectory.
        static GameState CloneStateForShadow(GameState src)
        {
            var dst = new GameState();
            dst.Map = src.Map;
            dst.ShadowMap = src.ShadowMap;
            dst.CurrentTick = src.CurrentTick;
            dst.Player1 = ClonePlayerState(src.Player1);
            dst.Player2 = ClonePlayerState(src.Player2);
            dst.Units = new List<Unit>(src.Units);
            dst.Hazards = new List<Hazard>(src.Hazards);
            return dst;
        }

        static string DescribeAction(byte actionId, TeamColour team)
        {
            if (actionId == 0) return "wait";
            if (actionId >= 1 && actionId <= 8)
            {
                var roster = GameDataManager.Teams.Find(t => t.Color == team)?.Roster;
                string unitId = roster != null && actionId - 1 < roster.Count ? roster[actionId - 1].Id : "?";
                return $"spawnT{actionId}({unitId})";
            }
            return actionId < ActionLabels.Length ? ActionLabels[actionId] : $"action{actionId}";
        }

        static void TraceOneHumanReplay(string path, Dictionary<string, (string gameMode, string opponentType)> opponentInfo = null)
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream, Encoding.UTF8);

            var magic = r.ReadBytes(4);
            if (magic[0] != 'C' || magic[1] != 'D' || magic[2] != 'R' || magic[3] != 'P')
                throw new InvalidDataException("Not a CDRP file");

            byte version = r.ReadByte();
            string gameId = Encoding.ASCII.GetString(r.ReadBytes(6));
            r.ReadInt64(); // timestamp
            ReadStr(r); // game_version

            string p1Team = ReadStr(r);
            string p1Off = ReadStr(r), p1Def = ReadStr(r), p1Sig = ReadStr(r);
            string p2Team = ReadStr(r);
            string p2Off = ReadStr(r), p2Def = ReadStr(r), p2Sig = ReadStr(r);
            byte winner = r.ReadByte();

            long startingTick = 0;
            double p1StartMoney = 0, p2StartMoney = 0;
            if (version >= 2)
            {
                startingTick = r.ReadInt64();
                p1StartMoney = r.ReadDouble();
                p2StartMoney = r.ReadDouble();
            }

            uint tickCount = r.ReadUInt32();

            var l1 = new[] { p1Off, p1Def, p1Sig }.Where(s => !string.IsNullOrEmpty(s)).ToArray();
            var l2 = new[] { p2Off, p2Def, p2Sig }.Where(s => !string.IsNullOrEmpty(s)).ToArray();

            int timeSkip = (int)(startingTick / (30 * 30));
            var state = new GameState();
            state.Player1 = new PlayerState(timeSkip);
            state.Player2 = new PlayerState(timeSkip);
            state.Player1.Side = 1; state.Player1.Team = Enum.Parse<TeamColour>(p1Team, ignoreCase: true);
            state.Player2.Side = 2; state.Player2.Team = Enum.Parse<TeamColour>(p2Team, ignoreCase: true);
            state.Player1.Money = p1StartMoney;
            state.Player2.Money = p2StartMoney;
            state.CurrentTick = startingTick;
            if (l1.Length == 3) state.Player1.SetLoadout(l1);
            if (l2.Length == 3) state.Player2.SetLoadout(l2);

            var engine = new GameEngine(state);
            var shadowBot = new HeuristicBot(1);

            string opponentTag = "";
            if (opponentInfo != null && opponentInfo.TryGetValue(gameId, out var info))
                opponentTag = $" [{info.gameMode ?? "?"}/{info.opponentType ?? "unknown opponent"}]";

            Console.WriteLine($"\n=== {gameId}: P1={p1Team} (off={p1Off} def={p1Def} sig={p1Sig}) vs P2={p2Team}{opponentTag}, winner=P{winner}, {tickCount} ticks ({tickCount / 30}s) ===");
            Console.WriteLine("tick\tsec\tACTION\tP1$\tP1inc\tP1inv\tP1hp%\tP2units\tBOTdanger\tBOTttd\tBOTtti\tBOTthreat\tBOTdef\tBOTwould");

            int minHpPctSeen = 100;
            int humanInvests = 0, humanRepairs = 0, botWouldInvestButDidnt = 0, humanInvestedWhileBotSaysDanger = 0;

            for (uint t = 0; t < tickCount && !state.IsGameOver; t++)
            {
                byte p1Action = r.ReadByte();
                byte p2Action = r.ReadByte();

                // Query the shadow bot BEFORE this tick's real actions are applied, so
                // it sees exactly the state the human was looking at when choosing.
                var clone = CloneStateForShadow(state);
                var cloneEngine = new GameEngine(clone);
                int beforeInv = clone.Player1.InvestmentCount;
                int beforeRep = clone.Player1.RepairCount;
                var beforeUnitIds = new HashSet<Guid>(clone.Units.Where(u => u.Side == 1).Select(u => u.InstanceId));
                shadowBot.Update(cloneEngine);
                string botWould = "wait";
                if (clone.Player1.InvestmentCount > beforeInv) botWould = "INVEST";
                else if (clone.Player1.RepairCount > beforeRep) botWould = "REPAIR";
                else
                {
                    var newUnit = clone.Units.Where(u => u.Side == 1 && !beforeUnitIds.Contains(u.InstanceId)).FirstOrDefault();
                    if (newUnit != null) botWould = $"spawnT{newUnit.Tier}({newUnit.DefinitionId})";
                }

                if (p1Action != 0)
                {
                    string actionName = DescribeAction(p1Action, state.Player1.Team);
                    var p2units = state.Units.Count(u => u.Side == 2);
                    var p1hpPct = 100.0 * state.Player1.CastleHealth / state.Player1.CastleMaxHealth;
                    minHpPctSeen = Math.Min(minHpPctSeen, (int)p1hpPct);
                    string ttd = shadowBot.LastTimeToDeathSeconds >= 999999f ? "inf" : shadowBot.LastTimeToDeathSeconds.ToString("F1");
                    string tti = shadowBot.LastTimeToInvestSeconds >= 999999f ? "inf" : shadowBot.LastTimeToInvestSeconds.ToString("F1");

                    if (p1Action == 9)
                    {
                        humanInvests++;
                        if (shadowBot.LastDecisionWasDanger) humanInvestedWhileBotSaysDanger++;
                    }
                    if (p1Action == 10) humanRepairs++;
                    if (botWould == "INVEST" && p1Action != 9) botWouldInvestButDidnt++;

                    Console.WriteLine($"{t}\t{t / 30}\t{actionName}\t{state.Player1.Money:F1}\t{state.Player1.Income:F1}\t{state.Player1.InvestmentCount}\t{p1hpPct:F0}\t{p2units}\t{shadowBot.LastDecisionWasDanger}\t{ttd}\t{tti}\t{shadowBot.LastThreatScore:F1}\t{shadowBot.LastDefenseScore:F1}\t{botWould}");
                }

                engine.ApplyAction(1, p1Action);
                engine.ApplyAction(2, p2Action);
                engine.Tick();
            }

            Console.WriteLine($"[Summary] {gameId}{opponentTag}: humanInvests={humanInvests} humanRepairs={humanRepairs} " +
                               $"minHP%={minHpPctSeen} humanInvestedWhileBotSaysDanger={humanInvestedWhileBotSaysDanger} " +
                               $"botWouldInvestButHumanDidnt={botWouldInvestButDidnt} finalWinner=P{state.WinnerSide} " +
                               $"finalTick={state.CurrentTick} ({state.CurrentTick / 30}s)");
        }

        // ── ACTION DISTRIBUTION ANALYSIS ───────────────────────────────────────────
        // What fraction of a human's non-idle actions are invest / repair / spawn a
        // given tier / use each gadget slot? Used as a behavioral baseline to compare
        // the heuristic bot against (see CastleDefense.BotArena's matching mode) --
        // rather than chasing win rate against specific opponents, check whether the
        // bot's own action mix looks like how humans actually play.
        public static readonly string[] ActionLabels = new[]
        {
            "wait", "spawnT1", "spawnT2", "spawnT3", "spawnT4", "spawnT5", "spawnT6", "spawnT7", "spawnT8",
            "invest", "repair", "offenseGadget", "defenseGadget", "sigGadget"
        };

        static void AnalyzeActionDistribution(string replayDir, bool bothPlayersHuman)
        {
            if (!Directory.Exists(replayDir))
            {
                Console.Error.WriteLine($"[Actions] Directory not found: {replayDir}");
                Environment.Exit(1);
            }

            var files = Directory.GetFiles(replayDir, "*.replay");
            if (files.Length == 0)
            {
                Console.Error.WriteLine($"[Actions] No .replay files in {replayDir}");
                Environment.Exit(1);
            }
            Console.WriteLine($"[Actions] Analysing {files.Length} replay(s) in {replayDir} (human side(s): {(bothPlayersHuman ? "P1+P2" : "P1 only")})\n");

            long[] counts = new long[14];
            long totalNonWait = 0;
            long investAvail = 0, investOther = 0;
            int gamesUsed = 0;

            foreach (var path in files.OrderBy(x => x))
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    using var r = new BinaryReader(stream, Encoding.UTF8);

                    var magic = r.ReadBytes(4);
                    if (magic[0] != 'C' || magic[1] != 'D' || magic[2] != 'R' || magic[3] != 'P')
                        throw new InvalidDataException("Not a CDRP file");

                    byte version = r.ReadByte();
                    string gameId = Encoding.ASCII.GetString(r.ReadBytes(6));
                    r.ReadInt64();
                    ReadStr(r);
                    string p1Team = ReadStr(r);
                    string p1Off = ReadStr(r); string p1Def = ReadStr(r); string p1Sig = ReadStr(r);
                    string p2Team = ReadStr(r);
                    string p2Off = ReadStr(r); string p2Def = ReadStr(r); string p2Sig = ReadStr(r);
                    byte winner = r.ReadByte();

                    long startingTick = 0;
                    double p1StartMoney = 0, p2StartMoney = 0;
                    if (version >= 2)
                    {
                        startingTick = r.ReadInt64();
                        p1StartMoney = r.ReadDouble();
                        p2StartMoney = r.ReadDouble();
                    }

                    uint tickCount = r.ReadUInt32();

                    var l1 = new[] { p1Off, p1Def, p1Sig }.Where(s => !string.IsNullOrEmpty(s)).ToArray();
                    var l2 = new[] { p2Off, p2Def, p2Sig }.Where(s => !string.IsNullOrEmpty(s)).ToArray();

                    if (version == 1 && tickCount > 0)
                    {
                        long ap = stream.Position;
                        byte[] probe = r.ReadBytes(Math.Min((int)tickCount, 100) * 2);
                        stream.Seek(ap, SeekOrigin.Begin);
                        var (ts, bonus) = InferV1TimeMachineState(p1Team, l1, p2Team, l2, probe, 100);
                        startingTick = 30L * 30 * ts;
                        var proto = new PlayerState(ts);
                        double amt = bonus ? proto.InvestmentPrice + proto.Income : proto.Money;
                        p1StartMoney = amt; p2StartMoney = amt;
                    }

                    int timeSkip = (int)(startingTick / (30 * 30));
                    var state = new GameState();
                    state.Player1 = new PlayerState(timeSkip); state.Player2 = new PlayerState(timeSkip);
                    state.Player1.Side = 1; state.Player2.Side = 2;
                    state.Player1.Money = p1StartMoney; state.Player2.Money = p2StartMoney;
                    state.CurrentTick = startingTick;
                    state.Player1.Team = Enum.Parse<TeamColour>(p1Team, ignoreCase: true);
                    state.Player2.Team = Enum.Parse<TeamColour>(p2Team, ignoreCase: true);
                    if (l1.Length == 3) state.Player1.SetLoadout(l1);
                    if (l2.Length == 3) state.Player2.SetLoadout(l2);
                    var engine = new GameEngine(state);

                    for (uint t = 0; t < tickCount && !state.IsGameOver; t++)
                    {
                        byte p1Action = r.ReadByte();
                        byte p2Action = r.ReadByte();

                        int[] p1Mask = state.GetActionMask(1);
                        if (p1Mask[9] == 1 && p1Action != 0)
                        {
                            investAvail++;
                            if (p1Action != 9) investOther++;
                        }
                        if (p1Action < counts.Length) counts[p1Action]++;
                        if (p1Action != 0) totalNonWait++;

                        if (bothPlayersHuman)
                        {
                            if (p2Action < counts.Length) counts[p2Action]++;
                            if (p2Action != 0) totalNonWait++;
                        }

                        engine.ApplyAction(1, p1Action);
                        engine.ApplyAction(2, p2Action);
                        engine.Tick();
                    }

                    gamesUsed++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [skip] {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            Console.WriteLine($"[Actions] Used {gamesUsed}/{files.Length} game(s)\n");
            Console.WriteLine("─── ACTION DISTRIBUTION (% of non-wait actions) ────────────────────");
            for (int i = 1; i < ActionLabels.Length; i++)
            {
                double pct = totalNonWait > 0 ? counts[i] * 100.0 / totalNonWait : 0;
                Console.WriteLine($"  {ActionLabels[i],-16} {counts[i],8}  ({pct,5:F2}%)");
            }
            Console.WriteLine($"  {"[wait]",-16} {counts[0],8}");
            Console.WriteLine();
            if (investAvail > 0)
                Console.WriteLine($"  Invest available, chose other: {investOther}/{investAvail} ({investOther * 100.0 / investAvail:F2}%)");
        }

        // ── CALIBRATION DATA COLLECTION ────────────────────────────────────────────
        // Runs N games at full speed (no networking), sampling the 6 board-eval
        // component scores every 30 ticks, then labels each sample with the winner.
        // Output is a lightweight CSV — no .replay files are written.

        static void CollectCalibrationData(int nGames, string onnxPath, string outCsv)
        {
            const int SAMPLE_EVERY   = 30;
            const int BRAIN_FREQ     = 9;
            const int LOG_EVERY      = 500;
            const int MAX_TICKS_GAME = GameEngine.MAX_TICKS;

            // Build a diverse pool: AI brains (league + base model) + simple bots.
            // P1 and P2 are drawn independently each game, so any matchup is possible.
            // Using a Func<GameState, int, int> delegate means bots and brains are interchangeable.
            var aiBrains = new List<(string name, AIBrain brain)>();

            if (File.Exists(onnxPath))
            {
                try
                {
                    aiBrains.Add((Path.GetFileNameWithoutExtension(onnxPath), new AIBrain(onnxPath)));
                    Console.WriteLine($"[Calib] Base model : {Path.GetFileNameWithoutExtension(onnxPath)}");
                }
                catch { Console.WriteLine($"[Calib] Could not load {onnxPath}"); }
            }

            string leagueDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "league_models");
            if (Directory.Exists(leagueDir))
            {
                foreach (var f in Directory.GetFiles(leagueDir, "*.onnx").Where(x => !x.EndsWith(".data")))
                {
                    try
                    {
                        aiBrains.Add((Path.GetFileNameWithoutExtension(f), new AIBrain(f)));
                        Console.WriteLine($"[Calib] League model: {Path.GetFileNameWithoutExtension(f)}");
                    }
                    catch (Exception ex) { Console.WriteLine($"[Calib] Skip {Path.GetFileName(f)}: {ex.Message}"); }
                }
            }

            // Build the unified pool: (name, action-selector delegate)
            // Delegate signature: (GameState state, int side) -> int action
            var pool = new List<(string name, Func<GameState, int, int> pick)>();

            foreach (var (name, brain) in aiBrains)
            {
                var b = brain;  // capture for lambda
                pool.Add((name, (state, side) =>
                    b.GetBestAction(state.GetStateVector(side), state.GetActionMask(side))));
            }

            // Simple bots — included so calibration covers weaker opponents and diverse play styles
            var randBot  = new RandomBot();
            var antiBot  = new AntiSpamBot();
            pool.Add(("RandomBot",  (state, side) => GetRandomValidAction(state.GetActionMask(side))));
            pool.Add(("AntiSpamBot",(state, side) => antiBot.GetAction(state.CurrentTick,
                                                        side == 1 ? state.Player2.Team : state.Player1.Team)));
            foreach (int tier in new[] { 1, 2, 4, 6, 8 })
            {
                var sb = new SpamBot(tier);
                pool.Add(($"SpamBotT{tier}", (state, side) => sb.GetAction()));
            }

            Console.WriteLine($"[Calib] Pool: {pool.Count} players ({aiBrains.Count} AI + {pool.Count - aiBrains.Count} bots). " +
                              $"P1 and P2 drawn independently per game.");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outCsv))!);

            int p1Wins = 0, p2Wins = 0;
            bool writeHeader = !File.Exists(outCsv);

            using var sw = new StreamWriter(outCsv, append: true);
            if (writeHeader)
                sw.WriteLine("hp_score,income_score,money_score,army_score,gadget_score,repair_score,winner");

            for (int g = 0; g < nGames; g++)
            {
                var p1Player = pool[_rand.Next(pool.Count)];
                var p2Player = pool[_rand.Next(pool.Count)];

                var state  = new GameState();
                state.Player1 = new PlayerState();
                state.Player2 = new PlayerState();
                state.Player1.Side = 1; state.Player2.Side = 2;
                state.Player1.Team = GameDataManager.GetRandomTeam();
                state.Player2.Team = GameDataManager.GetRandomTeam();
                state.Player1.SetLoadout(new[] {
                    GameDataManager.GetRandomOGadgetId(),
                    GameDataManager.GetRandomDGadgetId(),
                    GameDataManager.GetSignatureGadgetIdForTeam(state.Player1.Team) });
                state.Player2.SetLoadout(new[] {
                    GameDataManager.GetRandomOGadgetId(),
                    GameDataManager.GetRandomDGadgetId(),
                    GameDataManager.GetSignatureGadgetIdForTeam(state.Player2.Team) });

                var engine  = new GameEngine(state);
                var samples = new List<(float hp, float income, float money, float army, float gadget, float repair)>();

                int p1Action = 0;
                int p2Action = 0;

                while (!state.IsGameOver && state.CurrentTick < MAX_TICKS_GAME)
                {
                    if (state.CurrentTick % SAMPLE_EVERY == 0)
                        samples.Add(state.GetEvalComponents());

                    if (state.CurrentTick % BRAIN_FREQ == 0)
                    {
                        p1Action = p1Player.pick(state, 1);
                        p2Action = p2Player.pick(state, 2);
                    }

                    engine.ApplyAction(1, p1Action);
                    engine.ApplyAction(2, p2Action);
                    engine.Tick();
                }

                int winner = state.WinnerSide;
                if (winner == 0)
                    winner = state.Player1.CastleHealth >= state.Player2.CastleHealth ? 1 : 2;

                if (winner == 1) p1Wins++; else p2Wins++;

                foreach (var (hp, income, money, army, gadget, repair) in samples)
                    sw.WriteLine($"{hp:F4},{income:F4},{money:F4},{army:F4},{gadget:F4},{repair:F4},{winner}");

                if ((g + 1) % LOG_EVERY == 0 || g == nGames - 1)
                {
                    int done = g + 1;
                    Console.WriteLine($"[Calib] {done}/{nGames} games | P1: {p1Wins}  P2: {p2Wins} " +
                                      $"| P1 win rate: {(double)p1Wins/done*100:F0}%");
                }
            }

            foreach (var (_, b) in aiBrains) b?.Dispose();
            Console.WriteLine($"[Calib] Written to {outCsv}");
        }

        // ── EVAL EXPORT ────────────────────────────────────────────────────────────

        static void ExportEvalForReplay(string path, StreamWriter sw)
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream, Encoding.UTF8);

            var magic = r.ReadBytes(4);
            if (magic[0] != 'C' || magic[1] != 'D' || magic[2] != 'R' || magic[3] != 'P')
                throw new InvalidDataException("Not a CDRP replay file");

            byte   version   = r.ReadByte();
            string gameId    = Encoding.ASCII.GetString(r.ReadBytes(6));
            r.ReadInt64();  // timestamp
            ReadStr(r);     // game_version
            string p1Team    = ReadStr(r);
            string p1Off     = ReadStr(r);
            string p1Def     = ReadStr(r);
            string p1Sig     = ReadStr(r);
            string p2Team    = ReadStr(r);
            string p2Off     = ReadStr(r);
            string p2Def     = ReadStr(r);
            string p2Sig     = ReadStr(r);
            byte   winner    = r.ReadByte();

            long   startingTick = 0;
            double p1StartMoney = 0, p2StartMoney = 0;
            if (version >= 2)
            {
                startingTick = r.ReadInt64();
                p1StartMoney = r.ReadDouble();
                p2StartMoney = r.ReadDouble();
            }

            uint tickCount = r.ReadUInt32();

            var l1 = new[] { p1Off, p1Def, p1Sig }.Where(s => !string.IsNullOrEmpty(s)).ToArray();
            var l2 = new[] { p2Off, p2Def, p2Sig }.Where(s => !string.IsNullOrEmpty(s)).ToArray();

            if (version == 1 && tickCount > 0)
            {
                long   ap    = stream.Position;
                byte[] probe = r.ReadBytes(Math.Min((int)tickCount, 100) * 2);
                stream.Seek(ap, SeekOrigin.Begin);
                var (ts, bonus) = InferV1TimeMachineState(p1Team, l1, p2Team, l2, probe, 100);
                startingTick    = 30L * 30 * ts;
                var proto       = new PlayerState(ts);
                double amt      = bonus ? proto.InvestmentPrice + proto.Income : proto.Money;
                p1StartMoney    = amt; p2StartMoney = amt;
            }

            int timeSkip = (int)(startingTick / (30 * 30));
            var state = new GameState();
            state.Player1 = new PlayerState(timeSkip);
            state.Player2 = new PlayerState(timeSkip);
            state.Player1.Side = 1;
            state.Player2.Side = 2;
            state.Player1.Money = p1StartMoney;
            state.Player2.Money = p2StartMoney;
            state.CurrentTick = startingTick;
            state.Player1.Team = Enum.Parse<TeamColour>(p1Team, ignoreCase: true);
            state.Player2.Team = Enum.Parse<TeamColour>(p2Team, ignoreCase: true);
            if (l1.Length == 3) state.Player1.SetLoadout(l1);
            if (l2.Length == 3) state.Player2.SetLoadout(l2);

            var engine   = new GameEngine(state);
            const float TICKS_PER_SEC = 30f;
            int  rows    = 0;

            void WriteRow(long tick, float timeSec)
            {
                var (hp, income, money, army, gadget, repair) = state.GetEvalComponents();
                float eval = state.EvaluateBoard();
                sw.WriteLine($"{gameId},{tick},{timeSec:F2},{eval:F4},{winner},{hp:F4},{income:F4},{money:F4},{army:F4},{gadget:F4},{repair:F4}");
            }

            // Write initial state before any action
            WriteRow(startingTick, startingTick / TICKS_PER_SEC);
            rows++;

            for (uint t = 0; t < tickCount && !state.IsGameOver; t++)
            {
                byte p1Action = r.ReadByte();
                byte p2Action = r.ReadByte();

                engine.ApplyAction(1, p1Action);
                engine.ApplyAction(2, p2Action);
                engine.Tick();

                float timeSec = state.CurrentTick / TICKS_PER_SEC;
                WriteRow(state.CurrentTick, timeSec);
                rows++;
            }

            Console.WriteLine($"[Eval] {gameId} ({p1Team} vs {p2Team}): {tickCount} ticks, winner=P{winner}, {rows} rows");
        }

        static void ExportEvalData(string replayDir, string outputPath)
        {
            if (!Directory.Exists(replayDir))
            {
                Console.Error.WriteLine($"[Eval] ERROR: replay directory not found: {replayDir}");
                Environment.Exit(1);
            }

            var replayFiles = Directory.GetFiles(replayDir, "*.replay");
            if (replayFiles.Length == 0)
            {
                Console.Error.WriteLine($"[Eval] ERROR: no .replay files in {replayDir}");
                Environment.Exit(1);
            }

            Console.WriteLine($"[Eval] Found {replayFiles.Length} replay file(s) in {replayDir}");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

            using var sw = new StreamWriter(outputPath);
            sw.WriteLine("game_id,tick,time_seconds,board_eval,winner,hp_score,income_score,money_score,army_score,gadget_score,repair_score");

            foreach (var f in replayFiles)
            {
                try   { ExportEvalForReplay(f, sw); }
                catch (Exception ex) { Console.Error.WriteLine($"[Eval] Skip {Path.GetFileName(f)}: {ex.Message}"); }
            }

            Console.WriteLine($"[Eval] Written to {outputPath}");
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
