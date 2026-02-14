import { showScreen } from '../../../src/router.js';
import loader from '../../../src/asset-loader.js';
import connection from '../../../src/game-connection.js';

export default async function initMPSelectTeam() {
    let selectedTeam = null;
    const teamElements = document.getElementsByClassName('team');
    
    const handleTeamClick = (e) => {
        // A. Reset: Remove 'selected' from ALL teams
        // (Convert HTMLCollection to Array to loop easily)
        Array.from(teamElements).forEach(team => {
            team.classList.remove('selected');
        });

        // B. Set: Add 'selected' to the ONE that was clicked
        // e.currentTarget ensures we get the main .team div, not a child element
        const clickedElement = e.currentTarget;
        clickedElement.classList.add('selected');

        // C. Update the data variable
        selectedTeam = clickedElement.id;

        // Set background colour
        document.getElementById('mp-select-team').style.backgroundColor = selectedTeam;
        if (selectedTeam == 'black')
            document.getElementById('mp-select-team').style.color = 'white';
        else 
            document.getElementById('mp-select-team').style.color = 'black';

        // Set character images
        document.getElementById('team-name').innerHTML = selectedTeam.toUpperCase() + ' TEAM:';

        const teamImages = loader.getTeam(selectedTeam);
        const characterElements = document.getElementsByClassName('character');
        // 2. Loop through them
        Array.from(characterElements).forEach((character, index) => {
            // Clear any old image first
            character.innerHTML = '';

            if (teamImages[index]) {
                // Check if your array contains URL strings or Image Objects
                const source = teamImages[index];
                
                const img = document.createElement('img');
                
                // Handle both String URLs and Image Objects
                if (typeof source === 'string') {
                    img.src = source;
                } else {
                    img.src = source.src; // If it's already a new Image() object
                }

                character.appendChild(img);
            }
        });
    };

    // 3. Attach the listener properly
    for (const team of teamElements) {
        team.addEventListener('click', handleTeamClick);
    }

    // Select white team by default
    document.getElementById('white').click();

    const btnBack = document.getElementById('btnBack');
    const btnCreate = document.getElementById('btnCreate');
    const btnJoin = document.getElementById('btnJoin');

    btnBack.onclick = () => {
        showScreen('main-menu');
    };
    btnCreate.onclick = () => {
        connection.createGame(selectedTeam);
        showScreen('lobby');
    };
    btnJoin.onclick = () => {
        showScreen('game-browser');
    };
}