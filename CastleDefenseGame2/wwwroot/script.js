import { showScreen } from './src/router.js';
import loader from './src/asset-loader.js';

showScreen('loading');
await loader.loadAll();
showScreen('main-menu');