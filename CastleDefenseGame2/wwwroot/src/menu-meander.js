import loader from './asset-loader.js';
import view from './view.js';

// Background life for the main menu: units wander in from off-screen, mill about, and
// wander off again. Purely decorative -- there is no game state, no combat and no server
// involved, so these are plain objects rather than VisualUnits.

// --- Pace ---------------------------------------------------------------------------
// The engine runs at 30 ticks/sec (GameEngine.TICKS_PER_SECOND), so a "5px per tick"
// stroll is 150 logical px/sec. Everything here is per-SECOND and scaled by delta time,
// so the animation runs at the same speed on any refresh rate.
const TICKS_PER_SECOND = 30;
const WALK_SPEED = 3 * TICKS_PER_SECOND;
const DRIFT_SPEED = 22;              // vertical amble, px/sec

// --- Where they walk ----------------------------------------------------------------
// In game, SpawnUnit puts a unit's feet at `360 + rand(0,51)` (GameEngine.cs). The menu
// widens that band by 100px each way so the crowd fills more of the screen. The lower
// bound is clamped to the map floor so nobody strolls off the bottom of the map.
const GAME_FEET_MIN = 360;
const GAME_FEET_MAX = 410;
// Asymmetric on purpose: a full 100px upward put units uncomfortably high up the map,
// so the band only reaches 50px above the in-game minimum while keeping the full 100
// below it.
const Y_SPREAD_UP = 50;
const Y_SPREAD_DOWN = 100;
const MAP_FLOOR = 500;               // logical canvas height (view.resize)
const FEET_MIN = GAME_FEET_MIN - Y_SPREAD_UP;
const FEET_MAX = Math.min(GAME_FEET_MAX + Y_SPREAD_DOWN, MAP_FLOOR);

// --- Scenes ---------------------------------------------------------------------------
// A scene is a set of screens that SHARE one background, and therefore share one set of
// wanderers. Navigating between screens inside a scene leaves everybody exactly where they
// were; only moving to a different scene starts a fresh crowd. The collection screens all
// sit on the purple map that collection.js draws -- unit-info and the gadget screens never
// redraw it, they just inherit it -- so they are one scene.
const SCENES = {
    'main-menu': {
        screens: ['main-menu'],
        feetMin: FEET_MIN
    },
    'collection': {
        screens: ['collection', 'unit-info', 'map-info', 'gadget-categories',
                  'gadget-info-offensive', 'gadget-info-tactical', 'gadget-info-signature'],
        // That map sits lower on the screen, so its walkable band starts another 50px down.
        feetMin: FEET_MIN + 50
    }
};

// --- Population ---------------------------------------------------------------------
const MAX_UNITS = 3;
// Lowered alongside the slower walk: units now linger far longer, so the old rate filled
// the screen to the cap almost immediately and every arrival looked like a queue.
const SPAWN_CHANCE_PER_SEC = 0.12;
const TIER_8_WEIGHT = 0.3;           // the big ones are shy, so they turn up less often
const CULL_MARGIN = 120;             // px past the edge before a unit is forgotten
const MAX_LIFETIME = 120;            // seconds before a wanderer decides to head home

// --- Idle behaviour -----------------------------------------------------------------
// Two decision cadences. A walking unit re-thinks every DECIDE_*; a standing one fidgets
// on the quicker PAUSE_DECIDE_* beat, because standing still is where the looking-around
// happens. A pause has no fixed length -- it lasts until the unit decides to move off,
// which at a ~28% chance per beat averages around five seconds and often runs much longer.
const DECIDE_MIN = 1.2;              // seconds between "what shall I do next"
const DECIDE_MAX = 3.0;
const PAUSE_DECIDE_MIN = 0.8;
const PAUSE_DECIDE_MAX = 2.0;
const RESUME_WALK_CHANCE = 0.22;
const HOP_HEIGHT = 40;
const HOP_TIME = 0.55;
// Sometimes a turn is only a glance over the shoulder -- the unit turns back a beat later.
const DOUBLE_TURN_CHANCE = 0.35;
const DOUBLE_TURN_DELAY = 1.0;       // seconds before turning back

