import loader from './asset-loader.js';
import AnimationManager from './animation-manager.js';
import GadgetManager from './gadget-manager.js';
import VisualUnit, { KNOCKBACK_DURATION_MS, LOW_GRAVITY_KNOCKBACK_DURATION_MS } from './visual-unit.js';
import connection from './game-connection.js';
import atmosphere from './atmosphere.js';

// The look of a shadow map. Defined once and used by every layer that has to match it --
// background, foreground and the ambient layer between them -- because a layer left out of
// the greying is instantly obvious, and three hand-copied filter strings is how that
// happens. The Collection's shadow tile carries its own copy in map-info.css, which is
// noted there.
const SHADOW_FILTER = 'grayscale(100%) brightness(50%) contrast(140%)';

// Does this device have a pointer that can hover -- i.e. a real cursor for an icon to sit
// under? Cached as a live MediaQueryList rather than re-queried, because it is read inside
// the draw loop; `.matches` updates itself if the pointer situation changes, so caching it
// costs nothing in correctness. Deliberately the SAME query that gates the :hover rules in
// global-styles.css and the unit tooltip in game.js, so all three agree on what a pointer is.
const CAN_HOVER = window.matchMedia('(hover: hover)');

// ── THE AUTO-SPAWNER ────────────────────────────────────────────────────────────────────
//
// A machine bolted to the side of each castle, drawn only once its upgrade has been bought.
//
// GEOMETRY IS SPECIFIED RELATIVE TO THE CASTLE'S BOTTOM-LEFT CORNER, in the artist's
// cartesian convention (y UP), because that is how the art was authored and it is the only
// frame in which "it lines up with the hole in the castle wall" is a stable statement. The
// castle sprite is 200x200 and drawn at a fixed spot, so the conversion to canvas
// coordinates (y DOWN) happens in exactly one place, in drawAutoSpawner.
const AUTO_SPAWNER = {
    SPRITE_W: 74,
    SPRITE_H: 62,
    CASTLE_SIZE: 200,
    // Bottom-left of the machine, from the bottom-left of the castle. The machine is 74 wide
    // starting at 151, so it deliberately overhangs the castle's 200px edge by 25px -- that
    // overhang is the part that reads as "coming out of the side".
    OFFSET_X: 151,
    OFFSET_Y: 11,
    // Bottom-left of the level NUMBER, from the bottom-left of the machine. Lands in the
    // blank half of the white panel, directly under the "Lv." that is painted into the art.
    NUM_X: 2,
    NUM_Y: 15,
    // 18px, matching every other number the game draws (View.drawNumber). Press Start 2P is
    // very close to 1:1 -- measured in-browser, an 18px font renders a 17px-tall, 18px-wide
    // digit -- so "18px high" and "18px font" are the same request here.
    NUM_FONT_PX: 18,
    // How much room the number actually has. The white panel runs from x=1 to x=34 at the
    // top of the number band but narrows to x=28 at the bottom, so measuring from NUM_X the
    // safe width is 26. One digit is 18 and fits; TWO are 36 and do not, and levels 10-19
    // are more than half the ladder -- so a two-digit level is scaled down to fit rather
    // than allowed to spill off the panel onto the conveyor.
    NUM_MAX_W: 26,

    // Conveyor loop: frames swap this often. Fast enough to read as running machinery,
    // slow enough that the belt segments are legible rather than a blur.
    BELT_MS: 110,
    // How long the lever stays pulled (frames 3/4) after a spawn. Short on purpose: the
    // throw should SNAP back, and at 220 it read as the lever resting in the pulled
    // position rather than being yanked.
    LEVER_MS: 110,

    // Vibration. Grows with level so a maxed machine visibly shakes itself apart while a
    // level 1 one only hums. Two incommensurate frequencies so the motion never settles
    // into a readable back-and-forth -- that reads as a buzz rather than a wobble.
    //
    // THE RAMP IS CUBIC, NOT LINEAR. Linear made the low levels far too violent for what
    // is meant to be a single small conveyor: half the ladder sat above half the maximum
    // amplitude. Cubing keeps the top end exactly where it is -- which is the part worth
    // keeping -- while collapsing the bottom, so level 1 is a hum you have to look for and
    // the shaking only becomes obvious in the last third.
    SHAKE_MIN: 0.12,   // px at level 1
    SHAKE_MAX: 2.6,    // px at max level, unchanged
    SHAKE_CURVE: 3,    // 1 = linear; higher pushes the growth later up the ladder
    SHAKE_HZ_X: 13,
    SHAKE_HZ_Y: 17,
};

// Units per second at each auto-spawner level -- index 0 is "not bought".
// MIRRORS PlayerState.AutoSpawnCycles, where the rate IS the cycle length. Duplicated here
// rather than sent over the wire because the client only needs it to pace an animation: the
// lever pull is timed to the real spawn RATE but is not phase-locked to the server's actual
// spawn ticks, which would cost a wire field per player per tick to do properly and would
// still be a frame or two out from what the player sees.
const AUTO_SPAWNER_RATE = [0, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6];
const MAX_AUTO_SPAWN_LEVEL = 19;

