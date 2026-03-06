import loader from '../asset-loader.js';

export default class SnipeAnimator {
    constructor(side, targetX, targetId) {
        this.side = side;
        this.targetId = targetId;
        
        // Sniper sits on top of the castle
        this.startX = this.side === 1 ? 150 : 1850; 
        this.startY = 200; // Adjust this based on your castle image height!

        this.lastKnownX = targetX;
        this.lastKnownY = 400; // Default ground level

        this.timer = 0;
        this.duration = 3100; // 3s laser lock + 100ms bullet travel
        this.isFinished = false;

        this.shakeX = 0;
        this.shakeY = 0;
    }

    update(deltaTime) {
        this.timer += deltaTime;

        if (this.timer >= this.duration) {
            this.isFinished = true;
        }

        // --- SCREEN SHAKE (At exactly 3000ms when the gun fires) ---
        if (this.timer >= 3000 && this.timer < 3150) {
            const shakeProgress = (this.timer - 3000) / 150; 
            const intensity = 20 * (1 - shakeProgress); // Massive, sharp kickback
            this.shakeX = (Math.random() * 2 - 1) * intensity;
            this.shakeY = (Math.random() * 2 - 1) * intensity;
        } else {
            this.shakeX = 0;
            this.shakeY = 0;
        }
    }

    draw(ctx, state) {
        // 1. Find the target unit in the current game state
        let targetUnit = null;
        if (state && state.units) {
            targetUnit = state.units.find(u => u.instanceId === this.targetId);
        }

        // 2. Update our targeting coordinates
        if (targetUnit) {
            this.lastKnownX = targetUnit.position;
            // Aim exactly at the center of the unit's chest
            this.lastKnownY = targetUnit.yPosition + 10; 
        }

        // 3. Calculate rotation angle
        const vx = this.lastKnownX - this.startX;
        const vy = this.lastKnownY - this.startY;
        const angle = Math.atan2(vy, vx);

        // 2. THE TRIGONOMETRY MAGIC
        // Because we will align the gun's barrel to the pivot point, 
        // the X distance is exactly 99, and the Y offset is 0!
        const barrelDistance = 99; 
        
        // Use Sin and Cos to find exactly where that 99px points in world space
        const barrelX = this.startX + (Math.cos(angle) * barrelDistance);
        const barrelY = this.startY + (Math.sin(angle) * barrelDistance);

        ctx.save();

        // --- PHASE 1: THE LOCK-ON (0ms to 3000ms) ---
        if (this.timer < 3000) {
            // Draw the targeting laser
            const direction = targetUnit.side == 1 ? -1 : 1;
            ctx.beginPath();
            ctx.moveTo(barrelX, barrelY);
            ctx.lineTo(this.lastKnownX + 10*direction, this.lastKnownY + 10);
            
            ctx.strokeStyle = `rgba(255, 0, 0, 0.8)`; 
            ctx.lineWidth = 2;
            ctx.stroke();
        }

        // --- PHASE 2: THE BULLET (3000ms to 3100ms) ---
        if (this.timer >= 3000) {
            const travelProgress = (this.timer - 3000) / 100;
            
            // Calculate bullet position starting from the barrel, not the castle!
            const bulletVx = this.lastKnownX - barrelX;
            const bulletVy = this.lastKnownY - barrelY;
            const currentBulletX = barrelX + (bulletVx * travelProgress);
            const currentBulletY = barrelY + (bulletVy * travelProgress);

            // Draw a blazing fast yellow tracer round
            ctx.beginPath();
            ctx.moveTo(currentBulletX - (vx * 0.1), currentBulletY - (vy * 0.1)); // Tail
            ctx.lineTo(currentBulletX, currentBulletY); // Head
            ctx.strokeStyle = 'rgba(255, 255, 100, 1.0)';
            ctx.lineWidth = 6;
            ctx.stroke();
        }

        // --- DRAW THE SNIPER RIFLE ---
        const rifleImg = loader.assets['gadgets']['snipe'];
        if (rifleImg) {
            ctx.translate(this.startX, this.startY);
            ctx.rotate(angle);

            // Flip the gun if pointing left so it isn't upside down!
            if (this.lastKnownX < this.startX) {
                ctx.scale(1, -1); 
            }

            // Draw offset so the stock sits on the castle and the barrel points out
            ctx.drawImage(rifleImg, 0, -21, 100, 50); 
        }

        ctx.restore();
    }
}