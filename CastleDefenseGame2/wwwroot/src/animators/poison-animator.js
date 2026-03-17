import loader from '../asset-loader.js';

export default class PoisonAnimator {
    // 1. Accept the level parameter!
    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.targetX = targetX; 
        this.level = level;
        
        // Flight path anchors
        this.startX = this.side === 1 ? 150 : 1850; 
        this.targetY = 400;

        // --- FETCH THE DYNAMIC RADIUS ---
        const dataKey = this.level === 1 ? 'poison' : `poison_${this.level}`;
        const gadgetData = loader.assets.gadgetData[dataKey];
        
        // Grab the Radius (falling back to 200 just in case)
        const radius = gadgetData ? (gadgetData.radius || gadgetData.Radius || 200) : 200; 
        
        // The full width of the drawing is twice the radius!
        this.hazardWidth = radius * 2; 
        
        // Lock the aspect ratio to 4:1 (based on the original 400x100 asset)
        this.hazardHeight = this.hazardWidth / 4;

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
            // Bulletproof image fallback for the flask
            const imgKey = this.level === 1 ? 'poison' : `poison_${this.level}`;
            const poisonBombImg = loader.assets.gadgets[imgKey] || loader.assets.gadgets['poison']; 
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
        // Bulletproof image fallback for the gas cloud
        const hazardKey = this.level === 1 ? 'poison' : `poison_${this.level}`;
        const poisonCloudImg = loader.assets.hazards[hazardKey] || loader.assets.hazards['poison']; 
        if (!poisonCloudImg) return;

        // Reset our timer to 0 so the math is clean for the hover effect
        const cloudTimer = this.timer - 2000;

        ctx.save();

        // 1. Calculate the Hover Math
        const hoverOffset = Math.sin(cloudTimer / 250) * 10;

        // 2. Shift the Y axis up by 25px per your design, plus the hover offset
        const hoverY = (this.targetY - 25) + hoverOffset;

        ctx.translate(this.targetX, hoverY);

        // Fade out smoothly during the last 500ms
        if (this.timer > 5500) {
            const fadeProgress = (this.timer - 5500) / 500;
            ctx.globalAlpha = Math.max(0, 1.0 - fadeProgress);
        }

        // 3. Draw the Dynamically Scaled Cloud
        // Offset X by half width to perfectly center the cloud on targetX
        // Offset Y by full height so the bottom of the image sits directly on our hover anchor
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