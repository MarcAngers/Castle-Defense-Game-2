import loader from '../asset-loader.js'; 

export default class NukeAnimator {
    // 1. MUST accept the new 'level' parameter!
    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.targetX = targetX;
        this.level = level;
        
        this.startX = this.side === 1 ? 150 : 1850; 
        this.targetY = 400; 

        this.timer = 0;
        this.duration = 4000; 
        this.isFinished = false;

        this.shakeX = 0;
        this.shakeY = 0;

        // --- TIER SETTINGS ---
        // Pre-calculate all the sizes and intensities based on the level!
        this.projSize = this.level === 1 ? 50 : (this.level === 3 ? 150 : 75);
        this.cloudWidth = this.level === 1 ? 100 : (this.level === 3 ? 300 : 150);
        this.cloudHeight = this.level === 1 ? 200 : (this.level === 3 ? 600 : 300);
        
        this.flashDuration = this.level === 1 ? 0 : (this.level === 3 ? 1000 : 100);
        this.shakeIntensity = this.level === 1 ? 4 : (this.level === 3 ? 45 : 15);
    }

    update(deltaTime) {
        this.timer += deltaTime;

        if (this.timer >= this.duration) {
            this.isFinished = true;
            this.shakeX = 0;
            this.shakeY = 0;
            return;
        }

        // --- SCREEN SHAKE ---
        // Start the shake exactly at impact (2000ms)
        if (this.timer >= 2000 && this.timer < 3000) {
            const shakeProgress = (this.timer - 2000) / 1000; 
            const intensity = this.shakeIntensity * (1 - shakeProgress); 

            this.shakeX = (Math.random() * 2 - 1) * intensity;
            this.shakeY = (Math.random() * 2 - 1) * intensity;
        } else {
            this.shakeX = 0;
            this.shakeY = 0;
        }
    }

    draw(ctx, state) {
        // --- PHASE 1: THE FLIGHT (0ms to 2000ms) ---
        if (this.timer < 2000) {
            const imgKey = this.level === 1 ? 'nuke' : `nuke_${this.level}`;
            const rocketImg = loader.assets['gadgets'][imgKey] || loader.assets['gadgets']['nuke'];
            
            if (!rocketImg) return;
            
            const t = this.timer / 2000;
            const arcHeight = 400; 

            const currentX = this.startX + ((this.targetX - this.startX) * t);
            const currentY = this.targetY - (arcHeight * Math.sin(t * Math.PI));

            ctx.save();
            ctx.translate(currentX, currentY);
            
            // Only apply the rotation calculation for Level 2 and 3!
            if (this.level > 1) {
                const vx = this.targetX - this.startX;
                const vy = -arcHeight * Math.PI * Math.cos(t * Math.PI);
                const angle = Math.atan2(vy, vx);
                ctx.rotate(angle + (Math.PI / 2)); 
            }
            
            ctx.drawImage(rocketImg, -this.projSize/2, -this.projSize/2, this.projSize, this.projSize);
            ctx.restore();

            return; 
        }

        // --- PHASE 2: THE DETONATION (2000ms to 4000ms) ---
        
        const detTime = this.timer - 2000;
        
        // 1. Draw the Flash overlay
        if (detTime < this.flashDuration) {
            ctx.save();
            ctx.resetTransform(); 
            
            // Fade the flash out linearly so the cloud emerges from the light
            const flashAlpha = 1.0 - (detTime / this.flashDuration);
            ctx.fillStyle = `rgba(255, 255, 255, ${flashAlpha})`; 
            ctx.fillRect(0, 0, window.innerWidth, window.innerHeight);
            ctx.restore();
            
            // Replicate the original behavior for Level 2 by skipping the cloud draw this frame
            if (this.level === 2 && detTime < 100) return; 
        }

        // 2. Draw the Mushroom Cloud
        const mushroomImg = loader.assets['gadgets']['mushroom-cloud'];
        if (!mushroomImg) return;

        ctx.save();
        ctx.translate(this.targetX, this.targetY);

        let scale = 1.0;
        
        // Delay the cloud expansion slightly based on the level's flash duration
        let expandStart = this.level === 3 ? 200 : (this.level === 2 ? 100 : 0);
        
        if (detTime < expandStart) {
            scale = 0.1; // Hold tiny during the brightest part of the flash
        } else if (detTime < expandStart + 200) {
            const expandProgress = (detTime - expandStart) / 200;
            scale = 0.1 + (2.4 * expandProgress);
        } else if (detTime < expandStart + 500) {
            const shrinkProgress = (detTime - (expandStart + 200)) / 300;
            scale = 2.5 - (1.0 * shrinkProgress);
        } else {
            scale = 1.5;
        }

        ctx.scale(scale, scale);

        // Fade out at the end
        if (this.timer > 3000) {
            const fadeProgress = (this.timer - 3000) / 1000;
            ctx.globalAlpha = Math.max(0, 1.0 - fadeProgress); 
        }

        ctx.drawImage(mushroomImg, -this.cloudWidth/2, -this.cloudHeight, this.cloudWidth, this.cloudHeight);
        ctx.restore();
    }
}