class View {
    constructor() {
        this.visualUnits = {};
        this.currentTime = performance.now();
        this.lastTime = performance.now();

        this.canvas = document.getElementById('bgCanvas');

        this.canvas.width = window.innerWidth;
        this.canvas.height = window.innerHeight;

        this.ctx = this.canvas.getContext('2d');
        this.ctx.imageSmoothingEnabled = false;

        this.MAP_WIDTH = 2000;
        // One-liner to do essentially the same math as movePan() and resize()
        this.cameraX = 0;
        // Decorative units drawn at the castles during the pre-game countdown. Set by the
        // game screen each frame; empty at every other time, including on every other screen.
        this.preGameSquad = [];

        // Auto-spawner animation clocks, one per side. Kept on the view rather than derived
        // from the game tick because these pace a LOOP the player watches, not a simulation
        // step. The view is a single long-lived instance and nothing resets these between
        // games, which is fine: they are three free-running counters with no dependence on
        // game identity, so a stale value only shifts the belt phase by a frame.
        this.autoSpawnAnim = {
            1: { beltMs: 0, spawnMs: 0, leverMs: 0 },
            2: { beltMs: 0, spawnMs: 0, leverMs: 0 },
        };

        // Input State for Panning
        this.isDragging = false;
        this.startX = 0;
        this.scrollStartCameraX = 0;

        // --- CAMERA INPUT HANDLERS ---
        this.canvas.addEventListener('mousedown', this.startPan);
        this.canvas.addEventListener('touchstart', this.startPan, {passive: false});

        window.addEventListener('mousemove', this.movePan);
        window.addEventListener('touchmove', this.movePan, {passive: false});

        window.addEventListener('mouseup', this.endPan);
        window.addEventListener('touchend', this.endPan);

        this.latestState = null;
        this.mapColour = null;


        window.addEventListener('resize', this.resize);

        this.gadgetManager = new GadgetManager(this);

        // Scratch canvas for status effects:
        this.scratchCanvas = document.createElement('canvas');
        this.scratchCanvas.width = 250; // Make this slightly bigger than your biggest unit
        this.scratchCanvas.height = 250;
        this.scratchCtx = this.scratchCanvas.getContext('2d');
        this.scratchCtx.imageSmoothingEnabled = false;

        // Animations:
        this.animationManager = new AnimationManager();
        connection.onPlayGadgetAnimation((gadgetId, side, position, targetId) => {
            this.animationManager.triggerAnimation(gadgetId, side, position, targetId);
        });

        // Statuses:
        this.StatusColorMap = {
            "Burn": [255, 0, 0, 0.5],           // Red
            "Freeze": [0, 162, 232, 0.5],       // Blue
            "Poison": [163, 73, 164, 0.5],      // Purple
            "Rage": [136, 0, 21, 0.5],          // Burgundy
            "Heal": [0, 255, 50, 0.5],          // Green
            "Speed": [255, 255, 255, 0.5],      // White
            "Slow": [112, 146, 190, 0.5],       // Blue-Gray
            "Blackhole": [0, 0, 0, 0.5],        // Black
        };

        this.tierColourCache = {};
    }

    startPan = (e) => {
        this.isDragging = true;
        this.startX = this.getX(e);
        this.scrollStartCameraX = this.cameraX;
    }

    movePan = (e) => {
        // --- TARGETING LOGIC ---
        if (this.targetingGadgetId) {
            // 1. Get Screen Space (using your existing getX method)
            const screenLogicalX = this.getX(e);
            
            // 2. Convert to World Space (Add the camera offset)
            this.crosshairWorldX = screenLogicalX + this.cameraX;
        }

        if (!this.isDragging) return;
        
        // 1. getX() ALREADY returns Logical Game Units!
        const currentX = this.getX(e);
        const diff = this.startX - currentX; // This is a purely logical difference
        
        // 2. Apply directly to camera
        this.cameraX = this.scrollStartCameraX + diff;
        
        // 3. Clamp using purely logical coordinates
        const maxScroll = Math.max(0, this.MAP_WIDTH - this.logicalScreenWidth);
        this.cameraX = Math.max(0, Math.min(this.cameraX, maxScroll));
    }

    endPan = () => {
        this.isDragging = false;
    }

    panTo = (targetLogicalX) => {
        const maxScroll = Math.max(0, this.MAP_WIDTH - this.logicalScreenWidth);
        this.cameraX = Math.max(0, Math.min(targetLogicalX, maxScroll));
    }

    // Helper for Mouse vs Touch coordinates
    getX = (e) => {
        if (!e) return 0;
        
        let physicalX = 0;
        
        if (e.touches && e.touches.length > 0) {
            physicalX = e.touches[0].clientX;
        } else if (e.changedTouches && e.changedTouches.length > 0) {
            physicalX = e.changedTouches[0].clientX;
        } else {
            physicalX = e.clientX || 0;
        }

        // Convert to Logical Space immediately!
        return physicalX / this.scale;
    }

    clear() {
        this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    }

