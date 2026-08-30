import { showScreen } from '../../../src/router.js';
import connection from '../../../src/game-connection.js';
import view from '../../../src/view.js';
import loader from '../../../src/asset-loader.js';
import meander from '../../../src/menu-meander.js';

// The intro plays on the FIRST page load only. This module is a singleton, so the flag
// survives every showScreen('main-menu') for the life of the page -- coming back from the
// Collection or from a finished game finds it already true and renders the menu plain.
// A real page refresh re-imports the module and is a first load again, which is correct.
let introPlayed = false;

export default async function initMainMenu() {
    view.mapColour = 'white';
    view.draw();

    // Background wanderers. Runs its own rAF loop, which redraws the map each frame and
    // stops itself once #main-menu leaves the DOM.
    meander.start('main-menu');

    const btnSP = document.getElementById('btnSingleplayer');
    const btnMP = document.getElementById('btnMultiplayer');
    const btnLeague = document.getElementById('btnTrainingLeague');
    const btnPractice = document.getElementById('btnPractice');
    const btnDefenceWatch = document.getElementById('btnDefenceWatch');
    const btnCollection = document.getElementById('btnCollection');

    btnSP.onclick = () => {
        connection.gameMode = 'sp';
        showScreen('select-team');
    };
    btnMP.onclick = () => {
        connection.gameMode = 'mp';
        showScreen('select-team');
    };
    // Acceptance Test. Replaced Training League (spectating v4 vs HeuristicBot) on
    // 2026-08-11: watching two bots play was diagnostic, and the diagnostic that
    // matters now is whether the flagship beats Marc. Straight into a game against
    // the shipped search bot with server-assigned random teams and loadouts on both
    // sides — no selection screens, so there is nothing to reroll.
    // btnLeague / btnPractice / btnDefenceWatch are commented out of main-menu.html
    // for now, so guard each one: without the check the first null assignment throws
    // and everything below it -- including btnCollection -- never gets wired up.
    if (btnLeague) btnLeague.onclick = async () => {
        connection.gameMode = 'accept';
        await connection.createGame();
    };
    if (btnPractice) btnPractice.onclick = () => {
        connection.gameMode = 'practice';
        showScreen('select-team');
    };

    // Watch Bots. Spectate the defence-only bot (P1) against the shipped bot (P2) in the
    // pinned White/nuke/reinforcements mirror -- the exact matchup every number in
    // BOT_TUNING.md was measured in. Both loadouts are fixed server-side, so there is
    // nothing to select and no reroll: straight into the game.
    if (btnDefenceWatch) btnDefenceWatch.onclick = async () => {
        connection.gameMode = 'defwatch';
        await connection.createGame();
    };

    btnCollection.onclick = () => {
        showScreen('collection');
    };

    if (!introPlayed) {
        introPlayed = true;
        playIntro();
    }
}

// Timings here are the CSS ones from global-styles.css. The only thing JS has to own is
// the burst, which has to fire when the 2 is at the top of its pop rather than when it
// starts, and the per-button delay, which follows DOM order so it still works if the
// commented-out buttons come back.
const TWO_POP_DELAY_MS = 1100;  // #main-menu.intro #title-two animation-delay
const TWO_POP_PEAK_MS = 350;    // 60% of its 0.58s pop -- the moment it overshoots
const BUTTON_START_MS = 1850;   // after the 2 has landed
const BUTTON_STAGGER_MS = 110;

// --- Star burst physics ---
// Plain projectile motion: constant horizontal velocity, constant downward acceleration.
const GRAVITY = 2300;           // px/s^2
const SPEED_MIN = 600;          // px/s launch speed
const SPEED_MAX = 1150;
const STAR_COUNT = 22;
const TRAJECTORY_STEPS = 24;    // samples per arc -- the parabola IS the keyframes
const OFFSCREEN_MARGIN = 60;    // px below the viewport before a star counts as gone

function playIntro() {
    const menu = document.getElementById('main-menu');
    if (!menu) return;

    menu.classList.add('intro');

    document.querySelectorAll('#main-menu button').forEach((btn, i) => {
        btn.style.animationDelay = `${BUTTON_START_MS + i * BUTTON_STAGGER_MS}ms`;
    });

    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

    setTimeout(burstStars, TWO_POP_DELAY_MS + TWO_POP_PEAK_MS);
}

