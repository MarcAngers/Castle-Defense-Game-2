import { showScreen } from './router.js';
import loader from './asset-loader.js';

// --- WIRE FORMAT -----------------------------------------------------------------------
//
// The server does not send its GameState objects any more. It sends the trimmed shape
// defined by CastleDefenseGame2/Services/GameStateWire.cs, in which every unit is a bare
// positional ARRAY rather than an object -- the fourteen JSON keys cost more per unit than
// the values they labelled, and a busy tick carries up to 158 units. Full state went from
// 613 KB/s per viewer to 146 KB/s (180 MB to 43 MB over a five-minute game), which is the
// difference between a game that is playable on cellular data and one that is not.
//
// THE ORDER BELOW IS THE CONTRACT, and its other half is UnitWireConverter.Write in
// GameStateWire.cs. The two lists must match exactly; a field added to one and not the
// other shifts everything after it by one slot. Nothing else in the client knows about any
// of this -- expandState puts the long property names back before latestState is assigned,
// so view.js, visual-unit.js, end-game-show.js and game.js are untouched.
const UNIT_FIELDS = [
    'instanceId', 'definitionId', 'side', 'tier',
    'position', 'yPosition', 'width', 'height', 'visualScale',
    'currentHealth', 'maxHealth', 'currentShield', 'attackCooldown',
    // 'statuses' is the last slot and is handled separately: it arrives as an array of
    // bare NAMES (or null, the common case), and the client expects objects with a .name.
];

function expandUnit(packed) {
    const unit = {};
    for (let i = 0; i < UNIT_FIELDS.length; i++) unit[UNIT_FIELDS[i]] = packed[i];
    const names = packed[UNIT_FIELDS.length];
    // Always an array, never null: view.js checks .length and end-game-show.js clears it
    // by assignment, so both need a real array to be there.
    unit.statuses = names ? names.map(name => ({ name })) : [];
    return unit;
}

// Tolerant of a state that has already been expanded, or has no units at all, so the
// GameJoined / GameStateUpdate / GameOver handlers can all route through it unconditionally
// -- including for a lobby state whose game has not started.
function expandState(state) {
    if (!state || !Array.isArray(state.units)) return state;
    if (state.units.length > 0 && !Array.isArray(state.units[0])) return state;
    state.units = state.units.map(expandUnit);
    return state;
}

class GameConnection {
    constructor() {
        // API Configuration
        this.API_URL = "http://localhost:5168";

        this.gadgetAnimationCallback = null;
        this.gadgetUpgradedCallback = null;
        
        this.connection = null;
        this.gameMode = null;
        this.currentGameId = null;
        this.selectedTeam = "white";
        this.selectedLoadout = [];
        this.selectedOpponent = null; // Practice mode: "spam1".."spam8", "antispam", or a model name
        this.mySide = 0; // 1 = Left, 2 = Right
        this.latestState = null;
        this.winnerSide = 0;
        // Whether the server has declared the game finished. SEPARATE FROM winnerSide
        // BECAUSE A DRAW IS winnerSide 0 -- the same value a game in progress has. Any
        // "are we done?" test written against winnerSide alone silently never fires on a
        // double-KO or a tied timeout. Use this instead; winnerSide answers WHO won, not
        // WHETHER it is over.
        this.gameOver = false;

        // --- DISCONNECTION / REJOIN ---
        // The seat token the server minted for this browser, mirrored into localStorage so
        // it survives the page being reloaded, crashed, or closed. See ReconnectService.
        this.sessionToken = null;
        this.graceSeconds = 60;
        // True while the OTHER player is missing and the game is frozen. game.js reads these
        // every frame to drive the pause overlay, rather than being pushed at -- a screen
        // that is torn down and rebuilt by the router cannot hold a subscription reliably.
        this.paused = false;
        this.pauseSecondsRemaining = 0;
        this.pausedSide = 0;
        // The 60 seconds have passed and this player may now end the game and take the win
        // -- or keep waiting, which is the default. See ReconnectService.
        this.pauseClaimable = false;
        this.pauseWaitedSeconds = 0;
        // This tab's own seat entry, once it has one.
        this.session = null;
        // --- PRE-GAME ---
        // The 4-second look at the field before a game starts. Held as a local DEADLINE
        // rather than a countdown so the camera pan can be interpolated smoothly between
        // the server's ~30/sec updates; each update re-anchors it, so it cannot drift.
        this.inPreGame = false;
        this.preGameEndsAt = 0;
        this.preGameTotalMs = 0;
        // When "BATTLE!!" fired, so the banner can play out after the intro ends.
        this.battleStartedAt = 0;
        // THIS browser's socket is down and SignalR is retrying it. Distinct from `paused`,
        // which is the opponent being gone: during a local drop no server message arrives
        // at all, so the game would otherwise just appear to stop for no stated reason.
        this.reconnecting = false;
        // Set when the game was awarded rather than played out, so the game-over screen can
        // say so instead of claiming a win nobody earned.
        this.endedByDisconnect = false;
        // True only for the one round-trip of a rejoin, so GameJoined knows to rebuild
        // team and loadout from the server's state.
        this.rejoining = false;
        // Fired by the reconnect UI (reconnect-ui.js). Kept as plain callbacks so this
        // module imports nothing from the UI and there is no import cycle.
        this.rejoinPromptCallback = null;
        this.connectionLostCallback = null;

        this.ready = this.buildConnection();
    }

