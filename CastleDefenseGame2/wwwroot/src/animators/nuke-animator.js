import loader from '../asset-loader.js'; 

export default class NukeAnimator {
    constructor(side, targetX, targetId) {
        this.side = side;
        this.targetX = targetX;
        
        this.startX = this.side === 1 ? 150 : 1850; 
        this.targetY = 400; 

        this.timer = 0;
        this.duration = 4000; // 2s flight + 2s explosion
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

        // --- SCREEN SHAKE (2100ms to 3000ms) ---
        if (this.timer > 2100 && this.timer < 3000) {
            const shakeProgress = (this.timer - 2100) / 900; 
            const intensity = 15 * (1 - shakeProgress); 

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
            const rocketImg = loader.assets['gadgets']['nuke'];
            if (!rocketImg) return;

            const t = this.timer / 2000;
            const arcHeight = 400; 

            const currentX = this.startX + ((this.targetX - this.startX) * t);
            const currentY = this.targetY - (arcHeight * Math.sin(t * Math.PI));

            const vx = this.targetX - this.startX;
            const vy = -arcHeight * Math.PI * Math.cos(t * Math.PI);
            const angle = Math.atan2(vy, vx);

            ctx.save();
            ctx.translate(currentX, currentY);
            ctx.rotate(angle + (Math.PI / 2)); 
            
            ctx.drawImage(rocketImg, -37.5, -37.5, 75, 75);
            ctx.restore();

            return; 
        }

        // --- PHASE 2: THE DETONATION (2000ms to 4000ms) ---
        
        if (this.timer < 2100) {
            ctx.save();
            ctx.resetTransform(); 
            ctx.fillStyle = 'rgba(255, 255, 255, 0.9)'; 
            ctx.fillRect(0, 0, window.innerWidth, window.innerHeight);
            ctx.restore();
            return; 
        }

        const mushroomImg = loader.assets['gadgets']['mushroom-cloud'];
        if (!mushroomImg) return;

        ctx.save();
        ctx.translate(this.targetX, this.targetY);

        let scale = 1.0;
        
        if (this.timer < 2300) {
            const expandProgress = (this.timer - 2100) / 200;
            scale = 0.1 + (2.4 * expandProgress);
        } else if (this.timer < 2600) {
            const shrinkProgress = (this.timer - 2300) / 300;
            scale = 2.5 - (1.0 * shrinkProgress);
        } else {
            scale = 1.5;
        }

        ctx.scale(scale, scale);

        if (this.timer > 3000) {
            const fadeProgress = (this.timer - 3000) / 1000;
            ctx.globalAlpha = 1.0 - fadeProgress; 
        }

        ctx.drawImage(mushroomImg, -50, -200, 100, 200);
        ctx.restore();
    }
}