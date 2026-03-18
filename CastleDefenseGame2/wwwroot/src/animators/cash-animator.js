import loader from '../asset-loader.js';

export default class CashAnimator {
    // Make sure we accept the level parameter (even if we don't use targetX/targetId)
    constructor(side, targetX, targetId, level = 1) {
        this.side = side;
        this.level = level;
        
        // "In front of the allied castle" - shifted ~100px forward from the castle locations
        this.dropX = this.side === 1 ? 300 : 1700; 
        
        // Starts above the top of the screen, lands at ground level
        this.startY = -50;
        this.targetY = 360;

        // --- FETCH THE DYNAMIC CASH AMOUNT ---
        // Level 3 drops multiple Level 2 crates, so it shares the "cash_2" data
        const dataKey = this.level === 1 ? 'cash' : 'cash_2';
        const gadgetData = loader.assets.gadgetData[dataKey];

        // Grab the BaseValue (falling back to 100 just in case the dictionary isn't loaded yet)
        this.amount = gadgetData ? (gadgetData.basevalue || gadgetData.BaseValue) : 100; 

        this.timer = 0;
        // 2s drop + 1.5s floating text = 3.5s total duration
        this.duration = 3500;   
        this.isFinished = false;

        this.shakeX = 0;
        this.shakeY = 0;
    }

    update(deltaTime) {
        this.timer += deltaTime;

        if (this.timer >= this.duration) {
            this.isFinished = true;
        }
    }

    draw(ctx, state) {
        ctx.save();

        // --- PHASE 1: THE PARACHUTE DROP (0ms to 2000ms) ---
        if (this.timer < 2000) {
            // Include our image fallback logic here too!
            const imgKey = this.level === 1 ? 'cash' : `cash_${this.level}`;
            const cashImg = loader.assets.gadgets[imgKey] || loader.assets.gadgets['cash']; 
            
            if (!cashImg) {
                ctx.restore();
                return;
            }

            const t = this.timer / 2000; // 0.0 to 1.0 over 2 seconds

            // Linear drop for Y
            const currentY = this.startY + ((this.targetY - this.startY) * t);

            // Add a gentle sway for the parachute effect!
            const swayX = Math.sin(this.timer / 300) * 30;
            const currentX = this.dropX + swayX;

            ctx.translate(currentX, currentY);
            
            // Updated to the 75x75 asset dimensions!
            // Offset by half-width (-37.5) and full-height (-75) to anchor to the floor.
            ctx.drawImage(cashImg, -37.5, -75, 75, 75);
        } 
        // --- PHASE 2: THE FLOATING TEXT (2000ms to 3500ms) ---
        else {
            const textTimer = this.timer - 2000;
            const t = textTimer / 1500; // 0.0 to 1.0 over 1.5 seconds

            // Text floats UP 60px from the ground over the 1.5 seconds
            const currentY = this.targetY - (t * 60); 

            // Fade out smoothly
            ctx.globalAlpha = 1.0 - t;

            ctx.translate(this.dropX, currentY);

            // Updated typography with retro pixel font and high-saturation yellow
            ctx.font = '32px "Press Start 2P", cursive'; 
            ctx.fillStyle = '#FFFF00';    
            ctx.strokeStyle = '#000000';  
            ctx.lineWidth = 4;
            ctx.textAlign = 'center';

            const textString = `+$${this.amount}`;
            
            // Draw the outline first, then fill the inside
            ctx.strokeText(textString, 0, 0);
            ctx.fillText(textString, 0, 0);
        }

        ctx.restore();
    }
}