    buildConnection = async () => {
        // --- SIGNALR CONNECTION ---
        // withAutomaticReconnect covers the case the grace window is really for: a network
        // blip with the page still open. The socket comes back with a NEW ConnectionId, so
        // reconnecting is not by itself enough to get the seat back -- onreconnected below
        // re-presents the token, which is what actually puts the player back in the game.
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(`/gameHub`)
            .withAutomaticReconnect([0, 2000, 5000, 10000, 15000, 20000, 30000])
            .build();

        this.connection.on("GameJoined", (side, state) => {
            this.mySide = side;
            this.latestState = expandState(state);
            // League and Acceptance Test both skip loadout selection, so read the
            // server-assigned gadgets from state. This is not cosmetic: game.js binds
            // the three gadget buttons and their targeting to selectedLoadout, so
            // without it the human's gadgets are unusable in acceptance games.
            //
            // A REJOIN goes through here too, and must take the same path whatever the
            // mode: the returning page has no memory of the selection screens, so its
            // selectedLoadout is empty and its gadget buttons would come back dead.
            if (this.rejoining || this.gameMode === 'league' || this.gameMode === 'accept'
                || this.gameMode === 'defwatch') {
                const p = side === 1 ? state.player1 : state.player2;
                const og = p.offensiveGadget;
                const dg = p.defensiveGadget;
                const sg = p.signatureGadget;
                this.selectedLoadout = [
                    og?.id ?? og?.Id,
                    dg?.id ?? dg?.Id,
                    sg?.id ?? sg?.Id,
                ];
                // TeamColour serialises as its NUMERIC enum value, not a name, so the
                // old string check never matched and every server-assigned game
                // silently reported the player's team as "white". Harmless in the
                // game screen — that reads loader.assets.teamList[state.playerN.team]
                // directly — but unit-info.js and game-browser.js both style
                // themselves from selectedTeam, so they showed the wrong roster.
                //
                // Resolved through the SAME teamList the game screen indexes rather
                // than a copy of the enum order here: a hand-maintained duplicate of
                // that ordering is precisely the kind of constant that drifts.
                const rawTeam = p.team ?? p.Team;
                this.selectedTeam =
                    typeof rawTeam === 'string' ? rawTeam.toLowerCase()
                    : (loader.assets.teamList[rawTeam] ?? 'white');
            }
        });

        this.connection.on("GameStateUpdate", (state) => {
            this.latestState = expandState(state);
        });

        this.connection.on("GameStarted", (preGameMs) => {
            this.winnerSide = 0;
            this.gameOver = false;
            this.endedByDisconnect = false;
            this.rejoining = false;
            // Set BEFORE showScreen so the game screen builds itself knowing whether an
            // intro is running -- it decides which castle the camera opens on.
            this.setPreGame(preGameMs || 0);
            this.paused = false;
            this.pauseSecondsRemaining = 0;
            this.pausedSide = 0;
            this.pauseClaimable = false;
            this.pauseWaitedSeconds = 0;
            showScreen("game");
        });

        // --- The opponent vanished: the server has frozen the game ---
        // Sent once when it happens and then once per second of the countdown, so this
        // client's timer is the server's timer rather than a local one drifting beside it.
        this.connection.on("GamePaused", (droppedSide, secondsRemaining, claimable, waitedSeconds) => {
            this.paused = true;
            this.pausedSide = droppedSide;
            this.pauseSecondsRemaining = secondsRemaining;
            // Past 60s the win is OFFERED, not taken: the game stays paused until this
            // player decides to stop waiting, so the overlay swaps its countdown for a
            // "waited for" clock and a button.
            this.pauseClaimable = !!claimable;
            this.pauseWaitedSeconds = waitedSeconds ?? 0;
        });

        // Sent every loop pass while the pre-game window is open, so the intro is driven by
        // the SERVER's clock -- both browsers in a multiplayer game must open the battle on
        // the same tick, and a client that joins the group mid-intro still picks it up.
        this.connection.on("PreGame", (msRemaining) => {
            this.setPreGame(msRemaining);
        });

        this.connection.on("BattleStart", () => {
            this.inPreGame = false;
            this.preGameEndsAt = 0;
            this.battleStartedAt = performance.now();
        });

        this.connection.on("GameResumed", () => {
            this.paused = false;
            this.pauseSecondsRemaining = 0;
            this.pausedSide = 0;
            this.pauseClaimable = false;
            this.pauseWaitedSeconds = 0;
        });

        // The grace window ran out. GameOver follows immediately; this only records WHY,
        // so the result screen does not present an awarded game as a fought one.
        this.connection.on("WinByDefault", () => {
            this.paused = false;
            this.endedByDisconnect = true;
        });

        this.connection.on("SessionToken", (gameId, side, token, graceSeconds) => {
            this.mySide = side;
            this.sessionToken = token;
            this.graceSeconds = graceSeconds;
            this.saveSession(gameId, side, token);
        });

        // A claim the server would not honour. Without this the button stays disabled for
        // the rest of the game and the player has no way to try again.
        this.connection.on("ClaimRefused", (message) => {
            console.warn('Claim refused:', message);
            const btn = document.getElementById('btnClaimVictory');
            if (btn) btn.disabled = false;
        });

        // The hub sends "Error" from several places and nothing was listening, so those
        // messages went nowhere at all.
        this.connection.on("Error", (message) => {
            console.error('Server error:', message);
        });

        this.connection.on("RejoinFailed", (message) => {
            this.rejoining = false;
            this.clearSession();
            if (this.rejoinPromptCallback) this.rejoinPromptCallback(false, 0, message);
        });

        this.connection.on("PlayGadgetAnimation", (gadgetId, side, position, targetId) => {
            if (this.gadgetAnimationCallback) {
                this.gadgetAnimationCallback(gadgetId, side, position, targetId);
            }
        });

        this.connection.on("GadgetUpgraded", (side, upgradedDef) => {
            if (this.gadgetUpgradedCallback) {
                this.gadgetUpgradedCallback(side, upgradedDef);
            }
        });

        this.connection.on("GameOver", (state) => {
            this.latestState = expandState(state);
            this.winnerSide = state.winnerSide;   // 0 on a draw
            this.gameOver = true;
            this.paused = false;
            // The game is finished, so the token names nothing: drop it now rather than
            // leaving a dead session for the next page load to offer a rejoin into.
            this.clearSession();
            showScreen("game-over");
        })

        // The socket died with the page still open (network dropped, server restarted).
        // Automatic reconnect is already retrying; the seat is only actually recovered by
        // presenting the token again, which is what these two do.
        this.connection.onreconnecting(() => {
            // THIS player's socket, not the opponent's -- so the game screen says
            // "reconnecting" rather than blaming the other player for a freeze that is
            // happening at this end.
            this.reconnecting = true;
        });

        this.connection.onreconnected(async () => {
            this.reconnecting = false;
            const saved = this.loadSession();
            if (saved) await this.rejoin(saved.gameId, saved.token);
        });

        this.connection.onclose(() => {
            // Retries exhausted. Nothing more happens on its own, so hand it to the UI.
            this.reconnecting = false;
            if (this.loadSession() && this.connectionLostCallback) this.connectionLostCallback();
        });

        try {
            await this.connection.start();
            console.log("SignalR Connected.");
        } catch (err) {
            console.error("SignalR Error:", err);
        }
    }

