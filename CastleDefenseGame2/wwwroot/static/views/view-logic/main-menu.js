import { showScreen } from '../../../src/router.js';
import connection from '../../../src/game-connection.js';
import View from '../../../src/view.js';

export default async function initMainMenu() {
    let mainMenuView = new View('bgCanvas');  
    mainMenuView.drawBackground('white');
    mainMenuView.drawForeground('white');

    const btnSP = document.getElementById('btnSingleplayer');
    const btnMP = document.getElementById('btnMultiplayer');
    const btnCollection = document.getElementById('btnCollection');
    
    btnSP.onclick = () => {
        connection.gameMode = 'sp';
        showScreen('select-team');
    };
    btnMP.onclick = () => {
        connection.gameMode = 'mp';
        showScreen('select-team');
    };
    btnCollection.onclick = () => {
        showScreen('collection');
    };
}