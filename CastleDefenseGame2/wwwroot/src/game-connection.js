import { showScreen } from './router.js';

class GameConnection {
    constructor() {
        // API Configuration
        this.API_URL = "http://localhost:5168";

        this.gadgetAnimationListeners = [];
        
        this.connection = null;
        this.currentGameId = null;
        this.selectedTeam = "white";
        this.selectedLoadout = [];
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
        });

        this.connection.on("GameStateUpdate", (state) => {
            this.latestState = state;
        });

        this.connection.on("GameStarted", () => {
            this.winnerSide = 0;
            showScreen("game");
        });

        this.connection.on("PlayGadgetAnimation", (gadgetId, side, position, targetId) => {
            console.log("Play animation for:", gadgetId, side, position);
            this.gadgetAnimationListeners.forEach(ga => ga(gadgetId, side, position, targetId));
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
        const response = await fetch(`/api/games`, { method: "POST" });
        const data = await response.json();

        await this.joinGame(data.gameId, this.selectedTeam);
    }

    joinGame = async (gameId) => {
        this.currentGameId = gameId;

        await this.connection.invoke("JoinGame", gameId, this.selectedTeam, this.selectedLoadout);
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
        this.gadgetAnimationListeners.push(callback);
    }
}

const connection = new GameConnection();
export default connection;