    draw = () => {
        if (this.latestState) {
            this.drawGameState(this.latestState);
        } else if (this.mapColour) {
            this.ctx.save();
            this.ctx.translate(-this.cameraX / 2, 0);
            this.drawBackground(this.mapColour);
            // Menu atmosphere. This runs for whichever map a menu screen has put up -- the
            // main menu's white and the Collection's purple -- and animates because those
            // screens are the ones menu-meander keeps a rAF loop running for. A menu with
            // no loop simply shows a still sky, which is what it showed before.
            atmosphere.render(this.ctx, this.mapColour, this.activeShadowFilter());
            this.ctx.restore();
            this.drawForeground(this.mapColour);
            atmosphere.renderOverlay(this.ctx, this.mapColour, this.logicalScreenWidth,
                                     this.cameraX, this.activeShadowFilter());
        } else {
            this.drawBackground('white');
            atmosphere.render(this.ctx, 'white', this.activeShadowFilter());
            this.drawForeground('white');
            atmosphere.renderOverlay(this.ctx, 'white', this.logicalScreenWidth,
                                     this.cameraX, this.activeShadowFilter());
        }
    }

    drawGameState(state) {
        this.clear();
        this.latestState = state;
        
        this.currentTime = performance.now();
        const deltaTime = this.currentTime - this.lastTime;
        this.lastTime = this.currentTime;

        // --- Draw Background (Paralax) ---
        const mapColour = loader.assets.teamList[this.latestState.map];
        this.ctx.save();
        this.ctx.translate(-this.cameraX / 2, 0);
        this.drawBackground(mapColour);
        // Ambient layer, inside the SAME transform as the background: it should scroll with
        // the sky it belongs to, sit behind the foreground and the units, and grey out with
        // the rest of the scene on a shadow map.
        atmosphere.render(this.ctx, mapColour, this.activeShadowFilter());
        this.ctx.restore();

        this.animationManager.update(deltaTime, state);

        this.ctx.save();
        
        // --- APPLY CAMERA TRANSFORM & SCREEN SHAKE ---
        this.ctx.translate(
            -this.cameraX + this.animationManager.shakeX, 
            this.animationManager.shakeY
        );

        // --- Draw Foreground ---
        this.drawForeground(loader.assets.teamList[this.latestState.map]);

        // --- DRAW GADGET EFFECTS ---
        this.animationManager.draw(this.ctx, state);
        
        // The auto-spawners, BEFORE the castles so the machine is embedded in the wall it is
        // bolted to rather than pasted over it -- the overlapping half tucks behind the
        // stonework and only the conveyor projects into the field. Safe for the readout:
        // the castle sprite is barely 5% opaque across the machine's panel, so the "Lv."
        // and the level number still read through. Same camera transform as the castles.
        this.drawAutoSpawner(state.player1, 1, deltaTime);
        this.drawAutoSpawner(state.player2, 2, deltaTime);

        // Draw Castles
        this.drawCastle(state.player1, 1); 
        this.drawCastle(state.player2, 2);

        // The pre-game squad waiting outside each castle. Drawn HERE, inside the camera
        // transform, because they stand at fixed world positions and the intro pans across
        // them -- drawMenuUnit's other caller (menu-meander) draws in screen space, where
        // there is no camera to follow. Behind the real units on purpose: once the battle
        // opens, a unit running out should pass in front of the ones still waiting.
        for (const squadUnit of this.preGameSquad) this.drawMenuUnit(squadUnit);

        // Draw Units
        //
        // Black is the low-gravity map (MapEffects.LowGravityKnockback), where the server
        // staggers a knocked-back unit for two seconds instead of one -- so the flight arc
        // has to be drawn over two seconds to match, or units act while still in the air.
        // A shadow map is never Black (GameState's constructor rules it out), so the shadow
        // flag has to be checked rather than the colour alone.
        const lowGravity = loader.assets.teamList[state.map] === 'black' && !state.shadowMap;
        const knockbackMs = lowGravity ? LOW_GRAVITY_KNOCKBACK_DURATION_MS : KNOCKBACK_DURATION_MS;

        state.units.forEach(unit => {
            if (!this.visualUnits[unit.instanceId]) {
                this.visualUnits[unit.instanceId] = new VisualUnit(unit);
            }
            const visualUnit = this.visualUnits[unit.instanceId];
            visualUnit.knockbackDuration = knockbackMs;
            visualUnit.update(unit, deltaTime);

            // Losers that have run off the map after the game ended stay in the state
            // (nothing is sending updates any more) but must stop being drawn.
            if (!visualUnit.hidden) this.drawUnit(unit, visualUnit);
        });

        // --- 1. DRAW TARGETED CROSSHAIR (World Space) ---
        if (this.gadgetManager && this.gadgetManager.activeGadgetId && this.gadgetManager.isTargeted) {
            this.ctx.save();
            
            this.ctx.strokeStyle = 'rgba(255, 50, 50, 0.8)';
            this.ctx.lineWidth = 4;
            this.ctx.setLineDash([15, 10]); 
            
            this.ctx.beginPath();
            this.ctx.moveTo(this.gadgetManager.crosshairWorldX, 0);
            this.ctx.lineTo(this.gadgetManager.crosshairWorldX, 500); 
            this.ctx.stroke();
            
            this.ctx.fillStyle = 'rgba(255, 50, 50, 0.5)';
            this.ctx.beginPath();
            this.ctx.arc(this.gadgetManager.crosshairWorldX, 400, 20, 0, Math.PI * 2);
            this.ctx.fill();

            this.ctx.restore();
        }

        // --- RESTORE CAMERA ---
        this.ctx.restore(); // Go back to "Screen" coords (0,0 is top left)

        // Weather between the player and the world -- rain. Screen space on purpose: it has
        // to fill the view wherever the camera is pointed, where a world-space sheet would
        // slide sideways every time the player panned. Before the gadget cursor below, so
        // the cursor stays on top of the storm.
        // cameraX is passed because the splash marks are world-anchored -- they belong to
        // the plank they landed on, not to a place on the screen.
        atmosphere.renderOverlay(this.ctx, mapColour, this.logicalScreenWidth, this.cameraX,
                                 this.activeShadowFilter());

        // --- 2. DRAW UNTARGETED ICON (Screen Space) ---
        // Drawn AFTER the camera is restored so it ignores panning and perfectly follows the mouse!
        if (this.gadgetManager && this.gadgetManager.activeGadgetId && !this.gadgetManager.isTargeted) {
                const baseId = this.gadgetManager.activeGadgetId.split('_')[0].toLowerCase();
                const img = loader.assets.gadgets[this.activeGadgetId] || loader.assets.gadgets[baseId];            
            if (img) {
                // WHERE THE ICON GOES depends on whether there is a cursor to follow.
                //
                // With a mouse, cursorLogicalX/Y track mousemove and the icon sits under the
                // pointer, which is the whole idea. A touch screen has no pointer -- but a tap
                // still emits a SYNTHETIC mousemove, so cursorLogical ends up wherever the
                // player last happened to touch, which for an untargeted cast is the gadget
                // button itself down in the HUD. The icon appeared there, in the corner, next
                // to the button rather than out on the field. Worse before the first tap of a
                // session: cursorLogical is still its initial 0,0, so the icon sat in the
                // top-left corner of the screen.
                //
                // Centring is the right answer rather than a workaround, because an untargeted
                // gadget HAS no position -- onClick sends 0 and the server ignores it. The icon
                // is pure feedback meaning "this is armed, tap anywhere to cast", so it belongs
                // where the player is already looking, not at an accident of their last tap.
                //
                // Targeted gadgets are deliberately NOT changed: their tap IS the aim, so the
                // synthetic mousemove that sets crosshairWorldX is doing real work there.
                const hasCursor = CAN_HOVER.matches;
                const iconX = hasCursor ? this.gadgetManager.cursorLogicalX : this.logicalScreenWidth / 2;
                const iconY = hasCursor ? this.gadgetManager.cursorLogicalY : this.logicalScreenHeight / 2;

                this.ctx.save();
                this.ctx.globalAlpha = 0.7; 
                // Draw exactly centered on the chosen logical screen coordinates
                this.ctx.drawImage(img, iconX - 25, iconY - 25, 50, 50);
                this.ctx.restore();
            }
        }

        // Garbage Collection: Remove VisualUnits for dead server units
        const currentServerUnitIds = new Set(state.units.map(u => u.instanceId));
        for (const id in this.visualUnits) {
            if (!currentServerUnitIds.has(id)) {
                delete this.visualUnits[id];
            }
        }
    }

