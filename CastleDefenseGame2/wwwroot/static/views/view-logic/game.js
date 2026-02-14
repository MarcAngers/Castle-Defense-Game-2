import { showScreen } from '../../../src/router.js';
import View from '../../../src/view.js';
import connection from '../../../src/game-connection.js';

export default function initGameScreen() {
    const gameView = new View('gameCanvas');
    let myTeam = null
    if (connection.latestStateconnection.mySide == 1) {
        myTeam = connection.latestState.player1.team;
    }
    if (connection.latestStateconnection.mySide == 2) {
        myTeam = connection.latestState.player2.team;
    }
    initShopUI(myTeam);

    const gameLoop = () => {
        gameView.clear();

        if (connection.latestState) {
            gameView.drawGameState(connection.latestState);
            updateUI(latestState, connection.mySide);
        }

        requestAnimationFrame(gameLoop);
    };

    requestAnimationFrame(gameLoop);
}

function updateUI(state, side) {

}

function initShopUI() {
    
}