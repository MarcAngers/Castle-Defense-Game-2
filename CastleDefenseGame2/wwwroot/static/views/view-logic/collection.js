import { showScreen } from '../../../src/router.js';
import View from '../../../src/view.js';

export default async function initCollection() {
    let collectionView = new View('bgCanvas');  
    collectionView.drawBackground('white');

    const btnSP = document.getElementById('btnUnits');
    const btnMP = document.getElementById('btnGadgets');
    
    btnUnits.onclick = () => {
        showScreen('units-select-team');
    };
    btnGadgets.onclick = () => {
        showScreen('gadgets');
    };
}