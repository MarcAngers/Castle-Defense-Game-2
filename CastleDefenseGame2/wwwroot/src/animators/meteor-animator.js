import loader from '../asset-loader.js'; 

export default class MeteorAnimator {
    constructor(side, targetX, targetId) {
        this.side = side;
        this.targetX = targetX;
        this.targetY = 360; // Ground level

        // Start way up in the sky (-600px), and 800px behind the target
        this.startX = this.targetX + (this.side === 1 ? -800 : 800); 
        this.startY = 0; 

        this.timer = 0;
        this.duration = 4000; // 3s flight + 1s aftermath
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

        // --- SCREEN SHAKE (3000ms to 4000ms) ---
        if (this.timer > 3000 && this.timer < 4000) {
            const shakeProgress = (this.timer - 3000) / 1000; 
            const intensity = 25 * (1 - shakeProgress); 

            this.shakeX = (Math.random() * 2 - 1) * intensity;
            this.shakeY = (Math.random() * 2 - 1) * intensity;
        } else {
            this.shakeX = 0;
            this.shakeY = 0;
        }
    }

    draw(ctx, state) {
        // --- PHASE 1: THE DESCENT (0ms to 3000ms) ---
        if (this.timer < 3000) {
            const meteorImg = loader.assets['gadgets']['meteor'];
            if (!meteorImg) return;

            const t = this.timer / 3000; // Progress from 0.0 to 1.0

            // Apply an ease-in cubic curve for dramatic acceleration!
            const easedT = t ** 10; 

            // Calculate position using the eased time
            const currentX = this.startX + ((this.targetX - this.startX) * easedT);
            const currentY = this.startY + ((this.targetY - this.startY) * easedT);

            // Scale accelerates right along with the movement
            const scale = 0.01 + (0.99 * easedT);

            ctx.save();
            ctx.translate(currentX, currentY);
            
            // If player 2 spawned it, flip the pre-drawn image so it points the right way
            if (this.side === 2) {
                ctx.scale(-1, 1);
            }

            ctx.scale(scale, scale);
            
            ctx.drawImage(meteorImg, -100, -100, 200, 200);
            ctx.restore();

            return; 
        }

        // --- PHASE 2: THE IMPACT (3000ms to 4000ms) ---
        const aftermathTimer = this.timer - 3000;
        const aftermathProgress = aftermathTimer / 1000; 
        
        // 1. The Blinding Flash (3000ms to 3100ms)
        if (aftermathTimer < 100) {
            ctx.save();
            ctx.resetTransform(); 
            ctx.fillStyle = 'rgba(255, 230, 200, 0.9)'; 
            ctx.fillRect(0, 0, window.innerWidth, window.innerHeight);
            ctx.restore();
            return; 
        }

        // 2. The Programmatic Shockwave
        ctx.save();
        ctx.translate(this.targetX, this.targetY);
        
        const currentRadius = 400 * aftermathProgress; 
        
        ctx.beginPath();
        ctx.ellipse(0, 0, currentRadius, currentRadius * 0.3, 0, 0, Math.PI * 2);
        
        ctx.lineWidth = 15 * (1 - aftermathProgress);
        ctx.strokeStyle = `rgba(255, 100, 0, ${1 - aftermathProgress})`; 
        ctx.stroke();

        ctx.restore();
    }
}