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
        // Symmetric with the fade-in on purpose: the dark arrived over two seconds and it
        // should leave the same way. It used to be cut off in a single frame the moment the
        // game ended, which read as the renderer dropping a layer rather than as the effect
        // ending.
        this.fadeOutMs = 2000;
        this.maxAlpha = 0.7; // Matches the level-3 black hole, so they read as the same event

        // There is no natural end. The one thing that clears it is the game finishing —
        // see update(). Without that check the overlay would survive into the next match,
        // because AnimationManager lives on the view and outlives a single game.
        this.isFinished = false;

        // Wall-clock instant the game ended, or null while it is still running.
        this.fadeOutStart = null;
    }

    update(deltaTime, state) {
        this.timer += deltaTime;

        if (state && state.isGameOver) {
            // ANCHORED TO A TIMESTAMP RATHER THAN ACCUMULATED FROM deltaTime, because the
            // frame this runs on is the worst possible one to trust a delta from. The game
            // loop cancels itself at game over and the game-over screen starts its own loop
            // after fetching and injecting a new view, so the first frame on the far side of
            // that swap carries the whole gap as one enormous deltaTime -- which would spend
            // most of the fade in a single frame and reproduce the very snap this exists to
            // remove. Elapsed wall-clock time is immune to that.
            if (this.fadeOutStart === null) this.fadeOutStart = performance.now();
            if (performance.now() - this.fadeOutStart >= this.fadeOutMs) this.isFinished = true;
        }
    }

    draw(ctx, state) {
        const fadeIn = Math.min(1, this.timer / this.fadeInMs);
        const fadeOut = this.fadeOutStart === null
            ? 1
            : Math.max(0, 1 - (performance.now() - this.fadeOutStart) / this.fadeOutMs);

        const alpha = fadeIn * fadeOut * this.maxAlpha;
        if (alpha <= 0) return;

        ctx.save();
        // Screen space, not world space — this covers the viewport, not the map.
        ctx.resetTransform();
        ctx.fillStyle = `rgba(0, 0, 0, ${alpha})`;
        ctx.fillRect(0, 0, window.innerWidth, window.innerHeight);
        ctx.restore();
    }
}
