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
        if (connection.winnerSide != 0) {
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

    // Update gadget section:
    // let signatureGadget, offensiveGadget, defensiveGadget;

    // const btnGadgetSignature = document.getElementById('btnGadgetSignature');
    // const btnGadgetOffense = document.getElementById('btnGadgetOffense');
    // const btnGadgetDefence = document.getElementById('btnGadgetDefence');

    // if (side == 1) {
    //     signatureGadget = state.player1.signatureGadget.Id;
    //     offensiveGadget = state.player1.offensiveGadget.Id;
    //     defensiveGadget = state.player1.defensiveGadget.Id;
    // }
    // if (side == 2) {
    //     signatureGadget = state.player2.signatureGadget.Id;
    //     offensiveGadget = state.player2.offensiveGadget.Id;
    //     defensiveGadget = state.player2.defensiveGadget.Id;
    // }
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

    // Set image and price for offensive gadget button
    let price = document.createElement('span');
    price.innerHTML = '$' + loader.assets.gadgetData[connection.selectedLoadout[0]]['cost'] + ': ';
    btnGadgetOffense.appendChild(price);
    let source = loader.assets.gadgets[connection.selectedLoadout[0]];
    let img = document.createElement('img');
    if (typeof source === 'string') {
            img.src = source;
        } else {
            img.src = source.src; // If it's already a new Image() object
        }
    btnGadgetOffense.appendChild(img);

    // Set image and price for defensive gadget button
    price = document.createElement('span');
    price.innerHTML = '$' + loader.assets.gadgetData[connection.selectedLoadout[1]]['cost'] + ': ';
    btnGadgetDefence.appendChild(price);
    source = loader.assets.gadgets[connection.selectedLoadout[1]];
    img = document.createElement('img');
    if (typeof source === 'string') {
            img.src = source;
        } else {
            img.src = source.src; // If it's already a new Image() object
        }
    btnGadgetDefence.appendChild(img);

    // Set image and price for signature gadget button
    price = document.createElement('span');
    price.innerHTML = '$' + loader.assets.gadgetData[connection.selectedLoadout[2]]['cost'] + ': ';
    btnGadgetSignature.appendChild(price);
    source = loader.assets.gadgets[connection.selectedLoadout[2]];
    img = document.createElement('img');
    if (typeof source === 'string') {
            img.src = source;
        } else {
            img.src = source.src; // If it's already a new Image() object
        }
    btnGadgetSignature.appendChild(img);

    btnGadgetSignature.addEventListener('click', () => {
        gameView.gadgetManager.activateTargeting(connection.selectedLoadout[2]);
    });

    btnGadgetOffense.addEventListener('click', () => {
        gameView.gadgetManager.activateTargeting(connection.selectedLoadout[0]);
    });

    btnGadgetDefence.addEventListener('click', () => {
        gameView.gadgetManager.activateTargeting(connection.selectedLoadout[1]);
    });
}