// --- Dance --------------------------------------------------------------------------
// A rare little routine: the unit shimmies back and forth on the spot, mixing quick low
// hops with a bigger jump every third beat. Rare by design -- it should read as a treat
// you happen to catch, not a thing the menu does constantly.
const DANCE_CHANCE = 0.007;          // per decision, while walking or standing
const DANCE_MIN = 4.5;               // seconds
const DANCE_MAX = 5.5;
const DANCE_BEAT = 0.42;             // seconds per step
const DANCE_SWAY_SPEED = 45;         // px/sec, half a walk -- it travels almost nowhere
const DANCE_BIG_JUMP_EVERY = 3;      // beats
const DANCE_SMALL_HOP = 0.5;         // multiples of HOP_HEIGHT
const DANCE_BIG_JUMP = 1.5;
// A big jump lasts HOP_TIME, which is LONGER than a normal beat -- so on the old fixed
// beat the next turn fired while the unit was still in the air and read as a snap the
// instant it landed. A big-jump beat now runs the length of the jump plus this hold, so
// the unit lands, stands still for a moment, and only then turns.
const DANCE_BIG_JUMP_HOLD = 0.3;     // seconds of stillness after landing

// --- Shyness (tier 8) ---------------------------------------------------------------
// A shy unit turns around once it has come this far past the edge. Most of the time it
// barely pokes its nose in; occasionally it plucks up the courage to come further, but
// never past SHY_MAX_DEPTH.
const SHY_MAX_DEPTH = 200;
const SHY_TIMID_DEPTH = 70;
const SHY_TIMID_CHANCE = 0.7;
const SHY_STARE_MIN = 0.4;           // beat spent looking before fleeing
const SHY_STARE_MAX = 1.4;

const randRange = (min, max) => min + Math.random() * (max - min);

class MenuMeander {
    constructor() {
        this.units = [];
        this.frameId = null;
        this.lastTime = 0;
        this.pool = null;
        this.sceneName = null;
        this.scene = null;
    }

    // Every roster unit that actually has a sprite loaded. Built once, on first start.
    buildPool() {
        const data = loader.assets.unitData || {};
        return Object.values(data)
            .map(stats => ({
                id: stats.id,
                tier: parseInt(stats.tier, 10),
                width: parseInt(stats.width, 10) || 50,
                height: parseInt(stats.height, 10) || 50
            }))
            .filter(u => u.id && loader.assets[u.id]);
    }

    // Call from every screen's init. Idempotent WITHIN a scene: a screen that belongs to
    // the scene already running is a no-op, which is what makes the crowd persist across
    // Collection -> UnitInfo -> GadgetInfo without anybody being respawned or teleported.
    start(sceneName) {
        const scene = SCENES[sceneName];
        if (!scene) return console.error(`Meander scene ${sceneName} not found!`);

        if (this.sceneName === sceneName && this.frameId !== null) return;

        // Never leave a second loop running.
        this.stop();

        if (!this.pool) this.pool = this.buildPool();
        if (!this.pool.length) return;

        this.sceneName = sceneName;
        this.scene = scene;
        this.units = [];
        this.lastTime = performance.now();
        this.frameId = requestAnimationFrame(this.loop);
    }

    stop() {
        if (this.frameId !== null) cancelAnimationFrame(this.frameId);
        this.frameId = null;
        this.sceneName = null;
        this.scene = null;
        this.units = [];
    }

