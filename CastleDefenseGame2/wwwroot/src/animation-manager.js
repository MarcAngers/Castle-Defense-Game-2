import { AnimatorRegistry } from './animator-registry.js';

export default class AnimationManager {
    constructor() {
        this.activeAnimations = [];
        
        // Expose global screen shake so View.js can apply it to the camera
        this.shakeX = 0;
        this.shakeY = 0;
    }

    triggerAnimation(gadgetId, side, position, targetId) {
        // 1. Extract the base ID and level from the string (e.g., "nuke_2" -> "nuke", 2)
        const parts = gadgetId.split('_');
        const baseId = parts[0].toLowerCase(); // Use this to lookup the animator
        const level = parts.length > 1 ? parseInt(parts[1], 10) || 1 : 1;

        // 2. Look up the animator using the clean baseId
        const AnimatorClass = AnimatorRegistry[baseId];
        
        if (AnimatorClass) {
            // 3. Pass the newly extracted level as the 4th argument!
            this.activeAnimations.push(new AnimatorClass(side, position, targetId, level));
        } else {
            console.warn(`No animator found for gadget: ${baseId} (Raw ID: ${gadgetId})`);
        }
    }

    update(deltaTime) {
        this.shakeX = 0;
        this.shakeY = 0;

        this.activeAnimations.forEach(anim => {
            anim.update(deltaTime);
            
            // Accumulate screen shake from any intense animations
            if (anim.shakeX) this.shakeX += anim.shakeX;
            if (anim.shakeY) this.shakeY += anim.shakeY;
        });

        // Garbage collection
        this.activeAnimations = this.activeAnimations.filter(anim => !anim.isFinished);
    }

    // Pass the ctx so it can draw directly over the game world
    draw(ctx, state) {
        this.activeAnimations.forEach(anim => anim.draw(ctx, state));
    }
}