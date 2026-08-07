/**
 * ARMAGEDDON's opening beat: the world goes dark and STAYS dark.
 *
 * Deliberately its own animator rather than reusing BlackholeAnimator at level 3. The
 * darkening is the only part of the black hole wanted here — borrowing that animator
 * would also paint a black hole sprite onto the field with no hazard underneath it, and
 * would fade the light back in after a fixed duration, which is exactly wrong for an
 * effect that runs until somebody loses.
 */
export default class ArmageddonAnimator {
    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.timer = 0;

        this.fadeInMs = 2000;
        this.maxAlpha = 0.7; // Matches the level-3 black hole, so they read as the same event

        // There is no natural end. The one thing that clears it is the game finishing —
        // see update(). Without that check the overlay would survive into the next match,
        // because AnimationManager lives on the view and outlives a single game.
        this.isFinished = false;
    }

    update(deltaTime, state) {
        this.timer += deltaTime;

        if (state && state.isGameOver) {
            this.isFinished = true;
        }
    }

    draw(ctx, state) {
        const alpha = Math.min(1, this.timer / this.fadeInMs) * this.maxAlpha;
        if (alpha <= 0) return;

        ctx.save();
        // Screen space, not world space — this covers the viewport, not the map.
        ctx.resetTransform();
        ctx.fillStyle = `rgba(0, 0, 0, ${alpha})`;
        ctx.fillRect(0, 0, window.innerWidth, window.innerHeight);
        ctx.restore();
    }
}
