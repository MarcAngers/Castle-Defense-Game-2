import initLoading from '../static/views/view-logic/loading.js';
import initMainMenu from '../static/views/view-logic/main-menu.js';
import initSelectTeam from '../static/views/view-logic/select-team.js';
import initSelectLoadout from '../static/views/view-logic/select-loadout.js';
import initSelectLevel from '../static/views/view-logic/select-level.js';
import initGameBrowser from '../static/views/view-logic/game-browser.js';
import initCollection from '../static/views/view-logic/collection.js';
import initUnitInfo from '../static/views/view-logic/unit-info.js';
import initMapInfo from '../static/views/view-logic/map-info.js';
import initGadgetCategories from '../static/views/view-logic/gadget-categories.js';
import initGadgetInfo from '../static/views/view-logic/gadget-info.js';
import initLobby from '../static/views/view-logic/lobby.js';
import initGameScreen from '../static/views/view-logic/game.js';
import initGameOverScreen from '../static/views/view-logic/game-over.js';

const appContainer = document.getElementById('app-container');

// --- VIEW STYLESHEETS ---
// Each view's .html carries its own <link>, but that link only starts loading AFTER the
// router has injected the markup, so the elements paint unstyled for a frame or two. That
// is the flash on the Collection chevrons -- amplified by their `transition: all 0.2s`,
// which makes the restyle visibly ANIMATE -- and on the shop tooltips, which are styled
// `opacity: 0` and so appear at full strength until game.css lands.
//
// These sheets CANNOT simply be hoisted into <head> together. Several of them style the
// same generic selectors (#previous/#next, .character, .team, .level), so more than one
// applied at once breaks the styling across the whole site. Exactly ONE view's CSS may be
// live at a time -- what used to scope them was the fact that a view's <link> only existed
// while that view was on screen.
//
// So: fetch the CSS text ahead of the swap, then apply it by replacing the contents of one
// persistent <style> element in the SAME synchronous block that injects the markup.
// Assigning textContent applies the rules immediately, so there is no frame where the new
// markup exists without its styles, and none where two views' rules overlap.
//
// Safe to inline because no view stylesheet uses url() or @import -- if one ever does, its
// relative paths would resolve against the document instead of the stylesheet, and this
// would need revisiting.
const viewStyleEl = document.createElement('style');
viewStyleEl.id = 'view-style';
document.head.appendChild(viewStyleEl);

// href -> Promise<css text>. Keyed so each sheet is fetched once per page load; later
// navigations apply it with no network at all.
const styleCache = new Map();

function fetchViewStyle(href) {
    if (!styleCache.has(href)) {
        styleCache.set(href, fetch(href)
            .then(response => response.text())
            .catch(error => {
                // A missing stylesheet should cost styling, not the whole screen.
                console.error(`Failed to load view stylesheet ${href}`, error);
                return '';
            }));
    }
    return styleCache.get(href);
}

// A map connecting file names to their logic functions
const routes = {
    'loading': { path: '../static/views/loading.html', logic: initLoading },
    'main-menu': { path: '../static/views/main-menu.html', logic: initMainMenu },
    'select-team': { path: '../static/views/select-team.html', logic: initSelectTeam },
    'select-loadout': { path: '../static/views/select-loadout.html', logic: initSelectLoadout },
    'select-level': { path: '../static/views/select-level.html', logic: initSelectLevel },
    'game-browser': { path: '../static/views/game-browser.html', logic: initGameBrowser },
    'collection': { path: '../static/views/collection.html', logic: initCollection },
    'unit-info': { path: '../static/views/unit-info.html', logic: initUnitInfo },
    'map-info': { path: '../static/views/map-info.html', logic: initMapInfo },
    'gadget-categories': { path: '../static/views/gadget-categories.html', logic: initGadgetCategories },
    'gadget-info-offensive': { path: '../static/views/gadget-info-offensive.html', logic: initGadgetInfo },
    'gadget-info-tactical': { path: '../static/views/gadget-info-tactical.html', logic: initGadgetInfo },
    'gadget-info-signature': { path: '../static/views/gadget-info-signature.html', logic: initGadgetInfo },
    'lobby': { path: '../static/views/lobby.html', logic: initLobby },
    'game': { path: '../static/views/game.html', logic: initGameScreen },
    'game-over': { path: '../static/views/game-over.html', logic: initGameOverScreen }
};

export async function showScreen(name) {
    const route = routes[name];
    if (!route) return console.error(`Screen ${name} not found!`);

    // 1. Fetch the HTML file as plain text
    const response = await fetch(route.path);
    const htmlText = await response.text();

    // 2. Split the view's own stylesheet off from its markup. The HTML parser hoists
    //    <link> into the parsed document's <head>, so doc.body is the markup by itself.
    const doc = new DOMParser().parseFromString(htmlText, 'text/html');
    const sheetLink = doc.querySelector('link[rel="stylesheet"]');
    const markup = doc.body.innerHTML;

    // 3. Have the CSS text in hand BEFORE anything on screen changes.
    const css = sheetLink ? await fetchViewStyle(sheetLink.getAttribute('href')) : '';

    // 4. Apply styles and markup together. Both are synchronous and in the same task, so
    //    no paint can land between them and nothing is ever shown unstyled.
    viewStyleEl.textContent = css;
    appContainer.innerHTML = markup;

    // 5. Run the logic to attach event listeners
    route.logic();
}