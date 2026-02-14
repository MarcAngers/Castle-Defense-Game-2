import { showScreen } from '../../../src/router.js';
import View from '../../../src/view.js';
import connection from '../../../src/game-connection.js';

export default function initLobby() {
    let lobbyView = new View('bgCanvas');  
    lobbyView.drawBackground('white');

    const btnExit = document.getElementById('btnExit');

    document.getElementById('lobby-id').innerHTML = 'LOBBY ID: ' + connection.currentGameId;

    btnExit.onclick = () => {
        // Delete Lobby here somehow?
        showScreen('main-menu');
    };
}