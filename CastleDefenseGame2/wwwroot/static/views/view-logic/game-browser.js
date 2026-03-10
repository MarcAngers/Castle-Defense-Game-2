import { showScreen } from '../../../src/router.js';
import connection from '../../../src/game-connection.js';


export default async function initGameBrowser() {
    const btnBack = document.getElementById('btnBack');
    const btnRefresh = document.getElementById('btnRefresh');
    const btnJoin = document.getElementById('btnJoin');

    const inputField = document.getElementById('game-id-input');

    document.getElementById('game-browser').style.backgroundColor = connection.selectedTeam;
    if (connection.selectedTeam == 'black')
        document.getElementById('browser-title').style.color = 'white';
    else 
        document.getElementById('browser-title').style.color = 'black';

    await loadGames();

    btnBack.onclick = () => {
        showScreen('select-loadout');
    };
    btnRefresh.onclick = () => {
        loadGames();
    }
    btnJoin.onclick = async () => {
        await connection.joinGame(inputField.value, connection.selectedTeam, connection.selectedLoadout);

        showScreen('game');
    };
}

async function loadGames() {
    const listContainer = document.getElementById('game-id-list');
    const inputField = document.getElementById('game-id-input');

    // 1. Show a loading state while fetching
    listContainer.innerHTML = '<p>Searching for games...</p>';

    // 2. Fetch the data from your server
    const gameList = await connection.getAllGames();
    
    // Clear the loading text
    listContainer.innerHTML = ''; 

    // Helper function to build a clickable row
    const createRow = (id, playerCount, isActive) => {
        const row = document.createElement('div');
        row.className = 'game-list-item';
        
        // If it's full, you might want to style it differently
        if (isActive) row.classList.add('game-full');

        // Flexbox will automatically push these to opposite sides
        const pixelIcon = `
            <svg width="16" height="16" viewBox="0 0 16 16" fill="black" xmlns="http://www.w3.org/2000/svg">
                <rect x="5" y="2" width="6" height="6" />
                <rect x="3" y="9" width="10" height="7" />
            </svg>
        `;

        row.innerHTML = `
            <span class="game-list-id">${id}</span>
            <span class="game-list-players" style="display: flex; align-items: center; gap: 6px;">
                ${playerCount}/2 ${pixelIcon}
            </span>
        `;

        // Click event to populate the input
        row.onclick = () => {
            inputField.value = id;
        };

        return row;
    };

    // 3. Handle the "Empty Server" case
    if (gameList.lobbyGames.length === 0 && gameList.activeGames.length === 0) {
        listContainer.innerHTML = '<p>No games currently hosted.</p>';
        return;
    }

    // 4. Append Lobby Games (1/2) first
    gameList.lobbyGames.forEach(id => {
        listContainer.appendChild(createRow(id, 1, false));
    });

    // 5. Append Active Games (2/2) last
    gameList.activeGames.forEach(id => {
        listContainer.appendChild(createRow(id, 2, true));
    });
}