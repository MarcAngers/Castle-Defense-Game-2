export default class FreezeAnimator {
    constructor(side, targetX, targetId) {
        this.side = side;
        // The targetX parameter is ignored visually since it spans the whole map!
        
        // Starts at the castle
        this.startX = this.side === 1 ? 150 : 1850; 
        
        // Aimed right at the chest-height of the units (Assuming ground is 400)
        this.targetY = 380; 

        this.timer = 0;
        this.duration = 2500; // 2s charge-up + 0.5s blast
        this.isFinished = false;

        this.shakeX = 0;
        this.shakeY = 0;
    }

    update(deltaTime) {
        this.timer += deltaTime;

        if (this.timer >= this.duration) {
            this.isFinished = true;
            this.shakeX = 0;
            this.shakeY = 0;
            return;
        }

        // --- PHASE 3: SCREEN SHAKE (2000ms to 2500ms) ---
        // The screen violently shakes ONLY when the beam actually fires
        if (this.timer >= 2000 && this.timer < 2500) {
            const shakeProgress = (this.timer - 2000) / 500; 
            const intensity = 12 * (1 - shakeProgress); 

            this.shakeX = (Math.random() * 2 - 1) * intensity;
            this.shakeY = (Math.random() * 2 - 1) * intensity;
        } else {
            this.shakeX = 0;
            this.shakeY = 0;
        }
    }

    draw(ctx, state) {
        ctx.save();

        // Calculate how far the beam needs to go. 
        // 1500 spans the entire map width. A negative width perfectly fires to the left!
        const beamWidth = this.side === 1 ? 2000 : -2000;

        // --- PHASE 1: THE CHARGE UP (0ms to 2000ms) ---
        if (this.timer < 2000) {
            const t = this.timer / 2000; // Progress from 0.0 to 1.0

            // Height starts at a massive 150px and violently collapses down to 2px
            const currentHeight = 150 - (148 * t);

            // Add a randomized alpha flicker to make it feel unstable and energetic
            const alpha = 0.4 + (Math.random() * 0.4); 
            ctx.fillStyle = `rgba(255, 255, 255, ${alpha})`;

            // Center the rectangle vertically by offsetting Y by half its height
            ctx.fillRect(this.startX, this.targetY - (currentHeight / 2), beamWidth, currentHeight);
        } 
        // --- PHASE 2: THE FREEZE BLAST (2000ms to 2500ms) ---
        else {
            const t = (this.timer - 2000) / 500; // Progress from 0.0 to 1.0

            // The beam starts at 120px thick and rapidly tapers off to 0px as it dissipates
            const currentHeight = 120 * (1 - t);
            
            // Fades out from fully opaque to invisible over the half second
            const globalAlpha = 1 - t;

            // 1. The Glow (Outer Light Blue)
            ctx.fillStyle = `rgba(150, 220, 255, ${globalAlpha})`;
            ctx.fillRect(this.startX, this.targetY - (currentHeight / 2), beamWidth, currentHeight);

            // 2. The Core (Inner Pure White)
            // Making the core 30% of the total height gives it a piercing, hot center
            const coreHeight = currentHeight * 0.3;
            ctx.fillStyle = `rgba(255, 255, 255, ${globalAlpha})`;
            ctx.fillRect(this.startX, this.targetY - (coreHeight / 2), beamWidth, coreHeight);
        }

        ctx.restore();
    }
}