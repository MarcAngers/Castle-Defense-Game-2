import loader from '../asset-loader.js';

export default class FirebombAnimator {
    // 1. Accept the level parameter
    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.targetX = targetX; 
        this.level = level; // Store it!
        
        this.startX = this.side === 1 ? 150 : 1850; 
        this.targetY = 400;
        this.hazardWidth = 300; 

        this.timer = 0;
        this.duration = 7000;   
        this.isFinished = false;

        this.shakeX = 0;
        this.shakeY = 0;
    }

    update(deltaTime) {
        this.timer += deltaTime;

        if (this.timer >= this.duration) {
            this.isFinished = true;
        }
    }

    draw(ctx, state) {
        // --- PHASE 1: THE FLIGHT (0ms to 2000ms) ---
        if (this.timer < 2000) {
            // Dynamically grab the correct bomb image based on level
            const bombKey = this.level === 1 ? 'firebomb' : `firebomb_${this.level}`;
            const bombImg = loader.assets.gadgets[bombKey] || loader.assets.gadgets['firebomb']; 
            if (!bombImg) return;

            const t = this.timer / 2000; 
            const arcHeight = 300; 

            const currentX = this.startX + ((this.targetX - this.startX) * t);
            const currentY = this.targetY - (arcHeight * Math.sin(t * Math.PI));

            const spinDirection = this.targetX > this.startX ? 1 : -1;
            const angle = t * (Math.PI * 4) * spinDirection;

            ctx.save();
            ctx.translate(currentX, currentY);
            ctx.rotate(angle); 
            
            ctx.drawImage(bombImg, -37.5, -37.5, 75, 75);
            ctx.restore();

            return; 
        }

        // --- PHASE 2: THE FIRE (2000ms to 7000ms) ---
        
        const fireTimer = this.timer - 2000; 
        const frameIndex = Math.floor(fireTimer / 150) % 2 === 0 ? 1 : 2;
        
        // Dynamically build the fire frame string (e.g., "fire_2-1" or "fire-1")
        const fireKey = this.level === 1 ? `fire-${frameIndex}` : `fire_${this.level}-${frameIndex}`;
        const fallbackKey = `fire-${frameIndex}`;

        // Try to load the tiered fire, fallback to the base fire!
        const fireImg = loader.assets.hazards[fireKey] || loader.assets.hazards[fallbackKey]; 
        if (!fireImg) return;

        ctx.save();
        ctx.translate(this.targetX, this.targetY);

        if (this.timer > 6500) {
            const fadeProgress = (this.timer - 6500) / 500;
            ctx.globalAlpha = Math.max(0, 1.0 - fadeProgress);
        }

        const fireWidth = 100;
        const fireHeight = 100;
        const numberOfTiles = 4;

        const startDrawX = -(this.hazardWidth / 2);
        const stepX = (this.hazardWidth - fireWidth) / (numberOfTiles - 1);

        for (let i = 0; i < numberOfTiles; i++) {
            ctx.drawImage(fireImg, startDrawX + (i * stepX), -fireHeight, fireWidth, fireHeight);
        }

        ctx.restore();
    }
}