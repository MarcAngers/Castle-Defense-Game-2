import view from '../../../src/view.js';
import loader from '../../../src/asset-loader.js';
import connection from '../../../src/game-connection.js';
import preGameSquad from '../../../src/pregame-squad.js';

// --- Pre-game intro choreography -------------------------------------------------------
// Against the server's 4-second window (GameHostingService.PreGameSeconds): a second looking
// at the OPPONENT's castle, two seconds panning home, then a second to settle before the
// battle opens. Timings are measured from the START of the window, so they are computed from
// elapsed = total - remaining and stay correct for a client that joins mid-intro.
const PAN_START_MS = 1000;
const PAN_DURATION_MS = 2000;

// Smoothstep: the pan eases out of the opponent's castle and into your own rather than
// starting and stopping dead.
const smoothstep = (t) => t * t * (3 - 2 * t);

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

    // Fill in the pause overlay's construction signs once, here, rather than from the frame
    // loop. Guarded because a missing asset must cost two decorative images, not the game.
    const constructionSign = loader.assets.tooltips?.construction;
    if (constructionSign) {
        document.querySelectorAll('#pause-overlay .pause-icon')
                .forEach(img => img.src = constructionSign.src);
    }

    // Wired once per visit to this screen, alongside the rest of the UI, rather than from
    // the frame loop -- which would reattach it thirty times a second.
    const claimBtn = document.getElementById('btnClaimVictory');
    if (claimBtn) claimBtn.onclick = () => {
        claimBtn.disabled = true;
        connection.claimVictory();
    };

    // The camera the player ends up at. During the intro it opens on the OPPONENT's castle
    // instead and pans here; without an intro it just starts here, as it always did.
    const homeCameraX = startingCameraX;
    const awayCameraX = connection.mySide === 0 ? startingCameraX
                      : (connection.mySide === 1 ? 2000 : 0);

    view.panTo(connection.inPreGame ? awayCameraX : homeCameraX);

    // Rebuilt per game, not per visit: coming back to this screen mid-game (a rejoin) must
    // not repopulate a crowd the battle has already sent onto the field.
    preGameSquad.ensure(connection.latestState, connection.currentGameId);

    let lastCountdownLabel = null;
    let lastFrameTime = performance.now();
    let animationFrameId;

    const gameLoop = () => {
        // NOT `winnerSide != 0` -- a DRAW ends the game with winnerSide 0, which that
        // test reads as "still playing", so the loop ran forever behind the game-over
        // screen (a leaked rAF chain still calling updateUI every frame).
        if (connection.gameOver) {
            cancelAnimationFrame(animationFrameId);
            animationFrameId = null;
            // The game-over screen draws the same state; a squad left in the list would be
            // drawn standing at a castle that has just fallen.
            view.preGameSquad = [];
            preGameSquad.reset();
            return;
        }
        
        view.clear();

        const now = performance.now();
        const deltaMs = now - lastFrameTime;
        lastFrameTime = now;

        // Must run BEFORE the draw: it sets the camera and the squad list the draw reads.
        lastCountdownLabel = runIntro(deltaMs, homeCameraX, awayCameraX, lastCountdownLabel);

        if (connection.latestState) {
            view.drawGameState(connection.latestState);
            updateUI(connection.latestState);
        }

        // Pulled every frame rather than pushed from the SignalR handler: this screen is
        // torn down and rebuilt by the router, so a subscription taken out here would
        // outlive the elements it writes to.
        syncPauseOverlay();

        animationFrameId = requestAnimationFrame(gameLoop);
    };

    requestAnimationFrame(gameLoop);
}

