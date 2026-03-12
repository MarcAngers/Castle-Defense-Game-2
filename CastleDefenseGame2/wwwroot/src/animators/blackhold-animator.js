import loader from '../asset-loader.js';

export default class BlackholeAnimator {
    constructor(side, targetX, targetId) {
        this.side = side;
        this.targetX = targetX;
        
        // We anchor this to the ground line. The center of the 400x200 image 
        // will sit exactly on this coordinate.
        this.targetY = 300; 

        this.timer = 0;
        // 2s startup + 7s active black hole = 9 seconds total
        this.duration = 9000;   
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

        // --- CONSTANT GRAVITY RUMBLE (2000ms to 8500ms) ---
        // A tiny, high-frequency vibration while the black hole is open
        if (this.timer >= 2000 && this.timer < 8500) {
            this.shakeX = (Math.random() * 2 - 1) * 2;
            this.shakeY = (Math.random() * 2 - 1) * 2;
        } else {
            this.shakeX = 0;
            this.shakeY = 0;
        }
    }

    draw(ctx, state) {
        ctx.save();

        // --- GLOBAL SCREEN DARKENING ---
        let darkAlpha = 0;
        if (this.timer < 2000) {
            // Fade in to 70% opacity black over 2 seconds
            darkAlpha = (this.timer / 2000) * 0.7; 
        } else if (this.timer < 8500) {
            // Hold the darkness while the black hole is active
            darkAlpha = 0.7; 
        } else {
            // Fade the light back in during the final 500ms
            darkAlpha = 0.7 * (1 - ((this.timer - 8500) / 500)); 
        }

        if (darkAlpha > 0) {
            ctx.save();
            ctx.resetTransform(); // Ignore the camera to cover the whole monitor
            ctx.fillStyle = `rgba(0, 0, 0, ${darkAlpha})`;
            ctx.fillRect(0, 0, window.innerWidth, window.innerHeight);
            ctx.restore();
        }

        // Move to the exact center of the impact zone
        ctx.translate(this.targetX, this.targetY);

        // --- PHASE 1: THE SINGULARITY STARTUP (0ms to 2000ms) ---
        if (this.timer < 2000) {
            const t = this.timer / 2000; // 0.0 to 1.0
            const currentRadius = 50 * t; // Grows from 0px to 50px

            ctx.beginPath();
            ctx.arc(0, 0, currentRadius, 0, Math.PI * 2);
            ctx.fillStyle = '#000000';
            
            // Add a subtle glowing purple edge to make the black dot pop 
            // against the darkening screen background!
            ctx.lineWidth = 4;
            ctx.strokeStyle = `rgba(180, 0, 255, ${t})`; 
            
            ctx.fill();
            ctx.stroke();
        } 
        // --- PHASE 2: THE EVENT HORIZON (2000ms to 7000ms) ---
        else {
            const activeTimer = this.timer - 2000;
            
            // Swaps between 1 and 2 every 150ms just like the firebomb
            const frameIndex = Math.floor(activeTimer / 150) % 2 === 0 ? 1 : 2;
            
            const bhImg = loader.assets.hazards[`blackhole-${frameIndex}`]; 
            if (!bhImg) {
                ctx.restore();
                return;
            }

            // Fade out the sprite smoothly at the very end
            if (this.timer > 8500) {
                const fadeProgress = (this.timer - 8500) / 500;
                ctx.globalAlpha = 1.0 - fadeProgress;
            }

            // Draw the 400x200 asset!
            // Offset X by -200 and Y by -100 to center the massive sprite exactly on targetX/targetY
            ctx.drawImage(bhImg, -200, -100, 400, 200);
        }

        ctx.restore();
    }
}