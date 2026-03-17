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

        const baseId = gadgetId.split('_')[0].toLowerCase();
        const currentXp = pState.gadgetXp[baseId] || 0;
        const upgradeCost = gadgetDef.upgradeCost || gadgetDef.UpgradeCost;

        // --- USE THE HELPER ---
        const currentLevel = getGadgetLevel(gadgetId);

        // Lock the bar at 100% if they hit Level 3
        if (currentLevel >= 3 || !upgradeCost || upgradeCost <= 0) {
            button.style.setProperty('--xp-pct', '100%');
        } else {
            const xpPercent = Math.min((currentXp / upgradeCost) * 100, 100);
            button.style.setProperty('--xp-pct', `${xpPercent}%`);
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
    const loadout0 = connection.selectedLoadout[0];
    buildGadgetDOM(
        btnGadgetOffense, 
        loader.assets.gadgetData[loadout0].cost || loader.assets.gadgetData[loadout0].Cost, 
        loader.assets.gadgets[loadout0],
        getGadgetLevel(loadout0)
    );

    const loadout1 = connection.selectedLoadout[1];
    buildGadgetDOM(
        btnGadgetDefence, 
        loader.assets.gadgetData[loadout1].cost || loader.assets.gadgetData[loadout1].Cost, 
        loader.assets.gadgets[loadout1],
        getGadgetLevel(loadout1)
    );

    const loadout2 = connection.selectedLoadout[2];
    buildGadgetDOM(
        btnGadgetSignature, 
        loader.assets.gadgetData[loadout2].cost || loader.assets.gadgetData[loadout2].Cost, 
        loader.assets.gadgets[loadout2],
        getGadgetLevel(loadout2)
    );

    btnGadgetSignature.addEventListener('click', () => {
        gameView.gadgetManager.activateTargeting(connection.selectedLoadout[2]);
    });

    btnGadgetOffense.addEventListener('click', () => {
        gameView.gadgetManager.activateTargeting(connection.selectedLoadout[0]);
    });

    btnGadgetDefence.addEventListener('click', () => {
        gameView.gadgetManager.activateTargeting(connection.selectedLoadout[1]);
    });

    connection.onGadgetUpgraded((side, newGadgetDef) => {
        if (side !== connection.mySide) return;

        const gadgetId = newGadgetDef.id || newGadgetDef.Id;
        let targetBtnId = null;

        // Helper to strip the string down to just "nuke", "firebomb", etc.
        const getBaseId = (id) => id.split('_')[0].toLowerCase();
        
        const incomingBase = getBaseId(gadgetId);

        if (incomingBase === getBaseId(connection.selectedLoadout[0])) {
            targetBtnId = 'btnGadgetOffense';
            connection.selectedLoadout[0] = gadgetId;
        }
        else if (incomingBase === getBaseId(connection.selectedLoadout[1])) {
            targetBtnId = 'btnGadgetDefence';
            connection.selectedLoadout[1] = gadgetId;
        }
        else if (incomingBase === getBaseId(connection.selectedLoadout[2])) {
            targetBtnId = 'btnGadgetSignature';
            connection.selectedLoadout[2] = gadgetId;
        }

        if (targetBtnId) {
            applyUpgradeToButton(targetBtnId, newGadgetDef);
        }
    });
}

function buildGadgetDOM(btnElement, cost, imgSrc, currentLevel) {
    btnElement.innerHTML = ''; 
    
    const priceSpan = document.createElement('span');
    priceSpan.innerHTML = '$' + cost + ': ';
    btnElement.appendChild(priceSpan);

    if (imgSrc) {
        const img = document.createElement('img');
        img.src = typeof imgSrc === 'string' ? imgSrc : imgSrc.src;
        btnElement.appendChild(img);
    }

    const xpContainer = document.createElement('div');
    xpContainer.className = 'xp-container';
    
    xpContainer.innerHTML = `
        <div class="xp-fill"></div>
        <span class="xp-text">Lvl: ${currentLevel}</span>
    `;
    
    btnElement.appendChild(xpContainer);
}

function applyUpgradeToButton(btnId, gadgetDef) {
    const btn = document.getElementById(btnId);
    if (!btn) return;

    // --- 1. PLAY VISUALS ---
    btn.classList.add('upgrade-flash');
    
    // A mix of bright white and varying shades of gray for depth
    const chevronColors = ['#ffffff', '#aaaaaa', '#dddddd', '#777777', '#ffffff'];
    
    // Spawn 5 full-width chevrons, tightly staggered
    for (let i = 0; i < chevronColors.length; i++) {
        setTimeout(() => {
            const chevron = document.createElement('div');
            chevron.classList.add('upgrade-chevron');
            
            // Inject the specific shade for this chevron
            chevron.style.setProperty('--chevron-color', chevronColors[i]);
            
            btn.appendChild(chevron);
            
            // Clean up the DOM node after it finishes flying
            setTimeout(() => chevron.remove(), 600);
        }, i * 90); // 90ms stagger creates a smooth, overlapping wave
    }

    // Clean up the flash class
    setTimeout(() => btn.classList.remove('upgrade-flash'), 800);

    // --- 2. UPDATE IMAGE & PRICE ---
    const cost = gadgetDef.cost || gadgetDef.Cost;
    const gadgetId = gadgetDef.id || gadgetDef.Id;
    const currentLevel = getGadgetLevel(gadgetId);

    // Clear out the old elements
    btn.innerHTML = '';
    
    // Build the new price tag
    const priceSpan = document.createElement('span');
    priceSpan.innerHTML = '$' + cost + ': ';
    btn.appendChild(priceSpan);

    // Build the new image
    const baseId = gadgetId.split('_')[0].toLowerCase();
    // If there's no new image, fallback to the same old one
    const imgSrc = loader.assets.gadgets[gadgetId] || loader.assets.gadgets[baseId];
    if (imgSrc) {
        const img = document.createElement('img');
        img.src = typeof imgSrc === 'string' ? imgSrc : imgSrc.src;
        btn.appendChild(img);
    }

    buildGadgetDOM(btn, cost, imgSrc, currentLevel);
}

// --- HELPER: Derives level strictly from the ID string ---
function getGadgetLevel(gadgetId) {
    if (!gadgetId) return 1;
    const parts = gadgetId.split('_');
    // If it has an underscore (e.g., ["nuke", "2"]), parse the number. Otherwise, Level 1.
    return parts.length > 1 ? parseInt(parts[1], 10) || 1 : 1;
}