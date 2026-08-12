import { showScreen } from './router.js';
import loader from './asset-loader.js';

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

        this.buildConnection();
    }

    buildConnection = async () => {
        // --- SIGNALR CONNECTION ---
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(`/gameHub`)
            .build();

        this.connection.on("GameJoined", (side, state) => {
            this.mySide = side;
            this.latestState = state;
            // League and Acceptance Test both skip loadout selection, so read the
            // server-assigned gadgets from state. This is not cosmetic: game.js binds
            // the three gadget buttons and their targeting to selectedLoadout, so
            // without it the human's gadgets are unusable in acceptance games.
            if (this.gameMode === 'league' || this.gameMode === 'accept') {
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
            this.latestState = state;
        });

        this.connection.on("GameStarted", () => {
            this.winnerSide = 0;
            showScreen("game");
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
            this.latestState = state;
            this.winnerSide = state.winnerSide;
            showScreen("game-over");
        })

        try {
            await this.connection.start();
            console.log("SignalR Connected.");
        } catch (err) {
            console.error("SignalR Error:", err);
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