import { showScreen } from '../../../src/router.js';
import view from '../../../src/view.js';
import connection from '../../../src/game-connection.js';

export default function initGameScreen() {
    view.panTo(connection.winnerSide == 1 ? 2000 : 0);
    view.drawGameState(connection.latestState);

    let viewingStats = true;
    let animationFrameId;

    document.getElementById('game-over-title').innerHTML = connection.winnerSide == 0
        ? 'DRAW!'
        : 'P' + connection.winnerSide + ' WINS!!!';
    document.getElementById('game-time').innerHTML = 'GAME TIME: ' + formatGameTime(connection.latestState.currentTick);

    document.getElementById('game-over-id-text').innerText = connection.currentGameId || '------';

    const btnCopyGameId = document.getElementById('btnCopyGameId');
    btnCopyGameId.onclick = async () => {
        if (!connection.currentGameId) return;

        try {
            await navigator.clipboard.writeText(connection.currentGameId);

            btnCopyGameId.innerText = "Copied!";

            setTimeout(() => {
                btnCopyGameId.innerText = "Copy";
            }, 2000);
        } catch (err) {
            console.error('Failed to copy game ID', err);
        }
    };

    const btnMainMenu = document.getElementById('btnMainMenu');

    btnMainMenu.onclick = () => {
        viewingStats = false;
        view.latestState = null;
        showScreen('main-menu');
    };

    const backgroundLoop = () => {
        if (!viewingStats) {
            cancelAnimationFrame(animationFrameId);
            animationFrameId = null;
            return;
        }
        
        view.clear();

        if (connection.latestState) {
            view.drawGameState(connection.latestState);
        }

        animationFrameId = requestAnimationFrame(backgroundLoop);
    };

    requestAnimationFrame(backgroundLoop);

    function formatGameTime(currentTick) {
        const TICKS_PER_SECOND = 30;

        // 1. Find the total number of actual seconds that have passed
        const totalSeconds = Math.floor(currentTick / TICKS_PER_SECOND);

        // 2. Break that down into minutes and remaining seconds
        const minutes = Math.floor(totalSeconds / 60);
        const seconds = totalSeconds % 60;

        // 3. Pad the numbers with a leading zero if they are under 10
        const formattedMinutes = String(minutes).padStart(2, '0');
        const formattedSeconds = String(seconds).padStart(2, '0');

        // 4. Return the classic MM:SS format
        return `${formattedMinutes}:${formattedSeconds}`;
    }
}