    drawUnit(unit, visualUnit) {
        const img = loader.assets[unit.definitionId];
    
        const x = unit.position + (visualUnit ? visualUnit.visualOffsetX + visualUnit.endGameOffsetX : 0);
        const y = unit.yPosition + (visualUnit ? visualUnit.visualOffsetY + visualUnit.endGameOffsetY : 0);
        const width = unit.width || 50;
        const height = unit.height || 50;
        const rotation = visualUnit ? visualUnit.visualRotation : 0;

        // APPEARANCE ONLY. width/height above are the unit's LOGICAL size -- what the engine
        // fights with -- and must not be scaled: making them per-instance broke combat
        // outright (see Unit.Width). visualScale only changes how big the sprite is drawn,
        // so a big weirdo's edges deliberately do not line up with where it actually
        // reaches. 1 for every other unit.
        const scale = unit.visualScale || 1;
        const drawW = width * scale;
        const drawH = height * scale;
        // Anchored at the BOTTOM of the logical box rather than its centre, so a scaled
        // sprite keeps its feet on the ground instead of sinking or floating.
        const drawTop = (height / 2) - drawH;

        let isInvulnerable = false;

        if (img) {
            this.ctx.save();

            const centerX = x + (width / 2);
            const centerY = y + (height / 2);
            this.ctx.translate(centerX, centerY);
            
            this.ctx.rotate(rotation);

            // --- STATUS TINTING LOGIC ---
            let imageToDraw = img; 
            let totalR = 0, totalG = 0, totalB = 0, totalA = 0;
            let activeTintCount = 0;

            if (unit.statuses && unit.statuses.length > 0) {
                for (const status of unit.statuses) {
                    if (this.StatusColorMap[status.name]) {
                        const [r, g, b, a] = this.StatusColorMap[status.name];
                        totalR += r;
                        totalG += g;
                        totalB += b;
                        totalA += a;
                        activeTintCount++;
                    } else if (status.name == 'Invulnerable') {
                        isInvulnerable = true;
                    }
                }
            }

            if (activeTintCount > 0) {
                // Average the colors together!
                const finalR = Math.floor(totalR / activeTintCount);
                const finalG = Math.floor(totalG / activeTintCount);
                const finalB = Math.floor(totalB / activeTintCount);
                const finalA = totalA / activeTintCount;

                this.scratchCanvas.width = width;
                this.scratchCanvas.height = height;

                this.scratchCtx.globalCompositeOperation = 'source-over';
                this.scratchCtx.drawImage(img, 0, 0, width, height);
                this.scratchCtx.globalCompositeOperation = 'source-atop';
                
                // Apply our newly mixed color
                this.scratchCtx.fillStyle = `rgba(${finalR}, ${finalG}, ${finalB}, ${finalA})`;
                this.scratchCtx.fillRect(0, 0, width, height);

                imageToDraw = this.scratchCanvas;
            }
            
            // Player 1 faces right and player 2 faces left, unless the end-game show has
            // taken over -- losers turn tail, winners dance, a draw leaves them looking about.
            const facing = (visualUnit && visualUnit.facingOverride)
                ? visualUnit.facingOverride
                : (unit.side === 1 ? 1 : -1);

            if (facing === -1) this.ctx.scale(-1, 1);
            this.ctx.drawImage(imageToDraw, -drawW / 2, drawTop, drawW, drawH);
            
            this.ctx.restore();
        } else {
            // Fallback Box
            this.ctx.fillStyle = unit.side === 1 ? 'red' : 'blue';
            this.ctx.fillRect(x + (width - drawW) / 2, y + height - drawH, drawW, drawH);
        }

        // --- DRAW STATUS PARTICLES ---
        if (visualUnit && visualUnit.particles && visualUnit.particles.length > 0) {
            this.ctx.save();

            const centerX = x + (width / 2);
            const centerY = y + (height / 2);
            this.ctx.translate(centerX, centerY);

            visualUnit.particles.forEach(p => {
                // Safely grab the image asset from the 'statuses' folder
                const particleImg = loader.assets['particles'] && loader.assets['particles'][p.imageKey];
                if (!particleImg) return;

                const progress = p.life / p.maxLife; // 1.0 down to 0.0
                
                // Fade out smoothly during the last half of its life
                this.ctx.globalAlpha = Math.min(1, progress * 2); 

                // Draw the particle centered on its calculated offset
                this.ctx.drawImage(
                    particleImg, 
                    p.offsetX - (p.size / 2), 
                    p.offsetY - (p.size / 2), 
                    p.size, 
                    p.size
                );
            });

            this.ctx.restore();
        }

        // Health Bar
        // Sits above the SPRITE, not above the logical box, so a scaled unit's bar still
        // reads as belonging to it rather than cutting across its head.
        this.drawHealthBar(x + (width - drawW) / 2 - 5, y + height - drawH - 10, drawW,
            unit.currentHealth, unit.maxHealth, unit.currentShield, unit.tier,
            loader.getUnitStats(unit.definitionId).team);
        // Draw health text for units? (might prefer 100hp segments similar to)
        // this.drawHealthText(x - 5, y - 10, width, unit.currentHealth, unit.maxHealth);

        // Invulnerability (divine_3) gets its own art and outranks a plain shield layer,
        // so a unit that has both shows the invulnerable one rather than two stacked overlays.
        if (isInvulnerable) {
            const divineShieldImage = loader.assets.gadgets['divine_3'];
            this.ctx.drawImage(divineShieldImage, x, y, width, height);
        } else if (unit.currentShield > 0) {
            const shieldImage = loader.assets.gadgets['divine'];
            this.ctx.drawImage(shieldImage, x, y, width, height);
        }
    }

