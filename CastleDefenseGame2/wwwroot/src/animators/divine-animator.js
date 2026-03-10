export default class DivineAnimator {
    constructor(side, targetX, targetId) {
        this.side = side;
        
        // Anchored perfectly to the allied castle!
        this.startX = this.side === 1 ? 150 : 1850; 
        this.targetY = 400; // Ground level

        this.timer = 0;
        // This animator ONLY exists for the 3-second startup delay.
        // Once it finishes, the units' own status rendering takes over!
        this.duration = 3000; 
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

        // --- THE DIVINE RUMBLE (2500ms to 3000ms) ---
        // As the light strikes down, the screen gets a smooth, powerful shake
        if (this.timer > 2500) {
            const shakeProgress = (this.timer - 2500) / 500; 
            const intensity = 8 * shakeProgress; // Builds up right before the strike
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
            
            // The sun sits high in the sky (Y = 100)
            const sunY = 100;
            // Grows from 0 to a massive 120px radius
            const radius = 120 * t; 
            
            // Draw the glowing aura (larger, very transparent)
            ctx.beginPath();
            ctx.arc(this.startX, sunY, radius * 1.5, 0, Math.PI * 2);
            ctx.fillStyle = `rgba(255, 220, 50, ${0.2 * t})`; // Soft yellow
            ctx.fill();

            // Draw the burning core (smaller, opaque)
            ctx.beginPath();
            ctx.arc(this.startX, sunY, radius, 0, Math.PI * 2);
            ctx.fillStyle = `rgba(255, 255, 200, ${0.8 + (0.2 * t)})`; // Piercing white-gold
            
            // Add a built-in canvas glow effect for that holy radiance
            ctx.shadowColor = 'rgba(255, 200, 0, 1)';
            ctx.shadowBlur = 40; 
            ctx.fill();
        } 
        // --- PHASE 2: THE PILLAR OF LIGHT (2500ms to 3000ms) ---
        else {
            const strikeTimer = this.timer - 2500;
            const t = strikeTimer / 500; // 0.0 to 1.0
            
            // The pillar is 300px wide, perfectly engulfing the castle
            const pillarWidth = 300;
            
            // Fades out smoothly from a blinding white-gold to invisible
            const alpha = 1.0 - t;

            // Draw the central pillar
            ctx.fillStyle = `rgba(255, 240, 150, ${alpha})`;
            ctx.shadowColor = 'rgba(255, 255, 255, 1)';
            ctx.shadowBlur = 50;
            
            // Draws from the top of the sky (Y = -100) all the way down to the ground
            ctx.fillRect(this.startX - (pillarWidth / 2), -100, pillarWidth, this.targetY + 100);

            // Draw a bright impact oval on the ground where the pillar hits
            ctx.beginPath();
            ctx.ellipse(this.startX, this.targetY, pillarWidth / 1.5, 40, 0, 0, Math.PI * 2);
            ctx.fillStyle = `rgba(255, 255, 255, ${alpha})`;
            ctx.fill();
        }

        ctx.restore();
    }
}