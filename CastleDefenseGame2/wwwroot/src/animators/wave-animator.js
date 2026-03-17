import loader from '../asset-loader.js';

export default class WaveAnimator {
    // 1. Accept the level parameter!
    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.level = level;
        
        // Spawn slightly off-screen so it rolls into view smoothly.
        this.startX = this.side === 1 ? -50 : 2050; 
        this.targetY = 400; // Ground level

        // --- FETCH DYNAMIC DATA ---
        const dataKey = this.level === 1 ? 'wave' : `wave_${this.level}`;
        const gadgetData = loader.assets.gadgetData[dataKey];

        // Get the hazard size (Assuming it's stored under 'Width' or 'width')
        this.waveSize = gadgetData ? (gadgetData.radius || gadgetData.Radius || 200) : 200;
        
        // Get the server duration in ticks (e.g., 5 seconds = 150 ticks)
        const hazardTicks = gadgetData ? (gadgetData.hazardDuration || gadgetData.HazardDuration || 210) : 210;
        
        // Convert server ticks (30 per sec) to frontend milliseconds!
        this.duration = (hazardTicks / 30) * 1000;

        this.timer = 0;
        this.isFinished = false;

        // --- TIER SETTINGS FOR SCREEN SHAKE ---
        if (this.level === 1) {
            this.shakeIntensity = 0.5; // Gentle rumble
        } else if (this.level === 3) {
            this.shakeIntensity = 3.5; // Violent tsunami shaking
        } else {
            this.shakeIntensity = 1.5; // Base level
        }

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

        // Add a constant rumble while the massive wave is active
        this.shakeX = (Math.random() * 2 - 1) * this.shakeIntensity;
        this.shakeY = (Math.random() * 2 - 1) * this.shakeIntensity;
    }

    draw(ctx, state) {
        // Bulletproof image fallback logic
        const imgKey = this.level === 1 ? 'wave' : `wave_${this.level}`;
        const waveImg = loader.assets.gadgets[imgKey] || loader.assets.gadgets['wave'];
        if (!waveImg) return;

        // --- THE PERFECT SPEED MATH ---
        // Speed = MAP_WIDTH (2000px) / duration in milliseconds
        // This ensures the animation distance matches the server physics distance flawlessly.
        const speed = 2000 / this.duration; 
        const distanceTraveled = this.timer * speed;

        // P1 moves right (+), P2 moves left (-)
        const currentX = this.side === 1 
            ? this.startX + distanceTraveled 
            : this.startX - distanceTraveled;

        // Add a subtle bobbing motion to the water
        const bobOffset = Math.sin(this.timer / 150) * 5;
        const currentY = this.targetY + bobOffset;

        ctx.save();
        ctx.translate(currentX, currentY);

        // If Player 2 spawned it, flip the image horizontally so the wave faces left!
        if (this.side === 2) {
            ctx.scale(-1, 1);
        }

        // Draw the dynamically sized square wave.
        // Offset X by half-width (-waveSize/2) to perfectly center the hitbox.
        // Offset Y by full-height (-waveSize) so the bottom sits exactly on the floor.
        ctx.drawImage(
            waveImg, 
            -(this.waveSize / 2), 
            -this.waveSize, 
            this.waveSize, 
            this.waveSize
        );

        ctx.restore();
    }
}