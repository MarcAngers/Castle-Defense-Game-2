const canvas = document.getElementById('gameCanvas');
const ctx = canvas.getContext('2d');
ctx.imageSmoothingEnabled = false;
const MAP_WIDTH = 1500;
let cameraX = 0;

// --- ASSET LOADING ---
const assets = {
    units: {},
    buildings: {},
    env: {}
};

// 1. Define what to load
const unitNames = [
    "doggo",
    "catto",
    "squirt",
    "ringo",
    "alpacco",
    "bread",
    "eggo",
    "corn"
];

const imagesToLoad = [
    { key: 'background', src: 'assets/env/background default.png', cat: 'env' },
    { key: 'castle', src: 'assets/buildings/white castle.png', cat: 'buildings' },
    { key: 'rubble', src: 'assets/buildings/dead castle.png', cat: 'buildings' }
];

// Add units to the load list dynamically
unitNames.forEach(name => {
    imagesToLoad.push({ key: name, src: `assets/units/${name}.png`, cat: 'units' });
});

let imagesLoaded = 0;

// 2. The Load Function
function loadAssets(callback) {
    if (imagesToLoad.length === 0) {
        callback(); 
        return;
    }

    imagesToLoad.forEach(imgDef => {
        const img = new Image();
        img.src = imgDef.src;
        img.onload = () => {
            assets[imgDef.cat][imgDef.key] = img;
            imagesLoaded++;
            if (imagesLoaded === imagesToLoad.length) {
                console.log("All assets loaded!");
                callback(); // Start the game loop only when ready
            }
        };
        img.onerror = () => {
            console.error(`Failed to load ${imgDef.src}`);
            // Still count it so the game doesn't hang
            imagesLoaded++; 
            if (imagesLoaded === imagesToLoad.length) callback();
        };
    });
}

// Adjust canvas size
canvas.width = window.innerWidth;
canvas.height = window.innerHeight;

// API Configuration
const API_URL = "http://localhost:5168"; // CHANGE THIS to your actual port
let currentGameId = null;
let mySide = 0; // 1 = Left, 2 = Right
let latestState = null;

function showScreen(screenId) {
    // Hide all screens
    document.querySelectorAll('.ui-screen').forEach(el => el.classList.add('hidden'));
    
    // Show the target screen
    const target = document.getElementById(screenId);
    if (target) target.classList.remove('hidden');
}