    // Main-menu background wanderers (see menu-meander.js). Deliberately NOT drawUnit:
    // these are set dressing, not combatants, so they get no health bar, no tier number
    // and no status tinting -- just the sprite, flipped to face the way they are walking.
    drawMenuUnit = (unit) => {
        const img = loader.assets[unit.definitionId];
        if (!img) return;

        this.ctx.save();
        this.ctx.translate(unit.x + unit.width / 2, unit.y + unit.height / 2 + unit.hopOffset);
        // Sprites are drawn facing right, so only a leftward walk needs the flip.
        if (unit.facing === -1) this.ctx.scale(-1, 1);
        this.ctx.drawImage(img, -unit.width / 2, -unit.height / 2, unit.width, unit.height);
        this.ctx.restore();
    }

    drawHealthBar(x, y, spriteSize, currentHealth, maxHealth, currentShield, tier, colour) {
        let pct = currentHealth/maxHealth;
        let width = (spriteSize + 10) * pct;

        this.ctx.fillStyle = "lightgray";
        this.ctx.fillRect(x, y, spriteSize + 10, 5);

        if (pct > 0.75) {
            this.ctx.fillStyle = "limegreen";
        } else if (pct > 0.30) {
            this.ctx.fillStyle = "yellow";
        } else if (pct > 0.10) {
            this.ctx.fillStyle = "red";
        } else {
            this.ctx.fillStyle = "darkred";
        }

        this.ctx.fillRect(x, y, width, 5);

        if (tier) {
            this.drawNumber(x - 8, y + 12, tier, colour);
        }

        // Draw shield
        //
        // The bar is a PROPORTION OF MAX HEALTH, not of the shield's own grant, so the
        // same 1,000 HP shield reads as half a bar on a 2,000 HP castle and a twelfth of
        // one after repairs take it to 12,000 -- the shield value itself never moves, only
        // how much of the castle it now covers.
        //
        // THE 30px OVERHANG IS DELIBERATE. The cap sits past the health bar's own width
        // (spriteSize + 10) so that a shield worth MORE than max health visibly spills
        // over the end of the bar rather than sitting flush with it -- that overhang is
        // the only cue distinguishing "shielded to full" from "shielded beyond full",
        // which stacked divine casts reach easily.
        if (currentShield > 0) {
            let shieldPct = currentShield / maxHealth;
            let shieldWidth = Math.min((spriteSize + 10) * shieldPct, (spriteSize + 30));
            this.ctx.fillStyle = "cyan";
            this.ctx.fillRect(x, y - 5, shieldWidth, 5);
        }               
    }

