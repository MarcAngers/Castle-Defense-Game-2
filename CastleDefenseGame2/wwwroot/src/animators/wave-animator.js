import loader from '../asset-loader.js';

export default class WaveAnimator {
    /**
     * Baseline for the wave's FOOT, in logical canvas units. The top edge of `#hud-bottom`
     * (20vh of a 500-unit logical canvas) is 400, so this stands the wave 10 units into the
     * shop bar rather than balanced on its lip. Levels 1 and 2 use it as-is.
     */
    static FOOT_Y = 410;

    /** Amplitude of the bob. The wave's top is highest at -BOB. */
    static BOB = 5;

    /**
     * Gap kept between the wave's crest at the peak of its bob and the bottom of the top HUD.
     *
     * Without it the clamp below puts the crest EXACTLY on the HUD's bottom edge, which still
     * reads as touching -- the wave visibly kisses the income line once a second as it bobs.
     */
    static HUD_MARGIN = 10;

    /**
     * How long the wave takes to drop out of sight once the server's hazard is gone.
     *
     * It now ends EARLY as a matter of course: WaveHazard collapses the moment it has
     * launched its budget of units (MaxKnockbacks), which at level 1 is 50 and can happen
     * a long way short of the far castle. Vanishing on that frame reads as a bug, so it
     * sinks out of the world instead and the screen shake ramps out with it.
     */
    static FALLOUT_MS = 400;

    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.level = level;

        // Spawn slightly off-screen so it rolls into view smoothly.
        this.startX = this.side === 1 ? -50 : 2050;

        // --- FETCH DYNAMIC DATA ---
        const dataKey = this.level === 1 ? 'wave' : `wave_${this.level}`;
        const gadgetData = loader.assets.gadgetData[dataKey];

        // Get the hazard size (Assuming it's stored under 'Width' or 'width')
        //
        // NUMBER(), NOT THE RAW CSV CELL. asset-loader stores every gadget column as the
        // STRING it parsed, and this was being used unconverted. It survived because the only
        // uses were `waveSize / 2` and drawImage, both of which coerce -- but the moment it
        // met a `+`, in the clamp below, "400" concatenated instead of adding and the clamp
        // silently evaluated to the unclamped baseline. Same trap waits for any other
        // gadgetData field used in arithmetic.
        this.waveSize = Number(gadgetData ? (gadgetData.radius || gadgetData.Radius || 200) : 200) || 200;

        // WHERE THE FOOT ACTUALLY SITS: low enough that the CREST never climbs past the
        // bottom of the top HUD.
        //
        // The sprite is drawn `waveSize` tall from the foot upwards, and waveSize is the
        // gadget's Radius -- 100 / 200 / 400. At the shared foot of 400 that puts level 3's
        // crest at 400 - 400 - 5 = -5, i.e. straight off the top of the canvas and through
        // the money box. Levels 1 and 2 top out at 295 and 195 and were never near it.
        //
        // So this is a CLAMP, not a new constant: each level is pushed down only by as much
        // as it overshoots, which moves level 3 (foot 400 -> ~538 on a landscape phone) and
        // leaves levels 1 and 2 exactly where they were. Lowering all three by the 138 the
        // tsunami needs was tried and is far worse -- it buries the level-1 wave entirely
        // behind the shop bar.
        const clearance = WaveAnimator.#hudClearanceLogical();
        this.footY = Math.max(
            WaveAnimator.FOOT_Y,
            clearance + this.waveSize + WaveAnimator.BOB + WaveAnimator.HUD_MARGIN);

        // Get the server duration in ticks (e.g., 5 seconds = 150 ticks)
        const hazardTicks = Number(gadgetData ? (gadgetData.hazardduration || gadgetData.HazardDuration || 210) : 210) || 210;

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
        // Where the hazard was when it disappeared, so the fall-out keeps drawing in place
        // rather than snapping back to the timer estimate.
        this._lastX = this.startX;
        this._falloutMs = -1;

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

