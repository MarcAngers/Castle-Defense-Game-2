using Microsoft.AspNetCore.SignalR;
using CastleDefense.Api.Services;
using CastleDefense.Engine.Models;
using CastleDefense.Engine.Data;

namespace CastleDefense.Api.Hubs
{
    public class GameHub : Hub
    {
        private readonly GameHostingService _gameService;
        private readonly ReconnectService _reconnect;

        public GameHub(GameHostingService gameService, ReconnectService reconnect)
        {
            _gameService = gameService;
            _reconnect = reconnect;
        }

        private static readonly Random _leagueRng = new();

        /// <summary>
        /// Hand a seat's rejoin token to the browser that just took it, and to nobody else
        /// -- Clients.Caller, never the group. The token is what lets that browser prove it
        /// owns the seat after its socket dies; see ReconnectService for why it cannot live
        /// in PlayerState instead.
        ///
        /// Called for HUMAN seats only. A spectator (side 0) and an "AI_BOT" seat get no
        /// token, which is exactly what makes a spectator closing their tab not pause a
        /// game and a bot game have no human seat to wait for.
        /// </summary>
        private async Task IssueSessionAsync(string gameId, int side)
        {
            string token = _reconnect.RegisterSeat(gameId, side, Context.ConnectionId);
            await Clients.Caller.SendAsync("SessionToken", gameId, side, token,
                                           ReconnectService.ClaimAfterSeconds);
        }

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
                await Clients.Caller.SendAsync("GameJoined", 0, GameStateWire.From(game._state));
                _gameService.StartGame(gameId);
                return;
            }