    drawCastle(playerState, side) {
        const team = loader.assets.teamList[playerState.team];
        const castleImg = playerState.castleHealth > 0 ? loader.assets.buildings[team + '-castle'] : loader.assets.buildings['dead-castle'];
        if (!castleImg) return;

        const y = 200;
        let x = 50;
        
        this.ctx.save();
        
        if (side === 1) {
            this.ctx.drawImage(castleImg, x, y);

            this.ctx.restore(); // Restore coordinate system for the health bar

            this.drawHealthBar(x, y - 10, 200, playerState.castleHealth, playerState.castleMaxHealth, playerState.castleShield);
            this.drawHealthText(x, y - 10, 200, playerState.castleHealth, playerState.castleMaxHealth);
        } else {
            x = this.MAP_WIDTH - 50;
            
            this.ctx.translate(x, y);
            this.ctx.scale(-1, 1); 
            this.ctx.drawImage(castleImg, 0, 0);
            
            this.ctx.restore(); // Restore coordinate system for the health bar
            
            this.drawHealthBar(x - 200, y - 10, 200, playerState.castleHealth, playerState.castleMaxHealth, playerState.castleShield);
            this.drawHealthText(x - 200, y - 10, 200, playerState.castleHealth, playerState.castleMaxHealth);
        }

        // If the player is invulnerable, draw a divine shield over their castle:
        if (playerState.isInvulnerable) {
            const shieldImage = loader.assets.gadgets['divine_3'];
            if (x > 1000) x -= 200;
            this.ctx.drawImage(shieldImage, x, y, 200, 200);
        }
    }

