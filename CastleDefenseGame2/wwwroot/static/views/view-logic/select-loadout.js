import { showScreen } from '../../../src/router.js';
import loader from '../../../src/asset-loader.js';
import connection from '../../../src/game-connection.js';

export default async function initSelectLoadout() {
    let selectedOGadget, selectedDGadget;
    const sGadgetElement = document.getElementById('signature-gadget');

    const signatureGadgetMap = {
        'black': 'blackhole',        
        'blue': 'wave',
        'green': 'goo',
        'orange': 'meteor',
        'purple': 'poison',
        'red': 'rage',
        'white': 'cash',
        'yellow': 'divine',
    }

    const oGadgetElements = document.getElementsByClassName('offense');
    const dGadgetElements = document.getElementsByClassName('defense');
    
    // Set background colour
    document.getElementById('select-loadout').style.backgroundColor = connection.selectedTeam;
    if (connection.selectedTeam == 'black')
        document.getElementById('select-loadout').style.color = 'white';
    else 
        document.getElementById('select-loadout').style.color = 'black';

    // Set gadget images:
    // Set signature gadget image:
    let sSource = loader.assets.gadgets[signatureGadgetMap[connection.selectedTeam]];
    let sImg = document.createElement('img');
    let sTitle = document.createElement('h5');
    sTitle.innerHTML = loader.assets.gadgetData[signatureGadgetMap[connection.selectedTeam]]['name'];
    
    if (typeof sSource === 'string') {
        sImg.src = sSource;
    } else {
        sImg.src = sSource.src; // If it's already a new Image() object
    }
    sGadgetElement.appendChild(sImg);
    sGadgetElement.after(sTitle);

    // Set offensive gadget images:
    Array.from(oGadgetElements).forEach((gadget, index) => {
        // Check if your array contains URL strings or Image Objects
        const source = loader.assets.gadgets[gadget.id];
        
        const img = document.createElement('img');
        let oTitle = document.createElement('h5');
        oTitle.innerHTML = loader.assets.gadgetData[gadget.id]['name'];
        
        // Handle both String URLs and Image Objects
        if (typeof source === 'string') {
            img.src = source;
        } else {
            img.src = source.src; // If it's already a new Image() object
        }

        gadget.appendChild(img);
        gadget.after(oTitle);
    });
    // Set defensive gadget images:
    Array.from(dGadgetElements).forEach((gadget, index) => {
        // Check if your array contains URL strings or Image Objects
        const source = loader.assets.gadgets[gadget.id];
        
        const img = document.createElement('img');
        let dTitle = document.createElement('h5');
        dTitle.innerHTML = loader.assets.gadgetData[gadget.id]['name'];
        
        // Handle both String URLs and Image Objects
        if (typeof source === 'string') {
            img.src = source;
        } else {
            img.src = source.src; // If it's already a new Image() object
        }

        gadget.appendChild(img);
        gadget.after(dTitle);
    });
    
    const handleOGadgetClick = (e) => {
        // A. Reset: Remove 'selected' from ALL teams
        // (Convert HTMLCollection to Array to loop easily)
        Array.from(oGadgetElements).forEach(gadget => {
            gadget.classList.remove('selected');
        });

        // B. Set: Add 'selected' to the ONE that was clicked
        // e.currentTarget ensures we get the main .team div, not a child element
        const clickedElement = e.currentTarget;
        clickedElement.classList.add('selected');

        // C. Update the data variable
        selectedOGadget = clickedElement.id;
    };
    const handleDGadgetClick = (e) => {
        // A. Reset: Remove 'selected' from ALL teams
        // (Convert HTMLCollection to Array to loop easily)
        Array.from(dGadgetElements).forEach(gadget => {
            gadget.classList.remove('selected');
        });

        // B. Set: Add 'selected' to the ONE that was clicked
        // e.currentTarget ensures we get the main .team div, not a child element
        const clickedElement = e.currentTarget;
        clickedElement.classList.add('selected');

        // C. Update the data variable
        selectedDGadget = clickedElement.id;
    };

    // 3. Attach the listener properly
    for (const gadget of oGadgetElements) {
        gadget.addEventListener('click', handleOGadgetClick);
    }
    for (const gadget of dGadgetElements) {
        gadget.addEventListener('click', handleDGadgetClick);
    }

    // TODO: Select previously selected loadout, or white team by default
    // if (connection.selectedTeam != null) {
    //     document.getElementById(connection.selectedTeam).click();
    // } 
    // else {
        document.getElementById('nuke').click();
        document.getElementById('heal').click();
    // }

    const btnBack = document.getElementById('btnBack');
    const btnCreate = document.getElementById('btnCreate');
    const btnJoin = document.getElementById('btnJoin');

    btnBack.onclick = () => {
        showScreen('select-team');
    };
    btnCreate.onclick = async () => {
        // Set gadget loadout here
        connection.selectedLoadout = [selectedOGadget, selectedDGadget, signatureGadgetMap[connection.selectedTeam]];
        await connection.createGame(connection.selectedTeam, connection.selectedLoadout);
        showScreen('lobby');
    };
    btnJoin.onclick = () => {
        connection.selectedLoadout = [selectedOGadget, selectedDGadget, signatureGadgetMap[connection.selectedTeam]];
        showScreen('game-browser');
    };
}