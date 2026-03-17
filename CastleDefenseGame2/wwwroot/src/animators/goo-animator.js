import loader from '../asset-loader.js';

export default class GooAnimator {
    // 1. Accept the level parameter!
    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.targetX = targetX; 
        this.level = level;
        
        // Flight path anchors
        this.startX = this.side === 1 ? 150 : 1850; 
        this.targetY = 400;

        // --- FETCH THE DYNAMIC RADIUS ---
        const dataKey = this.level === 1 ? 'goo' : `goo_${this.level}`;
        const gadgetData = loader.assets.gadgetData[dataKey];
        
        // Grab the Radius (falling back to 200 just in case)
        const radius = gadgetData ? (gadgetData.radius || gadgetData.Radius || 200) : 200; 
        
        // The full width of the drawing is twice the radius!
        this.hazardWidth = radius * 2; 

        this.timer = 0;
        // 2s flight + 6s goo puddle = 8 seconds total
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
            const imgKey = this.level === 1 ? 'goo' : `goo_${this.level}`;
            const gooProjectileImg = loader.assets.gadgets[imgKey] || loader.assets.gadgets['goo']; 
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

        // --- PHASE 2: THE GOO PUDDLE (2000ms to 8000ms) ---
        const hazardKey = this.level === 1 ? 'goo' : `goo_${this.level}`;
        const gooHazardImg = loader.assets.hazards[hazardKey] || loader.assets.hazards['goo']; 
        if (!gooHazardImg) return;

        ctx.save();
        ctx.translate(this.targetX, this.targetY);

        // Fade out smoothly during the last 500ms
        if (this.timer > 7500) {
            const fadeProgress = (this.timer - 7500) / 500;
            ctx.globalAlpha = Math.max(0, 1.0 - fadeProgress);
        }

        // NO TILING: Just one massive stretchy puddle!
        // Offset X by half the hazardWidth to perfectly center it on targetX
        // Offset Y by -50 so the bottom of the puddle sits exactly on the floor
        // Use this.hazardWidth to dynamically stretch the image!
        ctx.drawImage(gooHazardImg, -(this.hazardWidth / 2), -50, this.hazardWidth, 50);

        ctx.restore();
    }
}