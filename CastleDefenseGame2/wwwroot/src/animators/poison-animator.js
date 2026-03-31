import loader from '../asset-loader.js';

export default class PoisonAnimator {
    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.targetX = targetX; 
        this.level = level;
        
        this.startX = this.side === 1 ? 150 : 1850; 
        this.targetY = 400;

        // --- FETCH DYNAMIC DATA ---
        const dataKey = this.level === 1 ? 'poison' : `poison_${this.level}`;
        const gadgetData = loader.assets.gadgetData[dataKey];
        
        // Grab the Radius
        const radius = gadgetData ? (gadgetData.radius || gadgetData.Radius || 200) : 200; 
        this.hazardWidth = radius * 2; 
        this.hazardHeight = this.hazardWidth / 4;

        // Get the server duration in ticks (Fallback to 120 ticks / 4 seconds)
        const hazardTicks = gadgetData ? (gadgetData.hazardduration || gadgetData.HazardDuration || 120) : 120;
        const activeDurationMs = (hazardTicks / 20) * 1000;

        this.timer = 0;
        // 2s flight + dynamic cloud duration
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
            const imgKey = this.level === 1 ? 'poison' : `poison_${this.level}`;
            const poisonBombImg = loader.assets.gadgets[imgKey] || loader.assets.gadgets['poison']; 
            if (!poisonBombImg) return;

            const t = this.timer / 2000; 
            const arcHeight = 300; 

            const currentX = this.startX + ((this.targetX - this.startX) * t);
            const currentY = this.targetY - (arcHeight * Math.sin(t * Math.PI));

            const spinDirection = this.targetX > this.startX ? 1 : -1;
            const angle = t * (Math.PI * 4) * spinDirection;

            ctx.save();
            ctx.translate(currentX, currentY);
            ctx.rotate(angle); 
            ctx.drawImage(poisonBombImg, -25, -25, 50, 50);
            ctx.restore();

            return; 
        }

        // --- PHASE 2: THE HOVERING CLOUD (2000ms to End) ---
        const hazardKey = this.level === 1 ? 'poison' : `poison_${this.level}`;
        const poisonCloudImg = loader.assets.hazards[hazardKey] || loader.assets.hazards['poison']; 
        if (!poisonCloudImg) return;

        const cloudTimer = this.timer - 2000;

        ctx.save();

        const hoverOffset = Math.sin(cloudTimer / 250) * 10;
        const hoverY = (this.targetY - 25) + hoverOffset;

        ctx.translate(this.targetX, hoverY);

        // Dynamic fade out during the last 500ms
        const fadeOutStart = this.duration - 500;
        if (this.timer > fadeOutStart) {
            const fadeProgress = (this.timer - fadeOutStart) / 500;
            ctx.globalAlpha = Math.max(0, 1.0 - fadeProgress);
        }

        ctx.drawImage(
            poisonCloudImg, 
            -(this.hazardWidth / 2), 
            -this.hazardHeight, 
            this.hazardWidth, 
            this.hazardHeight
        );

        ctx.restore();
    }
}