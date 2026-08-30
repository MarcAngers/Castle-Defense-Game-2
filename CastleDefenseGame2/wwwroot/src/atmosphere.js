import loader from './asset-loader.js';

// Ambient map animation -- the weather and the life of a map, drawn behind the fight.
//
// PURELY DECORATIVE AND ENTIRELY CLIENT-SIDE. Nothing here is sent to the server, read by
// the engine, or recorded in a replay, which is why it draws freely from Math.random where
// anything touching gameplay would have to use the engine's seeded stream (see the
// replay-determinism notes in CLAUDE.md). Two browsers watching the same game see different
// clouds and that is fine -- if any of this ever starts affecting play, it needs a seed.
//
// TWO SURFACES, and which one a layer belongs on is a real decision.
//
// `render` draws INSIDE the background's parallax transform (see view.js), which buys three
// things for free: the layer scrolls at the background's half-speed as the camera pans, it
// sits behind the foreground and the units, and a shadow map greys it out with everything
// else. Clouds, embers, fireflies and meteors are all part of the PLACE, so they live there.
//
// `renderOverlay` draws in SCREEN space, after the camera has been restored. Weather between
// the player and the world belongs here: rain must fill the view wherever the camera happens
// to be pointed, and in world space it would slide sideways whenever the player panned --
// which is not how looking through rain works.

const MAP_WIDTH = 2000;

// The canvas is drawn in a fixed 500-tall logical space and scaled to the window (see
// view.resize), so this is a constant while the WIDTH is not -- screen-space layers are
// handed the visible width every frame.
const LOGICAL_HEIGHT = 500;

