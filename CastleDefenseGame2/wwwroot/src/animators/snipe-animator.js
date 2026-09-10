import loader from '../asset-loader.js';

const MS_PER_TICK = 1000 / 30;

/**
 * SNIPE, AS A BURST WEAPON (rewritten 2026-09-08).
 *
 * ── THE BULLET MUST ARRIVE ON THE DAMAGE TICK, NOT LEAVE ON IT ───────────────────────
 *
 *   cast ──lockOn──▶ shot i FIRES ──bulletDuration──▶ shot i LANDS  ==  engine damage tick
 *
 * The engine applies shot i's damage exactly `Delay + i * interval` ticks after the cast:
 * `ProcessActions()` runs at the top of `Tick`, `ScheduleEffect` stamps
 * `ExecuteAtTick = CurrentTick + Delay`, and the scheduled-effect loop runs in that same pass
 * before `CurrentTick++`. The impact instant is therefore fixed by the engine, and the
 * animation is solved BACKWARDS from it:
 *
 *      impact(i) = (Delay + i * interval) * MS_PER_TICK      <- the anchor
 *      fire(i)   = impact(i) - bulletDuration                <- derived
 *
 * Anchoring the FIRING time instead -- which this animator did until 2026-09-09 -- lands every
 * bullet `bulletDuration` LATE: 300ms at level 1, 100ms at levels 2-3. The unit visibly took
 * the hit before the tracer reached it. Marc caught it in playtesting, and snipe is the one
 * gadget where the impact instant is the entire read.
 *
 * The previous cut had already closed a worse version of the same bug: the ORIGINAL animator
 * held a hardcoded 3000ms lock-on against a `Delay` of 70 ticks = 2333ms, so the bullet had
 * not even left the barrel until ~667ms after the damage landed.
 *
 * ── THE FOLLOW-UP TARGETS ARE RE-DERIVED HERE, AND THAT IS A CONTRACT ────────────────
 *
 * `PlayGadgetAnimation` carries ONE target id, and it is deliberately left that way:
 * GameRecorder writes a cast from that same event, so firing it per shot would record one
 * cast as ten. The client therefore reproduces `SnipeEffect.SelectTargets`' rule for shots
 * 1..N-1:
 *
 *      the highest MaxHealth enemy not already picked, NEVER a wall,
 *      ties broken by lower position
 *
 * Change one side without the other and the tracers point at different units than the ones
 * that take the damage. It is cosmetic when it drifts -- the engine is authoritative for who
 * dies -- but it is still a rule written in two places, so it is called out in both.
 */
export default class SnipeAnimator {
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

        // --- THE SHOT TABLE, MIRRORING SnipeEffect.ShotsFor / ShotIntervalFor ---------
        // Hardcoded on BOTH sides since 2026-09-09. These were CSV columns for one day, purely
        // so the level-3 count could be swept; the sweep is done and the columns were removed
        // rather than left bloating a file the client, the engine and every arena all read.
        // SnipeEffect is the source of truth -- change one side without the other and the
        // tracers stop matching the damage.
        this.shots = this.level === 3 ? 10 : this.level === 2 ? 2 : 1;
        this.intervalTicks = this.level === 3 ? 5 : this.level === 2 ? 15 : 0;
        this.intervalMs = this.intervalTicks * MS_PER_TICK;