// Drives everything that happens before the battle opens: which castle the camera is
// looking at, the squad waiting outside them, and the countdown banner.
//
// Runs EVERY frame, not just during the intro, because two of its three jobs outlive the
// window: the squad keeps thinning out over the first seconds of the battle as its members
// run onto the field, and "BATTLE!!" plays out after the intro has ended.
//
// Returns the label currently shown, which the caller threads back in -- the banner has to
// restart its animation only when the label CHANGES, and this function holds no state.
function runIntro(deltaMs, homeCameraX, awayCameraX, lastLabel) {
    const inPreGame = connection.inPreGame;
    const remaining = connection.preGameRemaining();

    // --- Camera ---
    if (inPreGame) {
        const total = connection.preGameTotalMs || 0;
        const elapsed = total - remaining;
        if (elapsed <= PAN_START_MS) {
            view.panTo(awayCameraX);
        } else {
            const t = Math.min(1, (elapsed - PAN_START_MS) / PAN_DURATION_MS);
            view.panTo(awayCameraX + (homeCameraX - awayCameraX) * smoothstep(t));
        }
    }

    // --- The squad outside the castles ---
    preGameSquad.ensure(connection.latestState, connection.currentGameId);
    preGameSquad.update(deltaMs);
    view.preGameSquad = preGameSquad.visible(connection.latestState, inPreGame);

    // --- Countdown banner ---
    // 3, 2 and 1 come off the remaining time; BATTLE!! is triggered by the window closing,
    // and is held for its animation rather than for a slice of the countdown.
    let label = null;
    if (inPreGame) {
        if (remaining <= 1000) label = '1';
        else if (remaining <= 2000) label = '2';
        else if (remaining <= 3000) label = '3';
    } else if (connection.battleStartedAt
               && performance.now() - connection.battleStartedAt < 1200) {
        label = 'BATTLE!!';
    }

    if (label !== lastLabel) showCountdown(label);
    return label;
}

// Restarting a CSS animation needs the class removed, a reflow forced, and the class put
// back -- assigning the same class again does nothing on its own.
function showCountdown(label) {
    const el = document.getElementById('countdown-text');
    if (!el) return;

    el.classList.remove('show', 'digit', 'battle');
    if (!label) { el.innerText = ''; return; }

    el.innerText = label;
    void el.offsetWidth;                       // force reflow
    el.classList.add('show', label === 'BATTLE!!' ? 'battle' : 'digit');
}

// The server has frozen the game because the other player's connection went away. The
// state keeps drawing -- it simply stops changing -- so the overlay is what tells the
// player the game is paused rather than merely quiet.
function syncPauseOverlay() {
    const overlay = document.getElementById('pause-overlay');
    if (!overlay) return;

    const countdown = overlay.querySelector('.pause-countdown');
    const note = document.getElementById('pause-note');
    const subtitle = document.getElementById('pause-subtitle');
    const claimBtn = document.getElementById('btnClaimVictory');

    // THIS end dropped, and SignalR is retrying. No server message can arrive to explain
    // the freeze, so the overlay has to be raised locally -- and it must NOT show the
    // 60-second countdown, which belongs to the opponent's clock and is not what this
    // player is waiting on.
    if (connection.reconnecting) {
        document.getElementById('pause-title').innerText = 'CONNECTION LOST';
        if (subtitle) subtitle.innerText = 'RECONNECTING...';
        if (countdown) countdown.style.display = 'none';
        if (note) note.innerText = 'YOUR SEAT IS BEING HELD';
        if (claimBtn) claimBtn.classList.add('hidden');
        overlay.classList.remove('hidden');
        return;
    }

    if (!connection.paused) {
        overlay.classList.add('hidden');
        return;
    }

    const title = document.getElementById('pause-title');
    if (title) {
        // A spectator sees which side dropped; a player only cares that it was the
        // opponent. mySide is 0 for spectators, so this never mislabels one as the other.
        title.innerText = connection.mySide === 0
            ? `P${connection.pausedSide} DISCONNECTED`
            : 'OPPONENT DISCONNECTED';
    }

    if (countdown) countdown.style.display = '';

    if (connection.pauseClaimable) {
        // The 60 seconds are up and nothing has happened on its own -- by design. What the
        // player watches now is how long they have waited, and the decision to end it is
        // theirs. Spectators get no button: there is no seat for them to win with.
        if (subtitle) subtitle.innerText = 'THEY HAVE NOT COME BACK YET';
        if (countdown) countdown.innerText = formatWait(connection.pauseWaitedSeconds);
        if (note) note.innerText = 'KEEP WAITING, OR TAKE THE WIN';
        if (claimBtn) claimBtn.classList.toggle('hidden', connection.mySide === 0);
    } else {
        if (subtitle) subtitle.innerText = 'GAME PAUSED — WAITING FOR THEM TO RECONNECT';
        if (countdown) countdown.innerText = connection.pauseSecondsRemaining + 's';
        if (note) note.innerText = 'IF THEY DO NOT RETURN, YOU CAN CLAIM THE WIN';
        if (claimBtn) claimBtn.classList.add('hidden');
    }

    overlay.classList.remove('hidden');
}

