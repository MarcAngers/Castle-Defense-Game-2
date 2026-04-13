import { showScreen } from '../../../src/router.js';
import view from '../../../src/view.js';

export default function initCollection() {
    view.mapColour = 'purple';
    view.panTo(1000);
    view.draw();

    const btnUnitInfo = document.getElementById('btnUnitInfo');
    const btnGadgetInfo = document.getElementById('btnGadgetInfo');
    const btnBack = document.getElementById('btnBack');
    
    btnUnitInfo.onclick = () => {
        showScreen('select-team');
    };
    btnGadgetInfo.onclick = () => {
        showScreen('gadget-categories');
    };
    btnBack.onclick = () => {
        showScreen('main-menu');
    };
}