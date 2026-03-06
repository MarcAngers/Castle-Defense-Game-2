import { showScreen } from '../../../src/router.js';
import View from '../../../src/view.js';

export default async function initMainMenu() {
    let mainMenuView = new View('bgCanvas');  
    mainMenuView.drawBackground('white');
    mainMenuView.drawForeground('white');

    const btnSP = document.getElementById('btnSingleplayer');
    const btnMP = document.getElementById('btnMultiplayer');
    const btnCollection = document.getElementById('btnCollection');
    //const btnMP = document.getElementById('btnMultiplayer');
    
    btnSP.onclick = () => {
        showScreen('sp-select-team');
    };
    btnMP.onclick = () => {
        showScreen('mp-select-team');
    };
    btnCollection.onclick = () => {
        showScreen('collection');
    };
}