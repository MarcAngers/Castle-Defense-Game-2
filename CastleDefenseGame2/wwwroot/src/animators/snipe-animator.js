import loader from '../asset-loader.js';

export default class SnipeAnimator {
    // 1. MUST accept the new 'level' parameter!
    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.targetId = targetId;
        this.level = level;
        
        // Sniper sits on top of the castle
        this.startX = this.side === 1 ? 150 : 1850; 
        this.startY = 200; 

        this.lastKnownX = targetX;
        this.lastKnownY = 400; 

        this.timer = 0;
        this.isFinished = false;

        this.shakeX = 0;
        this.shakeY = 0;

        // --- TIER SETTINGS ---
        if (this.level === 1) {
            this.gunWidth = 50;
            this.gunHeight = 50;
            this.barrelDist = 49;     // The X coordinate of the barrel tip
            this.pivotY = 27;         // The Y coordinate of the barrel tip
            this.hasLaser = false;
            this.shakeIntensity = 0;  // No shake
            this.bulletDuration = 300; // Slower bullet (300ms)
            this.bulletColor = 'rgba(100, 150, 255, 1.0)'; // Blue tracer
        } else if (this.level === 3) {
            this.gunWidth = 150;
            this.gunHeight = 75;
            this.barrelDist = 149;
            this.pivotY = 31;
            this.hasLaser = true;
            this.shakeIntensity = 40; // Massive shake
            this.bulletDuration = 100;
            this.bulletColor = 'rgba(255, 255, 100, 1.0)'; // Yellow tracer
        } else {
            // Level 2
            this.gunWidth = 100;
            this.gunHeight = 50;
            this.barrelDist = 99;
            this.pivotY = 32;
            this.hasLaser = false;
            this.shakeIntensity = 10; // Minor shake
            this.bulletDuration = 100;
            this.bulletColor = 'rgba(255, 255, 100, 1.0)'; // Yellow tracer
        }

        // Dynamic duration based on the bullet speed
        this.duration = 3000 + this.bulletDuration; 
    }

    update(deltaTime) {
        this.timer += deltaTime;

        if (this.timer >= this.duration) {
            this.isFinished = true;
            this.shakeX = 0;
            this.shakeY = 0;
            return;
        }

        // --- SCREEN SHAKE (Starts when the gun fires at 3000ms) ---
        if (this.shakeIntensity > 0 && this.timer >= 3000 && this.timer < 3150) {
            const shakeProgress = (this.timer - 3000) / 150; 
            const intensity = this.shakeIntensity * (1 - shakeProgress); 
            this.shakeX = (Math.random() * 2 - 1) * intensity;
            this.shakeY = (Math.random() * 2 - 1) * intensity;
        } else {
            this.shakeX = 0;
            this.shakeY = 0;
        }
    }

    draw(ctx, state) {
        // 1. Find the target unit
        let targetUnit = null;
        if (state && state.units) {
            targetUnit = state.units.find(u => u.instanceId === this.targetId);
        }

        // 2. Update tracking coordinates
        if (targetUnit) {
            this.lastKnownX = targetUnit.position;
            this.lastKnownY = targetUnit.yPosition + 10; 
        }

        // 3. Calculate rotation angle
        const vx = this.lastKnownX - this.startX;
        const vy = this.lastKnownY - this.startY;
        const angle = Math.atan2(vy, vx);

        // 4. Calculate exactly where the tip of the barrel is in world space
        const barrelX = this.startX + (Math.cos(angle) * this.barrelDist);
        const barrelY = this.startY + (Math.sin(angle) * this.barrelDist);

        ctx.save();

        // --- PHASE 1: THE LOCK-ON (0ms to 3000ms) ---
        if (this.timer < 3000) {
            // Only draw the targeting laser if this tier allows it (Level 3)
            if (this.hasLaser && targetUnit) {
                const direction = targetUnit.side == 1 ? -1 : 1;
                ctx.beginPath();
                ctx.moveTo(barrelX, barrelY);
                ctx.lineTo(this.lastKnownX + 10 * direction, this.lastKnownY + 10);
                
                ctx.strokeStyle = `rgba(255, 0, 0, 0.8)`; 
                ctx.lineWidth = 2;
                ctx.stroke();
            }
        }

        // --- PHASE 2: THE BULLET (3000ms to End) ---
        if (this.timer >= 3000) {
            // Progress goes from 0.0 to 1.0 based on dynamic bullet speed
            const travelProgress = (this.timer - 3000) / this.bulletDuration;
            
            const bulletVx = this.lastKnownX - barrelX;
            const bulletVy = this.lastKnownY - barrelY;
            const currentBulletX = barrelX + (bulletVx * travelProgress);
            const currentBulletY = barrelY + (bulletVy * travelProgress);

            ctx.beginPath();
            ctx.moveTo(currentBulletX - (vx * 0.1), currentBulletY - (vy * 0.1)); 
            ctx.lineTo(currentBulletX, currentBulletY); 
            ctx.strokeStyle = this.bulletColor; // Dynamic color!
            ctx.lineWidth = 6;
            ctx.stroke();
        }

        // --- DRAW THE SNIPER RIFLE ---
        // Dynamically grab the correct image based on the level!
        const imgKey = this.level === 1 ? 'snipe' : `snipe_${this.level}`;
        const rifleImg = loader.assets['gadgets'][imgKey] || loader.assets['gadgets']['snipe'];

        if (rifleImg) {
            ctx.translate(this.startX, this.startY);
            ctx.rotate(angle);

            if (this.lastKnownX < this.startX) {
                ctx.scale(1, -1); 
            }

            // The magic offset! By pulling the image UP by the pivotY, 
            // the mathematical center of rotation perfectly lines up with the barrel.
            ctx.drawImage(rifleImg, 0, -this.pivotY, this.gunWidth, this.gunHeight); 
        }

        ctx.restore();
    }
}