    /// The auto-spawner bolted to a castle's flank: four-frame conveyor animation, a
    /// level-scaled vibration, and the level number painted into the panel.
    ///
    /// FRAME PAIRS. 1/2 are the idle belt loop; 3/4 are the same loop with the lever pulled.
    /// The machine runs 1<->2 continuously and swaps to 3<->4 for a moment each time it
    /// produces a unit, so the lever visibly throws as something comes off the belt.
    ///
    /// THE LEVER IS RATE-MATCHED, NOT PHASE-LOCKED. It fires on a client clock running at
    /// the level's real units-per-second, so the pull happens as often as a spawn really
    /// happens, but not necessarily on the same tick. Phase-locking would mean sending the
    /// engine's spawn accumulator every tick for both players, which is a per-tick wire cost
    /// (the wire was deliberately trimmed 3.95x) for a difference of a frame or two.
    drawAutoSpawner(playerState, side, deltaTime) {
        if (!playerState) return;
        const level = playerState.autoSpawnLevel | 0;
        if (level <= 0) return;
        // Nothing to bolt it to: the castle sprite is the ruin at this point.
        if (playerState.castleHealth <= 0) return;

        const A = AUTO_SPAWNER;
        const anim = this.autoSpawnAnim[side];
        if (!anim) return;

        // --- advance the clocks -------------------------------------------------------
        // deltaTime is clamped because a backgrounded tab returns with a huge delta, which
        // would otherwise burn through hundreds of belt frames in one step.
        const dt = Math.min(Math.max(deltaTime || 0, 0), 100);

        anim.beltMs += dt;
        const beltFrame = Math.floor(anim.beltMs / A.BELT_MS) % 2;   // 0 or 1

        const rate = AUTO_SPAWNER_RATE[Math.min(level, MAX_AUTO_SPAWN_LEVEL)] || 0;
        if (rate > 0) {
            const period = 1000 / rate;
            anim.spawnMs += dt;
            if (anim.spawnMs >= period) {
                // Modulo rather than reset to 0, so a rate faster than the frame rate does
                // not silently drop pulls.
                anim.spawnMs %= period;
                anim.leverMs = A.LEVER_MS;
            }
        }
        if (anim.leverMs > 0) anim.leverMs = Math.max(0, anim.leverMs - dt);

        // 1/2 while idle, 3/4 while the lever is thrown.
        const frameIndex = (anim.leverMs > 0 ? 3 : 1) + beltFrame;
        const img = loader.assets.buildings[`auto-spawner${frameIndex}`];
        if (!img) return;

        // --- vibration ----------------------------------------------------------------
        // Amplitude ramps linearly from SHAKE_MIN at level 1 to SHAKE_MAX at max level.
        const t = MAX_AUTO_SPAWN_LEVEL > 1 ? (level - 1) / (MAX_AUTO_SPAWN_LEVEL - 1) : 0;
        const amp = A.SHAKE_MIN + Math.pow(t, A.SHAKE_CURVE) * (A.SHAKE_MAX - A.SHAKE_MIN);
        const now = this.currentTime / 1000;
        const dx = amp * Math.sin(now * 2 * Math.PI * A.SHAKE_HZ_X);
        const dy = amp * Math.sin(now * 2 * Math.PI * A.SHAKE_HZ_Y + 1.3);

        // --- geometry ------------------------------------------------------------------
        // The castle occupies local (0,0)-(200,200) with y DOWN; drawCastle puts that box at
        // (50,200) for P1 and mirrors it about (MAP_WIDTH-50) for P2. Converting the artist's
        // y-UP offsets once, here, is what keeps the two seats from drifting apart.
        const localX = A.OFFSET_X;
        const localY = A.CASTLE_SIZE - A.OFFSET_Y - A.SPRITE_H;
        const castleTopY = 200;

        this.ctx.save();
        if (side === 1) {
            this.ctx.translate(50, castleTopY);
        } else {
            // Same mirror drawCastle uses, so the machine comes out of the flank that faces
            // the battlefield on both sides.
            this.ctx.translate(this.MAP_WIDTH - 50, castleTopY);
            this.ctx.scale(-1, 1);
        }
        this.ctx.drawImage(img, localX + dx, localY + dy);
        this.ctx.restore();

        // --- the level number ----------------------------------------------------------
        // DRAWN OUTSIDE THE MIRROR ON PURPOSE. Inside it, P2's number would render
        // back-to-front. Only its POSITION is mirrored; the glyphs are not.
        const numLocalX = localX + A.NUM_X;
        const numBaselineLocalY = A.CASTLE_SIZE - A.OFFSET_Y - A.NUM_Y;

        const text = `${level}`;

        this.ctx.save();
        // Fit the number to the panel: full size when it fits (every level below 10), scaled
        // down only when it does not. Chosen over a permanently smaller font so the common
        // case is drawn at the size the art was made for.
        this.ctx.font = `${A.NUM_FONT_PX}px "Press Start 2P", cursive`;
        const naturalW = this.ctx.measureText(text).width;
        if (naturalW > A.NUM_MAX_W) {
            const fitted = Math.max(1, Math.floor(A.NUM_FONT_PX * A.NUM_MAX_W / naturalW));
            this.ctx.font = `${fitted}px "Press Start 2P", cursive`;
        }
        this.ctx.textBaseline = 'alphabetic';
        this.ctx.fillStyle = 'black';
        this.ctx.strokeStyle = 'white';
        this.ctx.lineWidth = 3;

        let numX;
        if (side === 1) {
            this.ctx.textAlign = 'left';
            numX = 50 + numLocalX + dx;
        } else {
            // The panel is on the machine's other side once mirrored, so the text box hangs
            // to the LEFT of the mirrored anchor -- right-aligning puts it there without
            // having to measure the string.
            this.ctx.textAlign = 'right';
            numX = (this.MAP_WIDTH - 50) - numLocalX - dx;
        }
        const numY = castleTopY + numBaselineLocalY + dy;

        this.ctx.strokeText(text, numX, numY);
        this.ctx.fillText(text, numX, numY);
        this.ctx.restore();
    }

    drawNumber(x, y, tier, colour) {
        if (tier) {
            this.ctx.save();
            
            this.ctx.translate(x, y);

            this.ctx.font = '18px "Press Start 2P", cursive'; 
            
            // Call the new method using 'this'
            this.ctx.fillStyle = this.getTierColor(colour, tier);
            
            this.ctx.strokeStyle = 'white';  
            if (colour === 'white' || colour === 'yellow')
                this.ctx.strokeStyle = 'black';  
            this.ctx.lineWidth = 4;
            this.ctx.textAlign = 'center';

            const textString = `${tier}`;
            
            this.ctx.strokeText(textString, 0, 0);
            this.ctx.fillText(textString, 0, 0);

            this.ctx.restore();
        }
    }

    drawHealthText(x, y, spriteSize, currentHealth, maxHealth) {
        // Format with commas and ensure we don't display decimals if health gets fractional
        const healthText = `${Math.ceil(currentHealth).toLocaleString()}/${maxHealth.toLocaleString()}`;
        
        this.ctx.save();
        this.ctx.fillStyle = "white";
        this.ctx.strokeStyle = "black"; 
        this.ctx.lineWidth = 3; // Thick outline for readability
        
        // Feel free to swap this to your retro font if you prefer!
        this.ctx.font = '10px "Press Start 2P", cursive'; 
        this.ctx.textAlign = "center";
        
        // Center the text horizontally over the health bar
        const centerX = x + (spriteSize + 10) / 2;
        
        // Shift the Y position up so it sits right above the shield/bar
        const textY = y - 5; 

        // Draw outline first, then the white fill inside
        this.ctx.strokeText(healthText, centerX, textY);
        this.ctx.fillText(healthText, centerX, textY);

        this.ctx.restore();
    }

