import View from '../../../src/view.js';
import loader from '../../../src/asset-loader.js';
import connection from '../../../src/game-connection.js';

export default function initGameScreen() {
    let startingCameraX = 0;
    let myTeam = null;
    if (connection.mySide == 1) {
        myTeam = loader.assets.teamList[connection.latestState.player1.team];
    }
    if (connection.mySide == 2) {
        myTeam = loader.assets.teamList[connection.latestState.player2.team];
        startingCameraX = 2000;
    }
    const gameView = new View('gameCanvas', startingCameraX);
    initShopUI(myTeam, gameView);

    let animationFrameId;

    const gameLoop = () => {
        if (connection.winningSide != 0) {
            cancelAnimationFrame(animationFrameId);
            animationFrameId = null;
            return;
        }
        
        gameView.clear();

        if (connection.latestState) {
            gameView.drawGameState(connection.latestState);
            updateUI(connection.latestState, connection.mySide);
        }

        animationFrameId = requestAnimationFrame(gameLoop);
    };

    requestAnimationFrame(gameLoop);
}

function updateUI(state, side) {
    const money = document.getElementById('money');
    const income = document.getElementById('income');
    const investment = document.getElementById('investment-price');
    const repair = document.getElementById('repair-price');

    if (side == 1) {
        money.innerHTML = Math.floor(state.player1.money);
        income.innerHTML = state.player1.income.toFixed(1);
        investment.innerHTML = Math.ceil(state.player1.investmentPrice);
        repair.innerHTML = state.player1.repairPrice;
    }
    if (side == 2) {
        money.innerHTML = Math.floor(state.player2.money);
        income.innerHTML = state.player2.income.toFixed(1);
        investment.innerHTML = Math.ceil(state.player2.investmentPrice);
        repair.innerHTML = state.player2.repairPrice;
    }

    // Update shop cooldowns:
}

function initShopUI(team, gameView) {
    if (connection.mySide == 1) {
        document.getElementById('hud-top').style.float = 'left';
    }
    if (connection.mySide == 2) {
        document.getElementById('hud-top').style.float = 'right';
    }

    const btnInvest = document.getElementById('btnInvest');
    const btnRepair = document.getElementById('btnRepair');

    btnInvest.addEventListener('click', () => {
        connection.invest();
    });
    btnRepair.addEventListener('click', () => {
        connection.repair();
    });

    document.getElementById('character-bar').style.backgroundColor = team;
    if (team == 'white' || team == 'yellow') {
        document.getElementById('character-bar').style.color = 'black';
    }

    const teamImages = loader.getTeam(team);
    const priceElements = document.getElementsByClassName('price');
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
            });

            priceElements[index].innerHTML = '$' + loader.getUnitStats(character.id).price;
        }
    });

    const btnGadgetSignature = document.getElementById('btnGadgetSignature');
    const btnGadgetOffense = document.getElementById('btnGadgetOffense');
    const btnGadgetDefence = document.getElementById('btnGadgetDefence');

    btnGadgetSignature.addEventListener('click', () => {
        gameView.targetingGadgetId = "firebomb";
    });
    btnGadgetOffense.addEventListener('click', () => {
        gameView.targetingGadgetId = "reinforcements";
    });
    btnGadgetDefence.addEventListener('click', () => {
        gameView.targetingGadgetId = "freeze";
    });
}