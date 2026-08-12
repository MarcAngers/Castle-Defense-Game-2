import { showScreen } from '../../../src/router.js';
import connection from '../../../src/game-connection.js';
import view from '../../../src/view.js';

export default async function initMainMenu() {
    view.mapColour = 'white';
    view.draw();

    const btnSP = document.getElementById('btnSingleplayer');
    const btnMP = document.getElementById('btnMultiplayer');
    const btnLeague = document.getElementById('btnTrainingLeague');
    const btnPractice = document.getElementById('btnPractice');
    const btnCollection = document.getElementById('btnCollection');

    btnSP.onclick = () => {
        connection.gameMode = 'sp';
        showScreen('select-team');
    };
    btnMP.onclick = () => {
        connection.gameMode = 'mp';
        showScreen('select-team');
    };
    // Acceptance Test. Replaced Training League (spectating v4 vs HeuristicBot) on
    // 2026-08-11: watching two bots play was diagnostic, and the diagnostic that
    // matters now is whether the flagship beats Marc. Straight into a game against
    // the shipped search bot with server-assigned random teams and loadouts on both
    // sides — no selection screens, so there is nothing to reroll.
    btnLeague.onclick = async () => {
        connection.gameMode = 'accept';
        await connection.createGame();
    };
    btnPractice.onclick = () => {
        connection.gameMode = 'practice';
        showScreen('select-team');
    };

    btnCollection.onclick = () => {
        showScreen('collection');
    };
}