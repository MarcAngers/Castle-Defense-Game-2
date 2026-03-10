import loader from '../asset-loader.js';

export default class PoisonAnimator {
    constructor(side, targetX, targetId) {
        this.side = side;
        this.targetX = targetX; 
        
        // Flight path anchors
        this.startX = this.side === 1 ? 150 : 1850; 
        this.targetY = 400;
        this.hazardWidth = 400; 

        this.timer = 0;
        // 2s flight + 4s hovering cloud = 6 seconds total
        this.duration = 6000;   
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
            const poisonBombImg = loader.assets.gadgets['poison']; 
            if (!poisonBombImg) return;

            const t = this.timer / 2000; // 0.0 to 1.0 over 2 seconds
            const arcHeight = 300; 

            const currentX = this.startX + ((this.targetX - this.startX) * t);
            const currentY = this.targetY - (arcHeight * Math.sin(t * Math.PI));

            // Tumbling Rotation Math
            const spinDirection = this.targetX > this.startX ? 1 : -1;
            const angle = t * (Math.PI * 4) * spinDirection;

            ctx.save();
            ctx.translate(currentX, currentY);
            ctx.rotate(angle); 
            
            // Assuming the flying flask/bomb sprite is 50x50
            ctx.drawImage(poisonBombImg, -25, -25, 50, 50);
            ctx.restore();

            return; // Don't draw the cloud yet!
        }

        // --- PHASE 2: THE HOVERING CLOUD (2000ms to 6000ms) ---
        const poisonCloudImg = loader.assets.hazards['poison']; 
        if (!poisonCloudImg) return;

        // Reset our timer to 0 so the math is clean for the hover effect
        const cloudTimer = this.timer - 2000;

        ctx.save();

        // 1. Calculate the Hover Math
        // Dividing by 250 controls the SPEED of the bob. 
        // Multiplying by 10 controls the HEIGHT of the bob (10px up/down).
        const hoverOffset = Math.sin(cloudTimer / 250) * 10;

        // 2. Shift the Y axis up by 25px per your design, plus the hover offset
        const hoverY = (this.targetY - 25) + hoverOffset;

        ctx.translate(this.targetX, hoverY);

        // Fade out smoothly during the last 500ms
        if (this.timer > 5500) {
            const fadeProgress = (this.timer - 5500) / 500;
            ctx.globalAlpha = 1.0 - fadeProgress;
        }

        // 3. Draw the 400x100 Cloud
        // Offset X by -200 to perfectly center the massive cloud on targetX
        // Offset Y by -100 so the bottom of the image sits directly on our hover anchor
        ctx.drawImage(poisonCloudImg, -200, -100, 400, 100);

        ctx.restore();
    }
}