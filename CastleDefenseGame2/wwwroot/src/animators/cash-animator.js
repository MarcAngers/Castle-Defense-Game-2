import loader from '../asset-loader.js';

/**
 * CASH DROP: a plane flies in from the far side of the map and releases its crates.
 *
 * Rewritten 2026-09-03. The old version had the crate simply materialise above the drop
 * point and float down, and at level 3 the EFFECT triggered a fresh animation on each of
 * its eight payouts -- nine animations for eight crates, which read on screen as one crate,
 * a pause, and then the rest. CashEffect now raises the animation exactly once per cast and
 * this class draws the whole run.
 *
 * THE TIMELINE IS SERVER-ANCHORED AND THE ANCHOR IS THE CSV. The engine pays out
 * `Delay` ticks after the cast, then every `CashEffect.PayoutIntervalTicks` for the
 * remaining level-3 crates. The numbers below have to describe that same schedule or the
 * money arrives while the crates are still in the air:
 *
 *     cast ──PLANE_MS──▶ release 1 ──CRATE_FALL_MS──▶ crate 1 lands ──TEXT_MS──▶ gone
 *                          └─PAYOUT_INTERVAL_MS─▶ release 2 ...  (level 3 only)
 *
 * so a crate lands at PLANE_MS + CRATE_FALL_MS = 5000ms, and the matching payout lands at
 * `Delay / 30 * 1000`ms. **Delay IS 150 ticks**, which is exactly 5000ms -- the money arrives
 * as the crate touches down. Those two numbers are a contract: shortening the fall or
 * lengthening the plane's run without moving `Delay` desynchronises the payout from the crate.
 */
export default class CashAnimator {
    // The plane's run-in, and the span of the fall. PLANE_MS is the 3 seconds the extra
    // cast delay was added to cover.
    static PLANE_MS = 3000;
    static CRATE_FALL_MS = 2000;
    static TEXT_MS = 1500;

    // MIRRORS CashEffect.PayoutIntervalTicks (10 ticks at 30 Hz). One crate per payout, on
    // the payout's own cadence -- change one and this must change too.
    static PAYOUT_INTERVAL_MS = 10 * (1000 / 30);

    static MAP_WIDTH = 2000;
    static GROUND_Y = 360;

    // The plane sprites are 209x120 and both face LEFT, so seat 1 (flying right-to-left)
    // draws them as-authored and seat 2 draws them mirrored.
    static PLANE_W = 209;
    static PLANE_H = 120;
    // Top of the sprite. 10 rather than the original 30: the plane reads as being properly
    // up in the sky rather than skimming the hilltops, and the crate's fall from its belly
    // gets 20px longer without the 2,000ms it takes changing -- so the payout stays anchored
    // to `Delay`. The top HUD overlaps this band on a landscape phone, but only over the
    // money box at the left edge, which the plane crosses for a fraction of its run.
    static PLANE_Y = 10;

    static CRATE_SIZE = 75;

    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.level = level;

        const C = CashAnimator;

        // Where the run ENDS: in front of the allied castle, ~100px forward of it. This is
        // the drop point for levels 1-2 and the LAST crate's drop point for level 3.
        this.dropX = this.side === 1 ? 300 : C.MAP_WIDTH - 300;

        // Enters from the opposite edge, fully off-map so it flies in rather than fading in.
        this.planeStartX = this.side === 1 ? C.MAP_WIDTH + C.PLANE_W : -C.PLANE_W;

        this.crateCount = this.level >= 3 ? 8 : 1;
        const dropWindowMs = (this.crateCount - 1) * C.PAYOUT_INTERVAL_MS;

        // ONE CONSTANT SPEED FOR THE WHOLE FLIGHT, solved so that the run-in takes PLANE_MS
        // and the LAST crate is released over dropX:
        //
        //     v = (planeStartX - releaseX0) / PLANE_MS          (the run-in)
        //     dropX = releaseX0 - v * dropWindowMs              (the last release)
        //  => releaseX0 = (dropX + f * planeStartX) / (1 + f),  f = dropWindowMs / PLANE_MS
        //
        // A plane that changed speed to stay over one spot would look wrong, and one that
        // held the level-1 speed through the level-3 drop window would travel 1,322px past
        // the castle and rain its crates off the edge of the map. Solving for the whole run
        // instead spreads the eight crates across ~835px of field, ending at the castle --
        // which is what "Raining Cash" should look like. The trade is that at level 3 the
        // plane's total crossing takes PLANE_MS + dropWindowMs, not PLANE_MS; the run-in
        // before the first crate is 3s at every level.
        const f = dropWindowMs / C.PLANE_MS;
        this.releaseX0 = (this.dropX + f * this.planeStartX) / (1 + f);
        this.planeVx = (this.releaseX0 - this.planeStartX) / C.PLANE_MS;