    loop = () => {
        // The router swaps out #app-container's contents on navigation, so the scene
        // having no screen on show is the signal to stop. Self-cancelling beats asking
        // every screen that might navigate away to remember to call stop(). The swap is
        // synchronous, so a move WITHIN a scene never leaves a frame with neither screen
        // present and never trips this.
        if (!this.scene.screens.some(id => document.getElementById(id))) {
            this.frameId = null;
            this.sceneName = null;
            this.scene = null;
            return;
        }

        const now = performance.now();
        // Clamp the step: a backgrounded tab resumes with a huge gap, which would otherwise
        // teleport everyone across the screen in one frame.
        const dt = Math.min((now - this.lastTime) / 1000, 0.1);
        this.lastTime = now;

        this.update(dt);

        view.clear();
        view.draw();
        for (const unit of this.units) view.drawMenuUnit(unit);

        this.frameId = requestAnimationFrame(this.loop);
    }

    update(dt) {
        const screenWidth = view.logicalScreenWidth || 800;

        if (this.units.length < MAX_UNITS && Math.random() < SPAWN_CHANCE_PER_SEC * dt) {
            this.spawn(screenWidth);
        }

        for (let i = this.units.length - 1; i >= 0; i--) {
            const unit = this.units[i];
            this.updateUnit(unit, dt, screenWidth);

            unit.age += dt;

            // At the slower walk a unit that keeps turning around can easily outlive
            // MAX_LIFETIME while still in plain view, and deleting it there would pop it
            // out of existence on screen. Send it to the nearest edge instead and let the
            // normal off-screen cull collect it.
            if (!unit.fleeing && unit.age > MAX_LIFETIME) {
                unit.fleeing = true;
                unit.doubleTurnTimer = 0;
                unit.state = 'walk';
                unit.facing = (unit.x + unit.width / 2) < screenWidth / 2 ? -1 : 1;
            }

            const goneLeft = unit.x + unit.width < -CULL_MARGIN;
            const goneRight = unit.x > screenWidth + CULL_MARGIN;

            // Failsafe only -- a unit should always leave via the edges long before this.
            const strandedTooLong = unit.age > MAX_LIFETIME * 2;

            if (goneLeft || goneRight || strandedTooLong) this.units.splice(i, 1);
        }
    }

    spawn(screenWidth) {
        // Weighted pick so tier 8s stay a treat rather than the norm.
        const candidates = this.pool.filter(u => u.tier !== 8 || Math.random() < TIER_8_WEIGHT);
        if (!candidates.length) return;

        const def = candidates[Math.floor(Math.random() * candidates.length)];
        const fromLeft = Math.random() < 0.5;
        const feet = randRange(this.scene.feetMin, FEET_MAX);
        const shy = def.tier === 8;

        const unit = {
            definitionId: def.id,
            width: def.width,
            height: def.height,
            tier: def.tier,

            // Start fully off-screen so they walk INTO view rather than popping in.
            x: fromLeft ? -def.width : screenWidth,
            y: feet - def.height,
            targetFeet: feet,

            facing: fromLeft ? 1 : -1,
            entryFacing: fromLeft ? 1 : -1,

            state: 'walk',
            stateTimer: randRange(DECIDE_MIN, DECIDE_MAX),
            hopTimer: 0,
            hopOffset: 0,
            hopHeight: HOP_HEIGHT,
            hopDuration: HOP_TIME,
            doubleTurnTimer: 0,
            danceTimer: 0,
            beatTimer: 0,
            danceStep: 0,
            bigJumpBeat: false,
            danceHolding: false,
            age: 0,

            shy,
            // How far in this one dares to come before it turns tail.
            peekDepth: shy
                ? (Math.random() < SHY_TIMID_CHANCE
                    ? randRange(20, SHY_TIMID_DEPTH)
                    : randRange(SHY_TIMID_DEPTH, SHY_MAX_DEPTH))
                : 0,
            fleeing: false
        };

        this.units.push(unit);
    }