            // DEFENCE WATCH -- spectate the defence-only bot against the shipped bot in the
            // pinned mirror. This is the configuration every number in
            // CastleDefense.BotArena/stall/BOT_TUNING.md was measured in, so it is reproduced
            // exactly rather than approximately:
            //
            //   * BOTH sides White / nuke / reinforcements. Counter-picking would make the
            //     bot's loadout a function of the opponent's and confound play with loadout;
            //     the pinned mirror is also a verified 100/100 DRAW between two equal bots,
            //     so neither seat carries a built-in edge to explain a result away with.
            //   * NO headstart. `new PlayerState()` and tick 0, matching the harness's plain
            //     `new GameState()`; the league mode's timeSkip hands out free investments
            //     (E = 2.118) and would move the whole game off the measured trajectory.
            //
            // The browser is a pure spectator (side 0) and is never assigned either player's
            // ConnectionId, so the action handlers below no-op for it.
            if (game._state.GameMode == "defwatch")
            {
                lock (game)
                {
                    if (game._state.Player1.ConnectionId == "AI_BOT") return; // already set up

                    foreach (var (p, seat) in new[] { (game._state.Player1, 1), (game._state.Player2, 2) })
                    {
                        p.Side         = seat;
                        p.ConnectionId = "AI_BOT";
                        p.Team         = TeamColour.White;
                        p.SetLoadout(new[] { "nuke", "reinforcements",
                                             GameDataManager.GetSignatureGadgetIdForTeam(TeamColour.White) });
                    }

                    game.RewirePlayerEvents();
                    _gameService.SetupDefenceWatchMatch(gameId);
                }

                await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
                await Clients.Caller.SendAsync("GameJoined", 0, GameStateWire.From(game._state));
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
                await Clients.Caller.SendAsync("GameJoined", 1, GameStateWire.From(game._state));
                await IssueSessionAsync(gameId, 1);
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
            await Clients.Caller.SendAsync("GameJoined", side, GameStateWire.From(game._state));

            // Only a real seat gets a rejoin token. side == 0 here means the game was
            // already full and this caller is a spectator, who has nothing to come back to.
            if (side != 0) await IssueSessionAsync(gameId, side);

            if (game._state.GameMode == "sp" || game._state.GameMode == "watch")
            {
                game._state.Player2.Side = 2;
                game._state.Player2.ConnectionId = "AI_BOT";

                // COUNTER-PICK. P2's loadout used to be a uniform random roll, which threw
                // away the one advantage the bot structurally has in this mode: it picks
                // second, with the human's team and gadgets already locked in. CounterPicker
                // answers with the best response measured by the counter-matrix sweep, and
                // falls back to the old random roll when no table is present.
                //
                // "watch" keeps the random roll: it is a spectator mode against the ONNX
                // model, not the matchup this table was fitted for.
                if (game._state.GameMode == "sp")
                {
                    // Base ids only: the table is keyed on the four base offensive and four
                    // base defensive gadgets, so an already-upgraded "nuke_2" would miss the
                    // lookup and silently fall through to a random pick.
                    var counter = CounterPicker.PickCounter(
                        game._state.Player1.Team,
                        CounterPicker.BaseGadgetId(game._state.Player1.OffensiveGadget?.Id),
                        CounterPicker.BaseGadgetId(game._state.Player1.DefensiveGadget?.Id));
                    game._state.Player2.Team = counter.Team;
                    game._state.Player2.SetLoadout(counter.Loadout);
                }
                else
                {
                    game._state.Player2.Team = GameDataManager.GetRandomTeam();
                    game._state.Player2.SetLoadout(new string[] { GameDataManager.GetRandomOGadgetId(), GameDataManager.GetRandomDGadgetId(), GameDataManager.GetSignatureGadgetIdForTeam(game._state.Player2.Team) });
                }

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
                // P2's team and loadout are now COUNTER-PICKED just above rather than
                // randomised, so at CounterPicker.TopK = 1 each human loadout does face a
                // single fixed opponent. That predictability is deliberate for now (measure
                // the ceiling first); raise TopK to trade some of it back for variety.
                //
                // WHICH bot is a config switch (Singleplayer:Opponent). "heuristic" points
                // singleplayer at the same plain HeuristicBot the defence-only bot is
                // benchmarked against, so a recorded human game is comparable to those
                // numbers; "search" (the default) is the flagship that ships.
                if (game._state.GameMode == "sp")
                {
                    if (string.Equals(GameHostingService.SingleplayerOpponent, "heuristic",
                                      StringComparison.OrdinalIgnoreCase))
                        _gameService.SetupHeuristicOpponent(gameId);
                    else
                        _gameService.SetupSearchOpponent(gameId);
                }

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
            await Clients.Caller.SendAsync("GameJoined", 1, GameStateWire.From(game._state));
            await IssueSessionAsync(gameId, 1);
            _gameService.StartGame(gameId);
        }

        // ── Disconnection and rejoin ───────────────────────────────────────────────────

        /// <summary>
        /// A socket closed -- a reload, a closed tab, a crashed browser, a dropped network.
        /// SignalR cannot tell these apart and neither can we, so all of them are treated
        /// as "this player may be back": the game is paused and the grace window starts.
        ///
        /// Nothing here ends a game. The resolution happens in GameHostingService's loop,
        /// which owns the deadline, so a player who reconnects one tick before it expires
        /// races against the same clock the opponent is watching.
        /// </summary>
        public override async Task OnDisconnectedAsync(Exception exception)
        {
            if (_reconnect.MarkDisconnected(Context.ConnectionId, out string gameId,
                                            out int side, out int secondsRemaining))
            {
                if (_gameService.IsActive(gameId))
                {
                    await Clients.Group(gameId).SendAsync("GamePaused", side, secondsRemaining);
                }
                else
                {
                    // Never started: a lobby waiting for an opponent. There is no game to
                    // pause and nobody to award it to, so drop it rather than leaving an
                    // unjoinable entry in the game browser.
                    _gameService.DiscardLobbyGame(gameId);
                    _reconnect.Release(gameId);
                }
            }

            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Can this browser still get back into this game? Called on page load with whatever
        /// the previous session left in storage, before anything is shown to the player, so
        /// the prompt only appears when there is genuinely a game waiting.
        ///
        /// RETURNS a value rather than firing an event, because the client asks about SEVERAL
        /// candidate sessions in turn and has to pair each answer with the question that
        /// produced it -- see game-connection.js. It also refuses a seat that is currently
        /// CONNECTED: a browser can hold tokens for both seats of one game (two tabs), and
        /// offering the seat that never went anywhere is what let a rejoining player be
        /// handed their opponent's seat.
        /// </summary>
        public RejoinStatus CheckRejoin(string gameId, string token)
        {
            bool valid = _gameService.IsActive(gameId)
                      && _reconnect.ValidateToken(gameId, token, out _);
            bool available = valid && !_reconnect.IsSeatConnected(gameId, token);

            return new RejoinStatus
            {
                Available = available,
                Valid = valid,
                GameId = gameId,
                SecondsRemaining = available ? _reconnect.SecondsRemaining(gameId) : 0,
                Claimable = available && _reconnect.IsClaimable(gameId)
            };
        }

        public class RejoinStatus
        {
            public bool Available { get; set; }
            /// <summary>The game and token are real, but the seat may be occupied. Lets the
            /// client tell "this game is over, forget it" from "that seat is my other tab's,
            /// keep it" -- deleting the second would break the tab that owns it.</summary>
            public bool Valid { get; set; }
            public string GameId { get; set; }
            public int SecondsRemaining { get; set; }
            /// <summary>The opponent may already end this game at any moment, so the returning
            /// player is told to hurry rather than shown a countdown that reads zero.</summary>
            public bool Claimable { get; set; }
        }

        /// <summary>
        /// Take the seat back. The token, not the socket, is the credential -- which is why
        /// it is only ever sent to the browser that owns the seat.
        ///
        /// PlayerState.ConnectionId is re-pointed at the new socket here because that field
        /// is what every action handler below compares against; without this the returning
        /// player would watch their own game without being able to play it, which is the
        /// exact failure this whole feature exists to remove.
        /// </summary>
        public async Task RejoinGame(string gameId, string token)
        {
            var game = _gameService.GetGame(gameId);
            if (game == null || !_gameService.IsActive(gameId))
            {
                await Clients.Caller.SendAsync("RejoinFailed", "That game is no longer running.");
                return;
            }

            if (!_reconnect.Rejoin(gameId, token, Context.ConnectionId, out int side, out bool resumed))
            {
                await Clients.Caller.SendAsync("RejoinFailed", "That game is no longer yours to rejoin.");
                return;
            }

            lock (game)
            {
                var seat = side == 1 ? game._state.Player1 : game._state.Player2;
                seat.ConnectionId = Context.ConnectionId;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
            await Clients.Caller.SendAsync("GameJoined", side, GameStateWire.From(game._state));
            await Clients.Caller.SendAsync("SessionToken", gameId, side, token,
                                           ReconnectService.ClaimAfterSeconds);
            // 0: a rejoining player does not replay the intro. If the game happens to
            // still be in its pre-game window the loop's PreGame messages take over
            // straight away and drop them into the middle of it, which is correct.
            await Clients.Caller.SendAsync("GameStarted", 0);

            // Only tell the group play is on again once EVERY human seat is filled. In a
            // game where both players dropped, the first one back is still waiting.
            if (resumed) await Clients.Group(gameId).SendAsync("GameResumed");
            else await Clients.Caller.SendAsync("GamePaused", _reconnect.DroppedSide(gameId),
                                                _reconnect.SecondsRemaining(gameId),
                                                _reconnect.IsClaimable(gameId),
                                                _reconnect.WaitedSeconds(gameId));
        }

        /// <summary>
        /// Decline the rejoin -- the "Abandon" button. Ends the wait immediately rather than
        /// making the opponent sit out a countdown for a game the player has already said
        /// they are not coming back to.
        ///
        /// SENDS THE CALLER NOTHING. It used to reply "RejoinFailed", which the client
        /// handled by re-showing the very prompt the button had just dismissed -- so the
        /// modal needed a second press to go away. The caller initiated this and already
        /// knows the outcome; there is nothing to tell it.
        /// </summary>
        public Task AbandonGame(string gameId, string token)
        {
            _reconnect.Forfeit(gameId, token);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Stop waiting and take the win. Only legal once the pause has passed
        /// ReconnectService.ClaimAfterSeconds, and only from a seat that is still connected.
        ///
        /// This is what makes 60 seconds an OFFER rather than a verdict: the game stays
        /// paused indefinitely (up to MaxPauseSeconds) until the waiting player decides they
        /// are done waiting, so someone whose friend is rebooting their router can simply
        /// keep waiting instead of being handed a win they did not want.
        /// </summary>
        public async Task ClaimVictory(string gameId)
        {
            if (_reconnect.ClaimNow(gameId, Context.ConnectionId, out string refusal)) return;

            // Logged, not just returned: a refused claim leaves a player staring at a button
            // that did nothing, and the reason lives entirely in server-side state they
            // cannot see.
            Console.WriteLine($"[Reconnect] claim refused game={gameId} " +
                              $"conn={Context.ConnectionId}: {refusal}");
            await Clients.Caller.SendAsync("ClaimRefused", "That win cannot be claimed yet.");
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

            // A PAUSED GAME MUST DROP ACTIONS, not bank them. The queue is drained by
            // GameEngine.Tick, and a paused game is not ticked -- so without this every
            // click made while the overlay is up would fire in one burst the instant the
            // opponent reconnects.
            if (_reconnect.IsPaused(gameId)) return;

            // Same reasoning for the pre-game window: the players are watching the field,
            // not playing it, and a queued action would fire the instant the battle opened.
            if (_gameService.IsPreGame(gameId)) return;

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

            // A PAUSED GAME MUST DROP ACTIONS, not bank them. The queue is drained by
            // GameEngine.Tick, and a paused game is not ticked -- so without this every
            // click made while the overlay is up would fire in one burst the instant the
            // opponent reconnects.
            if (_reconnect.IsPaused(gameId)) return;

            // Same reasoning for the pre-game window: the players are watching the field,
            // not playing it, and a queued action would fire the instant the battle opened.
            if (_gameService.IsPreGame(gameId)) return;

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

            // A PAUSED GAME MUST DROP ACTIONS, not bank them. The queue is drained by
            // GameEngine.Tick, and a paused game is not ticked -- so without this every
            // click made while the overlay is up would fire in one burst the instant the
            // opponent reconnects.
            if (_reconnect.IsPaused(gameId)) return;

            // Same reasoning for the pre-game window: the players are watching the field,
            // not playing it, and a queued action would fire the instant the battle opened.
            if (_gameService.IsPreGame(gameId)) return;

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

            // A PAUSED GAME MUST DROP ACTIONS, not bank them. The queue is drained by
            // GameEngine.Tick, and a paused game is not ticked -- so without this every
            // click made while the overlay is up would fire in one burst the instant the
            // opponent reconnects.
            if (_reconnect.IsPaused(gameId)) return;

            // Same reasoning for the pre-game window: the players are watching the field,
            // not playing it, and a queued action would fire the instant the battle opened.
            if (_gameService.IsPreGame(gameId)) return;

            // THREAD SAFETY: We do NOT modify State here. We queue it.
            game.EnqueueAction(() =>
            {
                game.UseGadget(side, gadgetId, (int)position);
            });
        }
    }
}