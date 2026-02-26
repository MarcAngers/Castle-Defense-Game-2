class AssetLoader {
    constructor() {
        const teamList = [ 'black', 'blue', 'green', 'orange', 'purple', 'red', 'white', 'yellow' ];
        this.assets = {
            'teamList': teamList,
            'unitList': {},
            'buildings': {}
        };
    }

    async loadAll() {
        for (const team of this.assets.teamList) {
            await this.loadAssets(team);

            // Load dead castle once:
            const img = new Image();
            const buildingSrc = '../assets/buildings/';

            img.src = buildingSrc + "dead-castle.png";
            img.onload = () => {
                this.assets.buildings["dead-castle"] = img;
            };
            img.onerror = () => {
                console.error(`Failed to load ${imgDef.src}`);
            };
        }
    }

    async loadAssets(colour) {
        // Load Map assets:
        await this.loadMap(colour);

        // Load Unit assets:
        let unitList = null;
        if (this.assets.unitList.colour) {
            unitList = this.assets.unitList[colour];
        } else {
            unitList = await this.getUnitList(colour);
        }
        let unitSrc = '../assets/units/';


        for (const unit of unitList) {
            const img = new Image();
            img.src = unitSrc + unit + '.png';
            img.onload = () => {
                this.assets[unit] = img;
            };
            img.onerror = () => {
                console.error(`Failed to load ${unit}`);
            };
        };

        // Load Castle/Gadget assets:
        // Need to add Gadget stuff to this
        const img = new Image();
        const buildingSrc = '../assets/buildings/';
        img.src = buildingSrc + colour + "-castle.png";
        img.onload = () => {
            this.assets.buildings[colour + "-castle"] = img;
        };
        img.onerror = () => {
            console.error(`Failed to load ${imgDef.src}`);
        };
        // This needs work ^
    }

    async loadMap(colour) {
        // TEMPORARY: ONLY LOAD DEFAULT BACKGROUND
        colour = 'white';
        return new Promise((resolve, reject) => {
            var img = new Image();
            
            img.onload = () => {
                this.assets[`background-${colour}`] = img;
                resolve(img); 
            };

            img.onerror = () => {
                reject(new Error(`Could not load image: ${colour}`)); 
            };

            img.src = `../assets/env/background-${colour}.png`;
        });
    }

    async getUnitList(team) {
        const response = await fetch(`../static/fullteams/${team}.json`);
        const unitList = await response.json(); 
        this.assets.unitList[team] = unitList.team;
        return unitList.team;
    }

    get(key) {
        return this.assets[key];
    }

    getTeam(colour) {
        let teamArray = [];
        for (const unit of this.assets.unitList[colour]) {
            teamArray.push(this.assets[unit]);
        }

        return teamArray;
    }
}

const loader = new AssetLoader();
export default loader;