// The scenes, keyed by map colour. A map with no entry simply has no atmosphere, so adding
// one is a matter of adding a row here plus its art to AssetLoader.AtmosphereAssets.
const SCENES = {
    white: {
        clouds: {
            // The vertical band a cloud must fit inside, as the y of its TOP and the lowest
            // its BOTTOM edge may reach. Stated as the band itself rather than as a margin
            // off the horizon, because the lowest edge is the thing that gets tuned by eye.
            //
            // For reference, MEASURED from white/background.png rather than guessed: the
            // sky runs from row 0 to row 241 and the hills start at 242. So the hillside is
            // not what constrains this -- 100 keeps the clouds high in the sky, well clear
            // of the ridge, with open air beneath them.
            bandTop: 6,
            bandBottom: 100,

            frames: ['cloud1', 'cloud2', 'cloud3', 'cloud4'],

            // Count and scale are set TOGETHER. What reads as "how cloudy is it" is the
            // share of sky the clouds cover, so shrinking them without adding more thins
            // the sky out. Coverage goes as scale SQUARED, so the count carries the
            // inverse square: this pairing holds total cloud area at ~100k px^2, the same
            // as the original 9 clouds at 0.75-1.35, with each cloud a quarter smaller.
            // Change one of these two numbers and the other has to move with it.
            count: 16,

            // px/sec. "Gently floating": at the slow end a cloud takes over three minutes
            // to cross a phone's view, which is drift you notice only if you look for it.
            speedMin: 5,
            speedMax: 13,

            scaleMin: 0.55,
            scaleMax: 1.0,

            // The art is already pale, so this is a light touch for depth rather than a
            // fade -- pushing it much lower washes the clouds out against the sky.
            alphaMin: 0.7,
            alphaMax: 1.0,

            bobAmplitude: 3,    // px of vertical drift
            bobPeriodMin: 7,    // seconds for one bob cycle
            bobPeriodMax: 15,
        },
    },

    orange: {
        // Rumbling Volcanoe: soot and embers lifting off the mountains and cooling as they
        // climb. No art -- every particle is a 1-3px square, which is the whole idea.
        particles: {
            // Across the full 2000px map, so roughly half are in frame at once on a
            // landscape phone. THE knob for "too sparse" / "too busy".
            count: 90,

            // Spawn band, in map rows. MEASURED: the foreground's ground line runs between
            // rows 299 and 373, so a particle born down here is hidden behind the ground at
            // birth and rises out from behind it rather than blinking into existence in
            // open air.
            spawnTop: 330,
            spawnBottom: 420,

            // px/sec upward, and this range is the FLOOR: gusts below only ever multiply
            // it up, so the calm state is the slowest the field ever moves.
            riseMin: 12,
            riseMax: 30,

            // Hot draughts. Every so often the whole field is lifted faster, then settles.
            // One global multiplier rather than per-particle, because a draught is a thing
            // happening to the air, not to individual motes -- they should surge together.
            //
            // The envelope is a sine bump, so a gust eases in and eases out instead of
            // switching on; peak strength is the multiplier at the top of the bump.
            // The gap is the CALM BETWEEN gusts, measured from the end of the previous one
            // -- not the interval between starts. Duration therefore adds to it: at these
            // values a gust begins every 14-31s and the field is lifted for roughly a third
            // of that. Set deliberately: an earlier cut shortened the gap to hold the
            // start-to-start interval fixed while the gusts lengthened, and the run-on
            // surges read as constant wind rather than as distinct draughts. The quiet
            // between them is what makes a gust legible as an event.
            gustGapMin: 7,          // seconds of calm between gusts
            gustGapMax: 18,
            gustDurationMin: 7,     // seconds from first lift back to calm
            gustDurationMax: 13,
            gustStrengthMin: 2.5,   // x rise speed at the peak of the bump
            gustStrengthMax: 4.2,

            // Sideways travel, expressed as an ANGLE OFF VERTICAL rather than as px/sec.
            // Two reasons. A fast mote and a slow one given the same angle trace the same
            // slope, so the field looks like one body of air rather than a fast population
            // and a slow one; and because the gust below multiplies rise but NOT drift, a
            // particle's path automatically steepens toward vertical while it is being
            // lifted, which is what a hot updraught does.
            //
            // The bias exponent shapes the spread: angle = max * random^bias, so at 1.8
            // most motes rise nearly straight while a few cut across at a real diagonal.
            // Bias 1 would make every angle equally likely and the whole field look
            // uniformly slanted, which is not the same thing at all.
            driftAngleMaxDeg: 42,
            driftAngleBias: 1.8,
            driftRightwardShare: 0.7,   // a prevailing wind, not a symmetric scatter

            swayAmplitude: 6,
            swayPeriodMin: 2.5,
            swayPeriodMax: 6,

            // LIFE is what ends a particle, not a height limit: embers cool at different
            // heights, so some die low over the ridge and some make it into the sky. Fade
            // in and out are fractions of that life, so nothing pops at either end.
            lifeMin: 9,
            lifeMax: 17,
            fadeIn: 0.12,
            fadeOut: 0.45,

            // Weighted small. Sizes are LOGICAL px in the 500-tall space, so they scale
            // with everything else rather than shrinking on a big monitor.
            sizes: [1, 2, 2, 2, 3, 3],

            // Two populations sharing one system. Embers flicker; soot is dark, dimmer,
            // steady, and reads as ash the fire has finished with.
            emberShare: 0.75,

            // THE game's orange, rgb(255,127,39), and deliberately the only ember colour.
            //
            // This is why the embers are NOT drawn additively, which was the obvious way to
            // make a warm mote read against warm tan mountains. Additive blending adds the
            // mote to the pixels behind it, so on a bright backdrop the red channel clips
            // and the result drifts to yellow and then to white -- the mote stops being
            // this colour and starts being "whatever was behind it, plus". Keeping one
            // exact colour and keeping it additive are mutually exclusive; the colour wins.
            // Contrast is carried by alpha and by the flicker instead.
            emberColour: '#ff7f27',
            emberAlphaMin: 0.55,
            emberAlphaMax: 1.0,
            flickerRateMin: 4,      // rad/sec
            flickerRateMax: 11,
            flickerDepth: 0.35,     // how much of the alpha the flicker swings

            sootColours: ['#3a3a3a', '#252525', '#4a4038'],
            sootAlphaMin: 0.35,
            sootAlphaMax: 0.7,
        },
    },

    green: {
        // Marshy Swamp: fireflies over the water. A separate layer from `particles` rather
        // than another mode on it, because almost nothing carries over -- a firefly does not
        // rise, does not have a lifetime, does not respawn, and is dark most of the time.
        // Folding both into one system would mean a config where half the keys are inert.
        fireflies: {
            // TWO BANDS, near and far. Everything below this is shared; a band overrides
            // only what makes it its own depth. Written as overrides rather than as two full
            // configs so that a change to the blink, the glow or the despawn rules cannot
            // apply to one depth and silently miss the other.
            bands: [
                {
                    // NEAR. MEASURED from the art: the canopy runs to about row 200 and the
                    // foreground's reeds and lily pads reach up to row 172 (median 328).
                    // This band is the open air between them, so a firefly reads against the
                    // dark trunks and water and only occasionally slips behind a reed.
                    count: 48,
                    bandTop: 150,
                    bandBottom: 340,
                    coreSizes: [2, 2, 3],
                    showDarkBody: true,
                    wanderXMin: 25,
                    wanderXMax: 60,
                    wanderYMin: 12,
                    wanderYMax: 32,
                },
                {
                    // FAR, up among the canopy. Smaller cores are the depth cue.
                    //
                    // The wander is scaled down with them, for two reasons. Distant things
                    // appear to move less, so matching the near band's 60px swing would make
                    // these read as tiny fireflies moving FAST rather than as far-off ones;
                    // and the band is only 100px tall, so a near-band amplitude would leave
                    // almost no room once inset (a 32px wander reaches 43px, which would
                    // squeeze a 100px band down to 14px of usable home positions).
                    count: 30,
                    bandTop: 50,
                    bandBottom: 150,
                    coreSizes: [1, 1, 1, 2],

                    // No body between flashes: at this distance the insect itself is below
                    // the resolution of the scene and only its light carries, so an unlit
                    // one showing as a dark speck would read as being just as close as the
                    // near band and flatten the depth the smaller size is buying. The near
                    // band keeps its silhouette for exactly the opposite reason.
                    showDarkBody: false,
                    wanderXMin: 12,
                    wanderXMax: 30,
                    wanderYMin: 6,
                    wanderYMax: 16,
                },
            ],

            // Warm yellow-green, drawn ADDITIVELY. Unlike the embers there is no exact
            // colour to preserve here, and the swamp is uniformly dark -- so adding light is
            // both free of the hue-shift problem and exactly what a bioluminescent glow does.
            colour: '#c8ff5a',

            // A bright core inside a single dim ring. Two rings (pad 4 at 0.10 plus pad 2 at
            // 0.34) made the bloom read as too large a smudge around what should be a small
            // insect; one tight, faint ring keeps the soft edge without the halo competing
            // with the core for attention.
            //
            // Kept as a LIST rather than a pair of scalars so a second ring is a data change
            // if a map ever wants a bigger glow -- the draw loop already walks it.
            halo: [
                { padding: 2, alpha: 0.10 },
            ],

            // WANDER: the sum of two sine pairs per axis, at rates that do not divide into
            // each other, so the path is a slow drifting loop that never visibly repeats.
            // Cheaper than a steering simulation and, unlike a random walk, it cannot wander
            // out of its band or pile the swarm up in a corner.
            slowRateMin: 0.15,  // rad/sec, the lazy loop
            slowRateMax: 0.45,
            fastRateMin: 0.6,   // rad/sec, the small jitter on top
            fastRateMax: 1.5,
            fastShare: 0.35,    // how much of the amplitude the jitter takes

            // BLINK: dark for most of the period, then one soft pulse. This is the thing
            // that makes them read as fireflies rather than as floating dots -- an always-on
            // glow looks like sparks, and a hard on/off looks like a broken pixel.
            periodMin: 2.5,     // seconds between flashes
            periodMax: 6,
            onFractionMin: 0.18,   // share of the period spent lit
            onFractionMax: 0.35,
            peakAlphaMin: 0.85,
            peakAlphaMax: 1.0,

            // BETWEEN flashes the insect is still there: a small dark body, drawn normally,
            // that goes on wandering. It crossfades against the glow rather than being
            // switched -- body alpha is (1 - glow) -- so a firefly dims to a silhouette and
            // brightens back out of one instead of popping between two states.
            darkColour: '#0b1405',
            darkAlpha: 0.85,

            // It only ever vanishes while dark, and only sometimes: each time a flash ends
            // it rolls this chance to leave. Losing one mid-glow would read as a light being
            // switched off, and vanishing every time would make the dark body pointless.
            darkDespawnChance: 0.25,
            hiddenMin: 2,       // seconds away before it turns up somewhere else
            hiddenMax: 7,
        },
    },

    purple: {
        // Warehouse. The one INDOOR map, so there is no weather to fall -- the life of the
        // place is its lighting. Two layers that depend on each other: the lamps cast pools
        // of light, and the dust is only visible where that light falls.
        lamps: {
            // MEASURED PER LAMP, and they are genuinely all different -- the shades hang at
            // different heights and are drawn at different widths. A single shared y and
            // width (which is what this had first) left every cone misaligned with its own
            // fixture.
            //
            // Each row is the widest horizontal run of white pixels in that shade: the row
            // is the y where the shade is at its widest, i.e. its open mouth, and the run's
            // width is the aperture the light leaves through. Re-derive by scanning
            // purple/background.png for white (>235 on all channels) if the art changes.
            fixtures: [
                { x: 224,  y: 58, halfWidth: 34 },
                { x: 549,  y: 51, halfWidth: 28 },
                { x: 804,  y: 50, halfWidth: 23 },
                { x: 1093, y: 53, halfWidth: 34 },
                { x: 1358, y: 51, halfWidth: 23 },
                { x: 1577, y: 53, halfWidth: 34 },
                { x: 1792, y: 52, halfWidth: 34 },
            ],

            colour: '255,238,201',      // warm, as industrial pendants are

            // The cone spreads from the shade's own mouth down to the floor, so a wide
            // fixture throws a wide cone and each one starts exactly where its shade ends.
            //
            // coneTopScale OVERSHOOTS the measured aperture on purpose, for two reasons
            // stacked. The gaussian below puts the visible edge at roughly 0.68 of the
            // nominal half-width, so feeding it the shade's true width made the lit patch
            // NARROWER than the shade it came out of -- which is what read as the cones not
            // lining up; 1/0.68 = 1.47 is the value that makes them exactly flush. Past that
            // it is taste: at 1.9 the light spills a little wider than the shade, as a bright
            // source seen against a dim room does.
            //
            // Note the bottom is measured from the RAW aperture, not from the scaled top, so
            // widening the top does not silently drag the base out with it.
            coneTopScale: 1.9,
            coneSpread: 7.5,            // bottom half-width = raw half-width x this
            // MEASURED: the foreground's coverage runs 0% at row 300, 31% at 340 and 100%
            // at 355 -- so 355 is the point past which a cone is still drawn but can never
            // be seen, and is therefore exactly as long as it is worth making them. This sat
            // at 310 and was throwing away 45px of visible throw.
            floorY: 356,
            coneAlpha: 0.30,

            // SOFT EDGES. The cone is a gaussian across its width rather than a hard clip:
            // light dissipates at the edge of a beam, it does not stop at a line. HIGHER is
            // crisper -- the value is the exponent, so the transition narrows as 1/sqrt of
            // it. (Unlike the lens flare's hexagons, where the crisp edge IS the subject --
            // an aperture image has a real boundary and a light beam does not.)
            //
            // Note this is measured in NORMALISED width, so it stays visually consistent
            // when coneSpread changes: widening the cone widens the falloff with it rather
            // than leaving a proportionally sharper edge.
            edgeSoftness: 5.0,
            depthFalloff: 1.25,         // exponent on the fade with distance from the bulb

            // FLICKER. Each lamp keeps its own schedule, so they never stutter in unison --
            // seven lamps blinking together would read as the screen glitching rather than
            // as one tube going.
            //
            // A stutter is a short burst of hard on/off at stutterHz, not a smooth fade:
            // that hard chatter is exactly what a failing fluorescent does, and a sine
            // wobble here reads as a dimmer being turned rather than a tube struggling.
            gapMin: 12,                 // seconds of steady light between stutters, per lamp
            gapMax: 45,
            stutterMin: 0.25,           // seconds a stutter lasts
            stutterMax: 1.1,
            stutterHz: 18,              // how fast it chatters while stuttering
            stutterLow: 0.18,           // brightness of the "off" steps, never fully dark
            stutterDutyOff: 0.55,       // share of steps that are dim

            // A very slow breath while steady, so a lamp is never a perfectly dead decal.
            breathPeriod: 7,
            breathDepth: 0.08,
        },

        dust: {
            // Motes hanging in the air of the warehouse. MEASURED band: the lamps are at
            // row 68 and the floor starts at 302, so this is the space between them.
            count: 80,
            bandTop: 80,
            bandBottom: 305,

            colour: '255,246,224',

            // SUB-PIXEL on purpose. The canvas is drawn in a 500-tall logical space and
            // scaled up to the window, so a 1-unit mote was already 2px+ on a desktop and
            // read as grit rather than as dust. Fractional fillRect sizes antialias down to
            // a fainter, smaller speck, which is exactly the look wanted -- and is the only
            // way to go below one logical unit at all.
            sizes: [0.6, 0.8, 1.0],

            // Barely moving. Dust in still indoor air drifts, it does not fall.
            sinkMin: 2,                 // px/sec downward
            sinkMax: 7,
            driftMin: -5,               // px/sec sideways
            driftMax: 5,
            swayAmplitude: 7,
            swayPeriodMin: 4,
            swayPeriodMax: 11,

            // THE POINT OF THIS LAYER. A mote is barely visible on its own and lights up as
            // it passes through a lamp's cone, which is what makes the light read as volume
            // rather than as a shape painted on the wall.
            //
            // The test follows the CONE, not a plain radius: a mote level with the bulb is
            // lit only if it is within the narrow top, while one near the floor is lit
            // across the full spread. Using a circle here would light motes sitting beside
            // the lamp where the shade is actually blocking the light.
            ambientAlpha: 0.10,         // brightness outside any cone
            litAlpha: 0.62,             // brightness in the heart of one

            twinklePeriodMin: 2.5,
            twinklePeriodMax: 6,
            twinkleDepth: 0.35,
        },
    },

    red: {
        // Cherry Forest: blossom coming down. A WORLD-SPACE layer, which on this map does
        // two useful things for free -- petals pass behind the tree trunks (the foreground
        // reaches the top of the screen in places), and they vanish behind the ground rather
        // than needing a landing state, because the foreground is fully opaque by row 380.
        leaves: {
            frames: ['leaf'],

            // Across the full 2000px map, so roughly half are in frame at once.
            //
            // Count and scale are usually a PAIR -- how much blossom is in the air is the
            // share of screen the petals cover, which goes as scale SQUARED, so shrinking
            // them normally means adding more to compensate (see the clouds, which do
            // exactly that). NOT HERE: this count was deliberately left where it was while
            // the petals shrank, so the fall thins out along with them. Halving the covered
            // area is the point, not a side effect to be corrected.
            count: 78,

            // Spawned above the top edge and recycled well below the point the ground covers
            // them, so a petal is never seen to appear or to stop.
            spawnTop: -140,
            spawnBottom: -10,
            recycleBelow: 430,

            // GENTLY. At the slow end a petal takes nearly half a minute to cross the
            // screen; blossom does not plummet.
            fallMin: 18,        // px/sec
            fallMax: 40,

            // A petal does not fall in a line -- it slips sideways, catches, and slips back.
            // This sway IS the effect; without it the layer reads as pink snow.
            swayAmplitude: 12,
            swayExtra: 16,      // added per petal at random, so the widths vary
            swayPeriodMin: 2.5,
            swayPeriodMax: 6,

            // A slow constant sideways drift on top of the sway, so the fall is not a
            // perfectly closed zigzag.
            driftMin: -7,
            driftMax: 10,

            // TUMBLE. Two motions: a lazy spin, and a separate edge-on flip done by scaling
            // the sprite's width by a cosine. The flip is what sells it -- a petal that only
            // rotated would look like a spinning sticker, where one that narrows to nothing
            // and opens out again reads as a thin thing turning over in the air.
            spinRateMin: 0.3,   // rad/sec
            spinRateMax: 1.1,
            flipRateMin: 1.5,
            flipRateMax: 4.0,

            // The sprite is 5x5, so this is 5-9px on screen.
            //
            // Arrived at by walking down: 1.8x-3.2x and 1.4x-2.4x both read as too big for
            // blossom. A near-identical range DID vanish once, but at a count of 45 -- with
            // 78 petals in the air there are enough of them to register even though each is
            // fainter, which is why the same size works now and did not then. Worth knowing
            // before shrinking further: this map's upper half is pale pink blossom, the
            // petal is pale pink, and the background art already carries a dense speckle of
            // blossom texture for a small sprite to disappear into. Size is the only lever,
            // since the colour is the sprite's own.
            scaleMin: 1.0,
            scaleMax: 1.7,

            alphaMin: 0.85,
            alphaMax: 1.0,
        },
    },

    black: {
        // Distant Planet: meteors across the star field. Unlike every other layer these are
        // EVENTS rather than a standing population -- the sky is empty most of the time and
        // one streak crosses it -- so the scene keeps a small pool of slots and lights one
        // up on a schedule instead of animating everything at once.
        shootingStars: {
            // How many may be in flight at the same time. Small on purpose: at the interval
            // below they rarely overlap, and the pool only exists so that two CAN, rather
            // than one being silently skipped.
            poolSize: 5,

            // A shooting star is meant to be a thing you are lucky to catch, so this is the
            // one layer that is mostly ABSENT: at 15-45s apart the sky averages about two
            // streaks a minute, i.e. around ten across a full game, with real gaps between
            // them. Judged at 1.2-3.0s while the look was being settled -- if the appearance
            // ever needs re-checking, drop it back there rather than waiting for one.
            intervalMin: 15,
            intervalMax: 45,

            // MEASURED from black/background.png: the star field runs to about row 240,
            // where the dark red hills begin. Stars START in the upper part of that, and
            // `horizonY` is the hard floor -- a streak's life is cut short if it would
            // otherwise reach the ground, so no meteor ever ploughs into the hills.
            bandTop: 10,
            bandBottom: 130,
            horizonY: 235,

            // Shallow angles on purpose. The sky here is only ~240px tall against a 2000px
            // map, so a realistically steep meteor would cross the whole sky in a moment and
            // be gone; a shallow one draws a long streak the way the shape of this sky wants.
            angleMinDeg: 6,
            angleMaxDeg: 22,

            speedMin: 380,      // px/sec
            speedMax: 650,
            lifeMin: 0.8,       // seconds, before the horizon clamp
            lifeMax: 1.5,
            fadeIn: 0.12,       // fractions of life
            fadeOut: 0.45,

            // Cool white, drawn additively on a black sky.
            colour: '#e8f2ff',
            headSize: 3,
            haloPadding: 2,
            haloAlpha: 0.30,

            // THE TAIL HAS TO BE A SOLID STREAK, and that is not a style choice on this map.
            // The star field is already thousands of bright 1-2px white dots, so a meteor
            // cannot be distinguished by being bright -- it is the same white as everything
            // around it. The only thing that separates it from the background is SHAPE: an
            // unbroken line, thicker than a star, moving. A first pass drew 14 dots spaced
            // ~7px apart and it vanished into the field completely.
            //
            // So the trail is stepped at 2px with 2px blocks -- overlapping into a continuous
            // band rather than a dotted one. At ~45 blocks per star and a pool of 5 that is
            // a couple of hundred fillRects in the worst case, which is nothing.
            trailStep: 2,         // px between blocks; must be <= trailThickness to stay solid
            trailThickness: 2,
            trailLengthMin: 55,
            trailLengthMax: 110,
            taperExponent: 1.6,   // >1 keeps the tail bright near the head and dies off fast
        },
    },

    yellow: {
        // Sunbaked Desert: sun glare. A SCREEN-SPACE layer, and the only one whose position
        // is derived rather than chosen -- a flare is an artifact of the LENS, so its ghosts
        // sit on the line running from the light source through the centre of the frame, and
        // they slide as the camera pans even though the sun itself is fixed to the world.
        //
        // Deliberately NOT photographic. Hexagonal iris ghosts and anamorphic streaks would
        // fight flat pixel art; this is a soft bloom plus a few translucent discs, which is
        // the same idea rendered in the game's own language.
        lensFlare: {
            // MEASURED, not guessed. The sun is CROPPED by the top of the map: its visible
            // cap spans rows 0-76 and x 600-900, and fitting a circle through those spans
            // puts the real disc at centre (750, -109) with radius 186. The flare has to
            // originate from where the light actually is, which is above the screen -- using
            // the visible cap's centre instead would hang the whole flare too low and the
            // ghosts would march off at the wrong angle.
            sunWorldX: 750,
            sunWorldY: -109,

            // Warm white, as an "r,g,b" triplet because the bloom needs it inside rgba()
            // colour stops rather than as a fillStyle.
            colour: '255,246,200',

            // The glow around the sun itself. Larger than the disc so it spills into the sky.
            bloomRadius: 265,
            bloomAlpha: 0.22,

            // Ghosts are POLYGONS, not discs. An iris ghost is an out-of-focus image of the
            // aperture, so a real one is the shape of the blades -- and a hard-edged n-gon
            // also happens to be the shape that sits properly in flat pixel art, where a
            // soft circle read as a smudge airbrushed onto the screen.
            //
            // All of them share one orientation, which is not laziness: they are all images
            // of the SAME aperture, so rotating them independently would be wrong.
            ghostSides: 6,
            ghostRotationDeg: 0,

            // Positioned along the sun-to-centre line. t is that line's parameter: 0 sits on
            // the sun, 1 on the middle of the screen.
            //
            // EVERY t HERE IS BELOW 0.983, AND THAT IS A HARD CONSTRAINT, not a preference.
            // The line's height is camera-independent -- y = sunY + t*(250 - sunY), i.e.
            // -109 + 359t -- and the dunes start at row 244, so t = 0.983 is exactly where
            // the chain would cross out of the sky and start sliding over the sand. The
            // range below tops out at 0.86, which puts the lowest ghost at y = 200.
            //
            // Sizes and alphas are deliberately uneven: an evenly spaced, evenly sized chain
            // reads as a string of beads rather than as optics. The two largest were trimmed
            // (42 and 31) when the spacing was compressed, since at the old radii they now
            // overlapped several neighbours and merged into one blob.
            ghosts: [
                { t: 0.300, radius: 13, alpha: 0.10 },
                { t: 0.380, radius: 22, alpha: 0.06 },
                { t: 0.470, radius: 9,  alpha: 0.13 },
                { t: 0.545, radius: 24, alpha: 0.05 },
                { t: 0.630, radius: 15, alpha: 0.09 },
                { t: 0.740, radius: 20, alpha: 0.055 },
                { t: 0.860, radius: 11, alpha: 0.08 },
            ],

            // A slow breath over the whole flare, so it is not a static decal painted on the
            // screen. Shallow on purpose: heat haze, not a pulsing light.
            shimmerPeriod: 5.5,     // seconds
            shimmerDepth: 0.22,

            // How far past the edge the sun may drift before the flare has fully faded. The
            // pan on this map never takes it that far, but a flare with no sun on screen
            // would be an obvious lie on any window shape that does.
            offScreenMargin: 280,
        },
    },

    blue: {
        // Rainy Dock. A SCREEN-SPACE layer -- see the note at the top of this file.
        rain: {
            // A light NEUTRAL grey -- deliberately neither white nor blue, and the two are
            // failure modes in opposite directions. Blue-tinted drops disappear into this
            // map, whose sky is grey-blue and whose water is blue, so they read as backdrop
            // rather than as water in front of it; white ones are too loud and pull the eye
            // off the fight. Note that dimming a drop by dropping its ALPHA instead would
            // walk straight back into the first problem, since a fainter drop is by
            // definition more of whatever is behind it -- here, blue. Grey stays grey.
            colour: '#d5dce3',

            // Two sheets at different depths. Each is one path and one stroke call, so the
            // whole storm costs two draw calls however many drops are in it.
            //
            // COUNTS ARE THE GUST PEAK, not the resting state -- `calmFraction` below is the
            // share actually drawn between gusts. Sizing the pool for the heaviest moment is
            // what lets the rain thicken during a squall without allocating mid-storm.
            layers: [
                {
                    // FAR: thin, slower, dim, short. Reads as depth behind the near sheet.
                    count: 62,
                    speed: 620,     // px/sec along the fall direction
                    length: 24,     // px, the streak drawn behind each drop
                    width: 1,
                    alpha: 0.24,
                },
                {
                    // NEAR: longer, faster, brighter. Still ONE px wide -- at two it stopped
                    // reading as rain and started reading as falling sticks.
                    count: 34,
                    speed: 920,
                    length: 44,
                    width: 1,
                    alpha: 0.42,
                },
            ],

            // WIND. The same shape as the volcano's hot draughts: calm most of the time,
            // then a sine bump that eases in and out. Two differences worth noting -- it
            // picks a DIRECTION each time, so squalls come from either side, and it drives
            // three things at once rather than one, because a gust of wind that only tilted
            // the rain would look like the whole storm was on a hinge.
            wind: {
                gapMin: 6,          // seconds of calm between squalls
                gapMax: 16,
                durationMin: 4,     // seconds from first stir back to still
                durationMax: 9,

                // A standing lean, always present. Dead-vertical rain reads as mechanical --
                // a grid of falling lines rather than weather -- and four degrees is enough
                // to break that up without looking like there is any wind to speak of.
                // Fixed in ONE direction, unlike the squalls, because a prevailing lean that
                // wandered from side to side would just be a very slow squall.
                baseSlantDeg: 4,

                // The squall's swing, ADDED to the lean above. So a squall running with the
                // prevailing wind peaks at 30 degrees and one running against it at -22,
                // which is a bit of asymmetry worth having for free.
                maxSlantDeg: 26,

                // Share of each sheet drawn when calm; the rest join in as the gust builds,
                // so a squall genuinely rains harder rather than just leaning over.
                //
                // Low, because the resting state is what the player looks at for most of a
                // game and it wants to be barely there. The side effect is that a squall is
                // now roughly a TRIPLING of the rain rather than a modest thickening, which
                // is what makes one feel like an event.
                calmFraction: 0.34,

                // And it comes down a little faster while it is being driven.
                speedBoost: 1.15,
            },

            // Drops respawn above the top edge, spread over this many px so a sheet never
            // arrives as a visible rank of drops all on the same line.
            spawnScatter: 90,

            // Rain landing on the dock.
            //
            // These are WORLD-ANCHORED even though they are drawn in the overlay pass: a
            // splash belongs to the plank it landed on, so it is stored at a map x and drawn
            // at x - cameraX. Screen-space splashes would slide across the boards during the
            // pre-game camera sweep, which is a long pan and would make it obvious.
            //
            // They are NOT tied to individual drops. Drops here fall past the dock because
            // the rain is in front of the whole scene, camera included -- tying a splash to
            // a drop would mean deciding which drops are "behind" the dock, and the effect
            // reads exactly the same without that bookkeeping.
            splash: {
                // The dock surface, in map rows. Scattering across the band rather than
                // sitting on one line is what gives the boards their sense of depth.
                groundTop: 350,
                groundBottom: 500,

                // Sized from the arithmetic, not guessed: at `ratePerSecond` spawns lasting
                // `life` each, no more than rate x life can ever be alive at once -- 16 x
                // 0.34, call it 6. Sixteen leaves roughly triple headroom and no more. A
                // measured run peaked at 5 concurrent.
                poolSize: 16,

                // Per second at a squall's peak, scaled down by the same intensity that
                // thins the rain -- so the dock quietens between squalls along with the sky.
                ratePerSecond: 16,

                life: 0.34,             // seconds

                // The impact mark: a short horizontal dash that widens as it fades.
                dashHalfWidthStart: 1,
                dashHalfWidthEnd: 4,

                // And a couple of specks kicked up and outward, falling back under gravity.
                dropletCount: 2,
                dropletSpeedMin: 18,    // px/sec
                dropletSpeedMax: 34,
                dropletGravity: 150,    // px/sec^2

                // Brighter than the falling rain on purpose. A drop is a thin streak seen
                // against the sky; a splash is a burst of water catching the light on a
                // solid surface, and at the rain's own alpha it barely registered on the
                // tan boards.
                alpha: 0.72,
            },
        },
    },
};

