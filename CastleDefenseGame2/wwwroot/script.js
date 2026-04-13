import { showScreen } from './src/router.js';
import loader from './src/asset-loader.js';
import view from './src/view.js';

showScreen('loading');
await loader.loadAll();
view.resize();
showScreen('main-menu');