    /// Anchor the pre-game deadline against this browser's clock. Called on every server
    /// update; msRemaining <= 0 ends the intro even if the BattleStart message is lost.
    setPreGame = (msRemaining) => {
        if (!(msRemaining > 0)) {
            this.inPreGame = false;
            this.preGameEndsAt = 0;
            return;
        }
        this.inPreGame = true;
        this.preGameEndsAt = performance.now() + msRemaining;
        if (msRemaining > this.preGameTotalMs) this.preGameTotalMs = msRemaining;
    }

    /// Milliseconds left in the intro, interpolated locally between server updates.
    preGameRemaining = () => {
        if (!this.inPreGame) return 0;
        return Math.max(0, this.preGameEndsAt - performance.now());
    }

    // --- Session persistence -------------------------------------------------------
    //
    // TWO STORES, EACH DOING WHAT ONLY IT CAN.
    //
    // localStorage holds a LIST of seats this browser is sitting in, because it survives the
    // tab being closed and the browser crashing -- which is most of what this feature is for.
    // It used to hold a single session under one key, and that was a real bug rather than an
    // untidiness: localStorage is shared by every tab on the origin, so with both seats of a
    // multiplayer game open in one browser the second join OVERWROTE the first. Player 1
    // then reloaded, was handed player 2's token, rejoined into player 2's SEAT, and lost the
    // game they were winning while their own seat sat empty.
    //
    // sessionStorage holds a pointer to WHICH of those seats belongs to THIS tab. It is
    // per-tab and survives a reload, which is exactly the distinction localStorage cannot
    // make. If it is missing -- the tab was closed, or the browser crashed -- the candidates
    // are tried newest-first and the server rejects any seat that is still connected, so the
    // occupied one is skipped and the empty one is found anyway.

