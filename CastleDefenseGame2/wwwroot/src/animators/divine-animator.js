export default class DivineAnimator {
    // 1. Accept the level parameter!
    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.level = level;
        
        // Anchored perfectly to the allied castle!
        this.startX = this.side === 1 ? 150 : 1850; 
        this.targetY = 400; // Ground level

        this.timer = 0;
        this.isFinished = false;

        this.shakeX = 0;
        this.shakeY = 0;

        // --- TIER SETTINGS ---
        if (this.level === 1) {
            this.sunRadius = 60;      // Tiny sun
            this.pillarWidth = 150;   // Skinny beam
            this.shakeIntensity = 3;  // Barely a rumble
            this.extraDuration = 0;
        } else if (this.level === 3) {
            this.sunRadius = 200;     // Massive, blinding sun
            this.pillarWidth = 500;   // Engulfs the entire screen edge
            this.shakeIntensity = 18; // Violent holy earthquake
            this.extraDuration = 1000; // 1 second extra for the golden screen flash
        } else {
            // Level 2 (Current Base)
            this.sunRadius = 120;
            this.pillarWidth = 300;
            this.shakeIntensity = 8;
            this.extraDuration = 0;
        }

        // 3s base duration + any lingering screen flash time
        this.duration = 3000 + this.extraDuration; 
    }

    update(deltaTime) {
        this.timer += deltaTime;

        if (this.timer >= this.duration) {
            this.isFinished = true;
            this.shakeX = 0;
            this.shakeY = 0;
            return;
        }

        // --- THE DIVINE RUMBLE (2500ms to 3000ms) ---
        // We cap the shake at 3000ms so Level 3 doesn't keep shaking during the fade out
        if (this.timer >= 2500 && this.timer <= 3000) {
            const shakeProgress = (this.timer - 2500) / 500; 
            const intensity = this.shakeIntensity * shakeProgress; 
            this.shakeX = (Math.random() * 2 - 1) * intensity;
            this.shakeY = (Math.random() * 2 - 1) * intensity;
        } else {
            this.shakeX = 0;
            this.shakeY = 0;
        }
    }

    draw(ctx, state) {
        ctx.save();
        
        // --- PHASE 1: THE CHARGING SUN (0ms to 2500ms) ---
        if (this.timer < 2500) {
            const t = this.timer / 2500; // 0.0 to 1.0
            
            const sunY = 100;
            // Dynamically scale the sun!
            const radius = this.sunRadius * t; 
            
            ctx.beginPath();
            ctx.arc(this.startX, sunY, radius * 1.5, 0, Math.PI * 2);
            ctx.fillStyle = `rgba(255, 220, 50, ${0.2 * t})`; 
            ctx.fill();

            ctx.beginPath();
            ctx.arc(this.startX, sunY, radius, 0, Math.PI * 2);
            ctx.fillStyle = `rgba(255, 255, 200, ${0.8 + (0.2 * t)})`; 
            
            ctx.shadowColor = 'rgba(255, 200, 0, 1)';
            ctx.shadowBlur = 40; 
            ctx.fill();
        } 
        // --- PHASE 2: THE PILLAR OF LIGHT & THE FLASH (2500ms to End) ---
        else {
            // 1. Draw the Pillar (Fades out completely at 3000ms)
            if (this.timer < 3000) {
                const strikeTimer = this.timer - 2500;
                const t = strikeTimer / 500; // 0.0 to 1.0
                
                const alpha = 1.0 - t;

                ctx.fillStyle = `rgba(255, 240, 150, ${alpha})`;
                ctx.shadowColor = 'rgba(255, 255, 255, 1)';
                ctx.shadowBlur = 50;
                
                // Dynamically scaled beam and impact oval
                ctx.fillRect(this.startX - (this.pillarWidth / 2), -100, this.pillarWidth, this.targetY + 100);

                ctx.beginPath();
                ctx.ellipse(this.startX, this.targetY, this.pillarWidth / 1.5, 40, 0, 0, Math.PI * 2);
                ctx.fillStyle = `rgba(255, 255, 255, ${alpha})`;
                ctx.fill();
            }

            // 2. The Level 3 Golden Screen Flash (2500ms to 3500ms)
            if (this.level === 3) {
                const flashTimer = this.timer - 2500;
                const flashDuration = this.extraDuration; // 1000ms
                
                if (flashTimer < flashDuration) {
                    const flashProgress = flashTimer / flashDuration;
                    const flashAlpha = 1.0 - flashProgress; // Fade out from 1.0 to 0
                    
                    ctx.save();
                    ctx.resetTransform(); // Lock to the screen!
                    
                    const w = window.innerWidth || 2000;
                    const h = window.innerHeight || 1000;
                    
                    // A powerful, warm golden wash over the entire UI
                    // Max alpha is 0.5 so it illuminates but doesn't totally blind the player
                    ctx.fillStyle = `rgba(255, 200, 50, ${flashAlpha * 0.5})`;
                    ctx.fillRect(0, 0, w, h);
                    
                    ctx.restore();
                }
            }
        }

        ctx.restore();
    }
}