import { showScreen } from '../../../src/router.js';
import loader from '../../../src/asset-loader.js';
import connection from '../../../src/game-connection.js';

export default async function initUnitInfo() {
    let selectedTeam = connection.selectedTeam;
    let currentUnitIndex = 0; // NEW: Track which unit we are looking at!

    const teamElements = document.getElementsByClassName('team');
    const characterContainer = document.querySelector('.character');
    const btnPrev = document.getElementById('previous');
    const btnNext = document.getElementById('next');

    // --- NEW: The Render Function ---
    const renderCarousel = () => {
        if (!selectedTeam) return;

        // 1. Get the 8 images for this team
        const teamImages = loader.getTeam(selectedTeam);

        // 2. Get the raw CSV data for this team (handles both Arrays and Object Dictionaries)
        const allUnits = Array.isArray(loader.assets.unitData) ? loader.assets.unitData : Object.values(loader.assets.unitData);
        
        // Filter out only the units for this team, and sort by Tier (1 to 8) to match the image array
        const teamUnits = allUnits
            .filter(u => u.team === selectedTeam)
            .sort((a, b) => a.tier - b.tier);

        if (teamUnits.length === 0) return; // Safeguard if data isn't loaded yet

        const unit = teamUnits[currentUnitIndex];
        const imageSource = teamImages[currentUnitIndex];
        
        // Parse the image source
        let imgSrc = '';
        if (imageSource) {
            imgSrc = typeof imageSource === 'string' ? imageSource : imageSource.src;
        }

        // 3. Inject the HTML card
        characterContainer.innerHTML = `
            <div class="collection-card">
                <h1>${unit.name.toUpperCase()}</h1>
                <img src="${imgSrc}" class="collection-image" draggable="false">
                <div class="collection-stats">
                    <span><strong>Price:</strong> <span style='color: #FFFF00'>$${unit.price}</span></span>
                    <span class='stat'><strong>HP:</strong> ${unit.health} <img src="${loader.assets.tooltips.heart.src}" alt="HP"></span>
                    <span class='stat'><strong>DMG:</strong> ${unit.damage} <img src="${loader.assets.tooltips.sword.src}" alt="DMG"></span>
                    <span class='stat'><strong>SPD:</strong>${unit.speed} <img src="${loader.assets.tooltips.boot.src}" alt="SPD"></span>
                    <span class='stat'><strong>Tier:</strong> ${unit.tier}</span>
                    <span class='stat'><strong>ATK SPD:</strong> ${Number(unit.attackspeed).toFixed(2)}</span>
                </div>
                <p class="collection-desc"><em>"${unit.description}"</em></p>
            </div>
        `;

        // 4. Update the Button States (Gray out at the boundaries)
        btnPrev.disabled = currentUnitIndex === 0;
        btnNext.disabled = currentUnitIndex === teamUnits.length - 1;
    };

    // --- CAROUSEL EVENT LISTENERS ---
    btnPrev.onclick = () => {
        if (currentUnitIndex > 0) {
            currentUnitIndex--;
            renderCarousel();
        }
    };

    btnNext.onclick = () => {
        // Assume maximum of 8 units per team, or dynamically check array length
        const maxIndex = loader.getTeam(selectedTeam).length - 1; 
        if (currentUnitIndex < maxIndex) {
            currentUnitIndex++;
            renderCarousel();
        }
    };

    const handleTeamClick = (e) => {
        Array.from(teamElements).forEach(team => {
            team.classList.remove('selected');
        });

        const clickedElement = e.currentTarget;
        clickedElement.classList.add('selected');

        selectedTeam = clickedElement.id;
        
        // Reset the carousel to Tier 1 whenever a new team is clicked
        currentUnitIndex = 0; 
        renderCarousel();
    };

    for (const team of teamElements) {
        team.addEventListener('click', handleTeamClick);
    }

    if (connection.selectedTeam != null) {
        document.getElementById(connection.selectedTeam).click();
    } 
    else {
        document.getElementById('white').click();
    }

    const btnBack = document.getElementById('btnBack');
    btnBack.onclick = () => {
        showScreen('collection');
    };
}