// mm:ss past a minute -- a bare second count is unreadable once someone has been waiting
// for twenty of them.
function formatWait(seconds) {
    const s = Math.max(0, seconds | 0);
    if (s < 60) return s + 's';
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
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
    const repair = document.getElementById('repair-price');

    money.innerHTML = Math.floor(pState.money);
    income.innerHTML = pState.income.toFixed(1);
    repair.innerHTML = pState.repairPrice.toFixed(0);

    // -------- UPDATE SHOP --------
    updateInvestButton(pState);

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

// Mirrors PlayerState.ArmageddonInvestmentCount. At the top of the economy ladder the
// invest button stops buying income and becomes the ARMAGEDDON purchase; once bought it
// is spent for good and reads "INVEST: MAX".
const ARMAGEDDON_INVESTMENT_COUNT = 8;

function updateInvestButton(pState) {
    const btn = document.getElementById('btnInvest');
    const label = document.getElementById('invest-label');
    const price = document.getElementById('investment-price');
    if (!btn || !label || !price) return;

    if (pState.armageddonUsed) {
        label.innerHTML = 'INVEST';
        price.innerHTML = 'MAX';
        btn.disabled = true;
        btn.classList.remove('armageddon-ready');
        return;
    }

    const isArmageddon = pState.investmentCount >= ARMAGEDDON_INVESTMENT_COUNT;

    label.innerHTML = isArmageddon ? 'ARMAGEDDON' : 'INVEST';
    price.innerHTML = '$' + Math.ceil(pState.investmentPrice);
    btn.disabled = pState.money < pState.investmentPrice;
    btn.classList.toggle('armageddon-ready', isArmageddon);
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
            // "?" on the stats this unit rolls fresh on every spawn -- the numbers here are
            // its base row, not what the player will actually get. See loader.isRandomStatUnit.
            const rolled = loader.isRandomStatUnit(character.id) ? '?' : '';
            tooltip.innerHTML = `
                <div class="tooltip-name">${(stats.name || character.id).toUpperCase()}</div>
                <div class="stat"><img src="${loader.assets.tooltips.heart.src}" alt="HP"> ${stats.health}${rolled}</div>
                <div class="stat"><img src="${loader.assets.tooltips.sword.src}" alt="DMG"> ${stats.damage}${rolled}</div>
                <div class="stat"><img src="${loader.assets.tooltips.boot.src}" alt="SPD"> ${stats.speed}${rolled}</div>
            `;
            wrapper.appendChild(tooltip);

            // --- 2. DESKTOP HOVER LOGIC ---
            // GUARDED TWICE, and both guards earn their place.
            //
            // The bug: a tap on a touch screen leaves a PHANTOM CURSOR parked on whatever
            // was tapped. The browser follows touchend with a synthetic mouse sequence, so
            // the old `mouseenter` listener re-added `visible` immediately after the touch
            // handler below removed it -- and no mouseleave ever arrived, because there is
            // no finger to move away. The tooltip then sat open until the next tap
            // somewhere else moved the phantom cursor. Tapping to BUY a unit therefore
            // left that unit's tooltip on screen.
            //
            // `hover: hover` is the load-bearing guard: a phone answers false, so no
            // hover path exists there at all no matter what the synthetic events claim to
            // be. It is deliberately the SAME query that gates button:hover in
            // global-styles.css, so the CSS and JS hover stories cannot drift apart.
            //
            // pointerType is the second guard, for the device the first one cannot settle:
            // a touchscreen laptop answers `hover: hover` truthfully and still takes taps.
            // Pointer events carry the pointerType, so there a real mouse is told from a
            // finger PER INTERACTION rather than per device.
            const canHover = () => window.matchMedia('(hover: hover)').matches;
            wrapper.addEventListener('pointerenter', (e) => {
                if (e.pointerType !== 'mouse' || !canHover()) return;
                tooltip.classList.add('visible');
            });
            wrapper.addEventListener('pointerleave', (e) => {
                if (e.pointerType !== 'mouse' || !canHover()) return;
                tooltip.classList.remove('visible');
            });

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
            // The unit id comes from `character` -- the element the listener is attached
            // to -- and NOT from walking up from e.target. It used to read
            // `e.target.parentElement.id`, which is only the right element when the click
            // landed on the <img>. A tap on the button's own area (its padding, or the gap
            // around the sprite) made e.target the .character div itself, so parentElement
            // was the .character-icon-wrapper, whose id is empty, and spawnUnit("") no-oped
            // silently -- no unit, no money spent, no error. On a phone the sprite covers
            // most of the button, which is exactly what made this easy to miss and
            // miserable to hit: "I tapped it and nothing happened", intermittently.
            character.addEventListener('click', () => {
                if (character.classList.contains('disabled')) return;
                connection.spawnUnit(character.id);
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