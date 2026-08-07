using CastleDefense.Api.Data;
using CastleDefense.Api.Hubs;
using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace CastleDefense.Api.Services
{
    public class GameHostingService : BackgroundService
    {
        private readonly ConcurrentDictionary<string, GameEngine> _activeGames = new();
        private readonly ConcurrentDictionary<string, GameEngine> _lobbyGames = new();
        private readonly ConcurrentDictionary<string, GameRecorder> _recorders = new();
        private readonly ConcurrentDictionary<string, Func<GameState, int>> _leagueOpponents = new();
        // Training League's "watch mode": both sides are AI, the connecting browser is
        // a pure spectator (see GameHub.JoinGame's "league" branch). P2 is always the
        // HeuristicBot (via _heuristicOpponents, same mechanism singleplayer already
        // uses); this dictionary drives P1 instead, via a specific ONNX league model.
        // Separate from _leagueOpponents (which only ever drives side 2) so Practice
        // mode -- which also uses "league"'s sibling _leagueOpponents mechanism for a
        // human-picked P2 opponent -- can never have its real human P1 overridden.
        private readonly ConcurrentDictionary<string, Func<GameState, int>> _leagueP1Opponents = new();
        // HeuristicBot doesn't fit the Func<GameState,int> pattern above -- it drives
        // engine.Invest/Repair/SpawnUnit/UseGadget directly (and can take more than one
        // of those in a single decision, e.g. a gadget cast AND an investment), and it
        // carries real per-game mutable state (decision cadence, rolling HP-drain window)
        // that must persist tick-to-tick, so it needs its own per-game instance rather
        // than a stateless closure. See ExecuteAsync for how it's driven.
        private readonly ConcurrentDictionary<string, HeuristicBot> _heuristicOpponents = new();

        // Singleplayer's opponent as of 2026-07-28: one-ply rollout search over the engine,
        // with HeuristicBot as its policy prior. Measured at 92.5% [80.1, 97.4] vs
        // HeuristicBot with every game ending decisively and an earned-investment lead of
        // +0.55 (search-test --live, n=40). Same class the benchmark harness drives, so
        // what gets played is exactly what gets measured — run `search-test --live` to
        // reproduce the shipping configuration exactly.
        //
        // Driven identically to HeuristicBot below — every real tick, self-throttling on
        // state.CurrentTick — because it manages its own decision cadence internally.
        private readonly ConcurrentDictionary<string, RolloutSearchBot> _searchOpponents = new();

        // Same thing driving side 1, for "Watch bots" (search vs HeuristicBot). Separate
        // dictionary rather than one keyed by side, mirroring the existing
        // _leagueOpponents / _leagueP1Opponents split, so a side-1 bot can never be
        // installed into a game where a human holds side 1.
        private readonly ConcurrentDictionary<string, RolloutSearchBot> _searchP1Opponents = new();
        // Human-readable description of what _leagueOpponents[gameId] actually is
        // ("spam4", "antispam", "model:castle_defense_p1_v21", "random") -- persisted to
        // the DB at game-end so recorded games can be mined later for "did the human
        // already beat this specific opponent" without re-deriving it from action
        // sequences. Previously this selection only ever lived in the Func closure
        // itself and was discarded at game-end, unrecoverable after the fact.
        private readonly ConcurrentDictionary<string, string> _opponentDescriptions = new();
        private readonly IHubContext<GameHub> _hubContext;
        private readonly AIBrain _aiBrain;
        private readonly List<(string name, AIBrain brain)> _leagueModels = new();
        private readonly GameDatabase _db;
        private readonly Random _watchRng  = new();
        private readonly Random _leagueRng = new();
        private readonly string _replayDir;
        private readonly string _gameVersion;

        public GameHostingService(IHubContext<GameHub> hubContext, AIBrain aiBrain,
            GameDatabase db, IConfiguration config, IHostEnvironment env)
        {
            _hubContext   = hubContext;
            _aiBrain      = aiBrain;
            _db           = db;
            // See the comment on dbPath in Program.cs -- recordings live under
            // ContentRootPath, not bin/, so a build cleanup can't destroy them.
            _replayDir    = Path.Combine(env.ContentRootPath, "recordings");
            _gameVersion  = config["GameVersion"] ?? "v1.0";

            string leagueDir = Path.Combine(AppContext.BaseDirectory, "league_models");
            if (Directory.Exists(leagueDir))
            {
                foreach (var f in Directory.GetFiles(leagueDir, "*.onnx")
                                           .Where(x => !x.EndsWith(".data")))
                {
                    try
                    {
                        _leagueModels.Add((Path.GetFileNameWithoutExtension(f), new AIBrain(f)));
                        Console.WriteLine($"[League] Loaded: {Path.GetFileNameWithoutExtension(f)}");
                    }
                    catch (Exception ex) { Console.WriteLine($"[League] Skip {Path.GetFileName(f)}: {ex.Message}"); }
                }
            }
            Console.WriteLine($"[League] {_leagueModels.Count} model(s) available for Training League.");
        }

        // ── Opponent selection ─────────────────────────────────────────────────────

        public void SetupLeagueOpponent(string gameId, int timeSkip)
        {
            int botSel   = _leagueRng.Next(16);
            int spamTier = Math.Max(Math.Min(timeSkip + _leagueRng.Next(-2, 3), 8), 1);

            Func<GameState, int> opponent;
            string description;

            if (botSel == 1)
            {
                opponent = state => AntiSpamAction(state.CurrentTick, state.Player2.Team);
                description = "antispam";
            }
            else if (botSel >= 2 && botSel <= 5)
            {
                opponent = _ => spamTier;
                description = $"spam{spamTier}";
            }
            else if (botSel > 5 && _leagueModels.Count > 0)
            {
                var chosen = _leagueModels[_leagueRng.Next(_leagueModels.Count)];
                opponent = state => chosen.brain.GetBestAction(
                    state.GetStateVector(2), state.GetActionMask(2));
                description = $"model:{chosen.name}";
            }
            else
            {
                // Random dummy
                opponent = state =>
                {
                    var mask  = state.GetActionMask(2);
                    var valid = Enumerable.Range(0, mask.Length).Where(i => mask[i] == 1).ToList();
                    return valid[_leagueRng.Next(valid.Count)];
                };
                description = "random";
            }

            _leagueOpponents[gameId] = opponent;
            _opponentDescriptions[gameId] = description;
        }

        // Training League "watch mode": P1 = castle_defense_p1_v4 (ONNX, via the same
        // league_models pool used elsewhere), P2 = HeuristicBot -- the connecting
        // browser is a spectator, not a player (see GameHub.JoinGame's "league"
        // branch, which no longer assigns Player1.ConnectionId to the caller). Built
        // per Marc's request to watch this specific matchup play out and diagnose why
        // it's the bot's single worst one, mirroring the "system to watch the bots
        // play each other" he'd set up before.
        /// <summary>
        /// "Watch bots": P1 = the rollout-search bot, P2 = HeuristicBot.
        ///
        /// Changed 2026-07-28 from castle_defense_p1_v4 (ONNX) vs HeuristicBot. v4 was the
        /// best ONNX checkpoint the project produced and still loses to HeuristicBot ~83%,
        /// so that matchup showed a weak agent being beaten. Search wins ~85% at this
        /// horizon with every game decisive, which is the matchup actually worth watching.
        ///
        /// Same configuration as singleplayer so what is observed here is what gets played.
        /// </summary>
        public void SetupTrainingLeagueWatchMatch(string gameId)
        {
            _searchP1Opponents[gameId] = new RolloutSearchBot(
                side: 1,
                // Same tuned configuration as singleplayer — see SetupSearchOpponent
                // for the measurements behind these three numbers.
                decisionInterval: 15,
                horizon: 300,
                rolloutsPerAction: 1,
                seed: Environment.TickCount ^ gameId.GetHashCode(),
                usePrior: true,
                overrideMargin: 0.10,
                useMacro: true,
                usePressMacro: true,
                // THE SEARCH BLOCKS THE TICK LOOP, so this budget is a visible freeze, not
                // spare capacity. A tick is 33ms; anything above that shows up as a stutter
                // regardless of how much real time is left before the next decision. 120ms
                // was chosen against the 333ms decision interval and was still four ticks
                // of frozen game every ten — which is what "still noticeable" was.
                //
                // 30ms keeps each decision inside a single tick. With ~18 cores that is
                // still ~540ms of CPU work per decision; the horizon just shortens under
                // pressure instead of the game hitching.
                // With asyncDecisions the search no longer blocks the tick loop, so this
                // can go back up: it now bounds how STALE a decision may be, not how long
                // the game freezes. 250ms is under one decision interval (333ms), so the
                // bot still acts on schedule.
                maxDecisionMs: 250,
                // One live game has the whole machine. Leave 2 cores for the OS, the web
                // server and the browser.
                maxParallelism: Math.Max(1, Environment.ProcessorCount - 2),
                asyncDecisions: true);
            _heuristicOpponents[gameId] = new HeuristicBot(2);
            _opponentDescriptions[gameId] = "leaguewatch:search_vs_heuristic";
        }

        // Practice mode: same opponent-execution mechanism as Training League
        // (a Func<GameState,int> stashed in _leagueOpponents and invoked identically in
        // ExecuteAsync), but the human picks the opponent explicitly instead of a random
        // roll -- built specifically to let a human play the bot's own worst-performing
        // matchups on demand (e.g. Green team vs Tier4 spam, or vs a specific ONNX
        // checkpoint like v4/v7) rather than waiting for League's dice roll to happen to
        // land on one. opponentSpec is one of: "spam1".."spam8", "antispam", or a
        // substring match against a loaded league model's filename (e.g. "v4").
        // Returns the resolved human-readable description (what actually got set up),
        // or null if opponentSpec couldn't be resolved to anything.
        public string SetupPracticeOpponent(string gameId, string opponentSpec)
        {
            if (string.IsNullOrWhiteSpace(opponentSpec)) return null;
            string spec = opponentSpec.Trim();

            // Checked before the generic model-name .Contains() fallback below so a
            // future league model can never accidentally shadow this exact-match spec.
            if (string.Equals(spec, "heuristic", StringComparison.OrdinalIgnoreCase))
            {
                SetupHeuristicOpponent(gameId);
                return "heuristic";
            }

            Func<GameState, int> opponent;
            string description;

            if (string.Equals(spec, "antispam", StringComparison.OrdinalIgnoreCase))
            {
                opponent = state => AntiSpamAction(state.CurrentTick, state.Player2.Team);
                description = "antispam";
            }
            else if (spec.StartsWith("spam", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(spec.Substring(4), out var tier) && tier >= 1 && tier <= 8)
            {
                opponent = _ => tier;
                description = $"spam{tier}";
            }
            else
            {
                var chosen = _leagueModels.FirstOrDefault(m => m.name.Contains(spec, StringComparison.OrdinalIgnoreCase));
                if (chosen.brain == null) return null; // unresolvable -- caller should reject the join
                opponent = state => chosen.brain.GetBestAction(state.GetStateVector(2), state.GetActionMask(2));
                description = $"model:{chosen.name}";
            }

            _leagueOpponents[gameId] = opponent;
            _opponentDescriptions[gameId] = description;
            return description;
        }

        // Singleplayer's opponent: the same tuned HeuristicBot used throughout this
        // codebase's own benchmarking (CastleDefense.BotArena) and recording analysis
        // (--trace-human), not the deployed ONNX model. A fresh instance per game since
        // it carries real per-game state (decision cadence, rolling HP window) that must
        // not leak between games.
        public void SetupHeuristicOpponent(string gameId)
        {
            _heuristicOpponents[gameId] = new HeuristicBot(2);
            _opponentDescriptions[gameId] = "heuristic";
        }

        /// <summary>
        /// Singleplayer's opponent: rollout search with HeuristicBot as its policy prior.
        ///
        /// Parameters are the best measured configuration (search-test, 2026-07-28):
        /// decide every 5 ticks matching HeuristicBot's own cadence, 1800-tick (60s)
        /// rollout horizon, override margin 0.03, both macros on, linear evaluator.
        ///
        /// HORIZON IS THE COST KNOB, and the original figures here were WRONG. search-test
        /// divided total wall clock by total decisions while running 18 games in parallel,
        /// which understated single-game latency by ~18x. Real per-decision cost, taken
        /// from per-game timings: horizon 1800 peaks near 490ms, 900 near 150ms, 300 near
        /// 100ms. A 5-tick interval allows 167ms, so 1800 was roughly 3x over budget and
        /// made the live game visibly sluggish.
        ///
        /// With rollouts properly parallelised (one Task per candidate) a decision costs
        /// ~7ms, worst case ~14ms, against the 333ms a 10-tick interval allows. That is
        /// roughly 45x headroom, so the 250ms cap never actually binds — `--live` produces
        /// results identical to running with no cap at all.
        ///
        /// The seed is per-game so two games from the same position don't play identically,
        /// while any single game stays internally reproducible for debugging.
        /// </summary>
        public void SetupSearchOpponent(string gameId)
        {
            _searchOpponents[gameId] = new RolloutSearchBot(
                side: 2,
                // TUNED 2026-08-05, replacing 10 / 900 / 0.03. Every figure below is
                // n=600, paired seeds, vs the current HeuristicBot:
                //
                //   horizon  900 -> 300   47.0% -> 68.5%   the single largest factor
                //   margin  0.03 -> 0.10  69.8% -> 75.0%
                //   interval  10 -> 15    62.5% -> 68.5%
                //
                // Horizon 900 washes the candidate action out: HeuristicBot drives BOTH
                // sides for 30 simulated seconds, so every branch converges to the same
                // self-play continuation and the search is scoring noise. Do NOT drop the
                // horizon below ~300 either — at 250 it falls to 30% and at 150 to 1%,
                // because the rollout then ends before an investment can repay, so buying
                // units always outscores investing and the bot never builds an economy.
                //
                // The high margin is not a tuning artefact. Search's PRIMITIVE moves are
                // actively harmful (margin 0.0 scores 56.0% and earns 1.5 FEWER investments
                // per game than HeuristicBot); its save-invest MACRO is the entire source of
                // strength (disabling it scores 44.0% — worse than not searching at all). A
                // high margin filters the former while still letting the latter through.
                // Search now overrides on ~8% of decisions and out-invests HeuristicBot by
                // +0.72 per game, which is what actually wins.
                decisionInterval: 15,
                horizon: 300,
                rolloutsPerAction: 1,
                seed: Environment.TickCount ^ gameId.GetHashCode(),
                usePrior: true,
                overrideMargin: 0.10,
                useMacro: true,
                usePressMacro: true,
                // THE SEARCH BLOCKS THE TICK LOOP, so this budget is a visible freeze, not
                // spare capacity. A tick is 33ms; anything above that shows up as a stutter
                // regardless of how much real time is left before the next decision. 120ms
                // was chosen against the 333ms decision interval and was still four ticks
                // of frozen game every ten — which is what "still noticeable" was.
                //
                // 30ms keeps each decision inside a single tick. With ~18 cores that is
                // still ~540ms of CPU work per decision; the horizon just shortens under
                // pressure instead of the game hitching.
                // With asyncDecisions the search no longer blocks the tick loop, so this
                // can go back up: it now bounds how STALE a decision may be, not how long
                // the game freezes. 250ms is under one decision interval (333ms), so the
                // bot still acts on schedule.
                maxDecisionMs: 250,
                // One live game has the whole machine. Leave 2 cores for the OS, the web
                // server and the browser.
                maxParallelism: Math.Max(1, Environment.ProcessorCount - 2),
                asyncDecisions: true);
            _opponentDescriptions[gameId] = "search";
        }

        // Lets the practice-mode opponent picker show only opponents that actually
        // exist right now (the loaded league model list can vary between machines/runs).
        // "heuristic" is always available (it has no external model file dependency) and
        // is listed alongside the league models in the same dropdown -- see select-level.js.
        public (int[] spamTiers, bool antiSpamAvailable, string[] modelNames) GetPracticeOpponentOptions()
        {
            var names = new[] { "heuristic" }.Concat(_leagueModels.Select(m => m.name)).OrderBy(n => n).ToArray();
            return (Enumerable.Range(1, 8).ToArray(), true, names);
        }

        private static int AntiSpamAction(long tick, TeamColour team)
        {
            if (tick <= 421)  return 10;
            if (tick <= 781)  return 9;
            if (tick <= 1050) return 3;
            if (tick <= 1380) return 10;
            if (tick <= 1590) return 3;
            if (tick <= 2100) return 9;
            return team == TeamColour.Orange ? 5
                 : (team == TeamColour.Purple || team == TeamColour.Blue) ? 3
                 : 4;
        }

        // ── Game lifecycle ─────────────────────────────────────────────────────────

        public GameEngine GetGame(string gameId)
        {
            if (_activeGames.TryGetValue(gameId, out var activeEngine)) return activeEngine;
            if (_lobbyGames.TryGetValue(gameId, out var lobbyEngine)) return lobbyEngine;
            return null;
        }

        public GameListResult GetAllGameIds()
        {
            return new GameListResult
            {
                ActiveGames = _activeGames.Keys.ToList(),
                LobbyGames  = _lobbyGames.Keys.ToList()
            };
        }

        public string CreateGame(string gameMode)
        {
            var gameId = Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper();
            var state  = new GameState();
            state.GameMode = gameMode;
            var engine = new GameEngine(state);

            engine.OnGadgetAnimation += (gadgetId, side, position, targetId) =>
            {
                _hubContext.Clients.Group(gameId).SendAsync("PlayGadgetAnimation", gadgetId, side, position, targetId);
                if (_recorders.TryGetValue(gameId, out var rec))
                    rec.RecordGadgetUse((int)engine._state.CurrentTick, side, gadgetId);
            };
            engine.OnGadgetUpgraded += (side, newGadgetDef) =>
            {
                _hubContext.Clients.Group(gameId).SendAsync("GadgetUpgraded", side, newGadgetDef);
            };

            _lobbyGames.TryAdd(gameId, engine);
            return gameId;
        }

        public string StartGame(string gameId)
        {
            if (_lobbyGames.TryRemove(gameId, out var engine))
            {
                _activeGames.TryAdd(gameId, engine);
                _recorders.TryAdd(gameId, new GameRecorder(gameId,
                    engine._state.CurrentTick,
                    engine._state.Player1.Money,
                    engine._state.Player2.Money));
                _hubContext.Clients.Group(gameId).SendAsync("GameStarted");
                return gameId;
            }
            return gameId;
        }

        // ── Main game loop ─────────────────────────────────────────────────────────

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var start = DateTime.UtcNow;

                foreach (var kvp in _activeGames)
                {
                    var gameId = kvp.Key;
                    var engine = kvp.Value;
                    _recorders.TryGetValue(gameId, out var recorder);

                    // ONE GAME MUST NOT BE ABLE TO KILL THE SERVER (added 2026-07-31 after a
                    // live crash). This is a BackgroundService, and since .NET 6 an unhandled
                    // exception in ExecuteAsync stops the HOST by default
                    // (BackgroundServiceExceptionBehavior.StopHost). There was no try/catch
                    // anywhere in this loop, so a single throw from any opponent's Update --
                    // HeuristicBot, the search bot, an ONNX brain -- tore down the whole web
                    // app for every connected player, and took the in-memory log with it, so
                    // nothing survived to diagnose. That is exactly what happened: the process
                    // vanished, the in-progress game was never recorded, and the Windows event
                    // log had nothing.
                    //
                    // Catch per GAME, not per loop iteration, so a single bad game is dropped
                    // and every other game keeps running. The stack trace is printed because
                    // losing it is what made the original crash undiagnosable.
                    try
                    {
                    lock (engine)
                    {
                        engine.ResetLastActions();

                        if (engine._state.CurrentTick % 3 == 0)
                        {
                            // P1 random dummy (watch mode only)
                            if (engine._state.GameMode == "watch")
                            {
                                int[] p1Mask     = engine._state.GetActionMask(1);
                                var   validP1    = Enumerable.Range(0, p1Mask.Length)
                                                             .Where(i => p1Mask[i] == 1).ToList();
                                int   p1Action   = validP1[_watchRng.Next(validP1.Count)];
                                if (p1Action != 0) engine.ApplyAction(1, p1Action);
                            }

                            // Training League watch mode's P1 (v4 ONNX) -- only present
                            // for games set up via SetupTrainingLeagueWatchMatch; Practice
                            // mode's "league" sibling never populates this, so a real human
                            // P1 there is never touched.
                            if (_leagueP1Opponents.TryGetValue(gameId, out var p1Func))
                            {
                                int p1Action = p1Func(engine._state);
                                if (p1Action != 0) engine.ApplyAction(1, p1Action);
                            }

                            // P2 AI / opponent
                            if ((engine._state.GameMode == "sp"  || engine._state.GameMode == "vai" ||
                                engine._state.GameMode == "watch")
                                && !_heuristicOpponents.ContainsKey(gameId)
                                && !_searchOpponents.ContainsKey(gameId))
                            {
                                // Standard ONNX singleplayer bot. This branch must be
                                // suppressed whenever ANOTHER opponent already drives side 2,
                                // or two agents fight over the same player.
                                //
                                // BUG FIXED 2026-07-28: the guard only excluded
                                // _heuristicOpponents. When singleplayer switched to the
                                // search bot, sp games no longer registered a heuristic
                                // opponent, so this ONNX path started running as well —
                                // side 2 was driven by the ONNX model every 3 ticks AND by
                                // the search bot every 5. The ONNX checkpoints earn ~0.0-0.4
                                // investments and spam tier 1, which is exactly how the
                                // opponent played. Any future opponent type needs adding to
                                // this guard too.
                                float[] aiState     = engine._state.GetStateVector(2);
                                int[]   aiActionMask = engine._state.GetActionMask(2);

                                var oob = aiState.Select((v, i) => (v, i)).Where(x => x.v > 1.01f || x.v < -0.01f).ToList();
                                if (oob.Any())
                                {
                                    Console.ForegroundColor = ConsoleColor.Red;
                                    Console.WriteLine($"\n[WARNING] {oob.Count} obs value(s) out of [0,1] range!");
                                    foreach (var (v, i) in oob)
                                        Console.WriteLine($"   -> [{i}] = {v}");
                                    Console.ResetColor();
                                }

                                int aiAction = _aiBrain.GetBestAction(aiState, aiActionMask);
                                if (aiAction != 0) engine.ApplyAction(2, aiAction);
                            }
                            else if (engine._state.GameMode == "league" || engine._state.GameMode == "practice")
                            {
                                // Training-league opponent (random dummy / spam bot / anti-spam / ONNX
                                // model), or the same mechanism driven by a human-picked opponent in
                                // Practice mode -- both just invoke whatever Func was stashed here.
                                if (_leagueOpponents.TryGetValue(gameId, out var oppFunc))
                                {
                                    int p2Action = oppFunc(engine._state);
                                    if (p2Action != 0) engine.ApplyAction(2, p2Action);
                                }
                            }
                        }

                        // HeuristicBot-driven opponent (Singleplayer's tuned bot, or a
                        // Practice-mode pick of it) -- deliberately OUTSIDE the %3==0
                        // gate above and called every real tick. HeuristicBot manages its
                        // own decision cadence internally (~6/sec, every 5 ticks, see
                        // DecisionIntervalTicks) and self-throttles via state.CurrentTick,
                        // exactly like CastleDefense.BotArena's arena loop and
                        // --trace-human's shadow-bot query already call it -- gating it
                        // behind this loop's own %3==0 cadence too would just misalign
                        // the two independent clocks for no benefit.
                        if (_heuristicOpponents.TryGetValue(gameId, out var heuristicBot))
                        {
                            heuristicBot.Update(engine);
                        }

                        // Rollout-search opponent (Singleplayer). Same contract as
                        // HeuristicBot above: called every real tick, self-throttles on its
                        // own decision interval. It internally CLONES this engine to run
                        // rollouts, which is safe — clone-check verifies that advancing a
                        // clone leaves the parent bit-identical.
                        if (_searchOpponents.TryGetValue(gameId, out var searchBot))
                        {
                            searchBot.Update(engine);
                        }

                        // Side-1 search bot — "Watch bots" only. Same every-tick,
                        // self-throttling contract.
                        if (_searchP1Opponents.TryGetValue(gameId, out var searchBotP1))
                        {
                            searchBotP1.Update(engine);
                        }

                        engine.Tick();
                        recorder?.RecordTick(engine.LastActionP1, engine.LastActionP2);
                    }

                    if (engine._state.IsGameOver)
                    {
                        _leagueOpponents.TryRemove(gameId, out _);
                        _leagueP1Opponents.TryRemove(gameId, out _);
                        _heuristicOpponents.TryRemove(gameId, out _);
                        _searchOpponents.TryRemove(gameId, out _);
                        _searchP1Opponents.TryRemove(gameId, out _);
                        _opponentDescriptions.TryRemove(gameId, out var opponentDescription);

                        if (_recorders.TryRemove(gameId, out var finishedRecorder))
                        {
                            bool isSingleplayer = engine._state.GameMode == "sp"
                                               || engine._state.GameMode == "vai"
                                               || engine._state.GameMode == "league"
                                               || engine._state.GameMode == "practice";
                            bool isWatch = engine._state.GameMode == "watch";
                            string subdir = isSingleplayer ? "singleplayer" : "multiplayer";
                            if (!isWatch) finishedRecorder.Save(Path.Combine(_replayDir, subdir),
                                engine._state.Player1, engine._state.Player2,
                                engine._state.WinnerSide, engine._state.CurrentTick, _gameVersion, _db,
                                engine._state.GameMode, opponentDescription);
                        }

                        await _hubContext.Clients.Group(gameId).SendAsync("GameOver", engine._state);
                        _activeGames.TryRemove(gameId, out _);
                    }
                    else
                    {
                        await _hubContext.Clients.Group(gameId).SendAsync("GameStateUpdate", engine._state);
                    }
                    }
                    catch (Exception ex)
                    {
                        // Print the FULL trace -- the whole point of this handler is that the
                        // original crash left nothing behind to diagnose.
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n[GAME LOOP EXCEPTION] game={gameId} " +
                                          $"mode={engine._state.GameMode} tick={engine._state.CurrentTick} " +
                                          $"opponent={(_opponentDescriptions.TryGetValue(gameId, out var od) ? od : "?")}");
                        Console.WriteLine(ex);
                        Console.ResetColor();

                        // Drop this game rather than retrying a state that is already broken:
                        // it would just throw on every subsequent tick and flood the log.
                        // Everything else keeps running, which is the point.
                        _activeGames.TryRemove(gameId, out _);
                        _leagueOpponents.TryRemove(gameId, out _);
                        _leagueP1Opponents.TryRemove(gameId, out _);
                        _heuristicOpponents.TryRemove(gameId, out _);
                        _searchOpponents.TryRemove(gameId, out _);
                        _searchP1Opponents.TryRemove(gameId, out _);
                        _recorders.TryRemove(gameId, out _);
                        _opponentDescriptions.TryRemove(gameId, out _);
                        try { await _hubContext.Clients.Group(gameId).SendAsync("Error", "The game ended unexpectedly."); }
                        catch { /* the client may already be gone; never let cleanup throw */ }
                    }
                }

                var elapsed     = (DateTime.UtcNow - start).TotalMilliseconds;
                var targetDelay = (1000 / GameEngine.TICKS_PER_SECOND) - (int)elapsed;
                if (targetDelay > 0) await Task.Delay(targetDelay, stoppingToken);
            }
        }
    }
}
