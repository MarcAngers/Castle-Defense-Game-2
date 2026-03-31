import loader from '../asset-loader.js';

export default class FirebombAnimator {
    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.targetX = targetX; 
        this.level = level; 
        
        this.startX = this.side === 1 ? 150 : 1850; 
        this.targetY = 400;
        this.hazardWidth = 300; 

        // --- FETCH DYNAMIC DATA ---
        const dataKey = this.level === 1 ? 'firebomb' : `firebomb_${this.level}`;
        const gadgetData = loader.assets.gadgetData[dataKey];

        // Get the server duration in ticks (Fallback to 180 ticks / 6 seconds)
        const hazardTicks = gadgetData ? (gadgetData.hazardduration || gadgetData.HazardDuration || 180) : 180;
        const activeDurationMs = (hazardTicks / 20) * 1000;

        this.timer = 0;
        // 2s flight + dynamic fire duration
        this.duration = 2000 + activeDurationMs;   
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

        // --- PHASE 2: THE FIRE (2000ms to End) ---
        const fireTimer = this.timer - 2000; 
        const frameIndex = Math.floor(fireTimer / 150) % 2 === 0 ? 1 : 2;
        
        const fireKey = this.level === 1 ? `fire-${frameIndex}` : `fire_${this.level}-${frameIndex}`;
        const fallbackKey = `fire-${frameIndex}`;

        const fireImg = loader.assets.hazards[fireKey] || loader.assets.hazards[fallbackKey]; 
        if (!fireImg) return;

        ctx.save();
        ctx.translate(this.targetX, this.targetY);

        // Dynamic fade out during the last 500ms
        const fadeOutStart = this.duration - 500;
        if (this.timer > fadeOutStart) {
            const fadeProgress = (this.timer - fadeOutStart) / 500;
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