    updateUnit(unit, dt, screenWidth) {
        // --- Shy units: poke in, have a look, leave -------------------------------------
        // This commits in ONE step -- turn round, hesitate a beat, then retreat -- and
        // latches `fleeing` immediately. An earlier version parked the unit in a 'stare'
        // state waiting for a timer, which the generic decision block below would reset
        // before the flee ever fired; the unit then crept inward a frame at a time and
        // sailed well past its limit.
        if (unit.shy && !unit.fleeing) {
            // Depth = how far the unit has pushed past the edge it came in through.
            const depth = unit.entryFacing === 1
                ? unit.x + unit.width
                : screenWidth - unit.x;

            if (depth >= unit.peekDepth) {
                // Pin them exactly on their limit so one long frame cannot carry them past it.
                unit.x -= (depth - unit.peekDepth) * unit.entryFacing;
                unit.fleeing = true;
                unit.doubleTurnTimer = 0;          // a pending glance must not turn it back inward
                unit.facing = -unit.entryFacing;
                unit.state = 'pause';              // a beat of hesitation before retreating
                unit.stateTimer = randRange(SHY_STARE_MIN, SHY_STARE_MAX);
            }
        }

        unit.stateTimer -= dt;

        // --- Hop ------------------------------------------------------------------------
        // Runs alongside whatever else is happening, so a unit can hop while strolling.
        if (unit.hopTimer > 0) {
            unit.hopTimer -= dt;
            const t = 1 - Math.max(unit.hopTimer, 0) / unit.hopDuration;   // 0 -> 1
            unit.hopOffset = -unit.hopHeight * (1 - Math.pow(2 * t - 1, 2));  // parabola
            if (unit.hopTimer <= 0) unit.hopOffset = 0;
        }

        // --- Double turn ----------------------------------------------------------------
        // A pending glance turns them back regardless of what else they decided meanwhile.
        if (unit.doubleTurnTimer > 0) {
            unit.doubleTurnTimer -= dt;
            if (unit.doubleTurnTimer <= 0) unit.facing *= -1;
        }

        // --- Pick something new to do ---------------------------------------------------
        // chooseAction handles standing and walking alike. It deliberately does NOT force a
        // paused unit back into walking: most of what it can pick keeps the unit standing,
        // which is what lets one pause run several beats of turning and looking about.
        if (unit.state === 'dance') {
            this.updateDance(unit, dt);
        } else if (unit.stateTimer <= 0) {
            this.chooseAction(unit);
        }

        // --- Move -----------------------------------------------------------------------
        if (unit.state === 'walk') {
            unit.x += WALK_SPEED * unit.facing * dt;
        } else if (unit.state === 'dance' && !unit.danceHolding) {
            // Sways with the beat rather than travelling: facing flips every beat, so the
            // steps cancel out and the unit ends up roughly where it started. Movement
            // stops during the post-jump hold so the pause is a real one.
            unit.x += DANCE_SWAY_SPEED * unit.facing * dt;
        }

        // Vertical amble happens whether walking or standing, so a pause can still drift.
        const feet = unit.y + unit.height;
        const diff = unit.targetFeet - feet;
        if (Math.abs(diff) > 1) {
            unit.y += Math.sign(diff) * Math.min(DRIFT_SPEED * dt, Math.abs(diff));
        }
    }

