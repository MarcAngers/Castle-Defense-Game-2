import StatusParticleMap from "./status-particle-map.js";

export default class VisualUnit {
    constructor(serverUnit) {
        this.id = serverUnit.instanceId;
        this.logicalX = serverUnit.position;
        
        // Visual Offsets
        this.visualOffsetX = 0;
        this.visualOffsetY = 0;
        
        // Animation Timers
        this.knockbackTimer = 0;
        this.knockbackDuration = 1000; // ms
        this.knockbackStartX = 0;
        this.knockbackTargetX = 0;
        
        this.attackTimer = 0;
        this.attackDuration = 50; // ms
        
        this.lastCooldown = serverUnit.attackCooldown;

        // Status Effects
        this.particles = [];
    }

    update(serverUnit, deltaTime) {
        // 1. Detect Knockback (Sudden large movement against their facing direction)
        const deltaX = serverUnit.position - this.logicalX;
        const direction = serverUnit.side === 1 ? 1 : -1;
        
        // If they moved backwards by more than a couple pixels instantly, they got hit!
        if (deltaX * direction < -5) {
            this.knockbackTimer = this.knockbackDuration;
            this.knockbackStartX = this.logicalX;
            this.knockbackTargetX = serverUnit.position;
        }

        // 2. Detect Attack (Cooldown reset from ~0 to Max)
        if (this.lastCooldown < serverUnit.attackCooldown) {
            this.attackTimer = this.attackDuration;
        }
        this.lastCooldown = serverUnit.attackCooldown;
        
        // Update logical position
        this.logicalX = serverUnit.position;

        // --- STATUS PARTICLE EFFECTS ---
        // Universal Spawning Logic
        if (serverUnit.statuses && serverUnit.statuses.length > 0) {
            for (const status of serverUnit.statuses) {
                const config = StatusParticleMap[status.name];
                
                // If the status has a particle config, and we beat the random spawn chance
                if (config && Math.random() < config.spawnRate) {
                    // Safely grab the unit's dimensions
                    const w = serverUnit.width || 50;
                    const h = serverUnit.height || 50;

                    const physics = config.spawn(w, h);
                    
                    this.particles.push({
                        imageKey: config.imageKey,
                        life: config.life,
                        maxLife: config.life,
                        size: config.size,
                        // Spread the physics properties directly onto the particle
                        ...physics 
                    });
                }
            }
        }

        // Universal Physics Logic (Move them and age them)
        for (let i = this.particles.length - 1; i >= 0; i--) {
            let p = this.particles[i];
            p.life -= deltaTime;

            if (p.life <= 0) {
                this.particles.splice(i, 1);
                continue;
            }

            // Apply velocity based on deltaTime to keep it smooth across all framerates
            p.offsetX += p.vx * (deltaTime * 0.05);
            p.offsetY += p.vy * (deltaTime * 0.05);

            // Apply sine wave sway if the particle requires it (like snow)
            if (p.sway) {
                p.offsetX += Math.sin(p.life / 100) * (p.sway * deltaTime * 0.05);
            }
        }

        // 3. Process Animations
        this.processAnimations(deltaTime, direction);
    }

    processAnimations(deltaTime, direction) {
        this.visualOffsetX = 0;
        this.visualOffsetY = 0;
        this.visualRotation = 0;

        // Process Knockback (The Parabola)
        if (this.knockbackTimer > 0) {
            this.knockbackTimer -= deltaTime;
            let t = 1 - (this.knockbackTimer / this.knockbackDuration); // 0 to 1
            if (t > 1) t = 1;

            // Smoothly slide X to the new position
            const distance = this.knockbackTargetX - this.knockbackStartX;
            this.visualOffsetX = (this.knockbackStartX + (distance * t)) - this.logicalX;

            const bounceHeight = Math.abs(distance) * 0.5; 

            // The Parabola Math for the Y Bounce
            const arcHeight = 1 - Math.pow(2 * t - 1, 2);
            this.visualOffsetY = -bounceHeight * arcHeight;

            const flightDirection = distance >= 0 ? 1 : -1; // Are we flying right or left?
            
            this.visualRotation = (Math.PI / 4) * flightDirection;
        }
        if (this.knockbackTimer <= 0) {
            this.visualRotation = 0;
        }

        // Process Attack Lunge
        if (this.attackTimer > 0) {
            this.attackTimer -= deltaTime;
            this.visualOffsetX += 5 * direction;
        }
    }
}