    /**
     * Bottom of the top HUD, in LOGICAL canvas units.
     *
     * Measured rather than hardcoded because the two do not scale together: the HUD is a DOM
     * box in fixed CSS pixels (~100px), while the canvas is scaled by innerHeight / 500. On a
     * landscape phone that 100px is 133 logical units; on a tall desktop window it is nearer
     * 50. A constant tuned for either one is wrong on the other, and the phone is the case
     * that matters -- height is the scarce resource there.
     *
     * Falls back to the landscape-phone figure if the element is missing (a filmstrip
     * harness, a test page, an animator built before the HUD exists), which errs toward
     * lowering the wave rather than letting it climb through a HUD that IS there.
     */
    static #hudClearanceLogical() {
        try {
            const el = document.getElementById('hud-top');
            const scale = window.innerHeight / 500;
            if (el && scale > 0) {
                const bottom = el.getBoundingClientRect().bottom;
                if (bottom > 0) return bottom / scale;
            }
        } catch { /* no DOM (or no layout yet) -- fall through */ }
        return 133;
    }

    update(deltaTime, state) {
        this.timer += deltaTime;

        // Once the server's hazard is gone the wave is over -- but it drops out of the world
        // over FALLOUT_MS rather than blinking off, and the shake ramps down with it.
        if (this._falloutMs >= 0) {
            this._falloutMs += deltaTime;
            const p = Math.min(1, this._falloutMs / WaveAnimator.FALLOUT_MS);
            const fade = 1 - p;
            this.shakeX = (Math.random() * 2 - 1) * this.shakeIntensity * fade;
            this.shakeY = (Math.random() * 2 - 1) * this.shakeIntensity * fade;
            if (p >= 1) {
                this.isFinished = true;
                this.shakeX = 0;
                this.shakeY = 0;
            }
            return;
        }

        // Stay on screen for as long as the server's real hazard exists, rather
        // than guessing from a fixed client-side duration -- Marc's report: the
        // wave was disappearing before units were done getting knocked back,
        // i.e. the visual's assumed lifetime was shorter than the hazard's true
        // one. `this.duration` (computed in the constructor from HazardDuration)
        // is still used for the brief pre-broadcast position estimate in draw()
        // below, but no longer decides when the animation ends.
        //
        // SINCE THE KNOCKBACK CAP this is doing more work than it used to. A wave that
        // spends its budget expires wherever it stands, so the hazard can vanish at any
        // point in the crossing -- following the server is what makes the sprite and the
        // real hitbox agree about that, with no new message to carry the news.
        const hazard = this.#findHazard(state);
        if (hazard) {
            this._hasSeenHazard = true;
            // Remembered HERE and not only in draw(): the fall-out has to hold the wave at
            // the last place the hazard really was, and update() is guaranteed to have seen
            // it whether or not a frame was drawn in between.
            this._lastX = hazard.position ?? hazard.Position;
        } else if (this._hasSeenHazard) {
            // The real hazard has genuinely gone -- either it crossed the map and expired on
            // HazardDuration, or it ran out of knockbacks. Start the fall-out.
            this._falloutMs = 0;
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

    #findHazard(state) {
        return state?.hazards?.find(h => (h.type || h.Type) === 'Wave' && (h.side ?? h.Side) === this.side);
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
        const hazard = this.#findHazard(state);
        let currentX;
        if (hazard) {
            currentX = hazard.position ?? hazard.Position;
            this._lastX = currentX;
        } else if (this._falloutMs >= 0) {
            currentX = this._lastX;          // gone: hold station and sink
        } else {
            const speed = 2000 / this.duration;
            const distanceTraveled = this.timer * speed;
            currentX = this.side === 1
                ? this.startX + distanceTraveled
                : this.startX - distanceTraveled;
        }

        // Add a subtle bobbing motion to the water
        const bobOffset = Math.sin(this.timer / 150) * WaveAnimator.BOB;

        // THE FALL-OUT IS A DIVE, NOT A DROP.
        //
        // A wave that stops dead and sinks straight down loses all its momentum on one frame,
        // which is what it looked like. It now keeps travelling at its own real speed
        // (MAP_WIDTH / duration, so a slow tsunami leans less than a fast level-1 wave) while
        // the sink runs on p SQUARED rather than p. Quadratic is what makes the path read as
        // a dive: the first frames are almost all forward motion and the last are almost all
        // down, i.e. an object that carried its momentum and then fell, instead of one that
        // slid along a straight ramp.
        //
        // It still lands a full waveSize + BOB below the foot, so nothing is left poking above
        // the shop bar when it finishes, and the horizontal travel adds no new lifetime.
        let sink = 0, drift = 0;
        if (this._falloutMs >= 0) {
            const p = Math.min(1, this._falloutMs / WaveAnimator.FALLOUT_MS);
            sink = (this.waveSize + WaveAnimator.BOB) * p * p;
            const vx = 2000 / this.duration;                   // px per ms, the wave's own speed
            drift = (this.side === 1 ? 1 : -1) * vx * Math.min(this._falloutMs, WaveAnimator.FALLOUT_MS);
        }

        const currentY = this.footY + bobOffset + sink;
        currentX += drift;

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
