import { showScreen } from '../../../src/router.js';
import View from '../../../src/view.js';
import connection from '../../../src/game-connection.js';

export default function initLobby() {
    let lobbyView = new View('bgCanvas');  
    lobbyView.drawBackground('white');
    lobbyView.drawForeground('white');

    document.getElementById('lobby-id-text').innerText = connection.currentGameId;

    const btnExit = document.getElementById('btnExit');
    const btnCopy = document.getElementById('btnCopyLobbyId');

    btnCopy.onclick = async () => {
        if (!connection.currentGameId) return; 

        try {
            await navigator.clipboard.writeText(connection.currentGameId);
            
            btnCopy.innerText = "Copied!";
            
            setTimeout(() => {
                btnCopy.innerText = "Copy";
            }, 2000);

        } catch (err) {
            console.error("Clipboard permission denied or failed: ", err);
            btnCopy.innerText = "Error";
        }
    };

    btnExit.onclick = () => {
        // Delete Lobby here somehow?
        showScreen('main-menu');
    };
}