    chooseAction(unit) {
        // A unit on its way out has made up its mind: it keeps going, and only ever pauses.
        if (unit.fleeing) {
            if (Math.random() < 0.15) {
                unit.state = 'pause';
                unit.stateTimer = randRange(0.3, 0.9);
            } else {
                unit.state = 'walk';
                unit.stateTimer = randRange(DECIDE_MIN, DECIDE_MAX);
            }
            return;
        }

        const roll = Math.random();

        // --- Standing about --------------------------------------------------------------
        // Only the first branch gets them moving again; everything else is something done
        // while stopped, so they can turn, look, turn back and hop without walking off.
        if (unit.state === 'pause') {
            unit.stateTimer = randRange(PAUSE_DECIDE_MIN, PAUSE_DECIDE_MAX);

            if (roll < RESUME_WALK_CHANCE) {
                unit.state = 'walk';
                unit.stateTimer = randRange(DECIDE_MIN, DECIDE_MAX);
            } else if (roll < 0.50) {
                this.turnAround(unit);                 // have a look the other way
            } else if (roll < 0.68) {
                this.startHop(unit, HOP_HEIGHT, HOP_TIME);         // hop on the spot
            } else if (roll < 0.72) {
                unit.targetFeet = randRange(this.scene.feetMin, FEET_MAX);   // shuffle up or down
            } else if (roll < 0.72 + DANCE_CHANCE) {
                this.startDance(unit);
            }
            // else: just keep standing there
            return;
        }

        // --- Walking ---------------------------------------------------------------------
        unit.stateTimer = randRange(DECIDE_MIN, DECIDE_MAX);

        if (roll < 0.55) {
            unit.state = 'pause';                      // stop and stand a while
            unit.stateTimer = randRange(PAUSE_DECIDE_MIN, PAUSE_DECIDE_MAX);
        } else if (roll < 0.67) {
            this.turnAround(unit);                     // turn around
        } else if (roll < 0.79) {
            this.startHop(unit, HOP_HEIGHT, HOP_TIME);  // hop without breaking stride
        } else if (roll < 0.83) {
            unit.targetFeet = randRange(this.scene.feetMin, FEET_MAX);   // amble up or down the map
        } else if (roll < 0.83 + DANCE_CHANCE) {
            this.startDance(unit);
        }
        // else: carry on as we were
    }

    startHop(unit, height, duration) {
        unit.hopHeight = height;
        unit.hopDuration = duration;
        unit.hopTimer = duration;
    }

    startDance(unit) {
        unit.state = 'dance';
        unit.danceTimer = randRange(DANCE_MIN, DANCE_MAX);
        unit.beatTimer = 0;              // first beat lands immediately
        unit.danceStep = 0;
        unit.bigJumpBeat = false;
        unit.danceHolding = false;
        // A glance scheduled a moment ago would fight the beat for control of the facing.
        unit.doubleTurnTimer = 0;
    }

    updateDance(unit, dt) {
        unit.danceTimer -= dt;
        unit.beatTimer -= dt;

        // The tail of a big-jump beat is a hold: the jump has landed and the unit stands
        // still until the next beat turns it.
        unit.danceHolding = unit.bigJumpBeat && unit.beatTimer <= DANCE_BIG_JUMP_HOLD;

        if (unit.beatTimer <= 0) {
            unit.facing *= -1;                                   // back and forth

            const big = unit.danceStep % DANCE_BIG_JUMP_EVERY === DANCE_BIG_JUMP_EVERY - 1;
            this.startHop(
                unit,
                HOP_HEIGHT * (big ? DANCE_BIG_JUMP : DANCE_SMALL_HOP),
                big ? HOP_TIME : HOP_TIME * 0.6
            );

            // Give a big jump room to land before the next turn; small hops finish well
            // inside a normal beat and need no extra time.
            unit.bigJumpBeat = big;
            unit.beatTimer = big ? HOP_TIME + DANCE_BIG_JUMP_HOLD : DANCE_BEAT;
            unit.danceHolding = false;
            unit.danceStep++;
        }

        if (unit.danceTimer <= 0) {
            // Stand and catch their breath before deciding anything else.
            unit.state = 'pause';
            unit.stateTimer = randRange(PAUSE_DECIDE_MIN, PAUSE_DECIDE_MAX);
        }
    }

    turnAround(unit) {
        unit.facing *= -1;
        // Don't stack glances: only schedule a turn-back when none is already pending,
        // otherwise a run of turns could leave the unit oscillating.
        if (unit.doubleTurnTimer <= 0 && Math.random() < DOUBLE_TURN_CHANCE) {
            unit.doubleTurnTimer = DOUBLE_TURN_DELAY;
        }
    }
}

const meander = new MenuMeander();
export default meander;
