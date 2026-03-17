import loader from '../asset-loader.js';

export default class FreezeAnimator {
    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.level = level;
        
        this.startX = this.side === 1 ? 150 : 1850; 
        this.targetY = 340; 

        this.timer = 0;
        
        // --- THE DURATION FIX ---
        // Level 3 gets an extra 10 seconds (10000ms) for the lingering ice effect!
        this.duration = this.level === 3 ? 12500 : 2500; 
        this.isFinished = false;

        this.shakeX = 0;
        this.shakeY = 0;

        // --- TIER SETTINGS ---
        if (this.level === 1) {
            this.chargeMaxHeight = 75;  
            this.blastMaxHeight = 60;   
            this.shakeIntensity = 5;    
            this.chargeAlpha = 0.2;     
        } else if (this.level === 3) {
            this.chargeMaxHeight = 300; 
            this.blastMaxHeight = 250;  
            this.shakeIntensity = 25;   
            this.chargeAlpha = 0.6;     
            
            this.snowflakes = [];
            for (let i = 0; i < 60; i++) {
                this.snowflakes.push({
                    x: Math.random() * (window.innerWidth || 2000),
                    y: Math.random() * (window.innerHeight || 1000),
                    scale: 0.5 + Math.random() * 2,
                    angle: Math.random() * Math.PI * 2 
                });
            }
        } else {
            this.chargeMaxHeight = 150;
            this.blastMaxHeight = 120;
            this.shakeIntensity = 12;
            this.chargeAlpha = 0.4;
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

        // --- SCREEN SHAKE (2000ms to 2500ms) ---
        if (this.timer >= 2000 && this.timer < 2500) {
            const shakeProgress = (this.timer - 2000) / 500; 
            const intensity = this.shakeIntensity * (1 - shakeProgress); 

            this.shakeX = (Math.random() * 2 - 1) * intensity;
            this.shakeY = (Math.random() * 2 - 1) * intensity;
        } else {
            this.shakeX = 0;
            this.shakeY = 0;
        }
    }

    draw(ctx, state) {
        ctx.save();

        const beamWidth = this.side === 1 ? 2000 : -2000;

        // --- PHASE 1: THE CHARGE UP (0ms to 2000ms) ---
        if (this.timer < 2000) {
            const t = this.timer / 2000; 

            const currentHeight = this.chargeMaxHeight - ((this.chargeMaxHeight - 2) * t);
            const alpha = this.chargeAlpha + (Math.random() * 0.4); 
            ctx.fillStyle = `rgba(255, 255, 255, ${alpha})`;

            ctx.fillRect(this.startX, this.targetY - (currentHeight / 2), beamWidth, currentHeight);
        } 
        // --- PHASE 2: THE FREEZE BLAST (2000ms to 2500ms) ---
        else if (this.timer < 2500) { // <-- BOUNDED TO 2500ms!
            const t = (this.timer - 2000) / 500; 
            const currentHeight = this.blastMaxHeight * (1 - t);
            const globalAlpha = 1 - t;

            ctx.fillStyle = `rgba(150, 220, 255, ${globalAlpha})`;
            ctx.fillRect(this.startX, this.targetY - (currentHeight / 2), beamWidth, currentHeight);

            const coreHeight = currentHeight * 0.3;
            ctx.fillStyle = `rgba(255, 255, 255, ${globalAlpha})`;
            ctx.fillRect(this.startX, this.targetY - (coreHeight / 2), beamWidth, coreHeight);
        }

        // --- PHASE 3: THE LEVEL 3 FULL SCREEN FREEZE (2000ms to 12500ms) ---
        if (this.level === 3 && this.timer >= 2000) {
            ctx.save();
            ctx.resetTransform(); 
            
            const w = window.innerWidth || 2000;
            const h = window.innerHeight || 1000;

            // Calculate a new alpha that stays solid for 8 seconds, 
            // then thaws (fades out) over the final 2 seconds (10500ms to 12500ms)
            let freezeAlpha = 1.0;
            if (this.timer > 10500) {
                freezeAlpha = Math.max(0, 1.0 - ((this.timer - 10500) / 2000));
            }

            ctx.fillStyle = `rgba(100, 200, 255, ${freezeAlpha * 0.35})`;
            ctx.fillRect(0, 0, w, h);

            ctx.strokeStyle = `rgba(150, 230, 255, ${freezeAlpha * 0.6})`;
            ctx.lineWidth = 40;
            ctx.strokeRect(0, 0, w, h);

            const flakeImg = loader.assets['gadgets']['freeze'];
            if (flakeImg) {
                ctx.globalAlpha = freezeAlpha * 0.8;
                this.snowflakes.forEach(flake => {
                    ctx.save();
                    ctx.translate(flake.x, flake.y);
                    ctx.rotate(flake.angle);
                    ctx.scale(flake.scale, flake.scale);
                    ctx.drawImage(flakeImg, -10, -10, 20, 20); 
                    ctx.restore();
                });
            }
            
            ctx.restore();
        }

        ctx.restore();
    }
}