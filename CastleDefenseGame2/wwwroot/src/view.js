import loader from './asset-loader.js';
import connection from '../../../src/game-connection.js';

export default class View {
    constructor(canvasId) {
        this.canvas = document.getElementById(canvasId);

        this.canvas.width = window.innerWidth;
        this.canvas.height = window.innerHeight;

        this.ctx = this.canvas.getContext('2d');
        this.ctx.imageSmoothingEnabled = false;

        this.MAP_WIDTH = 1500;
        this.cameraX = 0;

        // Input State for Panning
        this.isDragging = false;
        this.startX = 0;
        this.scrollStartCameraX = 0;

        // --- CAMERA INPUT HANDLERS ---
        this.canvas.addEventListener('mousedown', this.startPan);
        this.canvas.addEventListener('touchstart', this.startPan, {passive: false});

        window.addEventListener('mousemove', this.movePan);
        window.addEventListener('touchmove', this.movePan, {passive: false});

        window.addEventListener('mouseup', this.endPan);
        window.addEventListener('touchend', this.endPan);

        this.latestState = null;
        this.latestMap = null;

        window.addEventListener('resize', this.resize);
        this.resize();
    }

    startPan = (e) => {
        this.isDragging = true;
        this.startX = this.getX(e);
        this.scrollStartCameraX = this.cameraX;
    }

    movePan = (e) => {
        if (!this.isDragging) return;
        
        // 1. getX() ALREADY returns Logical Game Units!
        const currentX = this.getX(e);
        const diff = this.startX - currentX; // This is a purely logical difference
        
        // 2. Apply directly to camera
        this.cameraX = this.scrollStartCameraX + diff;
        
        // 3. Clamp using purely logical coordinates
        const maxScroll = Math.max(0, this.MAP_WIDTH - this.logicalScreenWidth);
        this.cameraX = Math.max(0, Math.min(this.cameraX, maxScroll));
    }

    endPan = () => {
        this.isDragging = false;
    }

    // Helper for Mouse vs Touch coordinates
    getX = (e) => {
        if (!e) return 0;
        
        let physicalX = 0;
        
        if (e.touches && e.touches.length > 0) {
            physicalX = e.touches[0].clientX;
        } else if (e.changedTouches && e.changedTouches.length > 0) {
            physicalX = e.changedTouches[0].clientX;
        } else {
            physicalX = e.clientX || 0;
        }

        // Convert to Logical Space immediately!
        return physicalX / this.scale;
    }

    clear() {
        this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    }

    draw = () => {
        if (this.latestState) {
            this.drawGameState(this.latestState);
        } else if (this.latestMap) {
            this.drawBackground(this.latestMap);
        } else {
            this.drawBackground('white');
        }
    }

    drawGameState(state) {
        this.clear();
        this.latestState = state;

        this.ctx.save();

        // --- APPLY CAMERA TRANSFORM ---
        this.ctx.translate(-this.cameraX, 0);

        this.drawBackground('white');

        // Draw Castles
        this.drawCastle(state.player1, 1); 
        this.drawCastle(state.player2, 2);

        // Draw Units
        state.units.forEach(unit => {
            this.drawUnit(unit);
        });

        // --- RESTORE CAMERA ---
        this.ctx.restore(); // Go back to "Screen" coords (0,0 is top left)
    }

    drawUnit(unit) {
        console.log('drawing unit: ', unit);
        const img = loader.assets[unit.definitionId];
    
        const x = unit.position;
        const width = unit.width;
        const y = unit.yPosition;

        if (img) {
            this.ctx.save();
            
            if (unit.side === 1) {
                // Player 1: Face Right
                this.ctx.drawImage(img, x, y);
            } else {
                // Player 2: Face Left
                this.ctx.translate(x + width, y); 
                this.ctx.scale(-1, 1);
                this.ctx.drawImage(img, 0, 0);
            }
            
            this.ctx.restore();
        } else {
            // Fallback Box
            this.ctx.fillStyle = unit.side === 1 ? 'red' : 'blue';
            this.ctx.fillRect(x, y, width, width);
        }

        // Health Bar
        this.drawHealthBar(x - 5, y - 10, width, unit.currentHealth, unit.maxHealth, unit.currentShield);
    }

    drawHealthBar(x, y, spriteSize, currentHealth, maxHealth, currentShield) {
        let pct = currentHealth/maxHealth;
        let width = (spriteSize + 10) * pct;

        this.ctx.fillStyle = "lightgray";
        this.ctx.fillRect(x, y, spriteSize + 10, 5);

        if (pct > 0.75)
        this.ctx.fillStyle = "green";
            else if (pct > 0.30)
        this.ctx.fillStyle = "yellow";
            else if (pct > 0.10)
        this.ctx.fillStyle = "red";
            else
        this.ctx.fillStyle = "darkred";

        this.ctx.fillRect(x, y, width, 5);

        // Draw shield
        this.ctx.fillStyle = "lightblue";
        this.ctx.fillRect(x, y - 5, currentShield, 5);
    }

    drawCastle(playerState, side) {
        const team = loader.assets.teamList[playerState.team];
        const castleImg = playerState.castleHealth > 0 ? loader.assets.buildings[team + '-castle'] : loader.assets.buildings['dead-castle'];
        if (!castleImg) return;

        const y = 200;
        
        this.ctx.save();
        
        if (side === 1) {
            let x = 50;
            this.ctx.drawImage(castleImg, x, y);

            this.ctx.restore(); // Restore coordinate system for the health bar

            this.drawHealthBar(x, y - 10, 200, playerState.castleHealth, playerState.castleMaxHealth);
        } else {
            let x = 1450;
            
            this.ctx.translate(x, y);
            this.ctx.scale(-1, 1); 
            this.ctx.drawImage(castleImg, 0, 0);
            
            this.ctx.restore(); // Restore coordinate system for the health bar
            
            this.drawHealthBar(x - 200, y - 10, 200, playerState.castleHealth, playerState.castleMaxHealth);
        }
    }

    drawBackground = (colour) => {
        this.latestMap = colour;
        this.ctx.drawImage(loader.get(`background-${colour}`), 0, 0);
    }

    resize = () => {
        const logicalHeight = 500;
        const windowWidth = window.innerWidth;
        const windowHeight = window.innerHeight;

        // 1. Calculate the fractional scale
        this.scale = windowHeight / logicalHeight;
        this.logicalScreenWidth = windowWidth / this.scale;

        // 2. Set the CANVAS Internal Resolution (The buffer size)
        // We match the window size exactly so the browser doesn't stretch anything via CSS
        this.canvas.width = windowWidth;
        this.canvas.height = windowHeight;

        // 3. Set the CSS display size (just to be safe, though 2 covers it)
        this.canvas.style.width = `${windowWidth}px`;
        this.canvas.style.height = `${windowHeight}px`;

        // 4. CRITICAL: Turn off smoothing
        // Browser resets this on resize, so we must re-apply it every time.
        this.ctx.imageSmoothingEnabled = false;

        this.ctx.scale(this.scale, this.scale);

        // 5. Restore previous canvas content
        this.draw();

        return { 
            scale: this.scale,
            width: this.logicalScreenWidth, 
            height: logicalHeight 
        };
    }
}