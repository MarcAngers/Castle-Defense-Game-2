import { showScreen } from '../../../src/router.js';
import view from '../../../src/view.js';
import connection from '../../../src/game-connection.js';
import endGameShow from '../../../src/end-game-show.js';

export default function initGameScreen() {
    // A DEFAULTED GAME GETS THE NEUTRAL SHOW, whether or not it has a winner. No castle
    // fell, nothing on the field was decided, and the armies have no idea anything has
    // happened -- so they mill about looking confused rather than one side throwing a
    // party over an opponent who simply left. This is the behaviour an abandoned game got
    // for free (winnerSide 0) and it is right for a claimed win too; only the SCOREBOARD
    // knows there was a winner.
    const showWinner = connection.endedByDisconnect ? 0 : connection.winnerSide;

    // Pan to the castle that FELL. On a draw both did, and on a default neither did, so
    // sit in the middle rather than framing a castle as though it had lost.
    view.panTo(showWinner == 0 ? 1000
             : showWinner == 1 ? 2000
             : 0);

    // Losers turn tail, winners throw a party, a draw leaves everyone looking about -- and
    // hazards and statuses left frozen in the final state get a hard two-second expiry.
    endGameShow.start(connection.latestState, showWinner);
    view.drawGameState(connection.latestState);

    let viewingStats = true;
    let animationFrameId;

    // A game awarded because the other player never came back is not the same result as
    // one that was played out, and saying so is the honest version of both headlines --
    // "P1 WINS" over an opponent who was never there reads as a fought win, and a game
    // both players left is not a draw.
    document.getElementById('game-over-title').innerHTML =
        connection.endedByDisconnect && connection.winnerSide == 0
            ? 'ABANDONED'                       // not a draw: nobody was there to draw
            : connection.winnerSide == 0
                ? 'DRAW!'
                : 'P' + connection.winnerSide + ' WINS!!!';

    // The qualifier goes on its own small line, not in the headline. A win awarded because
    // the other player never came back is a different thing from one that was played out,
    // and saying nothing would present it as the same.
    const note = document.getElementById('game-over-note');
    if (note) {
        note.innerText = connection.winnerSide == 0
            ? 'BOTH PLAYERS DISCONNECTED'
            : 'OPPONENT DISCONNECTED - WIN BY DEFAULT';
        note.classList.toggle('hidden', !connection.endedByDisconnect);
    }
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
        endGameShow.reset();
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
            // Must run before the draw: it writes this frame's offsets and facings.
            endGameShow.update(connection.latestState);
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