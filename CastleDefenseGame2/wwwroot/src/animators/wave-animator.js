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
        const hazardTicks = gadgetData ? (gadgetData.hazardduration || gadgetData.HazardDuration || 210) : 210;

        // Convert server ticks (30 per sec, GameEngine.TICKS_PER_SECOND) to frontend
        // milliseconds. Was dividing by 20 -- the comment already said 30 per sec, but
        // the divisor didn't match, making every level's animation run 1.5x too long
        // (5s/7s/10s became 7.5s/10.5s/15s) and its speed proportionally too slow, since
        // speed is derived from this.duration below. The server's WaveHazard sweeps the
        // real knockback hitbox at the TRUE (faster) rate, so it was reaching and
        // launching units well before the slower visual wave sprite appeared to catch
        // up to them.
        this.duration = (hazardTicks / 30) * 1000;

        this.timer = 0;
        this.isFinished = false;
        this._hasSeenHazard = false;

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

    update(deltaTime, state) {
        this.timer += deltaTime;

        // Stay on screen for as long as the server's real hazard exists, rather
        // than guessing from a fixed client-side duration -- Marc's report: the
        // wave was disappearing before units were done getting knocked back,
        // i.e. the visual's assumed lifetime was shorter than the hazard's true
        // one. `this.duration` (computed in the constructor from HazardDuration)
        // is still used for the brief pre-broadcast position estimate in draw()
        // below, but no longer decides when the animation ends.
        const hazard = state?.hazards?.find(h => (h.type || h.Type) === 'Wave' && (h.side ?? h.Side) === this.side);
        if (hazard) {
            this._hasSeenHazard = true;
        } else if (this._hasSeenHazard) {
            // The real hazard has genuinely expired server-side -- end here.
            this.isFinished = true;
            this.shakeX = 0;
            this.shakeY = 0;
            return;
        } else if (this.timer >= this.duration * 2) {
            // Safety net only: never saw a real hazard at all (e.g. state
            // unavailable this frame) -- don't animate forever. Generous
            // multiple of the nominal duration so it never cuts off a real,
            // still-running hazard.
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

        // Drive position from the server's actual WaveHazard, not a client-side
        // clock. The hazard is already broadcast every tick in GameStateUpdate
        // (it's just a field on GameState nothing previously read), and its
        // Position is in the same raw game-unit space units render at directly
        // (see view.js's unit.position usage) -- so this is exact, immune to
        // frame-rate jitter, and doesn't depend on matching a start-position
        // constant to WaveEffect's (it was off by 50 units: -50/2050 here vs
        // the engine's actual -100/2100). Only fall back to the local timer-
        // based estimate for the brief window before the first state update
        // naming this hazard arrives.
        const hazard = state?.hazards?.find(h => (h.type || h.Type) === 'Wave' && (h.side ?? h.Side) === this.side);
        let currentX;
        if (hazard) {
            currentX = hazard.position ?? hazard.Position;
        } else {
            const speed = 2000 / this.duration;
            const distanceTraveled = this.timer * speed;
            currentX = this.side === 1
                ? this.startX + distanceTraveled
                : this.startX - distanceTraveled;
        }

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