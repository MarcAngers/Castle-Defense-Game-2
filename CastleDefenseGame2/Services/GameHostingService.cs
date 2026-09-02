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

        // Engine RNG seed per game, drawn in CreateGame and written into the replay by
        // StartGame so the run is reproducible. See the v3 note in GameRecorder.
        private readonly ConcurrentDictionary<string, int> _gameSeeds = new();

        // Same thing driving side 1, for "Watch bots" (search vs HeuristicBot). Separate
        // dictionary rather than one keyed by side, mirroring the existing
        // _leagueOpponents / _leagueP1Opponents split, so a side-1 bot can never be
        // installed into a game where a human holds side 1.
        private readonly ConcurrentDictionary<string, RolloutSearchBot> _searchP1Opponents = new();

        // Side-1 HeuristicBot -- the "Defence Watch" spectator mode. Separate from
        // _heuristicOpponents (side 2) for the same reason _leagueP1Opponents is separate
        // from _leagueOpponents: a side-1 bot must never be reachable by a mode that has a
        // human in seat 1.
        private readonly ConcurrentDictionary<string, HeuristicBot> _heuristicP1Opponents = new();
        // Human-readable description of what _leagueOpponents[gameId] actually is
        // ("spam4", "antispam", "model:castle_defense_p1_v21", "random") -- persisted to
        // the DB at game-end so recorded games can be mined later for "did the human
        // already beat this specific opponent" without re-deriving it from action
        // sequences. Previously this selection only ever lived in the Func closure
        // itself and was discarded at game-end, unrecoverable after the fact.
        private readonly ConcurrentDictionary<string, string> _opponentDescriptions = new();

        // How a game ended, when that was NOT by play: "disconnect" (the grace window ran
        // out and the survivor was awarded the game) or "abandoned" (nobody was left to
        // award it to). Written straight through to the DB's end_reason column, which is
        // what keeps a default win out of every win-rate number derived from recordings.
        private readonly ConcurrentDictionary<string, string> _endReasons = new();

        /// <summary>
        /// When each game's PRE-GAME window ends. Until then the game exists and is
        /// broadcast so both browsers can show the field, but it is not stepped at all and
        /// the hub refuses actions -- the players are watching, not playing.
        ///
        /// Server-held rather than client-timed for the same reason the disconnect countdown
        /// is: in multiplayer both browsers must open the battle on the SAME tick, and two
        /// client clocks will not agree. Clients are told the remaining milliseconds every
        /// loop pass and drive their camera and countdown off that, so one that joins the
        /// group late still lands mid-intro instead of missing it.
        /// </summary>
        private readonly ConcurrentDictionary<string, DateTime> _preGameUntil = new();

        /// <summary>Length of that window. The client's intro is choreographed against it
        /// (1s hold, 2s pan, 1s settle), so changing it here alone will desynchronise them.</summary>
        public const double PreGameSeconds = 4.0;
        private readonly IHubContext<GameHub> _hubContext;
        private readonly AIBrain _aiBrain;
        private readonly List<(string name, AIBrain brain)> _leagueModels = new();
        private readonly GameDatabase _db;
        private readonly ReconnectService _reconnect;
        private readonly Random _watchRng  = new();
        private readonly Random _leagueRng = new();
        private readonly string _replayDir;
        private readonly string _gameVersion;

        public GameHostingService(IHubContext<GameHub> hubContext, AIBrain aiBrain,
            GameDatabase db, ReconnectService reconnect, IConfiguration config, IHostEnvironment env)
        {
            _hubContext   = hubContext;
            _aiBrain      = aiBrain;
            _db           = db;
            _reconnect    = reconnect;
            // See the comment on dbPath in Program.cs -- recordings live under
            // ContentRootPath, not bin/, so a build cleanup can't destroy them.
            //
            // SAME HELPER AS THE DATABASE, deliberately: if only one of the two honoured
            // the redirect, an agent's games would be half-separated -- files in their own
            // folder but rows still in game_records.db, which is where every win-rate query
            // looks. See RecordingPaths.
            _replayDir    = RecordingPaths.Root(config, env.ContentRootPath);
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
                // WAS "same tuned configuration as singleplayer" - no longer true.
                // SetupSearchOpponent moved to horizon 1600 on 2026-08-19; this watch-mode
                // match is deliberately LEFT at 300 so it still demonstrates the frozen
                // flagship config (FLAGSHIP_BASELINE.md section 5). See SetupSearchOpponent
                // for the measurements behind the change.
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

        /// <summary>
        /// DEFENCE WATCH -- the exact matchup `bot-checksum --p1-defence-only` measures,
        /// made watchable: the defence-only HeuristicBot in seat 1 against the shipped
        /// HeuristicBot in seat 2.
        ///
        /// Deliberately the SAME configuration as the harness, so what is on screen is what
        /// the numbers describe: DefenceOnlyProfile for P1, stock settings for P2, and
        /// GameHub pins both loadouts and skips the headstart. Anything that diverges here
        /// makes this a different experiment that happens to look similar.
        /// </summary>
        public void SetupDefenceWatchMatch(string gameId)
        {
            _heuristicP1Opponents[gameId] = new HeuristicBot(1, HeuristicBotSettings.DefenceOnlyProfile);
            _heuristicOpponents[gameId]   = new HeuristicBot(2);
            _opponentDescriptions[gameId] = "defencewatch:defenceonly_vs_heuristic";
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

        /// <summary>
        /// Which bot singleplayer puts in seat 2: "search" (the flagship RolloutSearchBot,
        /// the default and what ships) or "heuristic" (the plain HeuristicBot).
        ///
        /// Exists so a recording session can be pointed at the SAME opponent the
        /// defence-only bot is benchmarked against -- every number in
        /// CastleDefense.BotArena/stall/BOT_TUNING.md is measured against `new HeuristicBot(2)`,
        /// so a human game against the search bot is not directly comparable to any of them.
        /// Set from appsettings (Singleplayer:Opponent) rather than hard-coded, so switching
        /// back is a config edit and not a rebuild.
        ///
        /// DELIBERATELY DOES NOT AFFECT THE ACCEPTANCE TEST, which has to keep measuring the
        /// bot that actually ships (see GameHub's "accept" branch and FLAGSHIP_BASELINE.md).
        /// </summary>
        public static string SingleplayerOpponent = "search";

        /// <summary>
        /// Pins the MAP for every hosted game -- multiplayer, singleplayer, league, all of
        /// them -- instead of the usual random roll. Null or empty restores the roll.
        ///
        /// TEMPORARY, FOR THE MAP-ATMOSPHERE WORK (2026-08-27). Each map is getting its own
        /// ambient animation, and judging one means being able to land on it on demand
        /// rather than rerolling until it turns up. See CLEANUP_BACKLOG.md -- this must go
        /// back to empty when that work is finished.
        ///
        /// Set from appsettings (Map:ForcedMap) so switching is a config edit, not a
        /// rebuild. NOT a gameplay balance setting, but it IS gameplay-affecting now that
        /// maps carry effects: a pinned map means every game is played under one map's
        /// rules, so nothing measured while this is set is comparable to anything measured
        /// with it clear.
        /// </summary>
        public static TeamColour? ForcedMap = null;

        /// <summary>
        /// Play the flagship PLUS the 2026-08-23 repair fixes (price check, absolute HP floor,
        /// burst rate-limit). Set from appsettings (Singleplayer:RepairFix). False restores the
        /// flagship exactly -- see FLAGSHIP_2026-08-23.md; the guarantee is that with this off
        /// `bot-checksum --games 24` still prints 643A6CA19C1851CF04A2A0C9F873195C.
        /// </summary>
        public static bool SingleplayerRepairFix = false;

        /// <summary>Also stop attacking into an enemy Wave/Blackhole (Singleplayer:HazardFix).</summary>
        public static bool SingleplayerHazardFix = false;

        /// <summary>Also brake killerInstinct near a rung and after a failed push
        /// (Singleplayer:EconomyBrake).</summary>
        public static bool SingleplayerEconomyBrake = false;

        /// <summary>
        /// EconomyBrake plus the cap-8 auto-spawner substitution. Highest precedence of the
        /// singleplayer toggles. Recorded in the DB as `heuristic_autospawn8` so these games
        /// can be told apart from earlier ones during analysis -- without that a play-test
        /// game is indistinguishable from a brake-profile game in game_records.db.
        /// </summary>
        public static bool SingleplayerAutoSpawner = false;

        // Singleplayer's opponent: the same tuned HeuristicBot used throughout this
        // codebase's own benchmarking (CastleDefense.BotArena) and recording analysis
        // (--trace-human), not the deployed ONNX model. A fresh instance per game since
        // it carries real per-game state (decision cadence, rolling HP window) that must
        // not leak between games.
        public void SetupHeuristicOpponent(string gameId)
        {
            _heuristicOpponents[gameId] = new HeuristicBot(2,
                SingleplayerAutoSpawner  ? HeuristicBotSettings.EconomyBrakeAutoSpawnProfile
              : SingleplayerEconomyBrake ? HeuristicBotSettings.EconomyBrakeProfile
              : SingleplayerHazardFix  ? HeuristicBotSettings.RepairFixPlusHazardProfile
              : SingleplayerRepairFix  ? HeuristicBotSettings.RepairFixProfile
                                       : null);
            // Recorded in the DB so the two arms can be told apart when the replays are
            // analysed later -- without this a repair-fix game is indistinguishable from a
            // flagship game in game_records.db.
            _opponentDescriptions[gameId] = SingleplayerAutoSpawner ? "heuristic_autospawn8"
                                          : SingleplayerEconomyBrake ? "heuristic_brake"
                                          : SingleplayerHazardFix ? "heuristic_repair_hazard"
                                          : SingleplayerRepairFix ? "heuristic_repairfix" : "heuristic";
            Console.WriteLine($"[Opponent] {gameId}: HeuristicBot (seat 2)"
                            + (SingleplayerAutoSpawner ? " + REPAIR + HAZARD + ECONOMY BRAKE + AUTO-SPAWNER(cap 8)"
                             : SingleplayerEconomyBrake ? " + REPAIR + HAZARD + ECONOMY BRAKE"
                             : SingleplayerHazardFix ? " + REPAIR FIX + HAZARD BLACKOUT"
                             : SingleplayerRepairFix ? " + REPAIR FIX" : " [flagship]"));
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
                //   margin  0.03 -> 0.10  69.8% -> 75.0%
                //   interval  10 -> 15    62.5% -> 68.5%
                //
                // THE HORIZON ROW OF THAT SWEEP HAS BEEN RETRACTED - see below.
                //
                // ---- HORIZON: 300 -> 1600, changed 2026-08-19 ----------------------
                //
                // This block used to record `horizon 900 -> 300  47.0% -> 68.5%  the
                // single largest factor`, and explained it as long rollouts converging to
                // the same self-play continuation so search scores noise. BOTH HALVES ARE
                // WRONG. Re-measured at the SHIPPED margin (search-test, seed 4242, paired,
                // interval 15, margin 0.10, no time cap, n=200/arm):
                //
                //   horizon   300     450     600     900    1200
                //   win rate  77.0%   80.5%   81.5%   78.0%   77.5%
                //   spread    0.203   0.251   0.273   0.298   0.341
                //
                // Horizon 900 is 78.0% [71.8, 83.2], NOT 47.0%. And the spread GROWS with
                // horizon, so branches diverge more, not less - the convergence story is
                // falsified. (What does rise is FLAT decisions, 3.5% -> 8.2%, as more
                // rollouts reach a terminal state and tie exactly. Bimodal, not convergent.)
                //
                // THE CONFOUND: that sweep was coordinate descent and tuned horizon BEFORE
                // margin, at margin ~0.03, then never revisited it. Margin 0.03 is the
                // high-intervention regime - horizon 300 there scores 73.0% with 17.1%
                // overrides against margin 0.10's 7.8%. A longer horizon widens the score
                // spread, which is costly where search overrides constantly and harmless
                // where it overrides rarely. The horizon x margin interaction was never
                // measured. This is the "number pasted into a doc and never re-derived"
                // failure mode CLAUDE.md warns about, caught in its own tuning record.
                //
                // 600 measured BEST: n=800 paired, 80.0% vs 300's 76.0%, delta +4.00,
                // b=94/c=62, McNemar exact p=0.0128, CI [+0.95, +7.05]. It also halves the
                // games decided on castle HP at the tick cap (10/608 -> 5/640).
                //
                // WHY 1600 IS SHIPPED INSTEAD OF THE MEASURED ARGMAX. Marc's call, made on
                // mechanism rather than on the argmax, so the config is not sitting in a
                // local optimum nobody can explain. Ticks needed to afford the next
                // investment from an empty wallet - note the HAND-TUNED top rung, which the
                // general (4*count + 8) seconds formula does not cover:
                //
                //   count   0    1    2    3    4    5    6      7
                //   ticks  270  360  480  600  720  840  960   1600
                //
                // (Count 0 is 270 not 240 because PlayerState's constructor hardcodes
                // price 18 / income 2; count 7 is the InvestmentPrice=40000 / Income=750
                // override in ApplyInvestmentStep.) The old cliff - 250 -> 30%, 200 -> 1.5%
                // - sits exactly below rung 0, so 300 was the smallest horizon that buys the
                // FIRST investment and nothing more. 1600 is the smallest that can see the
                // LAST one.
                //
                // 1600 IS UNMEASURED at the time of the change. The sweep topped out at
                // 1200 (+0.5, p=1.00, i.e. indistinguishable from 300). Expect it to cost
                // roughly the 4 points 600 was winning by. It is the base for evaluator
                // work, not a strength claim. FLAGSHIP_BASELINE.md section 5 is the revert.
                //
                // PLAYABILITY: longer horizons make the bot intervene more (overrides
                // 7.8% -> 11.1% at 600) and press-macro fire 4x (0.4% -> 1.5%) - it banks
                // and then hits harder. If the feel degrades, revert per section 5.
                //
                // The high margin is not a tuning artefact. Search's PRIMITIVE moves are
                // actively harmful (margin 0.0 scores 56.0% and earns 1.5 FEWER investments
                // per game than HeuristicBot); its save-invest MACRO is the entire source of
                // strength (disabling it scores 44.0% - worse than not searching at all). A
                // high margin filters the former while still letting the latter through.
                // CORRECTION 2026-08-07: BOTH parenthetical figures are margin-0.01
                // numbers and were never labelled as such. At the shipped margin 0.10,
                // search with NO macros still scores 63.7% (not 44.0%), i.e. the primitives
                // are net POSITIVE here and the macro is worth +11.2 points, not +31.
                // Search now overrides on ~8% of decisions and out-invests HeuristicBot by
                // +0.72 per game, which is what actually wins.
                decisionInterval: 15,
                horizon: 1600,
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
                asyncDecisions: true,
                // ── REACTIVE OPENING (2026-08-20, Marc's design) ───────────────────
                // Search may not spend on units or offence/defence gadgets until the human
                // has put a unit on the board. Investment, repair and the signature gadget
                // stay available, so the bot's opening is forced to be economic.
                //
                // THIS REPLACED TWO INVESTMENT-COUNT GUARDS THAT BOTH FAILED THE SAME WAY.
                // earlyGadgetGuardMinInvest: 1 stopped the $12 Reinforcements opening and the
                // bot bought a $9 tier-3 unit instead; adding earlySpendGuardMinInvest: 1
                // stopped that and it bought the tier-3 immediately after the first
                // investment landed. The investment count was never what made those buys
                // wrong -- an empty board was. Both knobs remain in the constructor at 0 for
                // measurement; neither is used here any more.
                reactiveOpeningGate: true);
            _opponentDescriptions[gameId] = "search";
            Console.WriteLine($"[Opponent] {gameId}: RolloutSearchBot (seat 2)");
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

        /// <summary>Are we in the pre-game window -- the game exists but is not being
        /// stepped, and no action from either player may be accepted yet?</summary>
        public bool IsPreGame(string gameId) => _preGameUntil.ContainsKey(gameId);

        /// <summary>Is this game actually running? A lobby game has been created but has no
        /// tick loop behind it yet, so a disconnect from one means something different.</summary>
        public bool IsActive(string gameId) => _activeGames.ContainsKey(gameId);

        /// <summary>
        /// Throw away a game that never started. Used when the player who created a lobby
        /// disconnects from it: there is no game to pause and no opponent to notify, and
        /// leaving the entry behind would keep an unjoinable lobby in the browser list.
        /// </summary>
        public bool DiscardLobbyGame(string gameId)
        {
            bool removed = _lobbyGames.TryRemove(gameId, out _);
            if (removed)
            {
                _gameSeeds.TryRemove(gameId, out _);
                _opponentDescriptions.TryRemove(gameId, out _);
            }
            return removed;
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

            // One choke point for every hosted game, so pinning here covers all modes.
            // ShadowMap is cleared alongside: the constructor may have rolled a shadow map
            // on its way to the map we are throwing away, and a pinned map that is
            // sometimes greyed out is not a pinned map.
            if (ForcedMap.HasValue)
            {
                state.Map = ForcedMap.Value;
                state.ShadowMap = false;
            }

            state.GameMode = gameMode;
            // SEEDED AS OF 2026-08-20 (replay format v3). This used to be
            // `new GameEngine(state)`, i.e. seed null, i.e. an UNSEEDED Random -- and that
            // stream drives unit y-position on spawn, which changes combat targeting. A live
            // game was therefore not reproducible even in principle, no matter what the
            // replay stored. The seed is drawn here and recorded in StartGame.
            int engineSeed = Random.Shared.Next();
            var engine = new GameEngine(state, null, engineSeed);
            _gameSeeds[gameId] = engineSeed;

            engine.OnGadgetAnimation += (gadgetId, side, position, targetId) =>
            {
                _hubContext.Clients.Group(gameId).SendAsync("PlayGadgetAnimation", gadgetId, side, position, targetId);
                // NOTE: recording moved to OnGadgetCast below. This event is raised by the
                // individual effects and five of them never raise it, so it silently dropped
                // whole gadget families. It stays purely for client animation.
            };
            engine.OnGadgetCast += (side, gadgetId, position) =>
            {
                if (_recorders.TryGetValue(gameId, out var rec))
                    rec.RecordGadgetUse((int)engine._state.CurrentTick, side, gadgetId, position);
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

                // The pre-game window opens here, not on a client message: a browser that is
                // still being added to the SignalR group at this moment (multiplayer's second
                // player is, see GameHub.JoinGame) would miss a one-shot announcement. The
                // loop re-sends the remaining time every pass instead.
                //
                // Spectator modes get none of it -- there is nobody to introduce the battle
                // to, and "watch" and "defwatch" exist to observe a bot matchup start to
                // finish, not to sit through an intro.
                bool spectatorOnly = engine._state.GameMode == "league"
                                  || engine._state.GameMode == "defwatch"
                                  || engine._state.GameMode == "watch";
                if (!spectatorOnly)
                    _preGameUntil[gameId] = DateTime.UtcNow.AddSeconds(PreGameSeconds);
                // Captured HERE, at game start, which is the whole point of the v3 fields:
                // the loadout is written at game over in v2 and so records the FINAL gadget
                // tiers, which a rebuild then equips from tick 0.
                _gameSeeds.TryGetValue(gameId, out int seed);
                _recorders.TryAdd(gameId, new GameRecorder(gameId,
                    engine._state.CurrentTick,
                    engine._state.Player1.Money,
                    engine._state.Player2.Money,
                    (byte)engine._state.Map,
                    engine._state.ShadowMap,
                    seed,
                    new[] { engine._state.Player1.OffensiveGadget?.Id ?? "",
                            engine._state.Player1.DefensiveGadget?.Id ?? "",
                            engine._state.Player1.SignatureGadget?.Id ?? "" },
                    new[] { engine._state.Player2.OffensiveGadget?.Id ?? "",
                            engine._state.Player2.DefensiveGadget?.Id ?? "",
                            engine._state.Player2.SignatureGadget?.Id ?? "" }));
                // The pre-game length rides along with GameStarted so the client knows
                // to open on the opponent's castle in the SAME frame it builds the game
                // screen. Learning it one message later would show a frame of the normal
                // camera first and snap. 0 means "no intro" (spectator modes).
                _hubContext.Clients.Group(gameId).SendAsync("GameStarted",
                    _preGameUntil.ContainsKey(gameId) ? (int)(PreGameSeconds * 1000) : 0);
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
                    // ── PRE-GAME ────────────────────────────────────────────────────────
                    // Four seconds of watching the field before anything moves. The game is
                    // NOT stepped -- no tick, no bots, no recorded actions -- but the state
                    // is still broadcast so both browsers can draw the castles and the squad
                    // waiting outside them, and the remaining time goes out with it so the
                    // camera pan and the countdown run off the server's clock.
                    //
                    // Checked before the disconnect pause below so that a player who drops
                    // during the intro still pauses the game rather than being ticked past.
                    if (_preGameUntil.TryGetValue(gameId, out var preGameEnd))
                    {
                        double msLeft = (preGameEnd - DateTime.UtcNow).TotalMilliseconds;
                        if (msLeft > 0)
                        {
                            await _hubContext.Clients.Group(gameId).SendAsync("PreGame", (int)msLeft);
                            await _hubContext.Clients.Group(gameId).SendAsync("GameStateUpdate", GameStateWire.From(engine._state));
                            continue;   // not stepped
                        }

                        _preGameUntil.TryRemove(gameId, out _);
                        await _hubContext.Clients.Group(gameId).SendAsync("BattleStart");
                    }

                    // ── DISCONNECT PAUSE ────────────────────────────────────────────────
                    // A game with an empty human seat is not stepped AT ALL: no tick, no
                    // bot decisions, no recorded actions, no state broadcast. Freezing it
                    // rather than letting it run on is the whole point -- the old behaviour
                    // kept ticking with one castle undefended, so a reload was a loss.
                    //
                    // The countdown is broadcast from here, once per whole second, instead
                    // of being left to each client's own clock: the deadline that actually
                    // ends the game is this one, and a client timer would drift away from it.
                    if (_reconnect.IsPaused(gameId))
                    {
                        if (!_reconnect.ShouldResolve(gameId))
                        {
                            // Keeps broadcasting PAST zero: at 60s the waiting player is
                            // offered the win rather than handed it, so the pause continues
                            // and what they watch changes from a countdown to how long they
                            // have waited. Ending it is their decision (ClaimVictory) --
                            // see ReconnectService.ShouldResolve for the three exceptions.
                            if (_reconnect.ShouldSendCountdown(gameId, out int secsLeft,
                                                               out bool claimable, out int waited))
                                await _hubContext.Clients.Group(gameId).SendAsync("GamePaused",
                                    _reconnect.DroppedSide(gameId), secsLeft, claimable, waited);
                            continue;   // frozen
                        }

                        // EXACTLY ONE human still connected is a win by default; nobody
                        // connected is not a win at all -- there is no one it could be
                        // awarded to, and recording a winner would invent a result out of a
                        // dropped network. Both are flagged in the DB and both are excluded
                        // from recording analysis (see IsRealResult).
                        var stillConnected = _reconnect.ConnectedHumanSides(gameId);
                        bool byDefault = stillConnected.Count == 1;
                        engine._state.WinnerSide = byDefault ? stillConnected[0] : 0;
                        engine._state.IsGameOver = true;
                        _endReasons[gameId] = byDefault ? "disconnect" : "abandoned";
                        Console.WriteLine($"[Reconnect] game={gameId} resolved after " +
                            $"{_reconnect.WaitedSeconds(gameId)}s paused: " +
                            (byDefault ? $"P{engine._state.WinnerSide} wins by default"
                                       : "abandoned by both players, no winner"));
                        await _hubContext.Clients.Group(gameId).SendAsync("WinByDefault",
                            engine._state.WinnerSide);
                        // Falls through to the game-over block below WITHOUT ticking:
                        // GameEngine.Tick would be a no-op now anyway (it early-returns on
                        // IsGameOver), but the bots above it would not be.
                    }
                    else
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

                        // Side-1 HeuristicBot — Defence Watch. Same contract again: every
                        // real tick, self-throttling on DecisionIntervalTicks, which is what
                        // makes the six-spawns-per-second ceiling identical to the harness's.
                        if (_heuristicP1Opponents.TryGetValue(gameId, out var heuristicBotP1))
                        {
                            heuristicBotP1.Update(engine);
                        }

                        engine.Tick();
                        recorder?.RecordTick(engine.LastActionP1, engine.LastActionP2);
                    }
                    }

                    if (engine._state.IsGameOver)
                    {
                        _reconnect.Release(gameId);
                        _preGameUntil.TryRemove(gameId, out _);
                        _endReasons.TryRemove(gameId, out var endReason);
                        _leagueOpponents.TryRemove(gameId, out _);
                        _leagueP1Opponents.TryRemove(gameId, out _);
                        _heuristicOpponents.TryRemove(gameId, out _);
                        _searchOpponents.TryRemove(gameId, out _);
                        _searchP1Opponents.TryRemove(gameId, out _);
                        _heuristicP1Opponents.TryRemove(gameId, out _);
                        _opponentDescriptions.TryRemove(gameId, out var opponentDescription);

                        if (_recorders.TryRemove(gameId, out var finishedRecorder))
                        {
                            bool isSingleplayer = engine._state.GameMode == "sp"
                                               || engine._state.GameMode == "vai"
                                               || engine._state.GameMode == "league"
                                               || engine._state.GameMode == "practice"
                                               // Acceptance-test games are one human vs one bot,
                                               // so they belong with the singleplayer replays that
                                               // --divergence and --export-policy-table read. They
                                               // carry game_mode="accept" in the DB, which is what
                                               // separates the ten-game test from the 51 ordinary
                                               // sp games already recorded against the same bot.
                                               || engine._state.GameMode == "accept";
                            // NOT RECORDED. Both seats are bots, so these replays have no
                            // analytical value -- and the last spectator mode that did get saved
                            // is why 12 of the 153 files in recordings/singleplayer/ are
                            // bot-vs-bot games that every tool reading that corpus now has to
                            // filter out by hand (see ReplayFile.SelectHumanGames). Not repeating
                            // that: a diagnostic spectator mode must not seed the replay corpus.
                            bool isWatch = engine._state.GameMode == "watch"
                                        || engine._state.GameMode == "defwatch";
                            string subdir = isSingleplayer ? "singleplayer" : "multiplayer";
                            // A disconnect-resolved game IS still written -- silently
                            // dropping it would be the same mistake as the rerolls that had
                            // to be reconstructed by hand later. endReason is what marks it
                            // as not a real result; nothing about the replay itself does.
                            if (!isWatch) finishedRecorder.Save(Path.Combine(_replayDir, subdir),
                                engine._state.Player1, engine._state.Player2,
                                engine._state.WinnerSide, engine._state.CurrentTick, _gameVersion, _db,
                                engine._state.GameMode, opponentDescription, endReason);
                        }

                        await _hubContext.Clients.Group(gameId).SendAsync("GameOver", GameStateWire.From(engine._state));
                        _activeGames.TryRemove(gameId, out _);
                    }
                    else
                    {
                        await _hubContext.Clients.Group(gameId).SendAsync("GameStateUpdate", GameStateWire.From(engine._state));
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
                        _heuristicP1Opponents.TryRemove(gameId, out _);
                        _recorders.TryRemove(gameId, out _);
                        _opponentDescriptions.TryRemove(gameId, out _);
                        _endReasons.TryRemove(gameId, out _);
                        _preGameUntil.TryRemove(gameId, out _);
                        _reconnect.Release(gameId);
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
