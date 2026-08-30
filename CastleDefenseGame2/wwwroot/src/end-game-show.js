import view from './view.js';

// What the battlefield does once the match is over.
//
// The server stops sending state at the final tick, so the game-over screen re-renders one
// FROZEN state every frame. That is what leaves the field cluttered: a hazard animator
// decides it has finished by consulting the state, and a state that never changes never
// tells it to stop -- so Wave and Fire (and the screen shake a Wave carries) run forever.
// Unit statuses have the same problem from the other direction: they sit in the frozen
// state, so the tint keeps applying and VisualUnit keeps spawning particles for them.
//
// Everything below is client-side choreography written onto the VisualUnits' endGame*
// fields. The state itself is left alone apart from the deliberate status wipe.

const EFFECT_EXPIRY = 2.0;          // seconds after game over before lingering effects are cut

// --- Losers ---------------------------------------------------------------------------
const RUN_SPEED_MIN = 180;          // px/sec -- varied per unit so they don't flee as a block
const RUN_SPEED_MAX = 260;
const RUN_DELAY_MAX = 0.5;          // stagger the panic slightly
const OFF_MAP_MARGIN = 150;         // px past the map edge before a runaway is dropped

// --- Winners --------------------------------------------------------------------------
// Same routine as the main-menu wanderers (see menu-meander.js): a beat that flips the
// facing, small hops with a bigger jump every third beat, and a hold after the big one so
// the next turn does not land on the same frame the unit does.
const DANCE_DELAY_MIN = 0.15;       // the party starts raggedly, not in lockstep
const DANCE_DELAY_MAX = 2.2;
const DANCE_BEAT = 0.42;
const HOP_HEIGHT = 40;
const HOP_TIME = 0.55;
const BIG_JUMP_EVERY = 3;
const BIG_JUMP_HOLD = 0.3;
const SMALL_HOP_SCALE = 0.5;
const BIG_JUMP_SCALE = 1.5;
const SWAY_SPEED = 45;

// --- Draw -----------------------------------------------------------------------------
const LOOK_MIN = 0.7;               // seconds between glances while standing about confused
const LOOK_MAX = 2.4;

const randRange = (min, max) => min + Math.random() * (max - min);

class EndGameShow {
    constructor() {
        this.reset();
    }

    reset() {
        this.active = false;
        this.elapsed = 0;
        this.effectsCleared = false;
        this.roles = new Map();     // instanceId -> behaviour record
        this.lastTime = 0;
    }

    // winnerSide: 1 or 2, or 0 for a draw.
    start(state, winnerSide) {
        this.reset();
        if (!state || !state.units) return;

        this.active = true;
        this.lastTime = performance.now();

        for (const unit of state.units) {
            this.roles.set(unit.instanceId, this.assignRole(unit, winnerSide));
        }
    }

    assignRole(unit, winnerSide) {
        const facing = unit.side === 1 ? 1 : -1;
        const base = { offsetX: 0, offsetY: 0, facing, delay: 0, gone: false };

        if (winnerSide === 0) {
            // Nobody won: they mill about looking for someone to tell them what happened.
            return { ...base, kind: 'confused', lookTimer: randRange(LOOK_MIN, LOOK_MAX) };
        }

        if (unit.side === winnerSide) {
            return {
                ...base, kind: 'dance',
                delay: randRange(DANCE_DELAY_MIN, DANCE_DELAY_MAX),
                beatTimer: 0, step: 0,
                hopTimer: 0, hopHeight: HOP_HEIGHT, hopDuration: HOP_TIME,
                bigBeat: false, holding: false
            };
        }

        return {
            ...base, kind: 'flee',
            delay: randRange(0, RUN_DELAY_MAX),
            speed: randRange(RUN_SPEED_MIN, RUN_SPEED_MAX),
            // Away from the enemy: side 1 advanced from the left, so it retreats left.
            direction: unit.side === 1 ? -1 : 1
        };
    }

