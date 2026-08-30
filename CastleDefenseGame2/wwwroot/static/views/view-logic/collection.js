import { showScreen } from '../../../src/router.js';
import view from '../../../src/view.js';
import meander from '../../../src/menu-meander.js';

export default function initCollection() {
    view.mapColour = 'purple';
    view.panTo(900);
    view.draw();

    // Background wanderers, shared with the screens below this one -- see the 'collection'
    // scene in menu-meander.js. Calling this from every screen in the scene is deliberate:
    // it is a no-op while the scene is already running, so the crowd carries over intact.
    meander.start('collection');

    const btnUnitInfo = document.getElementById('btnUnitInfo');
    const btnGadgetInfo = document.getElementById('btnGadgetInfo');
    const btnMapInfo = document.getElementById('btnMapInfo');
    const btnBack = document.getElementById('btnBack');
    
    btnUnitInfo.onclick = () => {
        showScreen('unit-info');
    };
    btnGadgetInfo.onclick = () => {
        showScreen('gadget-categories');
    };
    btnMapInfo.onclick = () => {
        showScreen('map-info');
    };
    btnBack.onclick = () => {
        showScreen('main-menu');
    };
}