    SESSIONS_KEY = 'sbp2.gameSessions';
    SEAT_KEY = 'sbp2.mySeat';

    // Only to stop the list growing without bound; the server is the authority on whether a
    // session is still good. Generous, because a paused game can now be held for 30 minutes
    // and the timestamp is from when the game STARTED, not when it paused.
    SESSION_TTL_MS = 2 * 60 * 60 * 1000;
    MAX_SESSIONS = 5;

    loadSessions = () => {
        try {
            const raw = localStorage.getItem(this.SESSIONS_KEY);
            if (!raw) return [];
            const list = JSON.parse(raw);
            if (!Array.isArray(list)) return [];
            const cutoff = Date.now() - this.SESSION_TTL_MS;
            return list.filter(s => s && s.gameId && s.token && (s.savedAt || 0) > cutoff);
        } catch (err) {
            return [];
        }
    }

    writeSessions = (list) => {
        try {
            localStorage.setItem(this.SESSIONS_KEY,
                JSON.stringify(list.slice(-this.MAX_SESSIONS)));
        } catch (err) {
            // Private browsing and a full quota both throw here. A failed save costs the
            // ability to rejoin, which is no worse than before this existed -- it must
            // never cost the game that is starting right now.
            console.warn('Could not save game session', err);
        }
    }

