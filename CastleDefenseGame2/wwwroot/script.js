import { showScreen } from './src/router.js';
import loader from './src/asset-loader.js';
import view from './src/view.js';
import reconnectUi from './src/reconnect-ui.js';

showScreen('loading');

// Assets start loading FIRST, but the rejoin question is asked without waiting for them:
// the 60-second grace window is already running by the time this page exists, and on a
// phone the asset load is long enough to spend a visible slice of it. The prompt can
// therefore appear over the loading screen -- which is why it is handed the same promise,
// so pressing Rejoin cannot enter the game screen before the sprites it draws exist.
const assetsReady = loader.loadAll();

reconnectUi.init(assetsReady);
reconnectUi.check();

await assetsReady;
view.resize();
showScreen('main-menu');
