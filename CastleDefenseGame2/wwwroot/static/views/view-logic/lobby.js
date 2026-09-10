import { showScreen } from '../../../src/router.js';
import view from '../../../src/view.js';
import connection from '../../../src/game-connection.js';

export default function initLobby() {
    view.draw();

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
        // Tear the lobby down on the way out. Leaving is a VIEW change, not a disconnect, so
        // the server's OnDisconnectedAsync cleanup never fired for this path and the empty
        // lobby lingered in the game browser until the tab was closed.
        //
        // Not awaited: the menu should appear the instant it is pressed rather than after a
        // round trip, and leaveLobby swallows its own errors. Nothing after this depends on
        // the result -- if the call fails the lobby is no worse off than it was before.
        connection.leaveLobby();
        showScreen('main-menu');
    };
}