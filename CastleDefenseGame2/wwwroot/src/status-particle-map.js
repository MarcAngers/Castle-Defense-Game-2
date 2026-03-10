// A central registry for how status particles behave:
const StatusParticleMap = {
    "Burn": {
        imageKey: "burn",           // Maps to loader.assets['statuses']
        spawnRate: 0.1,             // % chance per frame to spawn
        life: 600,                  // Lifetime of the particle
        size: 20,                   // Sprite size
        
        // The factory function that gives the particle its starting position and speed
        spawn: (w, h) => ({
            offsetX: (Math.random() * w) - (w / 2),         // Spread evenly across the unit
            offsetY: h/2 - 20,                              // Starting Y value (h/2: bottom of unit, -h/2: top of unit)
            vx: (Math.random() * 0.4) - 0.2,                // Horizontal velocity
            vy: -0.8 - Math.random() * 0.5,                 // Vertical velocity
            sway: 0.2                                       // Horizontal sway
        })
    },
    "Freeze": {
        imageKey: "freeze",
        spawnRate: 0.02,
        life: 1000,
        size: 20,
        spawn: (w, h) => ({
            offsetX: (Math.random() * w) - (w / 2),
            offsetY: -h/2 + 20,                             // Spawn near the top of the unit
            vx: 0,
            vy: 0.3 + Math.random() * 0.2,                  // Slowly move down
            sway: 0.5
        })
    },
    "Heal": {
        imageKey: "heal",
        spawnRate: 0.02,
        life: 600,
        size: 20,
        spawn: (w, h) => ({
            offsetX: (Math.random() * w) - (w / 2),
            offsetY: -h/2 + 20,                             // Spawn near the top of the unit
            vx: 0,
            vy: - 0.6 - Math.random() * 0.2,                // Move up
            sway: 0
        })
    },
    "Speed": {
        imageKey: "speed",          
        spawnRate: 0.01,             
        life: 600,                  
        size: 20,                   
        
        spawn: (w, h) => ({
            offsetX: (Math.random() * w) - (w / 2), 
            offsetY: h/2 - 20,                              // Spawn near the bottom of the unit
            vx: 0,                                          // No horizontal drift
            vy: -0.8 - Math.random() * 0.5,                 // Quickly move up
            sway: 0                                         // No sway
        })
    },
    "Poison": {
        imageKey: "poison",          
        spawnRate: 0.03,             
        life: 600,                  
        size: 20,                   
        
        spawn: (w, h) => ({
            offsetX: (Math.random() * w) - (w / 2), 
            offsetY: h/2 - 20,                              // Spawn near the bottom of the unit
            vx: 0,                                          // No horizontal drift
            vy: -0.3 - Math.random() * 0.2,                 // Slowly move up
            sway: 0.25                                      // Slight sway
        })
    },
    "Slow": {
        imageKey: "slow",          
        spawnRate: 0.01,             
        life: 600,                  
        size: 20,                   
        
        spawn: (w, h) => ({
            offsetX: (Math.random() * w) - (w / 2), 
            offsetY: -h/2 + 20,                             // Spawn near the top of the unit
            vx: 0,                                          // No horizontal drift
            vy: 0.8 + Math.random() * 0.5,                  // Quickly move down
            sway: 0                                         // No sway
        })
    },
    "Rage": {
        imageKey: "rage",          
        spawnRate: 0.06,             
        life: 200,                  
        size: 20,                   
        
        spawn: (w, h) => ({
            offsetX: (Math.random() * w) - (w / 2), 
            offsetY: -h/2,                                  // Spawn at the top of the unit
            vx: 0,                                          // No horizontal drift
            vy: 0,                                          // No vertical movement
            sway: 0                                         // No sway
        })
    },
};

export default StatusParticleMap;