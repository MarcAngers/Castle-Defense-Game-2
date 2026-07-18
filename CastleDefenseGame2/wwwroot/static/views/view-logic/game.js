import view from '../../../src/view.js';
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

    if (connection.mySide == 0) {
        // Spectator (Training League watch mode): both sides are AI-controlled and
        // there's nothing for a human to click, so skip the interactive shop UI
        // entirely and show both players' stats side by side instead of just one.
        startingCameraX = 1000;
        document.getElementById('hud-bottom').style.display = 'none';
        document.getElementById('hud-top').style.float = 'left';
        document.getElementById('hud-top-label').style.display = 'block';
        document.getElementById('hud-top-p2').style.display = 'block';
    } else {
        initShopUI(myTeam);
    }

    view.panTo(startingCameraX);

    let animationFrameId;

    const gameLoop = () => {
        if (connection.winnerSide != 0) {
            cancelAnimationFrame(animationFrameId);
            animationFrameId = null;
            return;
        }
        
        view.clear();

        if (connection.latestState) {
            view.drawGameState(connection.latestState);
            updateUI(connection.latestState);
        }

        animationFrameId = requestAnimationFrame(gameLoop);
    };

    requestAnimationFrame(gameLoop);
}

function updateUI(state) {
    if (connection.mySide == 0) {
        // Spectator: no shop to keep in sync, just both sides' money/income readout.
        document.getElementById('money').innerHTML = Math.floor(state.player1.money);
        document.getElementById('income').innerHTML = state.player1.income.toFixed(1);
        document.getElementById('money-p2').innerHTML = Math.floor(state.player2.money);
        document.getElementById('income-p2').innerHTML = state.player2.income.toFixed(1);
        return;
    }

    const pState = connection.mySide == 1 ? state.player1 : state.player2;
    const money = document.getElementById('money');
    const income = document.getElementById('income');
    const investment = document.getElementById('investment-price');
    const repair = document.getElementById('repair-price');

    money.innerHTML = Math.floor(pState.money);
    income.innerHTML = pState.income.toFixed(1);
    investment.innerHTML = Math.ceil(pState.investmentPrice);
    repair.innerHTML = pState.repairPrice.toFixed(0);

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
            if (window.view && window.view.gadgetManager && window.view.gadgetManager.activeGadgetId === gadgetId) {
                window.view.gadgetManager.cancelTargeting();
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

function initShopUI(team) {
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
            const source = teamImages[index];
            const img = document.createElement('img');
            
            if (typeof source === 'string') {
                img.src = source;
            } else {
                img.src = source.src; 
            }
            img.draggable = false; 

            character.id = loader.assets.unitList[team][index];
            character.appendChild(img);

            // --- 1. BUILD THE TOOLTIP DOM ---
            const stats = loader.getUnitStats(character.id);
            const wrapper = character.parentElement; // The .character-icon-wrapper

            const tooltip = document.createElement('div');
            tooltip.className = 'unit-tooltip';
            tooltip.innerHTML = `
                <div class="stat"><img src="${loader.assets.tooltips.heart.src}" alt="HP"> ${stats.health}</div>
                <div class="stat"><img src="${loader.assets.tooltips.sword.src}" alt="DMG"> ${stats.damage}</div>
                <div class="stat"><img src="${loader.assets.tooltips.boot.src}" alt="SPD"> ${stats.speed}</div>
            `;
            wrapper.appendChild(tooltip);

            // --- 2. DESKTOP HOVER LOGIC ---
            wrapper.addEventListener('mouseenter', () => tooltip.classList.add('visible'));
            wrapper.addEventListener('mouseleave', () => tooltip.classList.remove('visible'));

            // --- 3. MOBILE LONG-PRESS LOGIC ---
            let pressTimer;
            let isLongPress = false;

            wrapper.addEventListener('touchstart', (e) => {
                isLongPress = false; // Reset on every new touch
                pressTimer = setTimeout(() => {
                    isLongPress = true; // Timer reached 1 second!
                    tooltip.classList.add('visible');
                }, 1000);
            }, { passive: true });

            wrapper.addEventListener('touchend', (e) => {
                clearTimeout(pressTimer);
                tooltip.classList.remove('visible');
                
                // THE LIFESAVER: If they held it long enough to see the tooltip, 
                // we destroy the synthetic click so they don't buy the unit!
                if (isLongPress) {
                    e.preventDefault(); 
                }
            });

            // If the user drags their finger away, cancel the timer
            wrapper.addEventListener('touchmove', () => {
                clearTimeout(pressTimer);
                tooltip.classList.remove('visible');
            }, { passive: true });
            
            wrapper.addEventListener('touchcancel', () => {
                clearTimeout(pressTimer);
                tooltip.classList.remove('visible');
            });

            // --- 4. THE SPAWN LOGIC ---
            character.addEventListener('click', (e) => {
                if (character.classList.contains('disabled')) return;
                connection.spawnUnit(e.target.parentElement.id);
            });

            priceElements[index].innerHTML = '$' + stats.price; // Re-used the stats object here!
        }
    });

    const btnGadgetSignature = document.getElementById('btnGadgetSignature');
    const btnGadgetOffense = document.getElementById('btnGadgetOffense');
    const btnGadgetDefence = document.getElementById('btnGadgetDefence');

    // Set image and price for gadget buttons.
    // Gadget IDs may be versioned (e.g. "nuke_2") when starting from a time-machine state,
    // so use the base ID for asset lookups but the full ID for level calculation.
    const getBaseId = (id) => id ? id.split('_')[0].toLowerCase() : '';

    const loadout0 = connection.selectedLoadout[0];
    const base0 = getBaseId(loadout0);
    const data0 = loader.assets.gadgetData[loadout0] || loader.assets.gadgetData[base0];
    buildGadgetDOM(
        btnGadgetOffense,
        data0?.cost || data0?.Cost,
        loader.assets.gadgets[loadout0] || loader.assets.gadgets[base0],
        getGadgetLevel(loadout0)
    );

    const loadout1 = connection.selectedLoadout[1];
    const base1 = getBaseId(loadout1);
    const data1 = loader.assets.gadgetData[loadout1] || loader.assets.gadgetData[base1];
    buildGadgetDOM(
        btnGadgetDefence,
        data1?.cost || data1?.Cost,
        loader.assets.gadgets[loadout1] || loader.assets.gadgets[base1],
        getGadgetLevel(loadout1)
    );

    const loadout2 = connection.selectedLoadout[2];
    const base2 = getBaseId(loadout2);
    const data2 = loader.assets.gadgetData[loadout2] || loader.assets.gadgetData[base2];
    buildGadgetDOM(
        btnGadgetSignature,
        data2?.cost || data2?.Cost,
        loader.assets.gadgets[loadout2] || loader.assets.gadgets[base2],
        getGadgetLevel(loadout2)
    );

    btnGadgetSignature.addEventListener('click', () => {
        view.gadgetManager.activateTargeting(connection.selectedLoadout[2]);
    });

    btnGadgetOffense.addEventListener('click', () => {
        view.gadgetManager.activateTargeting(connection.selectedLoadout[0]);
    });

    btnGadgetDefence.addEventListener('click', () => {
        view.gadgetManager.activateTargeting(connection.selectedLoadout[1]);
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