const rand = (min, max) => min + Math.random() * (max - min);

/// Deterministic 0..1 from two numbers. Used for the lamp chatter, where the value has to be
/// stable for a whole stutter STEP -- a fresh Math.random() every frame would flicker at the
/// frame rate instead of at the configured stutter rate, and would look different on every
/// machine. The constants are the well-worn GLSL one-liner; nothing depends on their quality
/// beyond "looks unpatterned".
const hash01 = (a, b) => {
    const v = Math.sin(a * 12.9898 + b * 78.233) * 43758.5453;
    return v - Math.floor(v);
};

class Atmosphere {
    constructor() {
        this.colour = null;
        this.clouds = [];
        this.particles = [];
        this.fireflies = [];
        this.stars = [];
        this.nextStarAt = 0;
        this.leaves = [];
        this.lamps = [];
        this.dust = [];
        this.lamps = [];
        this.dust = [];
        this.rainLayers = [];
        this.wind = null;
        this.splashes = [];
        this.splashAccum = 0;
        this.lastDt = 0;
        this.gust = null;
        this.lastTime = performance.now();
        this.elapsed = 0;
    }

    /// Draws the ambient layer for one map, advancing it by however long it has been since
    /// the last call. Safe to call every frame, on any map, before any art has loaded.
    ///
    /// `shadowFilter` is the canvas filter string for a shadow map, or null -- passed in
    /// rather than imported so this module stays independent of view.js.
    render(ctx, colour, shadowFilter = null) {
        // Advance the clock FIRST, and unconditionally. Doing it after the early-outs below
        // would bank the entire time spent on a map with no atmosphere and spend it in one
        // jump on arriving at a map that has one.
        const now = performance.now();
        const dt = Math.min((now - this.lastTime) / 1000, 0.1);
        this.lastTime = now;
        this.elapsed += dt;
        // Kept for renderOverlay, which runs later in the SAME frame and must not compute a
        // second dt of its own -- the two calls would then split one frame's worth of time
        // between them and everything would move at half speed.
        this.lastDt = dt;

        this.#ensureScene(colour);
        if (!this.clouds.length && !this.particles.length && !this.fireflies.length
            && !this.stars.length && !this.leaves.length
            && !this.lamps.length && !this.dust.length) return;

        ctx.save();
        if (shadowFilter) ctx.filter = shadowFilter;

        for (const cloud of this.clouds) {
            // Position is periodic over the map's width, so a cloud leaving the right edge
            // is already re-entering on the left. Deliberately NOT a respawn-when-offscreen
            // scheme: there is no moment where a cloud pops out of existence, no reset to
            // get the timing of wrong, and the sky is identical every lap.
            cloud.x = (cloud.x + cloud.speed * dt) % MAP_WIDTH;

            const y = cloud.y + Math.sin(this.elapsed * cloud.bobRate + cloud.bobPhase) * cloud.bob;

            ctx.globalAlpha = cloud.alpha;

            // Three copies, one map-width apart. The visible slice of the background can be
            // wider than the map on a very wide window, and the wrap point itself has to be
            // covered from both sides, so drawing only at `x` would show a cloud vanish at
            // one edge before its copy appeared at the other. Off-screen copies cost a
            // rejected drawImage each and nothing else.
            for (let lap = -1; lap <= 1; lap++) {
                ctx.drawImage(
                    cloud.image,
                    cloud.x + lap * MAP_WIDTH,
                    y,
                    cloud.width,
                    cloud.height);
            }
        }

        this.#renderParticles(ctx, dt);
        this.#renderFireflies(ctx);
        this.#renderShootingStars(ctx, dt);
        this.#renderLeaves(ctx, dt);
        this.#renderLamps(ctx);
        this.#renderDust(ctx, dt);

        ctx.restore();
    }

