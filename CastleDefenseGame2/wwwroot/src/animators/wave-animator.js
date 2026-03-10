import loader from '../asset-loader.js';

export default class WaveAnimator {
    constructor(side, targetX, targetId) {
        this.side = side;
        
        // TargetX isn't really used since it sweeps the whole map,
        // but we keep the constructor signature consistent.
        
        // Spawn slightly off-screen so it rolls into view smoothly.
        // Remember you expanded the map to 2000px!
        this.startX = this.side === 1 ? -50 : 2050; 
        this.targetY = 400; // Ground level

        this.timer = 0;
        this.duration = 9000; // 9 seconds total
        this.isFinished = false;

        this.shakeX = 0;
        this.shakeY = 0;
    }

    update(deltaTime) {
        this.timer += deltaTime;

        if (this.timer >= this.duration) {
            this.isFinished = true;
        }

        // Add a constant, low-level rumble while the massive wave is active
        if (this.timer < this.duration) {
            this.shakeX = (Math.random() * 2 - 1) * 1.5;
            this.shakeY = (Math.random() * 2 - 1) * 1.5;
        }
    }

    draw(ctx, state) {
        const waveImg = loader.assets.gadgets['wave'];
        if (!waveImg) return;

        // --- THE SPEED MATH ---
        // 300 pixels per second == 0.3 pixels per millisecond
        const speed = 0.22;
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

        // Draw the 200x200 wave.
        // Offset X by -100 to center the asset on the coordinate.
        // Offset Y by -200 so the bottom of the water sits exactly on the floor.
        ctx.drawImage(waveImg, -100, -200, 200, 200);

        ctx.restore();
    }
}