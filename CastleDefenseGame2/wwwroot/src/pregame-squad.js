import loader from './asset-loader.js';

// The tier-1 units milling around each castle during the pre-game countdown, hopping on the
// spot as if itching to go. Purely decorative -- there is no server unit behind any of them.
//
// THEY ARE NOT THE REAL UNITS, BUT THEY ARE THE SAME UNITS. The engine gives each side
// GameEngine.OpeningSquadSize free tier-1 units over the first seconds of the battle
// (ticks 1, 31, 61, ...). As each real one spawns, one of these is retired, so the crowd
// outside the castle visibly empties onto the field instead of vanishing at the whistle and
// being replaced by a stream from nowhere.
//
// The count is DERIVED from the authoritative tick rather than counted down by a local
// timer, so it cannot drift out of step with the spawns it is standing in for.

const SQUAD_SIZE = 5;              // must match GameEngine.OpeningSquadSize
const TICKS_PER_SECOND = 30;       // must match GameEngine.TICKS_PER_SECOND

// Where they wait: AROUND the castle, deliberately overlapping it.
//
// The castle sprites are 200x200 drawn at x=50 and mirrored from x=1950 (view.drawCastle),
// so they occupy 50..250 and 1750..1950. The bands below bracket that with a little ground
// either side, and the squad is drawn AFTER the castles, so the crowd stands in front of
// the walls rather than disappearing behind them.
//
// THESE ARE THE SPANS THE SPRITES OCCUPY, not the range of x values. x is a sprite's LEFT
// edge, so each band is shortened by one unit width to land the whole 50px sprite inside
// it; taking 1725..1975 as left edges instead would hang player 2's squad 25px off the
// right edge of the map, where the camera cannot follow.
//
// Real units spawn with their leading edge at 100 / 1900, inside these bands, so a
// replacement emerges from roughly where the crowd was standing.
const MAP_WIDTH = 2000;
const UNIT_SIZE = 50;
const P1_SPAN_MIN = 25, P1_SPAN_MAX = 275;
const P2_SPAN_MIN = MAP_WIDTH - P1_SPAN_MAX, P2_SPAN_MAX = MAP_WIDTH - P1_SPAN_MIN;

const P1_MIN = P1_SPAN_MIN;
const P1_MAX = P1_SPAN_MAX - UNIT_SIZE;
const P2_MIN = P2_SPAN_MIN;
const P2_MAX = P2_SPAN_MAX - UNIT_SIZE;

// Feet land in the same band GameEngine.SpawnUnit uses (360 + rand(0,51)), so the squad
// stands on the same ground the battle is fought on.
const FEET_MIN = 360, FEET_MAX = 410;

// --- Hop ------------------------------------------------------------------------------
// Same parabola as the main-menu wanderers (menu-meander.js): up and down over HOP_TIME,
// with a wait in between so the group looks restless rather than synchronised.
const HOP_HEIGHT = 26;
const HOP_TIME = 0.42;
const WAIT_MIN = 0.5;
const WAIT_MAX = 2.6;

const randRange = (min, max) => min + Math.random() * (max - min);

class PreGameSquad {
    constructor() {
        this.units = [];
        this.gameId = null;
        this.key = null;
    }

