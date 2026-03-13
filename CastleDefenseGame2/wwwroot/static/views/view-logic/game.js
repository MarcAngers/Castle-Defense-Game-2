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
            updateUI(connection.latestState);
        }

        animationFrameId = requestAnimationFrame(gameLoop);
    };

    requestAnimationFrame(gameLoop);
}

function updateUI(state) {
    const pState = connection.mySide == 1 ? state.player1 : state.player2;
    const money = document.getElementById('money');
    const income = document.getElementById('income');
    const investment = document.getElementById('investment-price');
    const repair = document.getElementById('repair-price');

    money.innerHTML = Math.floor(pState.money);
    income.innerHTML = pState.income.toFixed(1);
    investment.innerHTML = Math.ceil(pState.investmentPrice);
    repair.innerHTML = pState.repairPrice;

    // -------- UPDATE SHOP --------
    // --- Update Invest/Repair Affordability ---
    const btnInvest = document.getElementById('btnInvest');
    if (btnInvest) btnInvest.disabled = pState.money < pState.investmentPrice;

    const btnRepair = document.getElementById('btnRepair');
    if (btnRepair) btnRepair.disabled = pState.money < pState.repairPrice;

    // --- Update Unit Affordability ---
    const characterElements = document.getElementsByClassName('character');
    Array.from(characterElements).forEach(charDiv => {
        const unitId = charDiv.id;
        if (!unitId) return;

        const stats = loader.getUnitStats(unitId);
        if (!stats) return;

        // Note: Check how your JSON is formatted (price vs Price)
        const cost = stats.price || stats.Price; 

        // Apply or remove the custom CSS class based purely on funds!
        if (pState.money < cost) {
            charDiv.classList.add('disabled');
        } else {
            charDiv.classList.remove('disabled');
        }
    });
    
    // 2. Helper function to process each gadget button cleanly
    function updateGadgetButton(btnElementId, gadgetDef) {
        const button = document.getElementById(btnElementId);
        if (!button || !gadgetDef) return;

        // Safely handle C# JSON serialization casing
        const gadgetId = gadgetDef.id || gadgetDef.Id;
        const cost = gadgetDef.cost || gadgetDef.Cost;
        const cooldownMs = gadgetDef.cooldownMs || gadgetDef.CooldownMs;

        // Check if the timer exists in the dictionary, default to 0
        const remainingTicks = pState.gadgetCooldowns[gadgetId] || 0;

        // 30 server ticks per second
        const maxTicks = cooldownMs / (1000 / 30); 

        // Calculate the percentage for the CSS overlay
        let percent = 0;
        if (remainingTicks > 0 && maxTicks > 0) {
            percent = (remainingTicks / maxTicks) * 100;
        }

        // Apply it directly to the CSS variable!
        button.style.setProperty('--cooldown-pct', `${percent}%`);

        // Disable the button entirely if it's on cooldown OR they are too poor
        if (remainingTicks > 0 || pState.money < cost) {
            button.disabled = true;
            
            // Failsafe: If they are currently targeting with a gadget they 
            // suddenly can't afford/use, cancel their targeting!
            if (window.gameView && window.gameView.gadgetManager && window.gameView.gadgetManager.activeGadgetId === gadgetId) {
                window.gameView.gadgetManager.cancelTargeting();
            }
        } else {
            button.disabled = false;
        }
    }

    // 3. Execute for all three slots
    updateGadgetButton('btnGadgetOffense', pState.offensiveGadget);
    updateGadgetButton('btnGadgetDefence', pState.defensiveGadget);
    updateGadgetButton('btnGadgetSignature', pState.signatureGadget);
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
                if (character.classList.contains('disabled')) return;

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