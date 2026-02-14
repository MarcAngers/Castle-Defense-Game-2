import { showScreen } from '../../../src/router.js';
import View from '../../../src/view.js';

export default async function initGameBrowser() {
    const btnBack = document.getElementById('btnBack');
    const btnJoin = document.getElementById('btnJoin');
    
    btnBack.onclick = () => {
        showScreen('mp-select-team');
    };
    btnJoin.onclick = () => {
        showScreen('game');
    };
}