    /// Build both sides' squads for this game. Idempotent per game id AND per pair of teams,
    /// so the frame loop can call it every frame without rebuilding (and re-randomising) the
    /// crowd -- but a team that was not known when the crowd was first built still corrects
    /// itself the moment it arrives.
    ///
    /// THE TEAMS ARE PART OF THE KEY BECAUSE ONE OF THEM CAN BE A LIE AT BUILD TIME.
    /// `TeamColour.Black` is 0, so an unassigned PlayerState.Team serialises as Black rather
    /// than as anything obviously missing. In multiplayer P1 joins first and is sent the
    /// state immediately, at which point P2 has no team yet -- so P1's copy of P2 reads as
    /// Black, and P1 alone saw the opponent's crowd as Black tier-1s for the whole game while
    /// P2 saw both sides correctly. Keying on the game id alone cached that first wrong
    /// answer forever, because the real units carry their own definitionId and looked right,
    /// which is why only the pre-game crowd was affected.
    ensure(state, gameId) {
        const key = state ? `${gameId}:${state.player1?.team}:${state.player2?.team}` : gameId;
        if (this.key === key && this.units.length) return;
        this.key = key;
        this.gameId = gameId;
        this.units = [];
        if (!state) return;

        for (const side of [1, 2]) {
            const player = side === 1 ? state.player1 : state.player2;
            const team = loader.assets.teamList[player.team];
            const roster = loader.assets.unitList[team];
            if (!roster || !roster.length) continue;
            const tier1 = roster[0];

            for (let i = 0; i < SQUAD_SIZE; i++) {
                const x = side === 1 ? randRange(P1_MIN, P1_MAX) : randRange(P2_MIN, P2_MAX);
                this.units.push({
                    side,
                    definitionId: tier1,
                    x,
                    y: randRange(FEET_MIN, FEET_MAX) - UNIT_SIZE,
                    width: UNIT_SIZE,
                    height: UNIT_SIZE,
                    // Facing the enemy, like the real units do when they spawn.
                    facing: side === 1 ? 1 : -1,
                    hopOffset: 0,
                    hopTimer: 0,
                    waitTimer: randRange(0, WAIT_MAX),
                });
            }
        }
    }

    reset() {
        this.units = [];
        this.gameId = null;
        this.key = null;
    }

    /// deltaMs comes from the render loop, so the hop runs at the same speed on any refresh
    /// rate rather than per frame.
    update(deltaMs) {
        const dt = Math.min(deltaMs, 100) / 1000;   // clamped so a stalled tab does not leap
        for (const u of this.units) {
            if (u.hopTimer > 0) {
                u.hopTimer -= dt;
                const t = 1 - Math.max(u.hopTimer, 0) / HOP_TIME;        // 0 -> 1
                u.hopOffset = -HOP_HEIGHT * (1 - Math.pow(2 * t - 1, 2)); // parabola
                if (u.hopTimer <= 0) {
                    u.hopOffset = 0;
                    u.waitTimer = randRange(WAIT_MIN, WAIT_MAX);
                }
            } else {
                u.waitTimer -= dt;
                if (u.waitTimer <= 0) u.hopTimer = HOP_TIME;
            }
        }
    }

    /// How many of each side's squad are still waiting outside the castle: the full squad
    /// until the battle opens, then one fewer for every free unit the engine has spawned.
    /// Derived from the tick so it stays in step with the server without a second timer.
    remaining(state, inPreGame) {
        if (inPreGame) return SQUAD_SIZE;
        const tick = state ? state.currentTick : 0;
        if (tick < 1) return SQUAD_SIZE;
        const spawned = Math.min(SQUAD_SIZE, Math.floor((tick - 1) / TICKS_PER_SECOND) + 1);
        return Math.max(0, SQUAD_SIZE - spawned);
    }

    /// The units to draw this frame. Retires from the FRONT of each side's queue so the ones
    /// that leave are the ones nearest the field, which is the direction they run off in.
    visible(state, inPreGame) {
        const keep = this.remaining(state, inPreGame);
        if (keep >= SQUAD_SIZE) return this.units;
        if (keep <= 0) return [];
        const out = [];
        for (const side of [1, 2]) {
            const mine = this.units.filter(u => u.side === side);
            // Side 1 runs rightward, so its front is its highest x; side 2's is its lowest.
            mine.sort((a, b) => side === 1 ? a.x - b.x : b.x - a.x);
            for (let i = 0; i < keep && i < mine.length; i++) out.push(mine[i]);
        }
        return out;
    }
}

const preGameSquad = new PreGameSquad();
export default preGameSquad;
