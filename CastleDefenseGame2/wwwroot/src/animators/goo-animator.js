import loader from '../asset-loader.js';

export default class GooAnimator {
    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.targetX = targetX; 
        this.level = level;
        
        this.startX = this.side === 1 ? 150 : 1850; 
        this.targetY = 400;

        // --- FETCH DYNAMIC DATA ---
        const dataKey = this.level === 1 ? 'goo' : `goo_${this.level}`;
        const gadgetData = loader.assets.gadgetData[dataKey];
        
        // Grab the Radius
        const radius = gadgetData ? (gadgetData.radius || gadgetData.Radius || 200) : 200; 
        this.hazardWidth = radius * 2; 

        // Get the server duration in ticks (Fallback to 180 ticks / 6 seconds)
        const hazardTicks = gadgetData ? (gadgetData.hazardduration || gadgetData.HazardDuration || 180) : 180;
        const activeDurationMs = (hazardTicks / 20) * 1000;

        this.timer = 0;
        // 2s flight + dynamic goo duration
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
            const imgKey = this.level === 1 ? 'goo' : `goo_${this.level}`;
            const gooProjectileImg = loader.assets.gadgets[imgKey] || loader.assets.gadgets['goo']; 
            if (!gooProjectileImg) return;

            const t = this.timer / 2000; 
            const arcHeight = 300; 

            const currentX = this.startX + ((this.targetX - this.startX) * t);
            const currentY = this.targetY - (arcHeight * Math.sin(t * Math.PI));

            ctx.save();
            ctx.translate(currentX, currentY);
            ctx.drawImage(gooProjectileImg, -37.5, -37.5, 75, 75);
            ctx.restore();

            return; 
        }

        // --- PHASE 2: THE GOO PUDDLE (2000ms to End) ---
        const hazardKey = this.level === 1 ? 'goo' : `goo_${this.level}`;
        const gooHazardImg = loader.assets.hazards[hazardKey] || loader.assets.hazards['goo']; 
        if (!gooHazardImg) return;

        ctx.save();
        ctx.translate(this.targetX, this.targetY);

        // Dynamic fade out during the last 500ms
        const fadeOutStart = this.duration - 500;
        if (this.timer > fadeOutStart) {
            const fadeProgress = (this.timer - fadeOutStart) / 500;
            ctx.globalAlpha = Math.max(0, 1.0 - fadeProgress);
        }

        ctx.drawImage(gooHazardImg, -(this.hazardWidth / 2), -50, this.hazardWidth, 50);

        ctx.restore();
    }
}