    // Call once per frame, BEFORE drawing the state.
    update(state) {
        if (!this.active || !state || !state.units) return;

        const now = performance.now();
        // Clamp the step so a backgrounded tab doesn't teleport everyone on its first frame back.
        const dt = Math.min((now - this.lastTime) / 1000, 0.1);
        this.lastTime = now;
        this.elapsed += dt;

        if (!this.effectsCleared && this.elapsed >= EFFECT_EXPIRY) {
            this.clearLingeringEffects(state);
        }

        for (const unit of state.units) {
            const role = this.roles.get(unit.instanceId);
            if (!role) continue;

            this.advance(role, dt, unit);

            const visualUnit = view.visualUnits[unit.instanceId];
            if (!visualUnit) continue;      // drawGameState creates it on its first frame

            visualUnit.endGameOffsetX = role.offsetX;
            visualUnit.endGameOffsetY = role.offsetY;
            visualUnit.facingOverride = role.facing;
            if (role.gone) visualUnit.hidden = true;
        }
    }

    // The hard cut-off. Anything still running two seconds after the final tick is a thing
    // that was never going to stop on its own.
    clearLingeringEffects(state) {
        this.effectsCleared = true;

        view.animationManager.activeAnimations = [];
        view.animationManager.shakeX = 0;
        view.animationManager.shakeY = 0;

        for (const unit of state.units) unit.statuses = [];
        for (const id in view.visualUnits) view.visualUnits[id].particles = [];
    }

    advance(role, dt, unit) {
        if (role.delay > 0) {
            role.delay -= dt;
            if (role.delay > 0) return;
        }

        switch (role.kind) {
            case 'flee': {
                role.facing = role.direction;
                role.offsetX += role.speed * role.direction * dt;

                // Measured against the MAP rather than the viewport: the player can pan the
                // camera anywhere on this screen, and a unit blinking out mid-view because
                // the camera happened to be elsewhere would look like a bug.
                const worldX = unit.position + role.offsetX;
                if (worldX + unit.width < -OFF_MAP_MARGIN || worldX > view.MAP_WIDTH + OFF_MAP_MARGIN) {
                    role.gone = true;
                }
                break;
            }

            case 'dance':
                this.advanceDance(role, dt);
                break;

            case 'confused':
                role.lookTimer -= dt;
                if (role.lookTimer <= 0) {
                    role.facing *= -1;
                    role.lookTimer = randRange(LOOK_MIN, LOOK_MAX);
                }
                break;
        }
    }

    advanceDance(role, dt) {
        role.beatTimer -= dt;

        // The tail of a big-jump beat is a hold: the jump has landed and the unit stands
        // still until the next beat turns it.
        role.holding = role.bigBeat && role.beatTimer <= BIG_JUMP_HOLD;

        if (role.hopTimer > 0) {
            role.hopTimer -= dt;
            const t = 1 - Math.max(role.hopTimer, 0) / role.hopDuration;   // 0 -> 1
            role.offsetY = -role.hopHeight * (1 - Math.pow(2 * t - 1, 2)); // parabola
            if (role.hopTimer <= 0) role.offsetY = 0;
        }

        if (role.beatTimer <= 0) {
            role.facing *= -1;

            const big = role.step % BIG_JUMP_EVERY === BIG_JUMP_EVERY - 1;
            role.hopHeight = HOP_HEIGHT * (big ? BIG_JUMP_SCALE : SMALL_HOP_SCALE);
            role.hopDuration = big ? HOP_TIME : HOP_TIME * 0.6;
            role.hopTimer = role.hopDuration;

            role.bigBeat = big;
            role.beatTimer = big ? HOP_TIME + BIG_JUMP_HOLD : DANCE_BEAT;
            role.holding = false;
            role.step++;
        }

        // Sways with the beat rather than travelling: the facing flips every beat, so the
        // steps cancel out and the unit stays roughly where it won.
        if (!role.holding) role.offsetX += SWAY_SPEED * role.facing * dt;
    }
}

const endGameShow = new EndGameShow();
export default endGameShow;
