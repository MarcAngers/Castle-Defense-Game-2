import loader from './asset-loader.js';
import AnimationManager from './animation-manager.js';
import GadgetManager from './gadget-manager.js';
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

        // --- CAMERA INPUT HANDLERS ---
        this.canvas.addEventListener('mousedown', this.startPan);
        this.canvas.addEventListener('touchstart', this.startPan, {passive: false});

        window.addEventListener('mousemove', this.movePan);
        window.addEventListener('touchmove', this.movePan, {passive: false});

        window.addEventListener('mouseup', this.endPan);
        window.addEventListener('touchend', this.endPan);

        this.latestState = null;

        window.addEventListener('resize', this.resize);
        this.resize();

        this.gadgetManager = new GadgetManager(this);

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
            "Blackhole": [0, 0, 0, 0.5],        // Black
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

        // --- 1. DRAW TARGETED CROSSHAIR (World Space) ---
        if (this.gadgetManager && this.gadgetManager.activeGadgetId && this.gadgetManager.isTargeted) {
            this.ctx.save();
            
            this.ctx.strokeStyle = 'rgba(255, 50, 50, 0.8)';
            this.ctx.lineWidth = 4;
            this.ctx.setLineDash([15, 10]); 
            
            this.ctx.beginPath();
            this.ctx.moveTo(this.gadgetManager.crosshairWorldX, 0);
            this.ctx.lineTo(this.gadgetManager.crosshairWorldX, 500); 
            this.ctx.stroke();
            
            this.ctx.fillStyle = 'rgba(255, 50, 50, 0.5)';
            this.ctx.beginPath();
            this.ctx.arc(this.gadgetManager.crosshairWorldX, 400, 20, 0, Math.PI * 2);
            this.ctx.fill();

            this.ctx.restore();
        }

        // --- RESTORE CAMERA ---
        this.ctx.restore(); // Go back to "Screen" coords (0,0 is top left)

        // --- 2. DRAW UNTARGETED ICON (Screen Space) ---
        // Drawn AFTER the camera is restored so it ignores panning and perfectly follows the mouse!
        if (this.gadgetManager && this.gadgetManager.activeGadgetId && !this.gadgetManager.isTargeted) {
            const img = loader.assets.gadgets[this.gadgetManager.activeGadgetId];
            if (img) {
                this.ctx.save();
                this.ctx.globalAlpha = 0.7; 
                // Draw exactly centered on the raw logical screen coordinates
                this.ctx.drawImage(
                    img, 
                    this.gadgetManager.cursorLogicalX - 25, 
                    this.gadgetManager.cursorLogicalY - 25, 
                    50, 50
                );
                this.ctx.restore();
            }
        }

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

        let isInvulnerable = false;

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
                    } else if (status.name == 'Invulnerable') {
                        isInvulnerable = true;
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

        // If the unit is invulnerable, draw a divine shield over it
        if (isInvulnerable) {
            const shieldImage = loader.assets.gadgets['divine'];
            this.ctx.drawImage(shieldImage, x, y, width, height);
        }
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
        let x = 50;
        
        this.ctx.save();
        
        if (side === 1) {
            this.ctx.drawImage(castleImg, x, y);

            this.ctx.restore(); // Restore coordinate system for the health bar

            this.drawHealthBar(x, y - 10, 200, playerState.castleHealth, playerState.castleMaxHealth);
        } else {
            x = this.MAP_WIDTH - 50;
            
            this.ctx.translate(x, y);
            this.ctx.scale(-1, 1); 
            this.ctx.drawImage(castleImg, 0, 0);
            
            this.ctx.restore(); // Restore coordinate system for the health bar
            
            this.drawHealthBar(x - 200, y - 10, 200, playerState.castleHealth, playerState.castleMaxHealth);
        }

        // If the player is invulnerable, draw a divine shield over their castle:
        if (playerState.isInvulnerable) {
            console.log("drawing shield...");
            const shieldImage = loader.assets.gadgets['divine'];
            if (x > 1000) x -= 200;
            this.ctx.drawImage(shieldImage, x, y, 200, 200);
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