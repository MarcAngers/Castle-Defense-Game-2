import { showScreen } from '../../../src/router.js';
import loader from '../../../src/asset-loader.js';
import connection from '../../../src/game-connection.js';

export default async function initSelectLevel() {
    let selectedTeam = null;
    let selectedLevel = null;
    const teamElements = document.getElementsByClassName('team');
    const levelElements = document.getElementsByClassName('level');
    
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
        document.getElementById('select-team').style.backgroundColor = selectedTeam;
        if (selectedTeam == 'black')
            document.getElementById('select-team').style.color = 'white';
        else 
            document.getElementById('select-team').style.color = 'black';
    };

    const handleLevelClick = (e) => {
        // A. Reset: Remove 'selected' from ALL teams
        // (Convert HTMLCollection to Array to loop easily)
        Array.from(levelElements).forEach(team => {
            team.classList.remove('selected');
        });

        // B. Set: Add 'selected' to the ONE that was clicked
        // e.currentTarget ensures we get the main .team div, not a child element
        const clickedElement = e.currentTarget;
        clickedElement.classList.add('selected');

        // C. Update the data variable
        selectedLevel = clickedElement.id;
    };

    // 3. Attach the listener properly
    for (const team of teamElements) {
        team.addEventListener('click', handleTeamClick);
    }
    for (const level of levelElements) {
        level.addEventListener('click', handleLevelClick);
    }

    // Select previously selected team, or white team by default
    if (connection.selectedTeam != null) {
        document.getElementById(connection.selectedTeam).click();
    } 
    else {
        document.getElementById('white').click();
    }

    const btnBack = document.getElementById('btnBack');
    const btnSelect = document.getElementById('btnSelect');

    btnBack.onclick = () => {
        showScreen('select-loadout');
    };
    btnSelect.onclick = async () => {
        await connection.createGame(connection.selectedTeam, connection.selectedLoadout);
    };
}