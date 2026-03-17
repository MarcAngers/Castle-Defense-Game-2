import loader from '../asset-loader.js';

export default class BlackholeAnimator {
    // 1. Accept the level parameter!
    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.targetX = targetX;
        this.level = level;
        
        this.targetY = 350 - 25 * level; 

        // --- FETCH DYNAMIC DATA ---
        const dataKey = this.level === 1 ? 'blackhole' : `blackhole_${this.level}`;
        const gadgetData = loader.assets.gadgetData[dataKey];

        // Grab the Radius (falling back to 200 just in case)
        const radius = gadgetData ? (gadgetData.radius || gadgetData.Radius || 200) : 200; 
        
        // The full width of the drawing is twice the radius!
        this.hazardWidth = radius * 2; 
        // Lock the aspect ratio to 2:1 (based on the original 400x200 asset)
        this.hazardHeight = this.hazardWidth / 2;

        // Get the server duration in ticks (e.g., 7 seconds = 210 ticks)
        // Fallback to 210 ticks if missing
        const hazardTicks = gadgetData ? (gadgetData.hazardDuration || gadgetData.HazardDuration || 210) : 210;
        const activeDurationMs = (hazardTicks / 30) * 1000;

        this.timer = 0;
        // 2s startup + dynamic active duration
        this.duration = 2000 + activeDurationMs;   
        this.isFinished = false;

        this.shakeX = 0;
        this.shakeY = 0;

        // --- TIER SETTINGS ---
        if (this.level === 1) {
            this.shakeIntensity = 0.5; // Weaker rumble
            this.hasDarkness = false;  // No global darkness
            this.useImage = true;     // Use the pulsing canvas circle
        } else if (this.level === 3) {
            this.shakeIntensity = 6.0; // Violent gravity tearing
            this.hasDarkness = true;   // Engulf the screen!
            this.useImage = true;
        } else {
            // Level 2 (Current Base)
            this.shakeIntensity = 2.0; 
            this.hasDarkness = false;
            this.useImage = true;
        }
    }

    update(deltaTime) {
        this.timer += deltaTime;

        if (this.timer >= this.duration) {
            this.isFinished = true;
            this.shakeX = 0;
            this.shakeY = 0;
            return;
        }

        // --- CONSTANT GRAVITY RUMBLE ---
        // Stops shaking during the final 500ms fade out
        if (this.timer >= 2000 && this.timer < this.duration - 500) {
            this.shakeX = (Math.random() * 2 - 1) * this.shakeIntensity;
            this.shakeY = (Math.random() * 2 - 1) * this.shakeIntensity;
        } else {
            this.shakeX = 0;
            this.shakeY = 0;
        }
    }

    draw(ctx, state) {
        ctx.save();

        // --- GLOBAL SCREEN DARKENING (LEVEL 3 ONLY) ---
        if (this.hasDarkness) {
            let darkAlpha = 0;
            const fadeOutStart = this.duration - 500;

            if (this.timer < 2000) {
                // Fade in to 70% opacity black over 2 seconds
                darkAlpha = (this.timer / 2000) * 0.7; 
            } else if (this.timer < fadeOutStart) {
                // Hold the darkness while the black hole is active
                darkAlpha = 0.7; 
            } else {
                // Fade the light back in during the final 500ms
                darkAlpha = 0.7 * (1 - ((this.timer - fadeOutStart) / 500)); 
            }

            if (darkAlpha > 0) {
                ctx.save();
                ctx.resetTransform(); 
                ctx.fillStyle = `rgba(0, 0, 0, ${darkAlpha})`;
                ctx.fillRect(0, 0, window.innerWidth, window.innerHeight);
                ctx.restore();
            }
        }

        ctx.translate(this.targetX, this.targetY);

        // Fade everything out smoothly during the final 500ms
        if (this.timer > this.duration - 500) {
            const fadeProgress = (this.timer - (this.duration - 500)) / 500;
            ctx.globalAlpha = Math.max(0, 1.0 - fadeProgress);
        }

        // Calculate the base radius for the canvas circle based on the total hazard width
        // (Original width 400 had a circle radius of 50, which is width / 8)
        const baseCircleRadius = this.hazardWidth / 8;

        // --- PHASE 1: THE SINGULARITY STARTUP (0ms to 2000ms) ---
        if (this.timer < 2000) {
            const t = this.timer / 2000; 
            const currentRadius = baseCircleRadius * t; 

            ctx.beginPath();
            ctx.arc(0, 0, currentRadius, 0, Math.PI * 2);
            ctx.fillStyle = '#000000';
            
            ctx.lineWidth = 4;
            ctx.strokeStyle = `rgba(180, 0, 255, ${t})`; 
            
            ctx.fill();
            ctx.stroke();
        } 
        // --- PHASE 2: THE EVENT HORIZON (2000ms to End) ---
        else {
            const activeTimer = this.timer - 2000;

            // LEVEL 1: Pulsing Canvas Circle
            if (!this.useImage) {
                // Rapidly throb between +10% and -10% of its size
                const pulse = Math.sin(activeTimer / 50) * (baseCircleRadius * 0.15);
                const pulsingRadius = baseCircleRadius + pulse;

                ctx.beginPath();
                ctx.arc(0, 0, pulsingRadius, 0, Math.PI * 2);
                ctx.fillStyle = '#000000';
                
                // Keep the purple glow fully opaque during the active phase
                ctx.lineWidth = 4;
                ctx.strokeStyle = 'rgba(180, 0, 255, 1)'; 
                
                ctx.fill();
                ctx.stroke();
            } 
            // LEVELS 2 & 3: Stretchy Image Sprite
            else {
                const frameIndex = Math.floor(activeTimer / 150) % 2 === 0 ? 1 : 2;
                
                // Bulletproof fallback logic for the tiered frames
                const imgKey = `blackhole_${this.level}-${frameIndex}`;
                const fallbackKey = `blackhole-${frameIndex}`;
                
                const bhImg = loader.assets.hazards[imgKey] || loader.assets.hazards[fallbackKey]; 
                
                if (bhImg) {
                    // Dynamically scale the image! 
                    // Offset by half width/height to perfectly center the sprite.
                    ctx.drawImage(
                        bhImg, 
                        -(this.hazardWidth / 2), 
                        -(this.hazardHeight / 2), 
                        this.hazardWidth, 
                        this.hazardHeight
                    );
                }
            }
        }

        ctx.restore();
    }
}