import loader from '../asset-loader.js';

export default class GooAnimator {
    constructor(side, targetX, targetId) {
        this.side = side;
        this.targetX = targetX; 
        
        // Flight path anchors
        this.startX = this.side === 1 ? 150 : 1850; 
        this.targetY = 400;
        this.hazardWidth = 400; 

        this.timer = 0;
        // 2s flight + 6s goo puddle = 7 seconds total
        this.duration = 8000;   
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
            const gooProjectileImg = loader.assets.gadgets['goo']; 
            if (!gooProjectileImg) return;

            const t = this.timer / 2000; // 0.0 to 1.0 over 2 seconds
            const arcHeight = 300; 

            const currentX = this.startX + ((this.targetX - this.startX) * t);
            const currentY = this.targetY - (arcHeight * Math.sin(t * Math.PI));

            ctx.save();
            ctx.translate(currentX, currentY);
            
            // NO ROTATION: The goo stays perfectly upright during flight
            
            ctx.drawImage(gooProjectileImg, -37.5, -37.5, 75, 75);
            ctx.restore();

            return; // Don't draw the puddle yet!
        }

        // --- PHASE 2: THE GOO PUDDLE (2000ms to 7000ms) ---
        const gooHazardImg = loader.assets.hazards['goo']; 
        if (!gooHazardImg) return;

        ctx.save();
        ctx.translate(this.targetX, this.targetY);

        // Fade out smoothly during the last 500ms
        if (this.timer > 7500) {
            const fadeProgress = (this.timer - 7500) / 500;
            ctx.globalAlpha = 1.0 - fadeProgress;
        }

        // NO TILING: Just one massive 400x50 puddle.
        // Offset X by -200 (half of 400) to perfectly center it on targetX
        // Offset Y by -50 so the bottom of the puddle sits exactly on the floor
        ctx.drawImage(gooHazardImg, -200, -50, 400, 50);

        ctx.restore();
    }
}