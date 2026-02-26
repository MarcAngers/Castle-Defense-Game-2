import { showScreen } from '../../../src/router.js';
import View from '../../../src/view.js';
import loader from '../../../src/asset-loader.js';
import connection from '../../../src/game-connection.js';

export default function initGameScreen() {
    const gameView = new View('gameCanvas');
    let myTeam = null
    if (connection.mySide == 1) {
        myTeam = loader.assets.teamList[connection.latestState.player1.team];
    }
    if (connection.mySide == 2) {
        myTeam = loader.assets.teamList[connection.latestState.player2.team];
    }
    initShopUI(myTeam);

    const gameLoop = () => {
        gameView.clear();

        if (connection.latestState) {
            gameView.drawGameState(connection.latestState);
            updateUI(connection.latestState, connection.mySide);
        }

        requestAnimationFrame(gameLoop);
    };

    requestAnimationFrame(gameLoop);
}

function updateUI(state, side) {
    const money = document.getElementById('money');
    const income = document.getElementById('income');

    if (side == 1) {
        money.innerHTML = state.player1.money;
        income.innerHTML = state.player1.income;
    }
    if (side == 2) {
        money.innerHTML = state.player2.money;
        income.innerHTML = state.player2.income;
    }

    // Update shop cooldowns:
}

function initShopUI(team) {
    if (connection.mySide == 1) {
        document.getElementById('hud-top').style.float = 'left';
    }
    if (connection.mySide == 2) {
        document.getElementById('hud-top').style.float = 'right';
    }

    document.getElementById('character-bar').style.backgroundColor = team;
    const teamImages = loader.getTeam(team);
    const characterElements = document.getElementsByClassName('character');

    Array.from(characterElements).forEach((character, index) => {
        // Clear any old image first
        character.innerHTML = '';

        if (teamImages[index]) {
            // Check if your array contains URL strings or Image Objects
            const source = teamImages[index];
            
            const img = document.createElement('img');
            
            // Handle both String URLs and Image Objects
            if (typeof source === 'string') {
                img.src = source;
            } else {
                img.src = source.src; // If it's already a new Image() object
            }

            character.id = loader.assets.unitList[team][index];
            character.appendChild(img);

            character.addEventListener('click', (e) => {
                connection.spawnUnit(e.target.parentElement.id);
            })
        }
    });


}