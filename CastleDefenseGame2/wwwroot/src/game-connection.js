import { showScreen } from './router.js';

class GameConnection {
    constructor() {
        // API Configuration
        this.API_URL = "http://localhost:5168";
        
        this.connection = null;
        this.currentGameId = null;
        this.selectedTeam = "white";
        this.mySide = 0; // 1 = Left, 2 = Right
        this.latestState = null;

        this.buildConnection();
    }

    buildConnection = async () => {
        // --- SIGNALR CONNECTION ---
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(`${this.API_URL}/gameHub`)
            .build();

        this.connection.on("GameJoined", (side, state) => {
            this.mySide = side;
            this.latestState = state;
        });

        this.connection.on("GameStateUpdate", (state) => {
            this.latestState = state;
        });

        this.connection.on("GameStarted", () => {
            showScreen("game");
        })

        try {
            await this.connection.start();
            console.log("SignalR Connected.");
        } catch (err) {
            console.error("SignalR Error:", err);
        }
    }

    createGame = async (p1Colour) => {
        const response = await fetch(`${this.API_URL}/api/games`, { method: "POST" });
        const data = await response.json();

        await this.joinGame(data.gameId, p1Colour);
    }

    joinGame = async (gameId, colour) => {
        this.currentGameId = gameId;

        await this.connection.invoke("JoinGame", gameId, colour);
    }

    getAllGames = async () => {
        try {
            const response = await fetch(`${this.API_URL}/api/games/all`);
            
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
}

const connection = new GameConnection();
export default connection;