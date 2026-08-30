import { showScreen } from '../../../src/router.js';
import loader from '../../../src/asset-loader.js';
import connection from '../../../src/game-connection.js';

export default async function initSelectLevel() {
    const isPractice = connection.gameMode === 'practice';

    let selectedTeam = null;
    let selectedLevel = null;
    const teamElements = document.getElementsByClassName('team');
    const levelElements = document.getElementsByClassName('level');
    const modelSelect = document.getElementById('model-select');

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

        if (isPractice) {
            // A tier/AntiSpam tile overrides any model dropdown choice.
            modelSelect.value = '';
            // "heuristic" and "antispam" are exact specs SetupPracticeOpponent matches by
            // name; everything else is a tier tile and becomes spamN.
            connection.selectedOpponent =
                (selectedLevel === 'antispam' || selectedLevel === 'heuristic')
                    ? selectedLevel
                    : `spam${selectedLevel}`;
        }
    };

    // 3. Attach the listener properly
    for (const team of teamElements) {
        team.addEventListener('click', handleTeamClick);
    }
    for (const level of levelElements) {
        level.addEventListener('click', handleLevelClick);
    }

    if (isPractice) {
        // Practice mode: the team was already picked on the previous screen (this is
        // the bot's OWN team, playing a random loadout each game, same as every other
        // matchup this bot has been benchmarked against) -- re-showing the team bar
        // here would just be confusing, so hide it.
        document.getElementById('team-bar').classList.add('d-none');
        document.querySelector('#select-team h2').textContent = 'SPAM TIER OR ANTISPAM:';

        const modelPicker = document.getElementById('model-picker');
        modelPicker.classList.remove('d-none');
        const opponents = await connection.getPracticeOpponents();
        for (const name of opponents.modelNames) {
            const opt = document.createElement('option');
            opt.value = name;
            opt.textContent = name;
            modelSelect.appendChild(opt);
        }
        modelSelect.onchange = () => {
            if (modelSelect.value) {
                Array.from(levelElements).forEach(level => level.classList.remove('selected'));
                connection.selectedOpponent = modelSelect.value;
            }
        };

        // Default to Tier 4 spam selected -- the bot's own most stubborn weak point
        // per its benchmark dashboard, and likely the first thing worth playing.
        document.getElementById('4').click();
    } else {
        // Select previously selected team, or white team by default
        if (connection.selectedTeam != null) {
            document.getElementById(connection.selectedTeam).click();
        }
        else {
            document.getElementById('white').click();
        }
    }

    const btnBack = document.getElementById('btnBack');
    const btnSelect = document.getElementById('btnSelect');

    btnBack.onclick = () => {
        showScreen('select-loadout');
    };
    btnSelect.onclick = async () => {
        if (isPractice) {
            if (!connection.selectedOpponent) {
                alert('Pick an opponent first.');
                return;
            }
            await connection.createPracticeGame();
        } else {
            await connection.createGame(connection.selectedTeam, connection.selectedLoadout);
        }
    };
}