// --- 1. SIGNALR CONNECTION ---
const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${API_URL}/gameHub`)
    .build();

connection.on("GameJoined", (side, state) => {
    mySide = side;
    
    console.log(`Joined as Player ${side}`);

    initShopUI();

    // If we are Player 2, the game is ready to start immediately
    if (side === 2) {
        showScreen('screen-game-p2');
    }
    // If we are Player 1, we might still be waiting in the lobby
    // We need to check if P2 is already there (re-joining) or wait for update
    if (state.player2.connectionId) {
        showScreen('screen-game-p1');
    }
});

// We need to know when the OTHER player joins to switch P1 from Lobby -> Game
connection.on("GameStateUpdate", (state) => {
    latestState = state;
    
    // Check for Game Over
    if (state.isGameOver) {
        showScreen('screen-gameover');
        const winnerText = state.winnerSide === 1 ? "PLAYER 1 WINS!" : "PLAYER 2 WINS!";
        document.getElementById('winnerName').innerText = winnerText;
        return; // Stop updating game UI
    }

    // Check if game just started (Player 2 arrived)
    // If we are currently in the lobby, and P2 exists now, switch to game!
    const lobby = document.getElementById('screen-lobby');
    if (!lobby.classList.contains('hidden') && state.player2.connectionId) {
        showScreen('screen-game');
    }

    updateUI(state);
});

async function startConnection() {
    try {
        await connection.start();
        console.log("SignalR Connected.");
    } catch (err) {
        console.error("SignalR Error:", err);
    }
}

// Input State for Panning
let isDragging = false;
let startX = 0;
let scrollStartCameraX = 0;

// --- CAMERA INPUT HANDLERS ---
canvas.addEventListener('mousedown', startPan);
canvas.addEventListener('touchstart', startPan, {passive: false});

window.addEventListener('mousemove', movePan);
window.addEventListener('touchmove', movePan, {passive: false});

window.addEventListener('mouseup', endPan);
window.addEventListener('touchend', endPan);

function startPan(e) {
    // Only drag if we are touching the canvas (not the UI)
    // (The UI layer handles its own clicks, so this usually works automatically)
    isDragging = true;
    startX = getX(e);
    scrollStartCameraX = cameraX;
}

function movePan(e) {
    if (!isDragging) return;
    
    // Calculate how far we moved
    const currentX = getX(e);
    const diff = startX - currentX;
    
    // Update Camera
    cameraX = scrollStartCameraX + diff;
    
    // Clamp Camera (Don't scroll past edges)
    // Max scroll is Map Width - Screen Width
    const maxScroll = Math.max(0, MAP_WIDTH - canvas.width);
    cameraX = Math.max(0, Math.min(cameraX, maxScroll));
}

function endPan() {
    isDragging = false;
}

// Helper for Mouse vs Touch coordinates
function getX(e) {
    return e.touches ? e.touches[0].clientX : e.clientX;
}

// --- 2. GAME LOOP (RENDERING) ---
function gameLoop() {
    // Clear screen
    ctx.clearRect(0, 0, canvas.width, canvas.height);

    if (latestState) {
        drawGame(latestState);
    }

    requestAnimationFrame(gameLoop);
}

function drawGame(state) {
    // 1. Clear Screen (Do NOT translate this, we always want full screen clear)
    ctx.fillStyle = "#5555FF"; 
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    // --- APPLY CAMERA TRANSFORM ---
    ctx.save(); // Save "Normal" state
    ctx.translate(-cameraX, 0); // Shift world left

    // 2. Draw World (Using MAP_WIDTH coords, not Screen coords)
    // Note: We changed scaleX back to 1.0 because we are now scrolling 1:1
    const groundY = canvas.height * 0.75; 

    // Draw Ground (Full Map Width)
    ctx.fillStyle = "#00AA00";
    ctx.fillRect(0, groundY, MAP_WIDTH, canvas.height - groundY);

    // Draw Background (Parallax optional, for now just stretch it)
    if (assets.env['background']) {
        ctx.drawImage(assets.env['background'], 0, 0, MAP_WIDTH, canvas.height);
    }

    // Draw Castles
    drawCastle(state.player1, 1, groundY); 
    drawCastle(state.player2, 2, groundY);

    // Draw Units
    state.units.forEach(unit => {
        drawUnit(unit, groundY);
    });

    // --- RESTORE CAMERA ---
    ctx.restore(); // Go back to "Screen" coords (0,0 is top left)
}

function drawCastle(playerState, side, groundY) {
    const castleImg = playerState.castleHealth > 0 ? assets.buildings['castle'] : assets.buildings['rubble'];
    if (!castleImg) return;

    const size = 200; // Fixed size in pixels
    const y = groundY - size; 
    
    ctx.save();
    
    if (side === 1) {
        // Player 1 (Left) - Fixed at X = 10
        ctx.drawImage(castleImg, 10, y, size, size);
        drawHealthBar(10, y - 20, size, playerState.castleHealth, playerState.castleMaxHealth);
    } else {
        // Player 2 (Right) - Fixed at MAP_WIDTH (World Edge)
        const x = MAP_WIDTH - 10;
        
        // Translate to the right edge, then flip
        ctx.translate(x, y);
        ctx.scale(-1, 1); 
        ctx.drawImage(castleImg, 0, 0, size, size);
        
        ctx.restore(); // Restore coordinate system for the health bar
        
        // Draw health bar at: World Edge - Size (so it aligns with the castle visually)
        drawHealthBar(x - size, y - 20, size, playerState.castleHealth, playerState.castleMaxHealth);
        return; 
    }
    
    ctx.restore();
}

function drawUnit(unit, groundY) {
    const img = assets.units[unit.definitionId];
    
    // Use raw position (0 to 1500)
    const x = unit.position;
    
    const spriteSize = 50 * 2.5; // 125px
    const y = groundY - spriteSize;

    if (img) {
        ctx.save();
        
        if (unit.side === 1) {
            // Player 1: Face Right
            ctx.drawImage(img, x, y, spriteSize, spriteSize);
        } else {
            // Player 2: Face Left
            // We translate to the center of where the unit SHOULD be
            // The unit occupies x to x + 125.
            // To flip it in place, we set pivot to x + 125, flip, then draw.
            ctx.translate(x + spriteSize, y); 
            ctx.scale(-1, 1);
            ctx.drawImage(img, 0, 0, spriteSize, spriteSize);
        }
        
        ctx.restore();
    } else {
        // Fallback Box
        ctx.fillStyle = unit.side === 1 ? 'red' : 'blue';
        ctx.fillRect(x, y, spriteSize, spriteSize);
    }

    // Health Bar
    const maxHp = unit.maxHealth || 100; 
    drawHealthBar(x, y - 10, spriteSize, unit.currentHealth, maxHp);
}

function drawHealthBar(x, y, width, current, max) {
    if (current <= 0) return;
    const pct = Math.max(0, Math.min(1, current / max));
    
    // Background (Red/Damage)
    ctx.fillStyle = "#FF0000";
    ctx.fillRect(x, y, width, 5);
    
    // Foreground (Green/Health)
    ctx.fillStyle = "#00FF00"; 
    ctx.fillRect(x, y, width * pct, 5);
}

function initShopUI() {
    const grid = document.getElementById('unitShopGrid');
    grid.innerHTML = ''; // Clear existing

    unitNames.forEach(unitId => {
        const btn = document.createElement('button');
        btn.className = 'shop-unit-btn';
        btn.id = `btn-unit-${unitId}`;
        btn.onclick = () => spawnUnit(unitId);

        // Image
        const img = document.createElement('img');
        // Use loaded asset if available, or direct path
        img.src = `assets/units/${unitId}.png`; 
        btn.appendChild(img);

        // Badge (Hidden by default)
        const badge = document.createElement('span');
        badge.className = 'charge-badge hidden';
        badge.id = `badge-${unitId}`;
        badge.innerText = "0";
        btn.appendChild(badge);

        grid.appendChild(btn);
    });
}

function updateUI(state) {
    // 1. Update Text Stats
    const myPlayer = mySide === 1 ? state.player1 : state.player2;
    document.getElementById('p1Money').innerText = state.player1.money;
    document.getElementById('p2Money').innerText = state.player2.money;

    // 2. Update Shop Buttons (Charges & Cooldowns)
    unitNames.forEach(unitId => {
        const btn = document.getElementById(`btn-unit-${unitId}`);
        const badge = document.getElementById(`badge-${unitId}`);
        if (!btn) return;

        // Charges Logic
        // (Note: C# dictionary keys are Case Sensitive. Ensure 'UnitCharges' keys match 'unitId')
        //console.log(myPlayer.unitCharges);
        const charges = myPlayer.unitCharges[unitId] || 0;
        
        if (charges > 1) {
            badge.classList.remove('hidden');
            badge.innerText = charges;
        } else {
            badge.classList.add('hidden');
        }

        // Cooldown Logic
        const cooldown = myPlayer.cooldownTimers[unitId] || 0;
        if (cooldown > 0) {
            btn.classList.add('cooldown');
        } else {
            btn.classList.remove('cooldown');
        }
    });
}

// --- 3. INPUT HANDLING ---
async function createGame() {
    const response = await fetch(`${API_URL}/api/games`, { method: "POST" });
    const data = await response.json();
    
    // 1. Show Lobby Screen immediately
    showScreen('screen-lobby');
    
    // 2. Display the ID
    document.getElementById('displayGameId').innerText = data.gameId;
    
    // 3. Connect to socket
    joinGame(data.gameId);
}

async function joinGame(gameId) {
    if (!gameId) gameId = document.getElementById('txtGameId').value;
    currentGameId = gameId;
    await connection.invoke("JoinGame", gameId);
}

function spawnUnit(unitId) {
    if(!currentGameId) return;
    console.log("Attempting spawn unit", currentGameId, unitId);
    connection.invoke("SpawnUnit", currentGameId, unitId);
}

// Event Listeners
document.getElementById('btnCreate').addEventListener('click', createGame);
document.getElementById('btnJoin').addEventListener('click', () => {
    const id = document.getElementById('txtGameId').value;
    if (id) joinGame(id);
});

// Start
showScreen('screen-menu');
loadAssets(() => {
    startConnection();
    gameLoop();
});