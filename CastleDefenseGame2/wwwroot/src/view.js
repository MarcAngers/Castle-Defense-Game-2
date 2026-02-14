import loader from './asset-loader.js';

export default class View {
    constructor(canvasId) {
        this.canvas = document.getElementById(canvasId);

        this.canvas.width = window.innerWidth;
        this.canvas.height = window.innerHeight;

        this.ctx = this.canvas.getContext('2d');
        this.ctx.imageSmoothingEnabled = false;

        this.latestState = null;
        this.latestMap = null;

        window.addEventListener('resize', this.resize);
        this.resize();
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

        // --- APPLY CAMERA TRANSFORM ---
        this.ctx.translate(-cameraX, 0);

        this.drawBackground(colour);

        // Draw Castles
        drawCastle(state.player1, 1); 
        drawCastle(state.player2, 2);

        // Draw Units
        state.units.forEach(unit => {
            drawUnit(unit);
        });

        // --- RESTORE CAMERA ---
        this.ctx.restore(); // Go back to "Screen" coords (0,0 is top left)
    }

    drawUnit(unit) {
        const img = assets.units[unit.definitionId];
    
        const x = unit.position;
        const spriteSize = unit.spriteSize;
        const y = unit.yposition;

        if (img) {
            this.ctx.save();
            
            if (unit.side === 1) {
                // Player 1: Face Right
                this.ctx.drawImage(img, x, y);
            } else {
                // Player 2: Face Left
                this.ctx.translate(x + spriteSize, y); 
                this.ctx.scale(-1, 1);
                this.ctx.drawImage(img, 0, 0);
            }
            
            this.ctx.restore();
        } else {
            // Fallback Box
            this.ctx.fillStyle = unit.side === 1 ? 'red' : 'blue';
            this.ctx.fillRect(x, y, spriteSize, spriteSize);
        }

        // Health Bar
        drawHealthBar(x - 5, y - 10, spriteSize, unit.currentHealth, unit.maxHealth, unit.currentShield);
    }

    drawHealthBar(x, y, spriteSize, currentHealth, maxHealth, currentShield) {
        let pct = currentHealth/maxHealth;
        let width = (spriteSize + 10) * currentHealth;

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
        const castleImg = playerState.castleHealth > 0 ? assets.buildings['castle'] : assets.buildings['rubble'];
        if (!castleImg) return;

        const y = 400;
        
        ctx.save();
        
        if (side === 1) {
            let x = 50;
            ctx.drawImage(castleImg, x, y);
            drawHealthBar(x, y - 10, 200, playerState.castleHealth, playerState.castleMaxHealth);
        } else {
            let x = 1250;
            
            ctx.translate(x, y);
            ctx.scale(-1, 1); 
            ctx.drawImage(castleImg, 0, 0);
            
            ctx.restore(); // Restore coordinate system for the health bar
            
            drawHealthBar(x - 200, y - 10, 200, playerState.castleHealth, playerState.castleMaxHealth);
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
        // Example: Screen is 1080px tall, Game is 200px tall.
        // Scale = 5.4 (not 5)
        const scale = windowHeight / logicalHeight;

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

        this.ctx.scale(scale, scale);

        // 5. Restore previous canvas content
        this.draw();

        return { 
            scale: scale,
            width: windowWidth / scale, 
            height: logicalHeight 
        };
    }
}