        // Per-crate release time and position, resolved up front so draw() stays arithmetic.
        this.crates = [];
        for (let i = 0; i < this.crateCount; i++) {
            const t = C.PLANE_MS + i * C.PAYOUT_INTERVAL_MS;
            this.crates.push({ releaseMs: t, x: this.planeStartX + this.planeVx * t });
        }

        // The per-crate payout. Reads the LEVEL'S OWN row rather than assuming level 3
        // shares level 2's: they happen to both be 1500 today, and a rebalance of one of
        // them should not silently mislabel the other.
        const key = this.level === 1 ? 'cash' : `cash_${this.level}`;
        const gadgetData = loader.assets.gadgetData[key] || loader.assets.gadgetData['cash'];
        this.amount = gadgetData ? (gadgetData.basevalue || gadgetData.BaseValue) : 100;

        this.timer = 0;
        this.duration = C.PLANE_MS + dropWindowMs + C.CRATE_FALL_MS + C.TEXT_MS;
        this.isFinished = false;

        this.shakeX = 0;
        this.shakeY = 0;
    }

    update(deltaTime) {
        this.timer += deltaTime;
        if (this.timer >= this.duration) this.isFinished = true;
    }

    draw(ctx) {
        const C = CashAnimator;

        this.#drawPlane(ctx);
        for (const crate of this.crates) this.#drawCrate(ctx, crate);
    }

    #drawPlane(ctx) {
        const C = CashAnimator;

        const img = loader.assets.gadgets[this.level >= 3 ? 'cash_plane_3' : 'cash_plane']
                 || loader.assets.gadgets['cash_plane'];
        if (!img) return;

        const x = this.planeStartX + this.planeVx * this.timer;

        // Gone once it is fully past the near edge; no point drawing it under the HUD.
        if (x < -C.PLANE_W * 1.5 || x > C.MAP_WIDTH + C.PLANE_W * 1.5) return;

        ctx.save();
        ctx.translate(x, C.PLANE_Y);
        // Seat 2 flies left-to-right, so the left-facing sprite is mirrored about its centre.
        if (this.side !== 1) {
            ctx.translate(C.PLANE_W / 2, 0);
            ctx.scale(-1, 1);
            ctx.translate(-C.PLANE_W / 2, 0);
        }
        ctx.drawImage(img, -C.PLANE_W / 2, 0, C.PLANE_W, C.PLANE_H);
        ctx.restore();
    }

    #drawCrate(ctx, crate) {
        const C = CashAnimator;

        const t = this.timer - crate.releaseMs;
        if (t < 0) return;                       // still on board

        ctx.save();
        if (t < C.CRATE_FALL_MS) {
            // --- THE PARACHUTE DROP ---
            const imgKey = this.level === 1 ? 'cash' : `cash_${this.level}`;
            const img = loader.assets.gadgets[imgKey] || loader.assets.gadgets['cash'];
            if (!img) { ctx.restore(); return; }

            // Leaves the plane at the plane's belly and drifts down to the ground.
            const from = C.PLANE_Y + C.PLANE_H * 0.7;
            const p = t / C.CRATE_FALL_MS;
            const y = from + (C.GROUND_Y - from) * p;

            // The gentle sway that sells the parachute. Offset by the release time so eight
            // crates in a row do not all swing in lockstep.
            const sway = Math.sin((this.timer + crate.releaseMs) / 300) * 30;

            ctx.translate(crate.x + sway, y);
            // Anchored to the floor: half-width across, full height up.
            ctx.drawImage(img, -C.CRATE_SIZE / 2, -C.CRATE_SIZE, C.CRATE_SIZE, C.CRATE_SIZE);
        } else {
            // --- THE FLOATING TAKE ---
            const p = (t - C.CRATE_FALL_MS) / C.TEXT_MS;
            if (p > 1) { ctx.restore(); return; }

            ctx.globalAlpha = 1.0 - p;
            ctx.translate(crate.x, C.GROUND_Y - p * 60);

            ctx.font = '32px "Press Start 2P", cursive';
            ctx.fillStyle = '#FFFF00';
            ctx.strokeStyle = '#000000';
            ctx.lineWidth = 4;
            ctx.textAlign = 'center';

            const text = `+$${this.amount}`;
            ctx.strokeText(text, 0, 0);
            ctx.fillText(text, 0, 0);
        }
        ctx.restore();
    }
}
