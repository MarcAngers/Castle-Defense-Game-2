import { showScreen } from '../../../src/router.js';
import view from '../../../src/view.js';
import meander from '../../../src/menu-meander.js';

export default function initGadgetCategories() {
    // Same scene as Collection, so this keeps the existing wanderers rather than respawning.
    meander.start('collection');

    const btnOffensive = document.getElementById('btnOffensive');
    const btnTactical = document.getElementById('btnTactical');
    const btnSignature = document.getElementById('btnSignature');
    const btnBack = document.getElementById('btnBack');
    
    btnOffensive.onclick = () => {
        showScreen('gadget-info-offensive');
    };
    btnTactical.onclick = () => {
        showScreen('gadget-info-tactical');
    };
    btnSignature.onclick = () => {
        showScreen('gadget-info-signature');
    };
    btnBack.onclick = () => {
        showScreen('collection');
    };
}