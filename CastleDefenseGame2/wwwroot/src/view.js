import loader from './asset-loader.js';
import AnimationManager from './animation-manager.js';
import VisualUnit from './visual-unit.js';
import connection from './game-connection.js';

export default class View {
    constructor(canvasId, cameraX = 0) {
        this.visualUnits = {};
        this.currentTime = performance.now();
        this.lastTime = performance.now();

        this.canvas = document.getElementById(canvasId);

        this.canvas.width = window.innerWidth;
        this.canvas.height = window.innerHeight;

        this.ctx = this.canvas.getContext('2d');
        this.ctx.imageSmoothingEnabled = false;

        this.MAP_WIDTH = 2000;
        // One-liner to do essentially the same math as movePan() and resize()
        this.cameraX = Math.max(0, Math.min(cameraX, Math.max(0, this.MAP_WIDTH - window.innerWidth / (window.innerHeight / 500))));;

        // Input State for Panning
        this.isDragging = false;
        this.startX = 0;
        this.scrollStartCameraX = 0;

        // Gadget targeting
        this.targetingGadgetId = null; 
        this.crosshairWorldX = 0;

        // --- CAMERA INPUT HANDLERS ---
        this.canvas.addEventListener('click', this.fireGadget);

        this.canvas.addEventListener('mousedown', this.startPan);
        this.canvas.addEventListener('touchstart', this.startPan, {passive: false});

        window.addEventListener('mousemove', this.movePan);
        window.addEventListener('touchmove', this.movePan, {passive: false});

        window.addEventListener('mouseup', this.endPan);
        window.addEventListener('touchend', this.endPan);

        this.latestState = null;

        window.addEventListener('resize', this.resize);
        this.resize();

        // Scratch canvas for status effects:
        this.scratchCanvas = document.createElement('canvas');
        this.scratchCanvas.width = 250; // Make this slightly bigger than your biggest unit
        this.scratchCanvas.height = 250;
        this.scratchCtx = this.scratchCanvas.getContext('2d');
        this.scratchCtx.imageSmoothingEnabled = false;

        // Animations:
        this.animationManager = new AnimationManager();
        connection.onPlayGadgetAnimation((gadgetId, side, position, targetId) => {
            this.animationManager.triggerAnimation(gadgetId, side, position, targetId);
        });

        // Statuses:
        this.StatusColorMap = {
            "Burn": [255, 0, 0, 0.5],           // Red
            "Freeze": [0, 162, 232, 0.5],       // Blue
            "Poison": [163, 73, 164, 0.5],      // Purple
            "Rage": [136, 0, 21, 0.5],          // Burgundy
            "Heal": [0, 255, 50, 0.5],          // Green
            "Speed": [255, 255, 255, 0.5],      // White
            "Slow": [112, 146, 190, 0.5],       // Blue-Gray  
        };
    }

    startPan = (e) => {
        this.isDragging = true;
        this.startX = this.getX(e);
        this.scrollStartCameraX = this.cameraX;
    }

