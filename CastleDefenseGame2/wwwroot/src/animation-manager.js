import { AnimatorRegistry } from './animator-registry.js';

export default class AnimationManager {
    constructor() {
        this.activeAnimations = [];
        
        // Expose global screen shake so View.js can apply it to the camera
        this.shakeX = 0;
        this.shakeY = 0;
    }

    triggerAnimation(gadgetId, side, position, targetId) {
        const AnimatorClass = AnimatorRegistry[gadgetId];
        if (AnimatorClass) {
            this.activeAnimations.push(new AnimatorClass(side, position, targetId));
        } else {
            console.warn(`No animator found for gadget: ${gadgetId}`);
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