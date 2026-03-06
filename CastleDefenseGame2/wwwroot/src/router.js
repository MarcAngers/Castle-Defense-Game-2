import initLoading from '../static/views/view-logic/loading.js';
import initMainMenu from '../static/views/view-logic/main-menu.js';
import initSPSelectTeam from '../static/views/view-logic/sp-select-team.js';
import initMPSelectTeam from '../static/views/view-logic/mp-select-team.js';
import initGameBrowser from '../static/views/view-logic/game-browser.js';
import initLobby from '../static/views/view-logic/lobby.js';
import initGameScreen from '../static/views/view-logic/game.js';
import initGameOverScreen from '../static/views/view-logic/game-over.js';

const appContainer = document.getElementById('app-container');

// A map connecting file names to their logic functions
const routes = {
    'loading': { path: '../static/views/loading.html', logic: initLoading },
    'main-menu': { path: '../static/views/main-menu.html', logic: initMainMenu },
    'sp-select-team': { path: '../static/views/sp-select-team.html', logic: initSPSelectTeam },
    'mp-select-team': { path: '../static/views/mp-select-team.html', logic: initMPSelectTeam },
    'game-browser': { path: '../static/views/game-browser.html', logic: initGameBrowser },
    //'collection': { path: '../static/views/collection.html', logic: initCollection },
    //'singleplayer': { path: '../static/views/singleplayer.html', logic: initSingleplayer },
    //'multiplayer': { path: '../static/views/multiplayer.html', logic: initMultiplayer },
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

    // 2. Inject it into the page
    appContainer.innerHTML = htmlText;

    // 3. Run the logic to attach event listeners
    // We wait for the DOM to update, then run the setup function
    route.logic();
}