    movePan = (e) => {
        // --- TARGETING LOGIC ---
        if (this.targetingGadgetId) {
            // 1. Get Screen Space (using your existing getX method)
            const screenLogicalX = this.getX(e);
            
            // 2. Convert to World Space (Add the camera offset)
            this.crosshairWorldX = screenLogicalX + this.cameraX;
        }

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
        } else {
            this.drawBackground('white');
            this.drawForeground('white');
        }
    }

    drawGameState(state) {
        this.clear();
        this.latestState = state;
        
        this.currentTime = performance.now();
        const deltaTime = this.currentTime - this.lastTime;
        this.lastTime = this.currentTime;

        // --- Draw Background (Paralax) ---
        this.ctx.save();
        this.ctx.translate(-this.cameraX / 2, 0);
        this.drawBackground(loader.assets.teamList[this.latestState.map]);
        this.ctx.restore();

        this.animationManager.update(deltaTime);

        this.ctx.save();
        
        // --- APPLY CAMERA TRANSFORM & SCREEN SHAKE ---
        this.ctx.translate(
            -this.cameraX + this.animationManager.shakeX, 
            this.animationManager.shakeY
        );

        // --- Draw Foreground ---
        this.drawForeground(loader.assets.teamList[this.latestState.map]);

        // --- DRAW GADGET EFFECTS ---
        this.animationManager.draw(this.ctx, state);
        
        // Draw Castles
        this.drawCastle(state.player1, 1); 
        this.drawCastle(state.player2, 2);

        // Draw Units
        state.units.forEach(unit => {
            if (!this.visualUnits[unit.instanceId]) {
                this.visualUnits[unit.instanceId] = new VisualUnit(unit);
            }
            this.visualUnits[unit.instanceId].update(unit, deltaTime);
            this.drawUnit(unit, this.visualUnits[unit.instanceId]);
        });

        // --- DRAW TARGETING CROSSHAIR ---
        if (this.targetingGadgetId) {
            this.ctx.save();
            
            // Bright red, dashed line for that geometric, tactile feel
            this.ctx.strokeStyle = 'rgba(255, 50, 50, 0.8)';
            this.ctx.lineWidth = 4;
            this.ctx.setLineDash([15, 10]); // Creates the dashed effect
            
            this.ctx.beginPath();
            // Draw from top of screen to the bottom
            this.ctx.moveTo(this.crosshairWorldX, 0);
            this.ctx.lineTo(this.crosshairWorldX, 500); // 500 is your logicalHeight
            this.ctx.stroke();
            
            // Optional: Draw a little target indicator at the bottom
            this.ctx.fillStyle = 'rgba(255, 50, 50, 0.5)';
            this.ctx.beginPath();
            this.ctx.arc(this.crosshairWorldX, 400, 20, 0, Math.PI * 2);
            this.ctx.fill();

            this.ctx.restore();
        }

        // --- RESTORE CAMERA ---
        this.ctx.restore(); // Go back to "Screen" coords (0,0 is top left)

        // Garbage Collection: Remove VisualUnits for dead server units
        const currentServerUnitIds = new Set(state.units.map(u => u.instanceId));
        for (const id in this.visualUnits) {
            if (!currentServerUnitIds.has(id)) {
                delete this.visualUnits[id];
            }
        }
    }

    drawUnit(unit, visualUnit) {
        const img = loader.assets[unit.definitionId];
    
        const x = unit.position + (visualUnit ? visualUnit.visualOffsetX : 0);
        const y = unit.yPosition + (visualUnit ? visualUnit.visualOffsetY : 0);
        const width = unit.width || 50;
        const height = unit.height || 50;
        const rotation = visualUnit ? visualUnit.visualRotation : 0;

        if (img) {
            this.ctx.save();

            const centerX = x + (width / 2);
            const centerY = y + (height / 2);
            this.ctx.translate(centerX, centerY);
            
            this.ctx.rotate(rotation);

            // --- STATUS TINTING LOGIC ---
            let imageToDraw = img; 
            let totalR = 0, totalG = 0, totalB = 0, totalA = 0;
            let activeTintCount = 0;

            if (unit.statuses && unit.statuses.length > 0) {
                for (const status of unit.statuses) {
                    if (this.StatusColorMap[status.name]) {
                        const [r, g, b, a] = this.StatusColorMap[status.name];
                        totalR += r;
                        totalG += g;
                        totalB += b;
                        totalA += a;
                        activeTintCount++;
                    }
                }
            }

            if (activeTintCount > 0) {
                // Average the colors together!
                const finalR = Math.floor(totalR / activeTintCount);
                const finalG = Math.floor(totalG / activeTintCount);
                const finalB = Math.floor(totalB / activeTintCount);
                const finalA = totalA / activeTintCount;

                this.scratchCanvas.width = width;
                this.scratchCanvas.height = height;

                this.scratchCtx.globalCompositeOperation = 'source-over';
                this.scratchCtx.drawImage(img, 0, 0, width, height);
                this.scratchCtx.globalCompositeOperation = 'source-atop';
                
                // Apply our newly mixed color
                this.scratchCtx.fillStyle = `rgba(${finalR}, ${finalG}, ${finalB}, ${finalA})`;
                this.scratchCtx.fillRect(0, 0, width, height);

                imageToDraw = this.scratchCanvas;
            }
            
            if (unit.side === 1) {
                // Player 1: Face Right
                this.ctx.drawImage(imageToDraw, -width / 2, -height / 2, width, height);
            } else {
                // Player 2: Face Left
                this.ctx.scale(-1, 1);
                this.ctx.drawImage(imageToDraw, -width / 2, -height / 2, width, height);
            }
            
            this.ctx.restore();
        } else {
            // Fallback Box
            this.ctx.fillStyle = unit.side === 1 ? 'red' : 'blue';
            this.ctx.fillRect(x, y, width, width);
        }

        // --- DRAW STATUS PARTICLES ---
        if (visualUnit && visualUnit.particles && visualUnit.particles.length > 0) {
            this.ctx.save();

            const centerX = x + (width / 2);
            const centerY = y + (height / 2);
            this.ctx.translate(centerX, centerY);

            visualUnit.particles.forEach(p => {
                // Safely grab the image asset from the 'statuses' folder
                const particleImg = loader.assets['particles'] && loader.assets['particles'][p.imageKey];
                if (!particleImg) return;

                const progress = p.life / p.maxLife; // 1.0 down to 0.0
                
                // Fade out smoothly during the last half of its life
                this.ctx.globalAlpha = Math.min(1, progress * 2); 

                // Draw the particle centered on its calculated offset
                this.ctx.drawImage(
                    particleImg, 
                    p.offsetX - (p.size / 2), 
                    p.offsetY - (p.size / 2), 
                    p.size, 
                    p.size
                );
            });

            this.ctx.restore();
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
            let x = this.MAP_WIDTH - 50;
            
            this.ctx.translate(x, y);
            this.ctx.scale(-1, 1); 
            this.ctx.drawImage(castleImg, 0, 0);
            
            this.ctx.restore(); // Restore coordinate system for the health bar
            
            this.drawHealthBar(x - 200, y - 10, 200, playerState.castleHealth, playerState.castleMaxHealth);
        }
    }

    drawBackground = (colour) => {
        // Logic for "shadow" maps
        if (this.latestState != null)
            if (this.latestState.shadowMap)
                this.ctx.filter = 'grayscale(100%) brightness(50%) contrast(140%)';
        
        this.ctx.drawImage(loader.get('background')[colour], 0, 0);
        this.ctx.filter = 'none';
    }
    drawForeground = (colour) => {
        if (this.latestState != null)
            if (this.latestState.shadowMap)
                this.ctx.filter = 'grayscale(100%) brightness(50%) contrast(140%)';

        this.ctx.drawImage(loader.get('foreground')[colour], 0, 0);
        this.ctx.filter = 'none';
    }

    fireGadget = (e) => {
        if (!this.targetingGadgetId) return;

        // Prevent firing if they were just trying to pan the camera
        // (If startX and currentX are different, they dragged)
        const currentX = this.getX(e);
        if (Math.abs(this.startX - currentX) > 10) return;

        // 1. Calculate final World X within the bounds of the castles
        let targetX = currentX + this.cameraX;
        const finalTargetX = Math.min(Math.max(targetX, 200), 1800);

        // 2. Send it to the server! 
        connection.useGadget(this.targetingGadgetId, finalTargetX);

        // 3. Turn off targeting mode
        this.targetingGadgetId = null;
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