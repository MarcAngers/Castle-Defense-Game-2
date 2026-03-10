import connection from './game-connection.js';
import loader from './asset-loader.js';

export default class GadgetManager {
    constructor(view) {
        this.view = view;
        
        this.activeGadgetId = null;
        this.isTargeted = true;
        
        // World coordinate (includes camera panning) for the 1D Server Targeting
        this.crosshairWorldX = 0;
        
        // Raw screen coordinates (ignores camera) so the icon follows the mouse perfectly
        this.cursorLogicalX = 0;
        this.cursorLogicalY = 0; 

        // --- INPUT LISTENERS ---
        window.addEventListener('mousemove', this.onMouseMove);
        this.view.canvas.addEventListener('click', this.onClick);
        window.addEventListener('contextmenu', this.onRightClick);
    }

    activateTargeting(gadgetId) {
        this.activeGadgetId = gadgetId;
        
        const def = loader.assets['gadgetData'][gadgetId];
        
        // "1" = Targeted (Nuke/Snipe), "0" = Untargeted (Wave/Heal)
        this.isTargeted = String(def.targeted) === "1"; 
    }

    cancelTargeting() {
        this.activeGadgetId = null;
    }

    onRightClick = (e) => {
        if (this.activeGadgetId) {
            e.preventDefault(); 
            this.cancelTargeting();
        }
    }

    onMouseMove = (e) => {
        if (!this.activeGadgetId) return;

        // 1. Raw Screen Coordinates
        this.cursorLogicalX = this.view.getX(e);
        
        const physicalY = e.clientY || (e.touches && e.touches[0].clientY) || 0;
        this.cursorLogicalY = physicalY / this.view.scale;
        
        // 2. World Coordinate (Screen X + Camera Offset) for the server
        this.crosshairWorldX = this.cursorLogicalX + this.view.cameraX;
    }

    onClick = (e) => {
        if (!this.activeGadgetId) return;

        // Prevent firing if they were just trying to pan the camera
        const currentX = this.view.getX(e);
        if (Math.abs(this.view.startX - currentX) > 10) return;

        let finalTargetX = 0;

        if (this.isTargeted) {
            // Only targeted gadgets care about the battlefield bounds
            finalTargetX = Math.min(Math.max(this.crosshairWorldX, 200), 1800);
        }

        // Send it to the server! (Untargeted gadgets just send 0, which the server ignores)
        connection.useGadget(this.activeGadgetId, finalTargetX);

        this.cancelTargeting();
    }
}