        // `Delay` IS still a CSV column and is read rather than hardcoded -- it is shared with
        // every other gadget and is the engine's own scheduling number.
        // Number(), not the raw value: asset-loader stores every gadget column as the STRING it
        // parsed, and this one meets `*` immediately -- the trap that silently disabled the
        // wave's HUD clamp on 2026-09-03 ("400" + 10 === "40010"). KEYS ARE LOWERCASE too,
        // because asset-loader does `headers.map(h => h.toLowerCase())`.
        const id = this.level === 1 ? 'snipe' : `snipe_${this.level}`;
        const data = (loader.assets.gadgetData && loader.assets.gadgetData[id]) || {};
        this.delayTicks = Number(data.delay) || 70;

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
            // TONED DOWN from 40 (Marc, 2026-09-08). At 40 a single shot threw the whole
            // canvas around; at ten shots five ticks apart the old value never stopped
            // shaking and the board was unreadable for the whole burst. 7 still reads as a
            // heavy rifle when the shots stack, because the per-shot decays now OVERLAP.
            this.shakeIntensity = 7;
            this.bulletDuration = 100;
            this.bulletColor = 'rgba(255, 255, 100, 1.0)'; // Yellow tracer
        } else {
            // Level 2
            this.gunWidth = 100;
            this.gunHeight = 50;
            this.barrelDist = 99;
            this.pivotY = 32;
            this.hasLaser = false;
            this.shakeIntensity = 5;
            this.bulletDuration = 100;
            this.bulletColor = 'rgba(255, 255, 100, 1.0)'; // Yellow tracer
        }

        // Solved BACKWARDS from the impact instant -- see the header. Placed after the tier
        // block because that is where bulletDuration is set.
        this.lockOnMs = this.delayTicks * MS_PER_TICK - this.bulletDuration;

        // How long one shot's screen shake takes to fall to zero. Shorter than the level-3
        // interval (5 ticks = 167ms) on purpose, so consecutive shots read as separate
        // recoils rather than one continuous rumble.
        this.shakeDecayMs = 120;

        // Resolved lazily in draw(), where the unit list is available. One entry per shot;
        // index 0 is the server's target, the rest are re-derived (see the class comment).
        this.targets = null;
        this.lastKnown = [];

        // Engine-time clock state -- see update(). castTick is latched on the first frame
        // that carries state, which is the tick the cast was broadcast on.
        this.castTick = null;
        this.lastTick = null;
        this.tickWall = 0;
        this.msPerTickEma = null;

        // Runs a beat past the FINAL IMPACT so the muzzle flash and shake settle.
        this.duration = this.impactTime(this.shots - 1) + 250;
    }

    /**
     * ms after the cast at which shot `i` LANDS. This is the anchor: it equals the engine's
     * damage tick `Delay + i * interval` exactly, so everything else is derived from it.
     */
    impactTime(i) { return (this.delayTicks + i * this.intervalTicks) * MS_PER_TICK; }

    /** ms after the cast at which shot `i` leaves the barrel. Derived from its impact. */
    shotTime(i) { return this.impactTime(i) - this.bulletDuration; }

    /**
     * THE CLOCK IS THE SERVER'S TICK COUNTER, NOT WALL TIME, AND THAT IS THE WHOLE FIX.
     *
     * `impactTime(i)` is in ENGINE milliseconds -- ticks x 33.33. A wall-clock timer only
     * matches that if the server actually completes 30 ticks per second, and IT DOES NOT:
     *
     *     GameHostingService: Task.Delay((1000 / TICKS_PER_SECOND) - elapsed)
     *
     * `1000 / 30` is INTEGER division = 33, and Task.Delay on Windows rounds up to the
     * ~15.6ms system timer granularity, so each sleep lands near 46.8ms. Measured live from
     * the browser against `state.currentTick`: **21.45 Hz, 46.63 ms per tick**. Snipe's
     * 70-tick delay therefore takes **3,264ms of real time** while a wall-clock animator
     * expects 2,333ms -- the bullet lands **931ms before the damage**, which is exactly the
     * "nearly a full second" Marc reported.
     *
     * Anchoring to `currentTick` makes the animation correct at ANY server rate, including
     * one that hitches under load, which a re-tuned constant would not. The server loop
     * running at 71% of its intended speed is a separate and larger defect -- it means the
     * whole game runs slow in wall time -- and is left alone deliberately: fixing it speeds
     * every live game up by 40%, which is a gameplay decision rather than an animation one.
     *
     * Interpolated WITHIN the current tick, using an EMA of the observed tick period, or the
     * burst would step 21 times a second instead of running smoothly. Falls back to wall
     * clock when there is no state (the filmstrip harness, and any future offline preview).
     */
    update(deltaTime, state) {
        const tick = state && typeof state.currentTick === 'number' ? state.currentTick : null;

        if (tick === null) {
            this.timer += deltaTime;                     // no state: wall clock, as before
        } else {
            const now = performance.now();
            if (this.castTick === null) {
                this.castTick = tick;
                this.lastTick = tick;
                this.tickWall = now;
            }
            if (tick !== this.lastTick) {
                // Real seconds this tick took, smoothed. Seeded on the first observed gap
                // rather than on the nominal 33.33, so it converges immediately instead of
                // spending the first shots wrong.
                const period = (now - this.tickWall) / (tick - this.lastTick);
                this.msPerTickEma = this.msPerTickEma === null
                    ? period
                    : this.msPerTickEma * 0.8 + period * 0.2;
                this.lastTick = tick;
                this.tickWall = now;
            }
            // Clamped to one tick so a stalled stream cannot run the animation past the
            // impact it is supposed to be waiting for.
            const frac = this.msPerTickEma
                ? Math.min(1, (now - this.tickWall) / this.msPerTickEma)
                : 0;
            this.timer = (tick - this.castTick + frac) * MS_PER_TICK;
        }

        if (this.timer >= this.duration) {
            this.isFinished = true;
            this.shakeX = 0;
            this.shakeY = 0;
            return;
        }

        // --- SCREEN SHAKE ---
        // Summed over every shot that has already fired rather than tracking "the current
        // one", so overlapping recoils reinforce instead of one cancelling the next. Each
        // contribution decays independently over shakeDecayMs.
        let intensity = 0;
        for (let i = 0; i < this.shots; i++) {
            const since = this.timer - this.shotTime(i);
            if (since < 0) break;                       // shots are in time order
            if (since >= this.shakeDecayMs) continue;
            intensity += this.shakeIntensity * (1 - since / this.shakeDecayMs);
        }

        if (intensity > 0) {
            // Capped so a long burst cannot stack into something worse than a single big
            // shot -- the whole point of lowering shakeIntensity was readability.
            const capped = Math.min(intensity, this.shakeIntensity * 1.6);
            this.shakeX = (Math.random() * 2 - 1) * capped;
            this.shakeY = (Math.random() * 2 - 1) * capped;
        } else {
            this.shakeX = 0;
            this.shakeY = 0;
        }
    }

    /**
     * Reproduces SnipeEffect.SelectTargets for shots 1..N-1. See the class comment for why
     * this rule lives in two places.
     */
    resolveTargets(state) {
        const units = (state && state.units) || [];
        const enemies = units.filter(u => u.side !== this.side);
        const chosen = [];
        const taken = new Set();

        const first = enemies.find(u => u.instanceId === this.targetId);
        if (first) { chosen.push(first); taken.add(first.instanceId); }

        while (chosen.length < this.shots) {
            let next = null;
            for (const u of enemies) {
                if (taken.has(u.instanceId)) continue;
                if (typeof u.definitionId === 'string' && u.definitionId.startsWith('wall')) continue;
                if (next === null
                    || u.maxHealth > next.maxHealth
                    || (u.maxHealth === next.maxHealth && u.position < next.position)) {
                    next = u;
                }
            }
            if (!next) break;
            chosen.push(next);
            taken.add(next.instanceId);
        }
        return chosen;
    }

    draw(ctx, state) {
        // Resolve once, at the moment the cast is drawn, so the picture matches the board the
        // engine chose from rather than drifting as units die mid-burst.
        if (this.targets === null) {
            this.targets = this.resolveTargets(state);
            this.lastKnown = this.targets.map(u => ({ x: u.position, y: u.yPosition + 10 }));
            if (this.targets.length === 0) {
                this.lastKnown = [{ x: this.lastKnownX, y: this.lastKnownY }];
            }
        }

        const units = (state && state.units) || [];
        const shotsToDraw = Math.max(1, Math.min(this.shots, this.lastKnown.length));

        // Track every target that is still alive, so the barrel follows and a tracer that
        // has not fired yet stays pointed at a moving unit.
        for (let i = 0; i < this.targets.length; i++) {
            const live = units.find(u => u.instanceId === this.targets[i].instanceId);
            if (live) this.lastKnown[i] = { x: live.position, y: live.yPosition + 10 };
        }

        // The gun points at whichever shot is next up (or the last one once the burst is
        // done), which is what makes it visibly sweep across the field during a level-3 burst.
        let aimIndex = 0;
        for (let i = 0; i < shotsToDraw; i++) {
            if (this.timer >= this.shotTime(i)) aimIndex = Math.min(i + 1, shotsToDraw - 1);
        }
        const aim = this.lastKnown[aimIndex] || { x: this.lastKnownX, y: this.lastKnownY };
        this.lastKnownX = aim.x;
        this.lastKnownY = aim.y;

        const vx = aim.x - this.startX;
        const vy = aim.y - this.startY;
        const angle = Math.atan2(vy, vx);

        const barrelX = this.startX + (Math.cos(angle) * this.barrelDist);
        const barrelY = this.startY + (Math.sin(angle) * this.barrelDist);

        ctx.save();

        // --- PHASE 1: THE LOCK-ON ---
        if (this.timer < this.lockOnMs && this.hasLaser) {
            const t = this.targets[0];
            if (t) {
                const live = units.find(u => u.instanceId === t.instanceId);
                const direction = (live ? live.side : t.side) == 1 ? -1 : 1;
                ctx.beginPath();
                ctx.moveTo(barrelX, barrelY);
                ctx.lineTo(aim.x + 10 * direction, aim.y + 10);
                ctx.strokeStyle = `rgba(255, 0, 0, 0.8)`;
                ctx.lineWidth = 2;
                ctx.stroke();
            }
        }

        // --- PHASE 2: THE BULLETS ---
        // Every shot in flight is drawn, so a level-3 burst has several tracers crossing at
        // once rather than one at a time.
        for (let i = 0; i < shotsToDraw; i++) {
            const since = this.timer - this.shotTime(i);
            if (since < 0) break;
            if (since > this.bulletDuration) continue;

            const dest = this.lastKnown[i];
            if (!dest) continue;

            const shotAngle = Math.atan2(dest.y - this.startY, dest.x - this.startX);
            const bx = this.startX + Math.cos(shotAngle) * this.barrelDist;
            const by = this.startY + Math.sin(shotAngle) * this.barrelDist;

            const p = since / this.bulletDuration;
            const cx = bx + (dest.x - bx) * p;
            const cy = by + (dest.y - by) * p;

            ctx.beginPath();
            ctx.moveTo(cx - (dest.x - bx) * 0.1, cy - (dest.y - by) * 0.1);
            ctx.lineTo(cx, cy);
            ctx.strokeStyle = this.bulletColor;
            ctx.lineWidth = 6;
            ctx.stroke();

            // Muzzle flash on the frame the shot leaves the barrel -- the cheap cue that
            // makes ten shots read as ten distinct events rather than a stream.
            if (since < 45) {
                ctx.beginPath();
                ctx.arc(bx, by, 7 * (1 - since / 45), 0, Math.PI * 2);
                ctx.fillStyle = 'rgba(255, 240, 180, 0.85)';
                ctx.fill();
            }
        }

        // --- DRAW THE SNIPER RIFLE ---
        const imgKey = this.level === 1 ? 'snipe' : `snipe_${this.level}`;
        const rifleImg = loader.assets['gadgets'][imgKey] || loader.assets['gadgets']['snipe'];

        if (rifleImg) {
            ctx.translate(this.startX, this.startY);
            ctx.rotate(angle);

            if (aim.x < this.startX) {
                ctx.scale(1, -1);
            }

            // The magic offset! By pulling the image UP by the pivotY,
            // the mathematical center of rotation perfectly lines up with the barrel.
            ctx.drawImage(rifleImg, 0, -this.pivotY, this.gunWidth, this.gunHeight);
        }

        ctx.restore();
    }
}
