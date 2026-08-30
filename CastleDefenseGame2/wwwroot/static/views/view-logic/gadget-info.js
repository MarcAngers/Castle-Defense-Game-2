import { showScreen } from '../../../src/router.js';
import loader from '../../../src/asset-loader.js';
import meander from '../../../src/menu-meander.js';

export default async function initGadgetInfo() {
    // Same scene as Collection, so this keeps the existing wanderers rather than respawning.
    meander.start('collection');

    let selectedGadget = null;
    let currentLevelIndex = 0; // 0 = Lvl 1, 1 = Lvl 2, 2 = Lvl 3

    const gadgetElements = document.getElementsByClassName('gadget');
    const characterContainer = document.querySelector('.character');
    const btnPrev = document.getElementById('previous');
    const btnNext = document.getElementById('next');

    // --- THE CAROUSEL RENDERER ---
    const renderCarousel = () => {
        if (!selectedGadget) return;

        // 1. Construct the ID for the current tier (e.g., 'nuke', 'nuke_2', 'nuke_3')
        const baseId = selectedGadget.toLowerCase();
        const levelIds = [baseId, `${baseId}_2`, `${baseId}_3`];
        const currentId = levelIds[currentLevelIndex];

        // 2. Fetch the data and image
        const data = loader.assets.gadgetData[currentId] || {};
        
        // Fallback to the base image if a specific tier image isn't found
        const imageSource = loader.assets.gadgets[currentId] || loader.assets.gadgets[baseId];
        let imgSrc = '';
        if (imageSource) {
            imgSrc = typeof imageSource === 'string' ? imageSource : imageSource.src;
        }

        // 3. Build the Stats HTML dynamically
        let statsHtml = `<span><strong>PRICE:</strong> <span style='color: #FFFF00'>$${data.cost || 0}</span></span>`;
        statsHtml += `<span><strong>COOLDOWN:</strong> ${data.cooldownms / 1000}s</span>`;

        if (data.statusname && data.statusname.trim() !== '') {
            statsHtml += `<span><strong>STATUS:</strong> ${data.statusname} `;
            if (data.statusname != 'BLACKHOLE')
                statsHtml += `<img src="${loader.assets.particles[data.statusname.toLowerCase()].src}" alt="DMG">`;
            statsHtml += `</span>`;
            statsHtml += `<span><strong>DURATION:</strong> ${Number(data.statusduration / 30).toFixed(2)}s</span>`;
        }
        
        statsHtml += `<span><strong>${(data.baselabel || 'VALUE').toUpperCase()}:</strong> ${data.basevalue || 0}`;
        if (data.baselabel == 'DMG' || data.baselabel == 'DPS') {
            statsHtml += ` <img src="${loader.assets.tooltips.sword.src}" alt="DMG">`;
        }
        statsHtml += `</span>`;

        // 4. Inject the HTML card
        characterContainer.innerHTML = `
            <div class="collection-card">
                <h1>${(data.name || currentId).toUpperCase()}</h1>
                <img src="${imgSrc}" class="collection-image" draggable="false">
                <h3 style='color: #FFFF00'><strong>LEVEL ${currentLevelIndex + 1}</h3>
                <div class="collection-stats">
                    ${statsHtml}
                </div>
                <p class="collection-desc"><em>"${data.description || ''}"</em></p>
            </div>
        `;

        // 5. Update Button States
        btnPrev.disabled = currentLevelIndex === 0;
        btnNext.disabled = currentLevelIndex === 2; // Gadgets max out at index 2 (Tier 3)
    };

    // --- CAROUSEL BUTTON LISTENERS ---
    btnPrev.onclick = () => {
        if (currentLevelIndex > 0) {
            currentLevelIndex--;
            renderCarousel();
        }
    };

    btnNext.onclick = () => {
        if (currentLevelIndex < 2) {
            currentLevelIndex++;
            renderCarousel();
        }
    };

    // --- GADGET BAR LISTENERS ---
    const handleGadgetClick = (e) => {
        Array.from(gadgetElements).forEach(g => g.classList.remove('selected'));
        
        const clickedElement = e.currentTarget;
        clickedElement.classList.add('selected');
        
        selectedGadget = clickedElement.id;
        currentLevelIndex = 0; // Reset to Tier 1 when a new gadget is clicked
        renderCarousel();
    };

    for (const el of gadgetElements) {
        const id = el.id;
        const gadgetImg = loader.assets.gadgets[id];
        
        if (gadgetImg) {
            const img = document.createElement('img');
            img.src = typeof gadgetImg === 'string' ? gadgetImg : gadgetImg.src;
            img.draggable = false;
            el.appendChild(img);
        }
        
        el.addEventListener('click', handleGadgetClick);
    }

    // Default selection
    if (gadgetElements.length > 0) {
        gadgetElements[0].click();
    }

    document.getElementById('btnBack').onclick = () => showScreen('collection');
}