    saveSession = (gameId, side, token) => {
        const entry = { gameId, side, token, mode: this.gameMode, savedAt: Date.now() };
        this.writeSessions(
            this.loadSessions().filter(s => !(s.gameId === gameId && s.side === side))
                               .concat([entry]));
        // Claim it for THIS tab. Survives a reload, dies with the tab.
        try { sessionStorage.setItem(this.SEAT_KEY, `${gameId}:${side}`); } catch (err) { /* ignore */ }
        this.session = entry;
    }

    /// This tab's own seat if it has one, else null. Used by the reconnect paths, which
    /// must never guess at a seat when the tab knows which one is its own.
    loadSession = () => {
        if (this.session) return this.session;
        let mine = null;
        try { mine = sessionStorage.getItem(this.SEAT_KEY); } catch (err) { /* ignore */ }
        if (!mine) return null;
        return this.loadSessions().find(s => `${s.gameId}:${s.side}` === mine) ?? null;
    }

    /// Every seat worth asking the server about, this tab's own first.
    candidateSessions = () => {
        let mine = null;
        try { mine = sessionStorage.getItem(this.SEAT_KEY); } catch (err) { /* ignore */ }
        const list = this.loadSessions().reverse();   // newest first
        return list.sort((a, b) => (`${b.gameId}:${b.side}` === mine ? 1 : 0)
                                 - (`${a.gameId}:${a.side}` === mine ? 1 : 0));
    }

    forgetSession = (gameId, side) => {
        this.writeSessions(this.loadSessions()
            .filter(s => !(s.gameId === gameId && (side == null || s.side === side))));
        if (this.session && this.session.gameId === gameId) this.session = null;
        try {
            const mine = sessionStorage.getItem(this.SEAT_KEY);
            if (mine && mine.startsWith(gameId + ':')) sessionStorage.removeItem(this.SEAT_KEY);
        } catch (err) { /* ignore */ }
    }

    /// Drop whatever seat this tab is holding -- the game ended, or the player abandoned it.
    clearSession = () => {
        const s = this.loadSession();
        if (s) this.forgetSession(s.gameId, s.side);
        this.session = null;
    }

    /// Ask the server, seat by seat, whether any of them is still waiting for this browser.
    /// Stops at the first one it can actually take. A seat the server says is VALID but not
    /// available is someone else's live tab -- left alone rather than deleted, because
    /// deleting it would break the tab that owns it.
    checkPendingSession = async () => {
        const candidates = this.candidateSessions();
        if (candidates.length === 0) return false;
        await this.ready;

        let mine = null;
        try { mine = sessionStorage.getItem(this.SEAT_KEY); } catch (err) { /* ignore */ }

        for (const c of candidates) {
            let res;
            try {
                res = await this.connection.invoke("CheckRejoin", c.gameId, c.token);
            } catch (err) {
                console.error('Rejoin check failed', err);
                return false;
            }
            if (res && res.available) {
                this.session = c;
                this.gameMode = c.mode ?? this.gameMode;
                try { sessionStorage.setItem(this.SEAT_KEY, `${c.gameId}:${c.side}`); }
                catch (err) { /* ignore */ }
                if (this.rejoinPromptCallback)
                    this.rejoinPromptCallback(true, res.secondsRemaining, null, res.claimable);
                return true;
            }
            // A TAB MAY ONLY PRUNE ITS OWN SEAT. These entries are shared with every other
            // tab on this origin, and "not valid" is not the same as "dead": a seat waiting
            // in a LOBBY reports invalid because the game has not started, so a second tab
            // opening the site was deleting the first tab's live session out from under it.
            if (res && !res.valid && mine === `${c.gameId}:${c.side}`)
                this.forgetSession(c.gameId, c.side);
        }
        return false;
    }

