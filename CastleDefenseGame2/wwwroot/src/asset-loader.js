class AssetLoader {
    constructor() {
        this.assets = {
            teamList: [],       // Populated dynamically from CSV
            unitList: {},       // Grouped by team: { white: ['doggo', 'catto'], red: [...] }
            unitData: {},       // Stores the actual CSV stats for the UI to use
            buildings: {},      // Castles and possibly gadget attachments
            foreground: {},     // Map assets
            background: {},
            atmosphere: {},     // Ambient map animation frames, nested by map colour
            gadgets: {},        // Gadget animation assets
            gadgetData: {},     // Store the CSV gadget stats for the UI to use
            mapData: {},        // Map names and effect text for the UI to use
            hazards: {},        // Hazard animation assets
            particles: {},      // Particles for status effects and map ambience
            tooltips: {},       // Tooltip icons
        };
    }

    async loadAll() {
        // 1. Fetch and parse the CSV as the absolute first step
        await this.loadMasterCSV();
        await this.loadMasterGadgetsCSV();
        await this.loadMasterMapsCSV();

        // 2. Loop through the dynamically generated team list to load their images
        for (const team of this.assets.teamList) {
            await this.loadAssets(team);
        }

        // 3. Load all assets for Gadgets, Hazards, and Particle effects
        await this.loadGadgets();
        await this.loadHazards();
        await this.loadParticles();
        await this.loadTooltips();

        // 4. Load unique assets once
        await this.loadImage('buildings', 'dead-castle', '../assets/buildings/dead-castle.png');
        await this.loadImage('wall', 'wall', '../assets/units/wall.png');
        await this.loadImage('wall_2', 'wall_2', '../assets/units/wall_2.png');
        await this.loadImage('wall_3', 'wall_3', '../assets/units/wall_3.png');

    }

    async loadMasterCSV() {
        // Make sure this path points to wherever your server is hosting the file!
        const response = await fetch('../assets/master_roster.csv');
        const csvText = await response.text();
        
        // Strip the invisible BOM character if it exists
        const cleanText = csvText.replace(/^\uFEFF/, '');
        const rows = this.parseCSV(cleanText);

        if (rows.length < 2) return;

        // Map header indices so we aren't guessing column numbers
        const headers = rows[0].map(h => h.toLowerCase());
        const teamIdx = headers.indexOf('team');
        const idIdx = headers.indexOf('id');
        
        // Parse the data rows
        for (let i = 1; i < rows.length; i++) {
            const row = rows[i];
            if (row.length < headers.length) continue;

            const teamName = row[teamIdx].toLowerCase();
            const unitId = row[idIdx];

            // If we haven't seen this team yet, create it!
            if (!this.assets.teamList.includes(teamName)) {
                this.assets.teamList.push(teamName);
                this.assets.unitList[teamName] = [];
            }

            // Add the unit to the roster
            this.assets.unitList[teamName].push(unitId);

            // Save all the juicy stats for your UI to use later
            const unitStats = {};
            for (let j = 0; j < headers.length; j++) {
                unitStats[headers[j]] = row[j];
            }
            this.assets.unitData[unitId] = unitStats;
        }
    }
    async loadMasterGadgetsCSV() {
        const response = await fetch('../assets/master_gadgets.csv');
        const csvText = await response.text();
        
        // Strip the invisible BOM character if it exists
        const cleanText = csvText.replace(/^\uFEFF/, '');
        const rows = this.parseCSV(cleanText);

        if (rows.length < 2) return;

        // Map header indices so we aren't guessing column numbers
        const headers = rows[0].map(h => h.toLowerCase());
        const idIdx = headers.indexOf('id');
        
        // Parse the data rows
        for (let i = 1; i < rows.length; i++) {
            const row = rows[i];
            if (row.length < headers.length) continue;

            const gadgetId = row[idIdx];

            // Save all the juicy stats for your UI to use later
            const gadgetStats = {};
            for (let j = 0; j < headers.length; j++) {
                gadgetStats[headers[j]] = row[j];
            }
            this.assets.gadgetData[gadgetId] = gadgetStats;
        }
    }

    // Map names and their "map effect" blurbs, keyed by the same colour id the map art
    // folders use (../assets/env/{ID}/). Unlike the roster and gadget CSVs this one has NO
    // copy in CastleDefense.Engine/Data: nothing server-side reads a map name, so a second
    // copy would only be a duplicate to keep in sync.
    async loadMasterMapsCSV() {
        const response = await fetch('../assets/master_maps.csv');
        const csvText = await response.text();

        // Strip the invisible BOM character if it exists
        const cleanText = csvText.replace(/^\uFEFF/, '');
        const rows = this.parseCSV(cleanText);

        if (rows.length < 2) return;

        // Map header indices so we aren't guessing column numbers
        const headers = rows[0].map(h => h.toLowerCase());
        const idIdx = headers.indexOf('id');

        // Parse the data rows
        for (let i = 1; i < rows.length; i++) {
            const row = rows[i];
            if (row.length < headers.length) continue;

            const mapId = row[idIdx].toLowerCase();

            const mapStats = {};
            for (let j = 0; j < headers.length; j++) {
                mapStats[headers[j]] = row[j];
            }
            this.assets.mapData[mapId] = mapStats;
        }
    }

    async loadAssets(colour) {
        // Load Map assets:
        await this.loadMap(colour);

        // Load Unit assets (now perfectly waiting for the Promise to resolve)
        const unitSrc = '../assets/units/';
        for (const unitId of this.assets.unitList[colour]) {
            await this.loadImage(unitId, unitId, `${unitSrc}${unitId}.png`);
        }

        // Load Castle
        await this.loadImage('buildings', `${colour}-castle`, `../assets/buildings/${colour}-castle.png`);
    }

    async loadMap(colour) {
        await this.loadImage('foreground', colour, `../assets/env/${colour}/foreground.png`);
        await this.loadImage('background', colour, `../assets/env/${colour}/background.png`);
        await this.loadAtmosphere(colour);
        // Later: Load map animation components
        // await this.loadImage('env', colour, `../assets/env/effects.png`);
    }

    // Ambient animation art for a map -- clouds, and whatever the other maps grow.
    //
    // Driven by a MANIFEST rather than by probing for known filenames. These files exist
    // only for maps that have an atmosphere, so blindly requesting cloud1..4 for all eight
    // colours would print a console error per miss on every single page load. Add a map's
    // row here when its art lands; a map with no row simply has no atmosphere.
    async loadAtmosphere(colour) {
        const frames = AssetLoader.AtmosphereAssets[colour];
        if (!frames) return;

        const bucket = this.assets.atmosphere[colour] = {};
        for (const id of frames) {
            await this.loadImageInto(bucket, id, `../assets/env/${colour}/${id}.png`);
        }
    }

    async loadGadgets() {
        const gadgetAssetList = [
            'blackhole',
            'cash',
            'divine',
            'divine_3',
            'firebomb',
            'firebomb_2',
            'firebomb_3',
            'freeze',
            'freeze_2',
            'freeze_3',
            'goo',
            'heal',
            'heal_2',
            'heal_3',
            'meteor',
            'mushroom-cloud',
            'nuke',
            'nuke_2',
            'nuke_3',
            'poison',
            'poison_2',
            'poison_3',
            'rage',
            'rage_2',
            'rage_3',
            'reinforcements',
            'snipe',
            'snipe_2',
            'snipe_3',
            'speed',
            'speed_2',
            'speed_3',
            'wall',
            'wave',
        ];

        for (const gadgetId of gadgetAssetList) {
            await this.loadImage('gadgets', gadgetId, `../assets/gadgets/${gadgetId}.png`);
        }
    }

    async loadHazards() {
        const hazardAssetList = [
            'blackhole-1',
            'blackhole-2',
            'fire-1',
            'fire-2',
            'fire_2-1',
            'fire_2-2',
            'fire_3-1',
            'fire_3-2',
            'goo',
            'poison',
            'poison_2',
            'poison_3',
            'wave',
        ];

        for (const hazardId of hazardAssetList) {
            await this.loadImage('hazards', hazardId, `../assets/hazards/${hazardId}.png`);
        }
    }

    async loadParticles() {
        const particleAssetList = [
            'burn',
            'freeze',
            'heal',
            'rage',
            'slow',
            'speed',
            'poison',
            'star',      // main-menu intro confetti
        ];

        for (const particleId of particleAssetList) {
            await this.loadImage('particles', particleId, `../assets/particles/${particleId}.png`);
        }
    }

    async loadTooltips() {
        const tooltipAssetList = [
            'sword',
            'heart',
            'boot',
            // Not a tooltip icon -- it flanks the pause overlay's title when a player has
            // dropped (see game.js). It lives in this folder, so it loads with this group
            // rather than earning a category of its own for one image.
            'construction',
        ];

        for (const tooltipId of tooltipAssetList) {
            await this.loadImage('tooltips', tooltipId, `../assets/tooltips/${tooltipId}.png`);
        }
    }

    // --- HELPER: Promise-Wrapped Image Loader, into an arbitrary target ---
    // loadImage below writes into this.assets[category], which is one level deep. The
    // atmosphere art is nested by colour AND frame, so it needs to say where it goes.
    loadImageInto(target, key, src) {
        return new Promise((resolve) => {
            const img = new Image();
            img.onload = () => { target[key] = img; resolve(); };
            img.onerror = () => {
                console.error(`Failed to load image: ${src}`);
                resolve();   // one missing frame must not freeze the whole load
            };
            img.src = src;
        });
    }

    // --- HELPER: Promise-Wrapped Image Loader ---
    loadImage(category, key, src) {
        return new Promise((resolve) => {
            const img = new Image();
            img.onload = () => {
                if (category === key) {
                    this.assets[key] = img; // Flat assets like units/backgrounds
                } else {
                    this.assets[category][key] = img; // Nested assets like buildings
                }
                resolve();
            };
            img.onerror = () => {
                console.error(`Failed to load image: ${src}`);
                resolve(); // We resolve anyway so one missing image doesn't freeze the whole game!
            };
            img.src = src;
        });
    }

    // --- CUSTOM CSV PARSER ---
    // Safely handles commas trapped inside description quotes
    parseCSV(text) {
        const result = [];
        let inQuotes = false;
        let buffer = '';
        let row = [];

        for (let i = 0; i < text.length; i++) {
            const c = text[i];
            if (c === '"') {
                inQuotes = !inQuotes;
            } else if (c === ',' && !inQuotes) {
                row.push(buffer.trim());
                buffer = '';
            } else if (c === '\n' && !inQuotes) {
                row.push(buffer.trim());
                result.push(row);
                row = [];
                buffer = '';
            } else if (c === '\r') {
                // Ignore carriage returns
            } else {
                buffer += c;
            }
        }
        
        if (buffer || row.length > 0) {
            row.push(buffer.trim());
            result.push(row);
        }
        
        return result;
    }

    // --- ACCESSORS ---
    get(key) {
        return this.assets[key];
    }

    /// Does this unit roll its stats fresh on every spawn? The roster row holds its BASE
    /// values, which is what every stat readout in the UI shows, so those screens mark the
    /// rolled stats with a "?" rather than presenting a number the player will not get.
    ///
    /// The id is duplicated from GameEngine.RandomStatUnitId, which is the source of truth
    /// -- the client has no other way to know, since the roster CSV carries no flag for it.
    /// Kept here so the string lives in ONE place on this side of the wire.
    isRandomStatUnit(unitId) {
        return unitId === 'weirdo';
    }

    // New Accessor! Use this to get stats for menus
    getUnitStats(unitId) {
        if (this.assets.unitData[unitId])
            return this.assets.unitData[unitId];
        else
            return { 'team': 'white' };
    }

    /// Name and map-effect text for one map, keyed by its colour.
    ///
    /// The Effect column is the text the Collection screen shows the player, and it is
    /// POPULATED -- this comment used to say it was "expected to be BLANK for now, the
    /// effects themselves are not written yet", which stopped being true when map effects
    /// shipped on 2026-08-27.
    ///
    /// It is also the half of a HAND-SYNCED PAIR: the engine never reads this CSV, it reads
    /// MapEffects (Engine/Models). Editing one without the other makes the game describe
    /// rules it does not play by. See the map-effects section of CLAUDE.md.
    ///
    /// Both fields still fall back to something printable rather than letting `undefined`
    /// reach the screen, so a map with no row at all renders as its colour with no effect
    /// text -- a missing CSV row rather than a crash.
    getMapStats(colour) {
        const data = this.assets.mapData[colour] || {};
        return {
            name: data.name || colour,
            effect: data.effect || ''
        };
    }

    getTeam(colour) {
        let teamArray = [];
        for (const unit of this.assets.unitList[colour]) {
            teamArray.push(this.assets[unit]);
        }
        return teamArray;
    }

    getGadgetsBySlot(slotName) {
        const gadgetData = this.assets['gadgetData'];

        return Object.values(gadgetData).filter(gadget => 
            // Using toLowerCase() makes this perfectly bulletproof against typos in the CSV
            String(gadget.Slot).toLowerCase() === String(slotName).toLowerCase()
        );
    }
}

// Which maps have ambient animation art, and what its frames are called. See
// loadAtmosphere -- kept OUTSIDE the class as a static so the manifest reads as data.
AssetLoader.AtmosphereAssets = {
    white: ['cloud1', 'cloud2', 'cloud3', 'cloud4'],
    red: ['leaf'],
};

const loader = new AssetLoader();
export default loader;