    /// The canvas filter for the map currently being drawn, or null. ONE place decides, so
    /// the background, the foreground and the ambient layer between them can never disagree
    /// about whether they are on a shadow map -- a layer left out of the greying is the most
    /// obvious bug this could have.
    ///
    /// The server is the only authority on it: menu screens have no state and are therefore
    /// never shadowed.
    activeShadowFilter = () => (this.latestState?.shadowMap ? SHADOW_FILTER : null)

    /// Map art that has not loaded yet, warned about ONCE per bucket+colour.
    ///
    /// Once per key rather than per call, because these run inside the draw loop: a plain
    /// console.warn here would print sixty lines a second. Same reasoning the atmosphere
    /// manifest is built on -- see loadAtmosphere.
    #missingArtWarned = new Set();

    /// Fetch one map layer, or null if it is not loaded.
    ///
    /// WHY THIS GUARD EXISTS: `resize` is wired up in the constructor, which runs at module
    /// import time -- BEFORE script.js has started loader.loadAll(). Any resize event during
    /// the asset load (a tab being laid out, a phone rotating, a window being dragged while
    /// the loading screen is up) therefore reached draw() with no images loaded at all. With
    /// no latestState and no mapColour, draw() falls through to its 'white' fallback, and
    /// `loader.get('background')['white']` was undefined -- so drawImage threw
    /// "The provided value is not of type (CSSImageValue or ...)" and the error escaped the
    /// resize handler uncaught. Reproducible on a clean page load.
    ///
    /// The guard is worth more than silencing that. drawBackground runs EARLY in
    /// drawGameState, so a throw there aborts the whole frame -- no atmosphere, no
    /// foreground, no units, no castles. Skipping one missing layer degrades to a missing
    /// backdrop instead of a blank screen.
    #mapLayer = (bucket, colour) => {
        const img = loader.get(bucket)?.[colour];
        if (img) return img;
        const key = `${bucket}/${colour}`;
        if (!this.#missingArtWarned.has(key)) {
            this.#missingArtWarned.add(key);
            console.warn(`[view] map art not loaded yet, skipping: ${key}`);
        }
        return null;
    }

    drawBackground = (colour) => {
        const img = this.#mapLayer('background', colour);
        if (!img) return;

        const filter = this.activeShadowFilter();
        if (filter) this.ctx.filter = filter;

        this.ctx.drawImage(img, 0, 0);
        this.ctx.filter = 'none';
    }
    drawForeground = (colour) => {
        const img = this.#mapLayer('foreground', colour);
        if (!img) return;

        const filter = this.activeShadowFilter();
        if (filter) this.ctx.filter = filter;

        this.ctx.drawImage(img, 0, 0);
        this.ctx.filter = 'none';
    }

    resize = () => {
        const logicalHeight = 500;
        const windowWidth = window.innerWidth;
        const windowHeight = window.innerHeight;

        // 1. Calculate the fractional scale
        this.scale = windowHeight / logicalHeight;
        this.logicalScreenWidth = windowWidth / this.scale;
        // Stored alongside the width because the untargeted gadget icon needs the centre of
        // the logical canvas, and deriving it from canvas.height/scale reads as arithmetic
        // rather than as "the middle of the screen".
        this.logicalScreenHeight = logicalHeight;

        // 2. Set the CANVAS Internal Resolution (The buffer size)
        // We match the window size exactly so the browser doesn't stretch anything via CSS
        this.canvas.width = windowWidth;
        this.canvas.height = windowHeight;

        // 3. Set the CSS display size (just to be safe, though 2 covers it)
        this.canvas.style.width = `${windowWidth}px`;
        this.canvas.style.height = `${windowHeight}px`;

        // 4. CRITICAL: Turn off smoothing
        // Browser resets this on resize, so we must re-apply it every time.
        this.ctx.imageSmoothingEnabled = false;

        this.ctx.scale(this.scale, this.scale);

        // 5. Restore previous canvas content
        this.draw();

        return { 
            scale: this.scale,
            width: this.logicalScreenWidth, 
            height: logicalHeight 
        };
    }

    // Helper function to get tier number colours
    getTierColor(baseColor, tier) {
        // Access the cache using 'this'
        let rgb = this.tierColourCache[baseColor];
        
        if (!rgb) {
            const offscreenCtx = document.createElement("canvas").getContext("2d", { willReadFrequently: true });
            offscreenCtx.fillStyle = baseColor;
            offscreenCtx.fillRect(0, 0, 1, 1);
            const data = offscreenCtx.getImageData(0, 0, 1, 1).data;
            
            rgb = [data[0], data[1], data[2]];
            this.tierColourCache[baseColor] = rgb; 
        }

        const tierDiff = Math.max(0, 8 - tier);
        let increment = 34 * tierDiff;
        // White units should start as gray and become more white
        if (baseColor === 'white') increment *= -1;

        const r = Math.min(255, rgb[0] + increment);
        const g = Math.min(255, rgb[1] + increment);
        const b = Math.min(255, rgb[2] + increment);

        return `rgb(${r}, ${g}, ${b})`;
    }
}

const view = new View();
export default view;