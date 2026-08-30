import { showScreen } from '../../../src/router.js';
import loader from '../../../src/asset-loader.js';
import connection from '../../../src/game-connection.js';
import meander from '../../../src/menu-meander.js';

// The shadow entry is not a map of its own -- it is the OTHER maps, desaturated -- so it
// has no art folder and its tile cycles through the pool instead of showing one picture.
const SHADOW_ID = 'shadow';

// Exactly the maps the game can actually produce a shadow version of. GameState's
// constructor rolls a shadow map only when Black is picked, and a shadow roll that lands
// back on Black is turned into the plain Black map -- so Black is never shadowed, and
// listing it here would show the player a variant that cannot occur.
//
// Cycled IN ORDER, and in the same order as the tiles along the top of the screen, so a
// player who wants to see a particular map shadowed can wait for it rather than hoping
// for it. Kept in that order deliberately -- sorting or shuffling this breaks the match.
const SHADOW_POOL = ['white', 'purple', 'blue', 'green', 'yellow', 'orange', 'red'];

const SHADOW_CYCLE_MS = 3000;

export default async function initMapInfo() {
    // Same scene as Collection, so this keeps the existing wanderers rather than respawning.
    // Note this screen deliberately does NOT touch view.mapColour: the whole scene shares
    // the purple map collection.js drew, and repainting the canvas per selection would both
    // break that and duplicate the art the card is already showing.
    meander.start('collection');

    let selectedMap = null;
    let cycleTimer = null;
    let shadowIndex = 0;

    const teamElements = document.getElementsByClassName('team');
    const characterContainer = document.querySelector('.character');

    const stopCycle = () => {
        if (cycleTimer !== null) {
            clearInterval(cycleTimer);
            cycleTimer = null;
        }
    };

    const nextShadowColour = () => {
        const colour = SHADOW_POOL[shadowIndex];
        shadowIndex = (shadowIndex + 1) % SHADOW_POOL.length;
        return colour;
    };

    // Swaps just the two layers, leaving the rest of the card alone. The cycle runs on this
    // rather than on a full re-render so the name and effect text do not flicker every few
    // seconds when nothing about them has changed.
    const paintArt = (colour) => {
        const layers = characterContainer.querySelectorAll('.map-art img');
        if (layers.length < 2) return;

        // The art is already in memory as Image objects -- the loader pulls both layers for
        // every colour at startup (loadMap) -- so this re-uses those rather than asking the
        // network for a second copy.
        const background = loader.assets.background[colour];
        const foreground = loader.assets.foreground[colour];
        layers[0].src = background ? background.src : '';
        layers[1].src = foreground ? foreground.src : '';
    };

    const renderCard = () => {
        if (!selectedMap) return;

        const isShadow = selectedMap === SHADOW_ID;
        const map = loader.getMapStats(selectedMap);

        characterContainer.innerHTML = `
            <div class="collection-card">
                <h1>${map.name.toUpperCase()}</h1>
                <div class="map-art${isShadow ? ' shadow' : ''}">
                    <img draggable="false" alt="">
                    <img draggable="false" alt="">
                </div>
                <h3 style='color: #FFFF00'>MAP EFFECT</h3>
                <p class="map-effect">${map.effect}</p>
            </div>
        `;

        stopCycle();

        if (!isShadow) {
            paintArt(selectedMap);
            return;
        }

        // Restart the run from the top each time the tile is selected, so it always opens
        // on the same map rather than resuming wherever a previous visit left off.
        shadowIndex = 0;
        paintArt(nextShadowColour());
        cycleTimer = setInterval(() => {
            // The router replaces #app-container's contents on navigation, so this screen
            // being gone is the signal to stop -- the same self-cancelling check
            // menu-meander uses, and for the same reason: the router offers no teardown
            // hook to clear the timer from.
            if (!document.getElementById('map-info')) {
                stopCycle();
                return;
            }
            paintArt(nextShadowColour());
        }, SHADOW_CYCLE_MS);
    };

    const handleTeamClick = (e) => {
        Array.from(teamElements).forEach(team => {
            team.classList.remove('selected');
        });

        const clickedElement = e.currentTarget;
        clickedElement.classList.add('selected');

        selectedMap = clickedElement.id;
        renderCard();
    };

    for (const team of teamElements) {
        team.addEventListener('click', handleTeamClick);
    }

    // Open on the player's own colour, the same way the Units screen does.
    if (connection.selectedTeam != null) {
        document.getElementById(connection.selectedTeam).click();
    }
    else {
        document.getElementById('white').click();
    }

    const btnBack = document.getElementById('btnBack');
    btnBack.onclick = () => {
        stopCycle();
        showScreen('collection');
    };
}
