import loader from '../asset-loader.js';

export default class FirebombAnimator {
    constructor(side, targetX, targetId) {
        this.side = side;
        this.targetX = targetX; 
        
        this.startX = this.side === 1 ? 150 : 1850; 
        this.targetY = 400;
        this.hazardWidth = 300; 

        this.timer = 0;
        // 2s flight + 5s fire = 7 seconds total
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
            // Assuming your thrown bomb asset is still in the 'gadgets' folder!
            const bombImg = loader.assets.gadgets['firebomb']; 
            if (!bombImg) return;

            const t = this.timer / 2000; // 0.0 to 1.0 over 2 seconds
            const arcHeight = 300; // Slightly lower arc than the massive Nuke

            const currentX = this.startX + ((this.targetX - this.startX) * t);
            const currentY = this.targetY - (arcHeight * Math.sin(t * Math.PI));

            // Tumbling Rotation Math!
            // Math.PI * 4 equals exactly 2 full spins. 
            // (1 spin over 2 seconds looks a bit too slow/floaty in a fast-paced game!)
            const spinDirection = this.targetX > this.startX ? 1 : -1;
            const angle = t * (Math.PI * 4) * spinDirection;

            ctx.save();
            ctx.translate(currentX, currentY);
            ctx.rotate(angle); 
            
            // Assuming the bomb sprite is 50x50
            ctx.drawImage(bombImg, -37.5, -37.5, 75, 75);
            ctx.restore();

            return; // Don't draw the fire yet!
        }

        // --- PHASE 2: THE FIRE (2000ms to 7000ms) ---
        
        // 1. Calculate fire frame (Swaps every 150ms)
        // We subtract 2000 so the fire animation math starts perfectly at 0 when it lands
        const fireTimer = this.timer - 2000; 
        const frameIndex = Math.floor(fireTimer / 150) % 2 === 0 ? 1 : 2;
        
        // Using your new hazard loader path!
        const fireImg = loader.assets.hazards[`fire-${frameIndex}`]; 
        if (!fireImg) return;

        ctx.save();
        ctx.translate(this.targetX, this.targetY);

        if (this.timer > 6500) {
            const fadeProgress = (this.timer - 6500) / 500;
            ctx.globalAlpha = 1.0 - fadeProgress;
        }

        // 2. Tile the fire!
        const fireWidth = 100;
        const fireHeight = 100;
        const numberOfTiles = 4;

        // We shift the starting X to the left by half the hazardWidth (-150px)
        // so the 300px fire is perfectly centered on the targetX where the bomb landed!
        const startDrawX = -(this.hazardWidth / 2);

        const stepX = (this.hazardWidth - fireWidth) / (numberOfTiles - 1);

        for (let i = 0; i < numberOfTiles; i++) {
            // Offset X by (i * fireWidth) to place them side-by-side
            ctx.drawImage(fireImg, startDrawX + (i * stepX), -fireHeight, fireWidth, fireHeight);
        }

        ctx.restore();
    }
}