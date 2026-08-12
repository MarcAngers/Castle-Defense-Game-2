using Microsoft.AspNetCore.SignalR;
using CastleDefense.Api.Services;
using CastleDefense.Engine.Models;
using CastleDefense.Engine.Data;

namespace CastleDefense.Api.Hubs
{
    public class GameHub : Hub
    {
        private readonly GameHostingService _gameService;

        public GameHub(GameHostingService gameService)
        {
            _gameService = gameService;
        }

        private static readonly Random _leagueRng = new();

        public async Task JoinGame(string gameId, string teamName, string[] loadout)
        {
            var game = _gameService.GetGame(gameId);
            if (game == null)
            {
                await Clients.Caller.SendAsync("Error", "Game not found.");
                return;
            }

            // Training League: watch castle_defense_p1_v4 (P1) play HeuristicBot (P2),
            // both with random teams/loadouts. Both sides are AI-controlled -- the
            // connecting browser is a pure spectator (side 0), never assigned either
            // player's ConnectionId, so it can't accidentally act for either side (the
            // Invest/Repair/SpawnUnit/UseGadget handlers below already no-op for any
            // caller that isn't recognized as Player1 or Player2).
            if (game._state.GameMode == "league")
            {
                lock (game)
                {
                    if (game._state.Player1.ConnectionId == "AI_BOT") return; // already set up

                    int timeSkip = Math.Max(_leagueRng.Next(-8, 9), 0);
                    string upg   = timeSkip > 5 ? "_3" : timeSkip > 3 ? "_2" : "";

                    game._state.Player1 = new PlayerState(timeSkip);
                    game._state.Player1.Side         = 1;
                    game._state.Player1.ConnectionId = "AI_BOT";
                    game._state.Player1.Team         = GameDataManager.GetRandomTeam();
                    game._state.Player1.SetLoadout(new[] {
                        GameDataManager.GetRandomOGadgetId()  + upg,
                        GameDataManager.GetRandomDGadgetId()  + upg,
                        GameDataManager.GetSignatureGadgetIdForTeam(game._state.Player1.Team) + upg });
                    game._state.Player2 = new PlayerState(timeSkip);
                    game._state.Player2.Side         = 2;
                    game._state.Player2.ConnectionId = "AI_BOT";
                    game._state.Player2.Team         = GameDataManager.GetRandomTeam();
                    game._state.Player2.SetLoadout(new[] {
                        GameDataManager.GetRandomOGadgetId()  + upg,
                        GameDataManager.GetRandomDGadgetId()  + upg,
                        GameDataManager.GetSignatureGadgetIdForTeam(game._state.Player2.Team) + upg });

                    // Single roll — both players receive starting money or neither does
                    if (_leagueRng.Next(5) == 0)
                    {
                        game._state.Player1.Money = game._state.Player1.InvestmentPrice + game._state.Player1.Income;
                        game._state.Player2.Money = game._state.Player2.InvestmentPrice + game._state.Player2.Income;
                    }

                    game._state.CurrentTick = 30 * 30 * timeSkip;
                    game.RewirePlayerEvents();
                    _gameService.SetupTrainingLeagueWatchMatch(gameId);
                }

                await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
                await Clients.Caller.SendAsync("GameJoined", 0, game._state);
                _gameService.StartGame(gameId);
                return;
            }

            // ACCEPTANCE TEST (2026-08-11). The bar for "a bot Marc cannot beat":
            // ten games, random loadouts on BOTH sides, no rerolling, passing if he
            // wins zero or one. This mode exists so that test is a button rather
            // than a protocol he has to remember to follow.
            //
            // WHY IT IS NOT JUST SINGLEPLAYER. In "sp" the human picks his own team
            // and loadout, and picking is itself a skill the bot does not have — he
            // knows which of his team/gadget combinations he plays well. Measuring
            // against a self-selected loadout measures Marc-at-his-best against the
            // bot at a random draw, which is not the stated goal. Here the server
            // assigns BOTH sides, so neither player chooses.
            //
            // NO REROLL is enforced structurally: there is no selection screen to
            // back out of, and the game is created and started in this one call.
            // Abandoning a game mid-play is still possible and is on the honour
            // system — but it lands in the DB as a row rather than silently
            // vanishing, which is how the 11 quarantined rerolls were found.
            //
            // NO HEADSTART, deliberately. CreateGame leaves both PlayerStates at
            // timeSkip 0, unlike Training League above which rolls one. A headstart
            // hands both sides E=2.118 free investments with SD 2.74, and across a
            // ten-game test that variance is comparable to the effect being
            // measured. It is also the regime Marc actually plays and the regime
            // every search-test number is quoted in.
            //
            // Human is P1 for the same reason: all 51 of his recorded games against
            // the search bot are from seat 1, so keeping the seat fixed lets this
            // test extend that record rather than start a new one.
            if (game._state.GameMode == "accept")
            {
                lock (game)
                {
                    if (!string.IsNullOrEmpty(game._state.Player1.ConnectionId)) return;

                    game._state.Player1.Side         = 1;
                    game._state.Player1.ConnectionId = Context.ConnectionId;
                    game._state.Player1.Team         = GameDataManager.GetRandomTeam();
                    game._state.Player1.SetLoadout(new[] {
                        GameDataManager.GetRandomOGadgetId(),
                        GameDataManager.GetRandomDGadgetId(),
                        GameDataManager.GetSignatureGadgetIdForTeam(game._state.Player1.Team) });

                    game._state.Player2.Side         = 2;
                    game._state.Player2.ConnectionId = "AI_BOT";
                    game._state.Player2.Team         = GameDataManager.GetRandomTeam();
                    game._state.Player2.SetLoadout(new[] {
                        GameDataManager.GetRandomOGadgetId(),
                        GameDataManager.GetRandomDGadgetId(),
                        GameDataManager.GetSignatureGadgetIdForTeam(game._state.Player2.Team) });

                    game.RewirePlayerEvents();
                    // The flagship. Identical configuration to Singleplayer's — this
                    // test must measure the bot that actually ships, so it shares
                    // SetupSearchOpponent rather than pinning its own parameters.
                    _gameService.SetupSearchOpponent(gameId);
                }

                await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
                await Clients.Caller.SendAsync("GameJoined", 1, game._state);
                _gameService.StartGame(gameId);
                return;
            }

            if (!Enum.TryParse(teamName, true, out TeamColour team))
            {
                await Clients.Caller.SendAsync("Error", "Invalid team colour.");
                return;
            }

            int side = 0;

            lock (game)
            {
                if (string.IsNullOrEmpty(game._state.Player1.ConnectionId))
                {
                    side = 1;
                    game._state.Player1.Side = side;
                    game._state.Player1.ConnectionId = Context.ConnectionId;
                    game._state.Player1.Team = team;
                    game._state.Player1.SetLoadout(loadout);
                }
                else if (string.IsNullOrEmpty(game._state.Player2.ConnectionId))
                {
                    side = 2;
                    game._state.Player2.Side = side;
                    game._state.Player2.ConnectionId = Context.ConnectionId;
                    game._state.Player2.Team = team;
                    game._state.Player2.SetLoadout(loadout);
                    _gameService.StartGame(gameId);
                }
            }

            // Add to SignalR Group so we can broadcast to them later
            await Groups.AddToGroupAsync(Context.ConnectionId, gameId);

            // Send initial State
            await Clients.Caller.SendAsync("GameJoined", side, game._state);

            if (game._state.GameMode == "sp" || game._state.GameMode == "watch")
            {
                game._state.Player2.Side = 2;
                game._state.Player2.ConnectionId = "AI_BOT";
                game._state.Player2.Team = GameDataManager.GetRandomTeam();
                game._state.Player2.SetLoadout(new string[] { GameDataManager.GetRandomOGadgetId(), GameDataManager.GetRandomDGadgetId(), GameDataManager.GetSignatureGadgetIdForTeam(game._state.Player2.Team) });

                // Singleplayer's opponent is the rollout-search bot (2026-07-28): one-ply
                // search over the real engine with HeuristicBot as its policy prior.
                // HeuristicBot in turn beats every ONNX checkpoint this project trained.
                // "watch" is left untouched (still ONNX); only "sp" changes.
                //
                // It beats HeuristicBot 75.0% [71.4, 78.3] (n=600, paired seeds, 2026-08-05)
                // with ~99% of wins decisive. The "~90%" this comment claimed until then was
                // never measured at n>20; the config actually shipping at the time measured
                // 47-58%. See SetupSearchOpponent for the tuning and its evidence.
                //
                // P2's team and loadout are randomised just above, so each game presents a
                // different matchup rather than a single memorisable script.
                if (game._state.GameMode == "sp")
                    _gameService.SetupSearchOpponent(gameId);

                _gameService.StartGame(gameId);
            }
        }