    /// Drifting motes -- embers and soot here, and whatever the other maps grow. Drawn as
    /// plain filled squares rather than sprites: at one to three pixels a PNG would buy
    /// nothing and cost a load, and a colour in a config is far easier to tune than art.
    #renderParticles(ctx, dt) {
        if (!this.particles.length) return;

        const config = SCENES[this.colour].particles;

        // Advance everything first, then draw in two passes. Splitting the passes is what
        // lets the embers use additive blending without a compositing-mode change per
        // particle, and it puts every ember in front of every mote of soot, which is the
        // right way round for something glowing.
        const lift = this.#gustMultiplier(config);

        for (const p of this.particles) {
            p.age += dt;
            if (p.age >= p.life) this.#resetParticle(p, config, false);

            p.x = (p.x + p.drift * dt + MAP_WIDTH) % MAP_WIDTH;
            p.y -= p.rise * lift * dt;
        }

        // Soot first, embers second, so every ember sits in front of every mote of ash.
        // Both passes use ordinary blending -- see emberColour for why not additive.
        for (const p of this.particles) {
            if (p.flickerRate !== 0) continue;
            this.#drawParticle(ctx, p, config);
        }
        for (const p of this.particles) {
            if (p.flickerRate === 0) continue;
            this.#drawParticle(ctx, p, config);
        }
    }

    /// Fireflies: wander, and blink.
    ///
    /// Position is derived from `elapsed` rather than integrated frame by frame, so no drift
    /// accumulates and a dropped frame costs nothing -- a firefly is always exactly where
    /// its own clock says it should be.
    #renderFireflies(ctx) {
        if (!this.fireflies.length) return;

        const config = SCENES[this.colour].fireflies;

        // --- Advance ------------------------------------------------------------------
        // Position and glow are worked out for every firefly first, so the two draw passes
        // below can each set one blend mode and then just paint.
        for (const f of this.fireflies) {
            if (this.elapsed < f.hiddenUntil) { f.visible = false; continue; }

            const phase = ((this.elapsed + f.blinkOffset) % f.period) / f.period;

            // The instant a flash ENDS is the only moment it may leave. Detected as a
            // crossing rather than by testing "is dark", so the roll happens exactly once
            // per cycle however many frames are drawn -- or, if frames were dropped, not at
            // all, which costs nothing.
            if (f.lastPhase <= f.onFraction && phase > f.onFraction
                && Math.random() < config.darkDespawnChance) {
                this.#spawnFirefly(f, f.cfg, false);
                continue;
            }
            f.lastPhase = phase;

            f.visible = true;
            f.glow = phase <= f.onFraction
                ? f.peakAlpha * Math.sin(Math.PI * (phase / f.onFraction))
                : 0;

            // fastShare is shared across bands, so `config` is the right source here; the
            // amplitudes it scales were drawn from this firefly's own band.
            f.x = f.homeX
                + Math.sin(this.elapsed * f.slowRateX + f.phaseX) * f.wanderX
                + Math.sin(this.elapsed * f.fastRateX + f.phaseX2) * f.wanderX * config.fastShare;
            f.y = f.homeY
                + Math.sin(this.elapsed * f.slowRateY + f.phaseY) * f.wanderY
                + Math.sin(this.elapsed * f.fastRateY + f.phaseY2) * f.wanderY * config.fastShare;
        }

        // --- The bodies ---------------------------------------------------------------
        // Ordinary blending, and fading out exactly as the glow comes up, so the insect is
        // never both a silhouette and a lamp at once.
        ctx.fillStyle = config.darkColour;
        for (const f of this.fireflies) {
            if (!f.visible || f.glow >= 1 || !f.cfg.showDarkBody) continue;
            ctx.globalAlpha = config.darkAlpha * (1 - f.glow);
            ctx.fillRect(f.x, f.y, f.size, f.size);
        }

        // --- The glow -----------------------------------------------------------------
        ctx.globalCompositeOperation = 'lighter';
        ctx.fillStyle = config.colour;
        for (const f of this.fireflies) {
            if (!f.visible || f.glow <= 0) continue;

            // Outermost ring first so the brighter ones stack on top of it.
            for (const ring of config.halo) {
                ctx.globalAlpha = f.glow * ring.alpha;
                ctx.fillRect(f.x - ring.padding, f.y - ring.padding,
                             f.size + ring.padding * 2, f.size + ring.padding * 2);
            }

            ctx.globalAlpha = f.glow;
            ctx.fillRect(f.x, f.y, f.size, f.size);
        }

        // Put the mode back: a scene with both layers must not inherit additive blending,
        // and render()'s save/restore is the only other thing standing between them.
        ctx.globalCompositeOperation = 'source-over';
    }

    /// Warehouse lamps. Each cone is PRE-RENDERED once into its own little canvas at scene
    /// build; per frame this is one drawImage apiece with the brightness as globalAlpha.
    ///
    /// That is what makes soft edges affordable. A gaussian falloff across the width cannot
    /// be expressed as a canvas gradient (they are radial or linear, not a widening wedge),
    /// so drawing it live would mean dozens of gradient strips per lamp per frame. The shape
    /// never changes -- only how bright it is -- so it is computed per pixel exactly once.
    #renderLamps(ctx) {
        if (!this.lamps.length) return;

        const config = SCENES[this.colour].lamps;

        ctx.save();
        ctx.globalCompositeOperation = 'lighter';

        for (const lamp of this.lamps) {
            lamp.brightness = this.#lampBrightness(lamp, config);
            if (!lamp.cone) continue;

            ctx.globalAlpha = config.coneAlpha * lamp.brightness;
            ctx.drawImage(lamp.cone, lamp.x - lamp.bottomHalf, lamp.y);
        }

        ctx.restore();
    }

    /// Paints one lamp's cone into an offscreen canvas: a gaussian across the width, fading
    /// with distance from the bulb. Called once per lamp when the scene is built.
    #buildCone(lamp, config) {
        const w = Math.max(1, Math.ceil(lamp.bottomHalf * 2));
        const h = Math.max(1, Math.ceil(lamp.length));

        const canvas = document.createElement('canvas');
        canvas.width = w;
        canvas.height = h;

        const cctx = canvas.getContext('2d');
        const image = cctx.createImageData(w, h);
        const data = image.data;

        const [r, g, b] = config.colour.split(',').map(Number);
        const centre = lamp.bottomHalf;

        for (let y = 0; y < h; y++) {
            const along = y / h;
            const halfWidth = lamp.topHalf + (lamp.bottomHalf - lamp.topHalf) * along;
            const depth = Math.pow(1 - along, config.depthFalloff);

            for (let x = 0; x < w; x++) {
                const u = (x - centre) / halfWidth;
                const across = Math.exp(-config.edgeSoftness * u * u);

                const i = (y * w + x) * 4;
                data[i] = r;
                data[i + 1] = g;
                data[i + 2] = b;
                data[i + 3] = Math.round(255 * across * depth);
            }
        }

        cctx.putImageData(image, 0, 0);
        return canvas;
    }

    /// One lamp's brightness right now: a slow breath while steady, or hard chatter while
    /// it is having a moment.
    #lampBrightness(lamp, config) {
        if (this.elapsed < lamp.stutterUntil) {
            // Quantised to stutterHz so the chatter runs at a fixed rate rather than at
            // whatever the frame rate happens to be -- otherwise a fast machine would show
            // a different (and much faster) flicker than a slow one.
            const step = Math.floor(this.elapsed * config.stutterHz);
            const roll = hash01(lamp.seed, step);
            return roll < config.stutterDutyOff
                ? config.stutterLow * (0.5 + roll)
                : 1;
        }

        if (this.elapsed >= lamp.nextStutterAt) {
            lamp.stutterUntil = this.elapsed + rand(config.stutterMin, config.stutterMax);
            lamp.nextStutterAt = lamp.stutterUntil + rand(config.gapMin, config.gapMax);
        }

        return 1 - config.breathDepth
            * (0.5 + 0.5 * Math.sin(this.elapsed * (2 * Math.PI) / config.breathPeriod + lamp.seed));
    }

    /// Dust hanging in the warehouse air, lit by whichever lamp it is passing under.
    #renderDust(ctx, dt) {
        if (!this.dust.length) return;

        const config = SCENES[this.colour].dust;
        const lampConfig = SCENES[this.colour].lamps;

        ctx.save();
        ctx.globalCompositeOperation = 'lighter';
        ctx.fillStyle = `rgb(${config.colour})`;

        for (const mote of this.dust) {
            mote.y += mote.sink * dt;
            mote.x = ((mote.x + mote.drift * dt) % MAP_WIDTH + MAP_WIDTH) % MAP_WIDTH;

            if (mote.y > config.bandBottom) {
                mote.y = config.bandTop;
                mote.x = Math.random() * MAP_WIDTH;
            }

            const x = mote.x + Math.sin(this.elapsed * mote.swayRate + mote.swayPhase) * mote.sway;

            // How lit is this mote? Brightest cone only -- summing every lamp would make
            // the whole ceiling uniformly bright and erase the separate pools entirely.
            //
            // Multiplied by the lamp's CURRENT brightness, so dust hanging in a stuttering
            // lamp's cone flickers along with it. That coupling is most of why the light
            // reads as something filling the air rather than a shape on the wall.
            // Each lamp carries its OWN cone geometry, so the test has to be per lamp --
            // they hang at different heights and throw different widths.
            let lit = 0;
            for (const lamp of this.lamps) {
                const depth = mote.y - lamp.y;
                if (depth < 0 || depth > lamp.length) continue;

                const along = depth / lamp.length;
                const halfWidth = lamp.topHalf + (lamp.bottomHalf - lamp.topHalf) * along;
                const u = (x - lamp.x) / halfWidth;

                // The same gaussian and fade the cone itself is painted with, so a mote is
                // exactly as lit as the light it is standing in.
                const fall = Math.exp(-lampConfig.edgeSoftness * u * u)
                           * Math.pow(1 - along, lampConfig.depthFalloff)
                           * lamp.brightness;
                if (fall > lit) lit = fall;
            }

            const twinkle = 1 - config.twinkleDepth
                * (0.5 + 0.5 * Math.sin(this.elapsed * mote.twinkleRate + mote.twinklePhase));

            ctx.globalAlpha = (config.ambientAlpha
                + (config.litAlpha - config.ambientAlpha) * lit) * twinkle;
            ctx.fillRect(x, mote.y, mote.size, mote.size);
        }

        ctx.restore();
    }

    /// Falling blossom. The only world-space layer that uses a SPRITE rather than shapes,
    /// so it is also the only one that touches the canvas transform -- each petal needs its
    /// own rotation and flip, which cannot be expressed as a rectangle.
    #renderLeaves(ctx, dt) {
        if (!this.leaves.length) return;

        const config = SCENES[this.colour].leaves;

        for (const leaf of this.leaves) {
            leaf.y += leaf.fall * dt;
            leaf.driftX += leaf.drift * dt;

            if (leaf.y > config.recycleBelow) this.#spawnLeaf(leaf, config, false);

            const x = leaf.homeX + leaf.driftX
                + Math.sin(this.elapsed * leaf.swayRate + leaf.swayPhase) * leaf.sway;

            // Edge-on for an instant twice a turn. Skipped rather than drawn at zero width:
            // a zero-width transform is a degenerate matrix, and there is nothing to see.
            const flip = Math.cos(this.elapsed * leaf.flipRate + leaf.flipPhase);
            if (Math.abs(flip) < 0.04) continue;

            const w = leaf.image.naturalWidth * leaf.scale;
            const h = leaf.image.naturalHeight * leaf.scale;

            ctx.save();
            ctx.globalAlpha = leaf.alpha;
            // Rotate and flip about the petal's CENTRE, not its corner, or a spinning petal
            // would orbit its own top-left instead of turning on the spot.
            ctx.translate(x + w / 2, leaf.y + h / 2);
            ctx.rotate(this.elapsed * leaf.spinRate + leaf.spinPhase);
            ctx.scale(flip, 1);
            ctx.drawImage(leaf.image, -w / 2, -h / 2, w, h);
            ctx.restore();
        }
    }

    /// Puts one petal back at the top with a fresh fall, sway and tumble. `initial` scatters
    /// the first batch down the whole screen so the map does not open with a bare sky and a
    /// front of blossom arriving together.
    #spawnLeaf(leaf, config, initial) {
        leaf.homeX = Math.random() * MAP_WIDTH;
        leaf.driftX = 0;
        leaf.y = initial
            ? rand(config.spawnTop, config.recycleBelow)
            : rand(config.spawnTop, config.spawnBottom);

        leaf.fall = rand(config.fallMin, config.fallMax);
        leaf.drift = rand(config.driftMin, config.driftMax);
        leaf.sway = config.swayAmplitude + Math.random() * config.swayExtra;
        leaf.swayRate = (2 * Math.PI) / rand(config.swayPeriodMin, config.swayPeriodMax);
        leaf.swayPhase = Math.random() * Math.PI * 2;
        leaf.spinRate = rand(config.spinRateMin, config.spinRateMax) * (Math.random() < 0.5 ? -1 : 1);
        leaf.spinPhase = Math.random() * Math.PI * 2;
        leaf.flipRate = rand(config.flipRateMin, config.flipRateMax);
        leaf.flipPhase = Math.random() * Math.PI * 2;
        leaf.scale = rand(config.scaleMin, config.scaleMax);
        leaf.alpha = rand(config.alphaMin, config.alphaMax);
    }

    /// The screen-space pass. Called after the camera transform has been restored, so
    /// nothing here scrolls with the map.
    ///
    /// Uses the dt that `render` already worked out this frame rather than measuring its own
    /// -- see lastDt. `screenWidth` is the visible width in LOGICAL px, which changes with
    /// the window, so it is passed in every frame rather than cached.
    renderOverlay(ctx, colour, screenWidth, cameraX = 0, shadowFilter = null) {
        this.#ensureScene(colour);
        if (!this.rainLayers.length && !SCENES[colour]?.lensFlare) return;

        const flare = SCENES[colour].lensFlare;
        if (flare) {
            ctx.save();
            if (shadowFilter) ctx.filter = shadowFilter;
            this.#renderLensFlare(ctx, flare, screenWidth, cameraX);
            ctx.restore();
            return;
        }

        const config = SCENES[colour].rain;

        // The squall is advanced ONCE per frame, here, and handed to both layers. It has to
        // live outside them because #windBump schedules the next squall as a side effect --
        // calling it from each layer would run the storm's clock at double speed -- and
        // because the splashes need the same intensity the rain is being drawn at.
        const wind = this.#windBump(config.wind);
        const angle = (config.wind.baseSlantDeg + config.wind.maxSlantDeg * wind.bump * wind.dir)
                    * Math.PI / 180;
        const speedScale = 1 + (config.wind.speedBoost - 1) * wind.bump;
        const share = config.wind.calmFraction + (1 - config.wind.calmFraction) * wind.bump;

        ctx.save();
        if (shadowFilter) ctx.filter = shadowFilter;

        // Splashes first: they sit ON the dock, and the rain falls in front of everything,
        // the dock and its splashes included.
        this.#renderSplashes(ctx, this.lastDt, screenWidth, cameraX, share);
        this.#renderRain(ctx, this.lastDt, screenWidth, angle, speedScale, share);

        ctx.restore();
    }

    /// Sun glare. Stateless -- there is nothing to advance, since the whole thing is a
    /// function of where the sun is on screen right now and what time it is.
    #renderLensFlare(ctx, config, screenWidth, cameraX) {
        const width = screenWidth > 0 ? screenWidth : MAP_WIDTH;

        // The sun belongs to the BACKGROUND, which view.js draws at half parallax, so its
        // screen position moves at half the camera's rate. Getting this wrong would be
        // invisible while the camera is still and obviously wrong the moment it panned.
        const sunX = config.sunWorldX - cameraX / 2;
        const sunY = config.sunWorldY;

        // Fade the whole flare out as the sun leaves the frame. No sun, no glare.
        let visible = 1;
        if (sunX < 0) visible = 1 + sunX / config.offScreenMargin;
        else if (sunX > width) visible = 1 - (sunX - width) / config.offScreenMargin;
        if (visible <= 0) return;
        visible = Math.min(1, visible);

        const shimmer = 1 - config.shimmerDepth
            * (0.5 + 0.5 * Math.sin(this.elapsed * (2 * Math.PI) / config.shimmerPeriod));
        const strength = visible * shimmer;

        // Additive: a flare ADDS light to the frame. Note this map's sky is saturated cyan,
        // so green and blue are already at the ceiling there -- adding a warm colour can
        // only lift the red channel, which is why the glare reads as a warm wash over the
        // sky and as a proper brightening over the dunes.
        ctx.globalCompositeOperation = 'lighter';

        // The bloom around the sun, as a radial gradient. Alpha lives in the colour stops
        // rather than in globalAlpha so the falloff is part of the gradient.
        const bloom = ctx.createRadialGradient(sunX, sunY, 0, sunX, sunY, config.bloomRadius);
        bloom.addColorStop(0, `rgba(${config.colour},${config.bloomAlpha * strength})`);
        bloom.addColorStop(1, `rgba(${config.colour},0)`);
        ctx.globalAlpha = 1;
        ctx.fillStyle = bloom;
        ctx.fillRect(sunX - config.bloomRadius, sunY - config.bloomRadius,
                     config.bloomRadius * 2, config.bloomRadius * 2);

        // Ghosts along the sun-to-centre line.
        //
        // The gradient does NOT fade to nothing at the rim -- it brightens toward it and
        // stops. The polygon's own edge is what ends the shape, giving a crisp aperture
        // silhouette rather than the soft blur a fully-fading gradient would produce.
        const centreX = width / 2;
        const centreY = LOGICAL_HEIGHT / 2;
        const rotation = config.ghostRotationDeg * Math.PI / 180;
        ctx.globalAlpha = 1;

        for (const ghost of config.ghosts) {
            const gx = sunX + (centreX - sunX) * ghost.t;
            const gy = sunY + (centreY - sunY) * ghost.t;
            const a = ghost.alpha * strength;

            const fill = ctx.createRadialGradient(gx, gy, 0, gx, gy, ghost.radius);
            fill.addColorStop(0, `rgba(${config.colour},${a * 0.45})`);
            fill.addColorStop(0.75, `rgba(${config.colour},${a * 0.9})`);
            fill.addColorStop(1, `rgba(${config.colour},${a * 0.7})`);
            ctx.fillStyle = fill;

            ctx.beginPath();
            for (let i = 0; i < config.ghostSides; i++) {
                const angle = rotation + (i / config.ghostSides) * Math.PI * 2;
                const px = gx + Math.cos(angle) * ghost.radius;
                const py = gy + Math.sin(angle) * ghost.radius;
                if (i === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py);
            }
            ctx.closePath();
            ctx.fill();
        }

        ctx.globalCompositeOperation = 'source-over';
        ctx.globalAlpha = 1;
    }

    /// Impacts on the dock. `intensity` is the same 0..1 share the wind uses to thin the
    /// rain, so the dock quietens and livens with the sky rather than drumming steadily
    /// through a lull.
    #renderSplashes(ctx, dt, screenWidth, cameraX, intensity) {
        const rain = SCENES[this.colour].rain;
        const config = rain.splash;
        if (!config || !this.splashes.length) return;

        const width = screenWidth > 0 ? screenWidth : MAP_WIDTH;

        // Fractional accumulator rather than a per-frame probability: the spawn rate then
        // means the same thing whatever the frame rate, and a slow frame owes exactly as
        // many splashes as the time it took.
        this.splashAccum += config.ratePerSecond * intensity * dt;
        while (this.splashAccum >= 1) {
            this.splashAccum -= 1;
            this.#launchSplash(config, width, cameraX);
        }

        ctx.strokeStyle = rain.colour;
        ctx.fillStyle = rain.colour;
        ctx.lineWidth = 1;

        for (const sp of this.splashes) {
            if (!sp.active) continue;

            sp.age += dt;
            if (sp.age >= config.life) { sp.active = false; continue; }

            const t = sp.age / config.life;
            const fade = 1 - t;
            const x = sp.worldX - cameraX;

            // Cull once it has scrolled off: cheaper than drawing it, and it keeps a pan
            // from spending the whole pool on splashes nobody can see.
            if (x < -20 || x > width + 20) continue;

            ctx.globalAlpha = config.alpha * fade;

            // The impact mark, widening as it fades.
            const half = config.dashHalfWidthStart
                + (config.dashHalfWidthEnd - config.dashHalfWidthStart) * t;
            ctx.beginPath();
            ctx.moveTo(x - half, sp.y);
            ctx.lineTo(x + half, sp.y);
            ctx.stroke();

            // Specks kicked up, arcing back down. Drawn at 1px: anything larger stops
            // reading as water and starts reading as debris.
            for (const drop of sp.droplets) {
                const dx = drop.vx * sp.age;
                const dy = drop.vy * sp.age + 0.5 * config.dropletGravity * sp.age * sp.age;
                ctx.fillRect(x + dx, sp.y + dy, 1, 1);
            }
        }
    }

    #launchSplash(config, screenWidth, cameraX) {
        const sp = this.splashes.find(x => !x.active);
        if (!sp) return;

        // Spawned across the VISIBLE slice rather than the whole map: at any moment most of
        // a 2000px map is off screen, and splashing there would spend the pool on nothing.
        sp.worldX = cameraX + Math.random() * screenWidth;
        sp.y = rand(config.groundTop, config.groundBottom);
        sp.age = 0;
        sp.active = true;

        sp.droplets = [];
        for (let i = 0; i < config.dropletCount; i++) {
            const speed = rand(config.dropletSpeedMin, config.dropletSpeedMax);
            sp.droplets.push({
                // Out to one side and up. The sign split means a splash throws specks both
                // ways rather than a pair that happen to agree.
                vx: (i % 2 === 0 ? 1 : -1) * speed * rand(0.5, 1),
                vy: -speed,
            });
        }
    }

    /// `angle`, `speedScale` and `share` all come from the one squall resolved in
    /// renderOverlay -- a sheet must never work out its own, or two sheets would lean
    /// different ways and read as two storms.
    #renderRain(ctx, dt, screenWidth, angle, speedScale, share) {
        const config = SCENES[this.colour].rain;

        // view.resize sets logicalScreenWidth and script.js calls it at startup, so this is
        // always a real number in practice. Guarded anyway because the failure mode is
        // permanent rather than transient: one NaN width turns every drop's x into NaN, and
        // NaN never recovers on a later frame -- the rain would simply stop for good.
        const width = screenWidth > 0 ? screenWidth : MAP_WIDTH;

        const sin = Math.sin(angle);
        const cos = Math.cos(angle);

        ctx.strokeStyle = config.colour;
        ctx.lineCap = 'butt';

        for (const sheet of this.rainLayers) {
            const layer = sheet.layer;
            const vx = sin * layer.speed * speedScale;
            const vy = cos * layer.speed * speedScale;
            const tailX = sin * layer.length;
            const tailY = cos * layer.length;

            // Drawn from the front of the pool, so the ones that drop out between squalls
            // are always the same tail of the array -- and since drops are anonymous and
            // the alpha ramps alongside, the boundary is not something the eye can find.
            const visible = Math.round(layer.count * share);

            ctx.globalAlpha = layer.alpha;
            ctx.lineWidth = layer.width;
            ctx.beginPath();

            for (let i = 0; i < sheet.drops.length; i++) {
                const drop = sheet.drops[i];

                // EVERY drop moves, drawn or not. Freezing the hidden tail would park a
                // block of drops in mid-air, to reappear in a frozen rank the moment the
                // next squall reached them.
                drop.x += vx * dt;
                drop.y += vy * dt;

                if (drop.y > LOGICAL_HEIGHT) {
                    drop.y = -rand(0, config.spawnScatter) - layer.length;
                    drop.x = Math.random() * width;
                }

                // Wrap sideways rather than respawning: a squall walks every drop steadily
                // off one edge, and rain is anonymous enough that reappearing on the other
                // side is invisible -- where respawning would thin out the downwind half.
                drop.x = ((drop.x % width) + width) % width;

                if (i >= visible) continue;

                // One path for the whole sheet, stroked once below.
                ctx.moveTo(drop.x, drop.y);
                ctx.lineTo(drop.x - tailX, drop.y - tailY);
            }

            ctx.stroke();
        }
    }

    /// Where the wind is in its cycle: `bump` rises from 0 to 1 and back across a squall and
    /// sits at 0 between them, `dir` is which way this squall leans. Same envelope as the
    /// volcano's draughts, and the same reason for it -- a gust that switched on would read
    /// as a glitch rather than as weather.
    #windBump(config) {
        const w = this.wind;

        if (this.elapsed < w.until) {
            const t = (this.elapsed - w.from) / (w.until - w.from);
            return { bump: Math.sin(Math.PI * t), dir: w.dir };
        }

        // Between squalls. Measured from the END of the last one, so two never run together.
        if (this.elapsed >= w.nextAt) {
            w.from = this.elapsed;
            w.until = this.elapsed + rand(config.durationMin, config.durationMax);
            w.dir = Math.random() < 0.5 ? -1 : 1;
            w.nextAt = w.until + rand(config.gapMin, config.gapMax);
        }

        return { bump: 0, dir: w.dir };
    }

    /// Meteors. A pool of slots, one lit on a schedule, each flying a straight line and
    /// fading out -- so the cost is the pool, not the frequency.
    #renderShootingStars(ctx, dt) {
        if (!this.stars.length) return;

        const config = SCENES[this.colour].shootingStars;

        if (this.elapsed >= this.nextStarAt) {
            this.#launchStar(config);
            this.nextStarAt = this.elapsed + rand(config.intervalMin, config.intervalMax);
        }

        ctx.globalCompositeOperation = 'lighter';
        ctx.fillStyle = config.colour;

        for (const star of this.stars) {
            if (!star.active) continue;

            star.age += dt;
            if (star.age >= star.life) { star.active = false; continue; }

            const t = star.age / star.life;
            let fade = 1;
            if (t < config.fadeIn) fade = t / config.fadeIn;
            else if (t > 1 - config.fadeOut) fade = (1 - t) / config.fadeOut;

            const travelled = star.speed * star.age;
            const headX = star.startX + star.dirX * travelled;
            const headY = star.startY + star.dirY * travelled;

            // Tail: blocks stepping backwards along the flight path, dimming toward the end.
            // Walked from the far end inwards so the brighter blocks land on top.
            const blocks = Math.max(1, Math.round(star.trailLength / config.trailStep));
            for (let i = blocks; i >= 1; i--) {
                const back = config.trailStep * i;
                const taper = Math.pow(1 - i / blocks, config.taperExponent);
                ctx.globalAlpha = fade * taper;
                ctx.fillRect(headX - star.dirX * back, headY - star.dirY * back,
                             config.trailThickness, config.trailThickness);
            }

            // Head: a bright square with a faint bloom, same trick as the fireflies.
            const pad = config.haloPadding;
            ctx.globalAlpha = fade * config.haloAlpha;
            ctx.fillRect(headX - pad, headY - pad,
                         config.headSize + pad * 2, config.headSize + pad * 2);

            ctx.globalAlpha = fade;
            ctx.fillRect(headX, headY, config.headSize, config.headSize);
        }

        ctx.globalCompositeOperation = 'source-over';
    }

    /// Lights up one idle slot. Does nothing if every slot is already in flight -- a missed
    /// launch is invisible, where growing the pool on demand would let a stutter in the
    /// scheduler turn into an unbounded shower.
    #launchStar(config) {
        const star = this.stars.find(x => !x.active);
        if (!star) return;

        const angle = rand(config.angleMinDeg, config.angleMaxDeg) * Math.PI / 180;
        const towardsRight = Math.random() < 0.5;

        star.dirX = towardsRight ? Math.cos(angle) : -Math.cos(angle);
        star.dirY = Math.sin(angle);          // always downward; meteors do not climb
        star.speed = rand(config.speedMin, config.speedMax);
        star.trailLength = rand(config.trailLengthMin, config.trailLengthMax);
        star.startY = rand(config.bandTop, config.bandBottom);

        // Start off the edge it is heading away from, so the streak is already at full
        // speed when it enters view rather than appearing from nothing mid-sky.
        star.startX = towardsRight
            ? rand(-200, MAP_WIDTH * 0.6)
            : rand(MAP_WIDTH * 0.4, MAP_WIDTH + 200);

        // THE HORIZON CLAMP. Life is whatever was rolled, or however long it takes to fall
        // to `horizonY`, whichever is shorter -- so a shallow star lives its full span and a
        // steep one burns out early instead of streaking into the hills.
        const fallTime = (config.horizonY - star.startY) / (star.speed * star.dirY);
        star.life = Math.min(rand(config.lifeMin, config.lifeMax), fallTime);
        star.age = 0;
        star.active = true;
    }

    /// Gives one firefly a fresh home, wander and blink cycle. Used both to build the swarm
    /// and to bring a vanished one back somewhere else, so the two can never drift apart --
    /// the same reasoning as #resetParticle.
    ///
    /// `initial` staggers the blink phases across the swarm on the first build. A firefly
    /// coming BACK instead gets a phase placed just after its lit window, so it reappears as
    /// a dark body and has to travel a while before it lights up; dropping it in mid-flash
    /// would be a lamp switching on out of nowhere.
    #spawnFirefly(f, config, initial) {
        const wanderX = rand(config.wanderXMin, config.wanderXMax);
        const wanderY = rand(config.wanderYMin, config.wanderYMax);

        // Inset the home position by the full excursion so the band is honoured exactly --
        // otherwise the widest wanderers swing up into the canopy and down through the
        // water. It is (1 + fastShare) of the amplitude, not the amplitude: the slow loop
        // and the jitter can peak together.
        const reachY = wanderY * (1 + config.fastShare);

        f.homeX = Math.random() * MAP_WIDTH;
        f.homeY = rand(config.bandTop + reachY, config.bandBottom - reachY);
        f.wanderX = wanderX;
        f.wanderY = wanderY;
        f.slowRateX = rand(config.slowRateMin, config.slowRateMax);
        f.slowRateY = rand(config.slowRateMin, config.slowRateMax);
        f.fastRateX = rand(config.fastRateMin, config.fastRateMax);
        f.fastRateY = rand(config.fastRateMin, config.fastRateMax);
        f.phaseX = Math.random() * Math.PI * 2;
        f.phaseY = Math.random() * Math.PI * 2;
        f.phaseX2 = Math.random() * Math.PI * 2;
        f.phaseY2 = Math.random() * Math.PI * 2;
        f.size = config.coreSizes[Math.floor(Math.random() * config.coreSizes.length)];
        f.period = rand(config.periodMin, config.periodMax);
        f.onFraction = rand(config.onFractionMin, config.onFractionMax);
        f.peakAlpha = rand(config.peakAlphaMin, config.peakAlphaMax);
        f.glow = 0;
        f.x = f.homeX;
        f.y = f.homeY;

        if (initial) {
            f.hiddenUntil = 0;
            f.blinkOffset = Math.random() * 100;
            f.visible = true;
            f.lastPhase = ((this.elapsed + f.blinkOffset) % f.period) / f.period;
        } else {
            f.hiddenUntil = this.elapsed + rand(config.hiddenMin, config.hiddenMax);
            f.visible = false;
            // Land just past the lit window at the moment it returns, so it comes back dark.
            const startPhase = f.onFraction + 0.02;
            f.blinkOffset = f.period * startPhase - f.hiddenUntil;
            f.lastPhase = startPhase;
        }
    }

    /// How much faster than its own rise speed the whole field is moving right now.
    ///
    /// Idles at exactly 1 -- the configured rise range is the floor, never an average -- and
    /// occasionally swells through a sine bump up to `gustStrength` and back. Returns 1 for
    /// any scene without gusts configured, so a map can opt out by saying nothing.
    #gustMultiplier(config) {
        if (!config.gustStrengthMax) return 1;

        const g = this.gust;

        if (this.elapsed < g.until) {
            const t = (this.elapsed - g.from) / (g.until - g.from);
            return 1 + (g.peak - 1) * Math.sin(Math.PI * t);
        }

        // Between gusts. Starting one here rather than on a timer means the gap is measured
        // from the END of the last gust, so two never run into each other.
        if (this.elapsed >= g.nextAt) {
            g.from = this.elapsed;
            g.until = this.elapsed + rand(config.gustDurationMin, config.gustDurationMax);
            g.peak = rand(config.gustStrengthMin, config.gustStrengthMax);
            g.nextAt = g.until + rand(config.gustGapMin, config.gustGapMax);
        }

        return 1;
    }

    #drawParticle(ctx, p, config) {
        // Fade in at birth and out at death, as fractions of this particle's own life.
        const t = p.age / p.life;
        let fade = 1;
        if (t < config.fadeIn) fade = t / config.fadeIn;
        else if (t > 1 - config.fadeOut) fade = (1 - t) / config.fadeOut;

        // Embers pulse, soot does not. flickerDepth is how much of the alpha the pulse
        // swings, so an ember never blinks fully out and never oversaturates.
        const flicker = p.flickerRate === 0
            ? 1
            : 1 - config.flickerDepth * (0.5 + 0.5 * Math.sin(this.elapsed * p.flickerRate + p.flickerPhase));

        ctx.globalAlpha = Math.max(0, Math.min(1, p.alpha * fade * flicker));
        ctx.fillStyle = p.colour;

        // One copy, no wrap laps -- unlike a 250px cloud, a 2px mote crossing the seam is a
        // single pixel and nobody can see it arrive.
        ctx.fillRect(p.x + Math.sin(this.elapsed * p.swayRate + p.swayPhase) * p.sway,
                     p.y, p.size, p.size);
    }

    /// Puts one particle at the bottom of the band with fresh properties. Used both to
    /// build the field and to recycle a dead one, so the two can never drift apart.
    /// `initial` staggers the ages on the first build, so the field does not open with
    /// every mote born on the same tick and pulsing in unison.
    #resetParticle(p, config, initial) {
        const isEmber = Math.random() < config.emberShare;

        p.x = Math.random() * MAP_WIDTH;
        p.y = rand(config.spawnTop, config.spawnBottom);
        p.size = config.sizes[Math.floor(Math.random() * config.sizes.length)];
        p.rise = rand(config.riseMin, config.riseMax);

        // Drift from the angle, so slope is what varies rather than raw sideways speed.
        const angle = (config.driftAngleMaxDeg * Math.PI / 180)
                    * Math.pow(Math.random(), config.driftAngleBias);
        const direction = Math.random() < config.driftRightwardShare ? 1 : -1;
        p.drift = p.rise * Math.tan(angle) * direction;
        p.sway = config.swayAmplitude;
        p.swayRate = (2 * Math.PI) / rand(config.swayPeriodMin, config.swayPeriodMax);
        p.swayPhase = Math.random() * Math.PI * 2;
        p.life = rand(config.lifeMin, config.lifeMax);
        p.age = initial ? Math.random() * p.life : 0;

        if (isEmber) {
            p.colour = config.emberColour;
            p.alpha = rand(config.emberAlphaMin, config.emberAlphaMax);
            p.flickerRate = rand(config.flickerRateMin, config.flickerRateMax);
            p.flickerPhase = Math.random() * Math.PI * 2;
        } else {
            p.colour = config.sootColours[Math.floor(Math.random() * config.sootColours.length)];
            p.alpha = rand(config.sootAlphaMin, config.sootAlphaMax);
            p.flickerRate = 0;
            p.flickerPhase = 0;
        }

        // On the FIRST build, fly the particle forward to match the age it was given.
        // Staggering the ages alone is not enough: every mote would still start down in the
        // spawn band, so the sky would begin empty and take a full lifetime to fill, and a
        // particle handed an age near the end of its life would fade out at ground level
        // without ever having risen. Advancing it here is the same as having simulated the
        // field before the first frame.
        if (initial) {
            p.y -= p.rise * p.age;
            p.x = (p.x + p.drift * p.age + MAP_WIDTH * 2) % MAP_WIDTH;
        }
    }

    /// Builds the cloud field when the map changes, and only then -- rebuilding every frame
    /// would reroll every position and leave the sky flickering.
    #ensureScene(colour) {
        if (this.colour === colour) return;
        this.colour = colour;
        this.clouds = [];
        this.particles = [];
        this.fireflies = [];
        this.stars = [];
        this.nextStarAt = 0;
        this.leaves = [];
        this.lamps = [];
        this.dust = [];
        this.lamps = [];
        this.dust = [];
        this.rainLayers = [];
        this.wind = null;
        this.splashes = [];
        this.splashAccum = 0;
        this.lastDt = 0;
        this.gust = null;

        const particleConfig = SCENES[colour]?.particles;
        if (particleConfig) {
            // Open calm: the first gust is a full gap away, not immediate.
            this.gust = {
                from: 0,
                until: 0,
                peak: 1,
                nextAt: this.elapsed + rand(particleConfig.gustGapMin ?? 0,
                                            particleConfig.gustGapMax ?? 0),
            };

            for (let i = 0; i < particleConfig.count; i++) {
                const p = {};
                this.#resetParticle(p, particleConfig, true);
                this.particles.push(p);
            }
        }

        const fireflyConfig = SCENES[colour]?.fireflies;
        if (fireflyConfig) {
            for (const band of fireflyConfig.bands) {
                // Flattened ONCE per band, not per firefly, and carried on each one so a
                // respawn lands back in the depth it came from rather than drifting between
                // them. Band keys win over the shared ones.
                const merged = { ...fireflyConfig, ...band };
                for (let i = 0; i < band.count; i++) {
                    const f = { cfg: merged };
                    this.#spawnFirefly(f, merged, true);
                    this.fireflies.push(f);
                }
            }
        }

        const rainConfig = SCENES[colour]?.rain;
        if (rainConfig) {
            // Direction is NOT resolved here any more -- the wind moves it, so vx/vy and the
            // tail vector are worked out per frame in #renderRain.
            this.wind = {
                from: 0,
                until: 0,
                dir: 1,
                nextAt: this.elapsed + rand(rainConfig.wind.gapMin, rainConfig.wind.gapMax),
            };

            for (const layer of rainConfig.layers) {
                const drops = [];
                for (let i = 0; i < layer.count; i++) {
                    // Seeded across the full height, not above the top edge: otherwise the
                    // storm opens with an empty screen and a wall of rain arriving together.
                    drops.push({ x: Math.random() * MAP_WIDTH, y: Math.random() * LOGICAL_HEIGHT });
                }
                this.rainLayers.push({ layer, drops });
            }

            if (rainConfig.splash) {
                for (let i = 0; i < rainConfig.splash.poolSize; i++) {
                    this.splashes.push({ active: false, droplets: [] });
                }
            }
        }

        const lampConfig = SCENES[colour]?.lamps;
        if (lampConfig) {
            lampConfig.fixtures.forEach((fixture, i) => {
                const lamp = {
                    x: fixture.x,
                    y: fixture.y,
                    topHalf: fixture.halfWidth * lampConfig.coneTopScale,
                    bottomHalf: fixture.halfWidth * lampConfig.coneSpread,
                    length: Math.max(1, lampConfig.floorY - fixture.y),
                    // Staggered so no two lamps ever reach for a stutter at the same moment.
                    seed: i * 17.13 + 3.7,
                    stutterUntil: 0,
                    nextStutterAt: this.elapsed + rand(lampConfig.gapMin, lampConfig.gapMax),
                    brightness: 1,
                };
                lamp.cone = this.#buildCone(lamp, lampConfig);
                this.lamps.push(lamp);
            });
        }

        const dustConfig = SCENES[colour]?.dust;
        if (dustConfig) {
            for (let i = 0; i < dustConfig.count; i++) {
                this.dust.push({
                    x: Math.random() * MAP_WIDTH,
                    y: rand(dustConfig.bandTop, dustConfig.bandBottom),
                    size: dustConfig.sizes[Math.floor(Math.random() * dustConfig.sizes.length)],
                    sink: rand(dustConfig.sinkMin, dustConfig.sinkMax),
                    drift: rand(dustConfig.driftMin, dustConfig.driftMax),
                    sway: dustConfig.swayAmplitude,
                    swayRate: (2 * Math.PI) / rand(dustConfig.swayPeriodMin, dustConfig.swayPeriodMax),
                    swayPhase: Math.random() * Math.PI * 2,
                    twinkleRate: (2 * Math.PI) / rand(dustConfig.twinklePeriodMin, dustConfig.twinklePeriodMax),
                    twinklePhase: Math.random() * Math.PI * 2,
                });
            }
        }

        const leafConfig = SCENES[colour]?.leaves;
        const leafArt = loader.assets.atmosphere[colour];
        if (leafConfig && leafArt) {
            const images = leafConfig.frames.map(id => leafArt[id]).filter(Boolean);
            if (images.length) {
                for (let i = 0; i < leafConfig.count; i++) {
                    const leaf = { image: images[i % images.length] };
                    this.#spawnLeaf(leaf, leafConfig, true);
                    this.leaves.push(leaf);
                }
            }
        }

        const starConfig = SCENES[colour]?.shootingStars;
        if (starConfig) {
            for (let i = 0; i < starConfig.poolSize; i++) this.stars.push({ active: false });
            // Do not open with one already streaking; wait a normal interval first.
            this.nextStarAt = this.elapsed + rand(starConfig.intervalMin, starConfig.intervalMax);
        }

        const config = SCENES[colour]?.clouds;
        const art = loader.assets.atmosphere[colour];
        if (!config || !art) return;

        const images = config.frames.map(id => art[id]).filter(Boolean);
        if (!images.length) return;

        for (let i = 0; i < config.count; i++) {
            const image = images[i % images.length];
            const scale = rand(config.scaleMin, config.scaleMax);
            const width = image.naturalWidth * scale;
            const height = image.naturalHeight * scale;

            // The lowest TOP this cloud can take and still keep its bottom edge inside the
            // band. Computed per cloud because a tall sprite has less headroom than a short
            // one; the Math.max below stops an oversized sprite inverting the range.
            const lowest = config.bandBottom - height;

            this.clouds.push({
                image,
                width,
                height,
                // Spread evenly around the loop and then jittered, so the field never opens
                // with a clump or an obvious procession.
                x: ((i + Math.random()) / config.count) * MAP_WIDTH,
                y: rand(config.bandTop, Math.max(config.bandTop, lowest)),
                speed: rand(config.speedMin, config.speedMax),
                alpha: rand(config.alphaMin, config.alphaMax),
                bob: config.bobAmplitude,
                bobRate: (2 * Math.PI) / rand(config.bobPeriodMin, config.bobPeriodMax),
                bobPhase: Math.random() * Math.PI * 2,
            });
        }
    }
}

const atmosphere = new Atmosphere();
export default atmosphere;