    rejoin = async (gameId, token) => {
        const saved = this.session ?? this.loadSession();
        gameId = gameId || saved?.gameId;
        token = token || saved?.token;
        if (!gameId || !token) return false;

        // Tells the GameJoined handler to rebuild team and loadout from the server's state
        // instead of trusting selection screens this page never saw.
        this.rejoining = true;
        this.currentGameId = gameId;
        this.sessionToken = token;
        this.gameMode = saved?.mode ?? this.gameMode;
        this.winnerSide = 0;
        this.gameOver = false;

        try {
            await this.ready;
            await this.connection.invoke("RejoinGame", gameId, token);
            return true;
        } catch (err) {
            console.error('Rejoin failed', err);
            this.rejoining = false;
            return false;
        }
    }

    /// Decline the rejoin. Ends the opponent's wait immediately instead of making them sit
    /// out a countdown for a game this player has already walked away from.
    abandonPendingSession = async () => {
        const saved = this.session ?? this.loadSession();
        this.clearSession();
        if (!saved) return;
        try {
            await this.ready;
            await this.connection.invoke("AbandonGame", saved.gameId, saved.token);
        } catch (err) {
            console.error('Abandon failed', err);
        }
    }

    /// Stop waiting for the missing player and take the win. Only offered once the server
    /// has said the game is claimable; it validates that again before acting.
    claimVictory = async () => {
        try {
            await this.connection.invoke("ClaimVictory", this.currentGameId);
        } catch (err) {
            console.error('Claim failed', err);
        }
    }

    createGame = async () => {
        const mode = connection.gameMode || 'mp';
        const response = await fetch(`/api/games`, { 
            method: "POST",
            headers: {
                "Content-Type": "application/json"
            },
            body: JSON.stringify({ gameMode: mode })
        });
        const data = await response.json();

        await this.joinGame(data.gameId, this.selectedTeam);
    }

    joinGame = async (gameId) => {
        this.currentGameId = gameId;
        this.winnerSide = 0;
        this.gameOver = false;
        this.latestState = null;

        await this.connection.invoke("JoinGame", gameId, this.selectedTeam, this.selectedLoadout);
    }

    // Practice mode: create the lobby then join with an explicit opponent choice
    // instead of Training League's random roll. See GameHub.JoinPracticeGame.
    createPracticeGame = async () => {
        const response = await fetch(`/api/games`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ gameMode: "practice" })
        });
        const data = await response.json();

        this.currentGameId = data.gameId;
        this.winnerSide = 0;
        this.gameOver = false;
        this.latestState = null;

        await this.connection.invoke("JoinPracticeGame", data.gameId, this.selectedTeam, this.selectedLoadout, this.selectedOpponent);
    }

    getPracticeOpponents = async () => {
        try {
            const response = await fetch(`/api/games/practice-opponents`);
            if (!response.ok) throw new Error("Failed to fetch practice opponents.");
            return await response.json();
        } catch (error) {
            console.error("Error fetching practice opponents:", error);
            return { spamTiers: [1, 2, 3, 4, 5, 6, 7, 8], antiSpamAvailable: true, modelNames: [] };
        }
    }

    getAllGames = async () => {
        try {
            const response = await fetch(`/api/games/all`);
            
            if (!response.ok) {
                throw new Error("Failed to fetch the game list.");
            }

            const data = await response.json();
            
            return data; 

        } catch (error) {
            console.error("Error fetching games:", error);
            return { activeGames: [], lobbyGames: [] }; 
        }
    }

    spawnUnit = (unitId) => {
        this.connection.invoke("SpawnUnit", this.currentGameId, unitId);
    }

    invest = () => {
        this.connection.invoke("Invest", this.currentGameId);
    }
    repair = () => {
        this.connection.invoke("Repair", this.currentGameId);
    }

    useGadget = (gadgetId, position) => {
        this.connection.invoke("UseGadget", this.currentGameId, gadgetId, position);
    }

    onPlayGadgetAnimation = (callback) => {
        this.gadgetAnimationCallback = callback;
    }
    onGadgetUpgraded = (callback) => {
        this.gadgetUpgradedCallback = callback;
    }
}

const connection = new GameConnection();
export default connection;