        // Practice mode: the human picks their OWN team/loadout (unlike Training
        // League, where the server randomizes everything) AND which specific opponent
        // to face ("spam1".."spam8", "antispam", or a league-model name fragment like
        // "v4") instead of getting League's random 16-way roll. Built specifically so
        // the bot's worst matchups can be replayed on demand for human-vs-bot
        // comparison (e.g. Green team vs Tier4 spam) rather than waiting for League to
        // happen to deal that exact combination.
        public async Task JoinPracticeGame(string gameId, string teamName, string[] loadout, string opponentSpec)
        {
            var game = _gameService.GetGame(gameId);
            if (game == null)
            {
                await Clients.Caller.SendAsync("Error", "Game not found.");
                return;
            }

            if (!Enum.TryParse(teamName, true, out TeamColour team))
            {
                await Clients.Caller.SendAsync("Error", "Invalid team colour.");
                return;
            }

            string resolvedOpponent;
            lock (game)
            {
                if (!string.IsNullOrEmpty(game._state.Player1.ConnectionId)) return;

                game._state.Player1.Side = 1;
                game._state.Player1.ConnectionId = Context.ConnectionId;
                game._state.Player1.Team = team;
                game._state.Player1.SetLoadout(loadout);

                game._state.Player2.Side = 2;
                game._state.Player2.ConnectionId = "AI_BOT";
                var oppTeam = GameDataManager.GetRandomTeam();
                game._state.Player2.Team = oppTeam;
                game._state.Player2.SetLoadout(new[] {
                    GameDataManager.GetRandomOGadgetId(),
                    GameDataManager.GetRandomDGadgetId(),
                    GameDataManager.GetSignatureGadgetIdForTeam(oppTeam) });

                resolvedOpponent = _gameService.SetupPracticeOpponent(gameId, opponentSpec);
            }

            if (resolvedOpponent == null)
            {
                await Clients.Caller.SendAsync("Error", $"Unknown opponent '{opponentSpec}'.");
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
            await Clients.Caller.SendAsync("GameJoined", 1, game._state);
            _gameService.StartGame(gameId);
        }

        public void SpawnUnit(string gameId, string unitId)
        {
            var game = _gameService.GetGame(gameId);
            if (game == null) return;
           
            // Identify which player is calling
            int side = 0;
            if (game._state.Player1.ConnectionId == Context.ConnectionId) side = 1;
            else if (game._state.Player2.ConnectionId == Context.ConnectionId) side = 2;

            if (side == 0) return; // Spectators can't spawn

            // THREAD SAFETY: We do NOT modify State here. We queue it.
            game.EnqueueAction(() =>
            {
                game.SpawnUnit(side, unitId);
            });
        }

        public void Invest(string gameId)
        {
            var game = _gameService.GetGame(gameId);
            if (game == null) return;

            // Identify which player is calling
            int side = 0;
            if (game._state.Player1.ConnectionId == Context.ConnectionId) side = 1;
            else if (game._state.Player2.ConnectionId == Context.ConnectionId) side = 2;

            if (side == 0) return; // Spectators can't invest

            // THREAD SAFETY: We do NOT modify State here. We queue it.
            game.EnqueueAction(() =>
            {
                game.Invest(side);
            });
        }

        public void Repair(string gameId)
        {
            var game = _gameService.GetGame(gameId);
            if (game == null) return;

            // Identify which player is calling
            int side = 0;
            if (game._state.Player1.ConnectionId == Context.ConnectionId) side = 1;
            else if (game._state.Player2.ConnectionId == Context.ConnectionId) side = 2;

            if (side == 0) return; // Spectators can't repair

            // THREAD SAFETY: We do NOT modify State here. We queue it.
            game.EnqueueAction(() =>
            {
                game.Repair(side);
            });
        }

        public void UseGadget(string gameId, string gadgetId, float position)
        {
            var game = _gameService.GetGame(gameId);
            if (game == null) return;

            // Identify which player is calling
            int side = 0;
            if (game._state.Player1.ConnectionId == Context.ConnectionId) side = 1;
            else if (game._state.Player2.ConnectionId == Context.ConnectionId) side = 2;

            if (side == 0) return; // Spectators can't use gadgets

            // THREAD SAFETY: We do NOT modify State here. We queue it.
            game.EnqueueAction(() =>
            {
                game.UseGadget(side, gadgetId, (int)position);
            });
        }
    }
}