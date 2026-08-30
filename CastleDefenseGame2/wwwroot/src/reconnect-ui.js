import connection from './game-connection.js';

/// The rejoin prompt (#rejoin-prompt in index.html).
///
/// WHY THIS IS NOT A VIEW. Every other screen is markup the router fetches and injects,
/// which is fine for a screen the player navigates to -- but this one has to appear on a
/// page that just finished loading from scratch, over whatever the router happens to have
/// put up, and specifically because the previous page went away. So it lives in index.html
/// and is driven from here.
///
/// The countdown ticks locally at 1Hz between server messages purely so the number moves
/// smoothly; every "GamePaused" resets it to the server's figure, and it is the server's
/// deadline that actually ends the game.
class ReconnectUi {
    constructor() {
        this.timerId = null;
        this.secondsRemaining = 0;
        // Past the 60s mark: the seat is still held, but the opponent can end the game at
        // any moment, so the prompt stops counting down and starts saying "hurry".
        this.claimable = false;
        this.assetsReady = Promise.resolve();
    }

    /// `assetsReady` resolves when the sprite/CSV load finishes. The prompt is deliberately
    /// allowed to appear before that -- see script.js -- so the rejoin itself waits on it,
    /// otherwise the game screen could be entered with no roster to draw from.
    init = (assetsReady) => {
        if (assetsReady) this.assetsReady = assetsReady;

        const btnRejoin = document.getElementById('btnRejoinGame');
        const btnAbandon = document.getElementById('btnAbandonGame');
        if (!btnRejoin || !btnAbandon) return;

        btnRejoin.onclick = async () => {
            btnRejoin.disabled = true;
            this.showMessage('Rejoining...');
            await this.assetsReady;
            const ok = await connection.rejoin();
            if (!ok) {
                btnRejoin.disabled = false;
                this.showMessage('Could not rejoin that game.');
            } else {
                this.hide();
            }
        };

        btnAbandon.onclick = async () => {
            this.hide();
            await connection.abandonPendingSession();
        };

        // `available === false` is the common case -- no stored game, or one already
        // resolved -- and must stay silent.
        connection.rejoinPromptCallback = (available, secondsRemaining, message, claimable) => {
            if (!available) {
                // A FAILURE MESSAGE MAY NOT RESURRECT A DISMISSED PROMPT. Abandon used to
                // draw a "RejoinFailed" reply from the server, which landed here and
                // re-opened the modal the button had just closed -- so it took two presses
                // to go away. The server no longer replies to Abandon, and this no longer
                // shows anything the player is not already looking at.
                if (message && this.isVisible()) {
                    this.showMessage(message);
                    const btn = document.getElementById('btnRejoinGame');
                    if (btn) btn.disabled = true;
                } else {
                    this.hide();
                }
                return;
            }
            this.show(secondsRemaining, claimable);
        };

        // Retries exhausted with the page still open. There is a game waiting and no
        // socket to reach it with, so offer the same prompt -- pressing Rejoin restarts
        // the connection attempt from a fresh invoke.
        connection.connectionLostCallback = () => {
            this.show(connection.pauseSecondsRemaining || connection.graceSeconds);
            this.showMessage('Connection lost.');
        };
    }

    /// Ask the server whether the game stored in localStorage is still live. Safe to call
    /// unconditionally: with nothing stored it does not even reach the network.
    check = async () => {
        await connection.checkPendingSession();
    }

    show = (secondsRemaining, claimable) => {
        const prompt = document.getElementById('rejoin-prompt');
        if (!prompt) return;

        const saved = connection.loadSession();
        const idEl = document.getElementById('rejoin-game-id');
        if (idEl && saved) idEl.innerText = saved.gameId;

        this.showMessage('You were disconnected.');

        const btnRejoin = document.getElementById('btnRejoinGame');
        if (btnRejoin) btnRejoin.disabled = false;

        this.secondsRemaining = Math.max(0, secondsRemaining | 0);
        this.claimable = !!claimable;
        this.renderTimer();
        prompt.classList.remove('hidden');

        clearInterval(this.timerId);
        this.timerId = setInterval(() => {
            if (this.secondsRemaining > 0) this.secondsRemaining--;
            else this.claimable = true;
            this.renderTimer();
        }, 1000);
    }

    isVisible = () => {
        const prompt = document.getElementById('rejoin-prompt');
        return !!prompt && !prompt.classList.contains('hidden');
    }

    hide = () => {
        clearInterval(this.timerId);
        this.timerId = null;
        const prompt = document.getElementById('rejoin-prompt');
        if (prompt) prompt.classList.add('hidden');
    }

    showMessage = (text, keepVisible) => {
        const el = document.getElementById('rejoin-message');
        if (el) el.innerText = text;
        if (!keepVisible) return;
        const prompt = document.getElementById('rejoin-prompt');
        if (prompt) prompt.classList.remove('hidden');
    }

    /// Two phases, because after 60 seconds a countdown would be a lie. Until then the seat
    /// is guaranteed and the number is how long is left; after it the seat is still there
    /// but the opponent may end the game at any moment, so the honest thing to show is that
    /// -- and the Rejoin button stays live, since rejoining still works right up until they
    /// do (or until the 30-minute ceiling).
    renderTimer = () => {
        // Deliberately does NOT go through #rejoin-timer: the claimable phase replaces that
        // span entirely, so anything that required it to exist would stop updating the
        // moment the countdown ran out.
        const line = document.querySelector('.rejoin-countdown');
        if (!line) return;
        line.innerText = this.claimable
            ? 'REJOIN NOW'
            : `${this.secondsRemaining}s TO REJOIN`;

        const note = document.getElementById('rejoin-message');
        if (note && this.claimable) note.innerText = 'Your opponent may end the game.';
    }
}

const reconnectUi = new ReconnectUi();
export default reconnectUi;