// Confetti out of the 2. Positions are read at fire time rather than up front, so the
// burst lands on the 2 wherever the layout actually put it (the title wraps differently
// on a phone). Bails out rather than throwing if the screen has moved on already.
function burstStars() {
    const two = document.getElementById('title-two');
    const star = loader.get('particles')?.star;
    if (!two || !star || !document.getElementById('main-menu')) return;

    const rect = two.getBoundingClientRect();
    const originX = rect.left + rect.width / 2;
    const originY = rect.top + rect.height / 2;

    // Every star has to clear the bottom edge, so the drop distance is measured from
    // where it was actually launched.
    //
    // FLOORED, because the 2 is not always on screen. On a viewport short enough (or
    // narrow enough, which wraps the title into more lines) to push the 2 below the
    // bottom edge, the raw distance goes NEGATIVE -- the star is already past the finish
    // line -- and the flight time below solves to a negative root for every star launched
    // downward. `img.animate` throws on a negative duration, and because that happens
    // inside the loop it aborted the whole burst: the layer was never appended, so NO
    // stars appeared at all and an uncaught error hit the console.
    //
    // The floor cannot change a working burst. dropDistance > OFFSCREEN_MARGIN is
    // algebraically identical to originY < innerHeight, i.e. "the 2 is above the bottom
    // edge" -- so whenever the title is visible the raw value already exceeds the floor
    // and Math.max returns it untouched. It only engages in the case that used to throw,
    // where it means "the 2 is below the fold, so just throw the stars clear of it".
    // A positive distance also keeps the discriminant above vy^2, so the root can be
    // neither negative nor NaN.
    const dropDistance = Math.max(window.innerHeight - originY + OFFSCREEN_MARGIN, OFFSCREEN_MARGIN);

    const layer = document.createElement('div');
    layer.id = 'star-burst';

    let longestFlight = 0;

    for (let i = 0; i < STAR_COUNT; i++) {
        // Spread evenly around the circle with a little jitter, so the ring reads as a
        // burst rather than a clock face. Downward launches simply fall sooner.
        const angle = (i / STAR_COUNT) * Math.PI * 2 + (Math.random() - 0.5) * 0.5;
        const speed = SPEED_MIN + Math.random() * (SPEED_MAX - SPEED_MIN);
        const vx = Math.cos(angle) * speed;
        const vy = Math.sin(angle) * speed;          // negative = launched upward
        const spin = Math.random() * 720 - 360;      // deg/s, constant

        // Time to fall past the bottom edge, from 0.5*g*t^2 + vy*t - dropDistance = 0.
        const flight = (-vy + Math.sqrt(vy * vy + 2 * GRAVITY * dropDistance)) / GRAVITY;
        longestFlight = Math.max(longestFlight, flight);

        const img = document.createElement('img');
        img.src = star.src;
        img.style.left = `${originX}px`;
        img.style.top = `${originY}px`;

        // Sample the real trajectory. Straight `linear` between dense samples means the
        // curve comes from the physics, not from a timing function chosen to look like it.
        const frames = [];
        for (let step = 0; step <= TRAJECTORY_STEPS; step++) {
            const t = (step / TRAJECTORY_STEPS) * flight;
            const x = vx * t;
            const y = vy * t + 0.5 * GRAVITY * t * t;
            frames.push({
                offset: step / TRAJECTORY_STEPS,
                transform: `translate(${x.toFixed(1)}px, ${y.toFixed(1)}px) rotate(${(spin * t).toFixed(1)}deg)`,
                easing: 'linear'
            });
        }

        // No opacity anywhere in the keyframes: the stars leave by falling off the
        // screen, the way something thrown in the air actually does.
        img.animate(frames, { duration: flight * 1000, fill: 'forwards' });
        layer.appendChild(img);
    }

    document.body.appendChild(layer);
    // Tear the layer down once the last star is well clear of the screen, so nothing
    // accumulates and no stray img is left sitting over the menu.
    setTimeout(() => layer.remove(), longestFlight * 1000 + 200);
}
