using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Gadgets;
using CastleDefense.Engine.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;

namespace CastleDefense.Engine
{
    public class GameEngine
    {
        public GameState _state;

        // Config
        public const int MAP_WIDTH = 2000;

        /// <summary>
        /// The x of each castle's wall -- the line an attacker must reach to hit it, and
        /// the edge of the corridor units actually fight over. Named so placement code
        /// (WallEffect) references the same numbers this class measures against rather
        /// than repeating the literals.
        /// </summary>
        public const int P1_CASTLE_WALL = 200;
        public const int P2_CASTLE_WALL = MAP_WIDTH - 200;

        public const int TICKS_PER_SECOND = 30;
        public const int MAX_TICKS = 18_000;
        private const int INCOME_FREQUENCY = 30;
        private ConcurrentQueue<Action> _actionQueue = new ConcurrentQueue<Action>();

        // Optimization: Fast Lookup Cache
        private Dictionary<string, UnitDefinition> _unitCache;
        private float _maxUnitWidth;   // set by BuildCache

        private Dictionary<string, GadgetDefinition> _gadgetCache;
        private RewardParams _rewardParams;

        // Event for successful gadget use
        // Parameters: string gadgetId, int side, int position
        public event Action<string, int, int, Guid> OnGadgetAnimation;
        public event Action<int, GadgetDefinition> OnGadgetUpgraded;

        // Delayed gadget phases, as DATA. Copyable, serialisable, and safe to clone.
        // This replaced a closure-based `List<Action>` (removed 2026-08-29 once the last
        // of the 13 gadget-effect files had been migrated); PendingEffect.cs records why
        // closures made the engine un-cloneable and therefore blocked search entirely.
        private List<PendingEffect> _scheduledEffects = new List<PendingEffect>();

        /// <summary>
        /// The engine's own randomness. Added 2026-07-28 — previously every site that
        /// needed a random number constructed its own `new Random()` inline, which is
        /// unseedable, so two runs of an identical setup diverged mid-game and no
        /// benchmark could be made reproducible. Everything gameplay-affecting should
        /// draw from here so a seeded GameEngine replays exactly.
        ///
        /// Also a prerequisite for search/lookahead: rollouts have to be repeatable to
        /// be comparable, and a cloned engine needs its own independent stream.
        /// </summary>
        // private set (rather than readonly) so Clone() can give the copy its OWN stream —
        // a clone sharing the parent's Random would advance the parent's sequence every
        // time a rollout drew a number, making the real game depend on how much searching
        // happened to be done. That would be silent and very hard to track down.
        public Random Rng { get; private set; }

        /// <summary>
        /// A SEPARATE seeded stream used only to mint Unit.InstanceId values.
        ///
        /// Kept apart from Rng on purpose. Unit ids previously came from Guid.NewGuid(),
        /// which is unseeded — so two rollouts branched from the same position produced
        /// units with different ids, and clone-check's determinism test failed even
        /// though the actual gameplay was identical. Ids are never used to make a
        /// decision (only for HashSet membership, snipe targeting and animation), so this
        /// was cosmetic — but "cosmetic nondeterminism" is exactly the kind of thing that
        /// makes a search implementation impossible to debug later.
        ///
        /// Drawing the 16 id bytes from Rng would have shifted every subsequent gameplay
        /// draw and invalidated the existing ladder baseline. A dedicated stream keeps
        /// the gameplay sequence byte-for-byte unchanged.
        /// </summary>
        private Random _idRng;

        /// <summary>Deterministic replacement for Guid.NewGuid() for spawned units.</summary>
        public Guid NextUnitId()
        {
            var bytes = new byte[16];
            _idRng.NextBytes(bytes);
            return new Guid(bytes);
        }

        // Offset so the id stream never mirrors the gameplay stream for a given seed.
        private const int ID_SEED_OFFSET = 0x5EED;

        public GameEngine(GameState state, RewardParams rewardParams = null, int? seed = null)
        {
            _state = state;
            _rewardParams = rewardParams ?? RewardParams.Default;
            Rng = seed.HasValue ? new Random(seed.Value) : new Random();
            _idRng = seed.HasValue ? new Random(seed.Value ^ ID_SEED_OFFSET) : new Random();
            BuildCache();

            _state.Player1.OnGadgetUpgraded += (side, def) => OnGadgetUpgraded?.Invoke(side, def);
            _state.Player2.OnGadgetUpgraded += (side, def) => OnGadgetUpgraded?.Invoke(side, def);
        }

        /// <summary>Number of delayed gadget effects still queued. Diagnostics only.</summary>
        public int PendingEffectCount => _scheduledEffects.Count;

        /// <summary>
        /// Produces an independent copy of this engine and its entire game state, safe to
        /// advance with Tick()/ApplyAction() without touching the original.
        ///
        /// THIS IS WHAT THE PendingEffect REFACTOR WAS FOR. While delayed gadget effects
        /// were closures, a clone was impossible in principle: the captured lambdas held
        /// references to the original engine, so a cloned game's pending nuke would
        /// detonate on the original board. As data records they are just a list copy.
        ///
        /// What is deliberately NOT carried over:
        ///  - **Event subscribers** (OnGadgetAnimation / OnGadgetUpgraded, and the
        ///    per-player equivalents cleared in PlayerState.Clone). A rollout must not
        ///    fire real UI or training callbacks.
        ///  - **Queued input actions** (_actionQueue). Those are live player clicks from
        ///    GameHub; replaying them inside a hypothetical line makes no sense.
        ///  - **The RNG stream.** The clone gets its own, seeded when you pass rngSeed.
        ///    Pass one for reproducible rollouts; omit it for a throwaway snapshot.
        ///
        /// What IS shared, intentionally: the unit/gadget definition caches and
        /// RewardParams. Those are immutable lookup data, so sharing avoids rebuilding
        /// the caches on every clone — which matters when search clones thousands of
        /// times per decision.
        /// </summary>
        public GameEngine Clone(int? rngSeed = null)
        {
            // MemberwiseClone is SHALLOW: every reference-type field below is shared with the
            // original until it is explicitly replaced. Anything mutable added to GameEngine
            // must be reset here or the rollouts will silently write into the live game.
            //
            // This is not hypothetical. The UnitsPurchased/MoneySpentOnUnits counters were
            // added on 2026-08-07 as plain arrays and immediately read 92,686 units bought in
            // an ~8,200-tick game — search runs ~231x the real game in rollouts, and every one
            // of them was incrementing the live counter through a shared array reference.
            // Caught by a 20-game smoke test only because the number was absurd on its face; a
            // counter that was merely inflated 10% would have shipped.
            var copy = (GameEngine)MemberwiseClone();
            copy._state = _state.Clone();
            copy.UnitsPurchased = new long[3];
            copy.MoneySpentOnUnits = new double[3];
            copy._scheduledEffects = new List<PendingEffect>(_scheduledEffects);
            copy._actionQueue = new ConcurrentQueue<Action>();
            copy.OnGadgetAnimation = null;
            copy.OnGadgetUpgraded = null;
            // MUST be nulled with the others. An event is a mutable reference field, so a
            // shallow clone inherits the subscriber list and every one of search rollouts
            // fires it -- game 9A60C6 recorded 49,448 gadget casts for a game containing
            // about 40, inflating the replay from ~16KB to 357KB and polluting the DB.
            // Any NEW event added to this class has to be added here too.
            copy.OnGadgetCast = null;
            copy.Rng = rngSeed.HasValue ? new Random(rngSeed.Value) : new Random();
            copy._idRng = rngSeed.HasValue ? new Random(rngSeed.Value ^ ID_SEED_OFFSET) : new Random();
            return copy;
        }

        // Re-subscribe to OnGadgetUpgraded after PlayerState objects are replaced
        // (e.g., when the league JoinGame branch swaps in new PlayerState instances).
        public void RewirePlayerEvents()
        {
            _state.Player1.OnGadgetUpgraded += (side, def) => OnGadgetUpgraded?.Invoke(side, def);
            _state.Player2.OnGadgetUpgraded += (side, def) => OnGadgetUpgraded?.Invoke(side, def);
        }

        private void BuildCache()
        {
            _unitCache = new Dictionary<string, UnitDefinition>();
            foreach (var team in GameDataManager.Teams)
            {
                foreach (var unit in team.Roster)
                {
                    if (!_unitCache.ContainsKey(unit.Id))
                        _unitCache[unit.Id] = unit;
                }
            }

            _gadgetCache = new Dictionary<string, GadgetDefinition>();
            foreach (var gadget in GameDataManager.Gadgets)
            {
                if (!_gadgetCache.ContainsKey(gadget.Id))
                    _gadgetCache[gadget.Id] = gadget;
            }

            _unitCache["wall"] = GameDataManager.WallDefinition(1);
            _unitCache["wall_2"] = GameDataManager.WallDefinition(2);
            _unitCache["wall_3"] = GameDataManager.WallDefinition(3);

            // ARMAGEDDON is code-built rather than a master_gadgets.csv row (same pattern
            // as the walls above). Keeping it out of GameDataManager.Gadgets is load
            // bearing: GetStateVector one-hots every Offense and Defense gadget, so a new
            // CSV row would change the 348-float observation length and invalidate every
            // trained model, and it would also show up in the loadout picker.
            _gadgetCache[ArmageddonEffect.GadgetId] = GameDataManager.ArmageddonDefinition();

            // Widest unit that can exist, used to bound FindTargetsFast's backward scan.
            // Derived rather than hardcoded: this was a literal 200f, which is the widest
            // ROSTER unit but not the widest unit -- wall_3 is 75*6 = 450 (see
            // GameDataManager.WallDefinition), so the scan could break before reaching a
            // wall that was actually in contact. Computed after the wall entries are
            // inserted above so they are included.
            _maxUnitWidth = 0f;
            foreach (var def in _unitCache.Values)
                if (def.Width > _maxUnitWidth) _maxUnitWidth = def.Width;
        }

        /// <summary>
        /// Looks up a gadget definition by id. Public so effects that chain into OTHER
        /// gadgets (ArmageddonEffect fires meteor_3, nuke_3, wave_3, …) can read those
        /// gadgets' real CSV values instead of hardcoding a second copy of the balance.
        /// </summary>
        public GadgetDefinition GetGadgetDefinition(string gadgetId)
        {
            if (gadgetId == null) return null;
            return _gadgetCache.TryGetValue(gadgetId, out var def) ? def : null;
        }

        public void EnqueueAction(Action action)
        {
            _actionQueue.Enqueue(action);
        }

        private void ProcessActions()
        {
            while (_actionQueue.TryDequeue(out var action))
            {
                action.Invoke();
            }
        }

        /// <summary>
        /// Queues a delayed gadget phase as data — see PendingEffect.cs. The caller
        /// supplies everything except the tick.
        /// </summary>
        public void ScheduleEffect(int delayInTicks, PendingEffect effect)
        {
            effect.ExecuteAtTick = (int)_state.CurrentTick + delayInTicks;
            _scheduledEffects.Add(effect);
        }

        /// <summary>
        /// Total castle damage ALREADY COMMITTED against <paramref name="side"/> by delayed
        /// gadget phases that are queued but have not fired yet -- i.e. damage that will
        /// land even if both players do nothing from here.
        ///
        /// This is what makes a nuke reactable rather than a coin flip. A nuke detonates
        /// def.Delay (48 ticks, ~1.6s) after the cast and damages BOTH castles, so between
        /// the cast and the blast there is a real window in which a defender can buy the HP
        /// to survive it. Without this query the only thing a bot could see was
        /// PendingEffectCount, which says something is in flight but not what or how big.
        ///
        /// Counts every queued blast regardless of how far out it is, which is the right
        /// question for "will I be alive once everything currently in the air has landed" --
        /// there is no action that removes one. Deliberately says nothing about ongoing unit
        /// damage; that is what the time-to-death estimators are for.
        /// </summary>
        public int IncomingCastleDamage(int side)
        {
            if (_scheduledEffects.Count == 0) return 0;

            int total = 0;
            foreach (var e in _scheduledEffects)
            {
                var effect = ResolveScheduledEffect(e.GadgetId);
                if (effect == null) continue;
                total += effect.PendingCastleDamage(e, side);
            }
            return total;
        }

        /// <summary>
        /// Finds the effect implementation for a queued PendingEffect. Uses the same
        /// definition cache UseGadget resolves through, so a scheduled phase always runs
        /// against the identical GadgetDefinition instance that scheduled it.
        /// </summary>
        private IGadgetEffect ResolveScheduledEffect(string gadgetId)
        {
            if (gadgetId == null) return null;
            return _gadgetCache.TryGetValue(gadgetId, out var def) ? def.GadgetEffect : null;
        }

        public void TriggerGadgetAnimation(string gadgetId, int side, int position, Guid instanceId = new Guid())
        {
            OnGadgetAnimation?.Invoke(gadgetId, side, position, instanceId);
        }

        public void Tick()
        {
            ProcessActions();

            // Iterate DOWNWARD from the current count, fire, then remove at that index.
            // The direction is load-bearing: anything a firing effect schedules is
            // appended past the starting index, so it runs on a later tick rather than
            // inside this same pass. FreezeEffect's level-3 slow depends on that.
            for (int i = _scheduledEffects.Count - 1; i >= 0; i--)
            {
                if (_state.CurrentTick >= _scheduledEffects[i].ExecuteAtTick)
                {
                    var pending = _scheduledEffects[i];
                    ResolveScheduledEffect(pending.GadgetId)?.ExecuteScheduled(this, in pending);
                    _scheduledEffects.RemoveAt(i);
                }
            }

            if (_state.IsGameOver) return;
            _state.CurrentTick++;

            SpawnOpeningSquad();
            TickAutoSpawn();

            // 1. Income & Cooldowns
            if (_state.CurrentTick % INCOME_FREQUENCY == 0)
            {
                GiveIncome(_state.Player1);
                GiveIncome(_state.Player2);
            }
            TickCooldowns(_state.Player1);
            TickCooldowns(_state.Player2);

            // 2. Process Gadget Effects
            ProcessHazards();
            ProcessMapHealPulse();

            // 3. Process Status Effects
            if (_state.CurrentTick % 5 == 0)
            {
                ProcessStatuses();
            }

            // 4. Movement & Combat
            MoveAndFight();

            // 5. Time limit — set game over so CalculateReward applies the overtime terminal reward
            if (!_state.IsGameOver && _state.CurrentTick >= MAX_TICKS)
            {
                _state.IsGameOver  = true;
                _state.IsTimeLimit = true;
                _state.WinnerSide  = _state.Player1.CastleHealth > _state.Player2.CastleHealth ? 1 :
                                     _state.Player2.CastleHealth > _state.Player1.CastleHealth ? 2 : 0;
            }
        }

        /// <summary>
        /// The one unit whose stats are rolled fresh on every spawn instead of being read
        /// straight off its roster row. Black's tier-4 unit; the roster description is the
        /// player-facing half of this.
        ///
        /// Keyed on the id rather than a roster column because it is a single bespoke unit
        /// with bespoke behaviour, exactly like the "monky" shield case further down. A CSV
        /// flag would be the right answer for the second one of these, not the first.
        /// </summary>
        public const string RandomStatUnitId = "weirdo";

        /// <summary>Bounds of the per-stat multiplier rolled for <see cref="RandomStatUnitId"/>.</summary>
        public const float RandomStatMinScale = 0.5f;
        public const float RandomStatMaxScale = 2.0f;

        /// <summary>
        /// One multiplier in [<see cref="RandomStatMinScale"/>, <see cref="RandomStatMaxScale"/>].
        /// Uniform, and taken from the caller's Random so the draw comes from the engine's
        /// seeded stream rather than an unseeded one.
        /// </summary>
        private static float RollStatScale(Random rng)
            => RandomStatMinScale + (float)rng.NextDouble() * (RandomStatMaxScale - RandomStatMinScale);

        /// <summary>
        /// How many free tier-1 units each side is given at the start of a game, one per
        /// second. These are the units the client shows milling around the castle during
        /// the pre-game countdown, running onto the field as the battle opens. 0 disables.
        ///
        /// BOTH SIDES GET THE SAME NUMBER AT THE SAME TICKS, so this is symmetric and cannot
        /// favour a seat -- but it is still a real balance change: it is five free units and
        /// it moves every opening. Benchmarks taken before 2026-08-26 are not comparable.
        /// </summary>
        public static int OpeningSquadSize = 5;

        /// <summary>
        /// The free opening units, one per second starting on the game's very first tick
        /// (ticks 1, 31, 61, ... so the first one runs on as the battle begins rather than
        /// after a second of nothing).
        ///
        /// Spawned with ignoreCost, which is what keeps them out of the action recording,
        /// the purchase counters and the money spent totals -- they are not a decision
        /// anybody made, and a replay that recorded them as spawn actions would replay them
        /// twice.
        ///
        /// Keyed on absolute CurrentTick, so a game started mid-way through the clock (the
        /// league mode's timeSkip puts CurrentTick at 30*30*timeSkip) gets no squad. That is
        /// deliberate: those are AI training and spectator modes with no pre-game to match.
        /// </summary>
        private void SpawnOpeningSquad()
        {
            if (OpeningSquadSize <= 0) return;
            long t = _state.CurrentTick;
            if (t < 1 || t > (OpeningSquadSize - 1) * TICKS_PER_SECOND + 1) return;
            if ((t - 1) % TICKS_PER_SECOND != 0) return;

            // Sides are written literally rather than read from PlayerState.Side: a plain
            // `new GameState()` leaves Side at 0 (only the hub assigns it), and SpawnUnit
            // maps any side that is not 1 to player 2 -- so trusting the field here would
            // hand BOTH squads to player 2 in every harness that builds a state directly.
            for (int side = 1; side <= 2; side++)
            {
                var player = side == 1 ? _state.Player1 : _state.Player2;
                var roster = GameDataManager.Teams.Find(x => x.Color == player.Team)?.Roster;
                if (roster == null || roster.Count == 0) continue;
                SpawnUnit(side, roster[0].Id, ignoreCost: true);
            }
        }

        /// <summary>
        /// The auto-spawner's free unit stream, run once per tick for both sides.
        ///
        /// Spawned with ignoreCost for the same reasons as the opening squad: these are not
        /// decisions anybody made, so they must stay out of the purchase counters, the money
        /// spent totals and the action recording -- a replay that recorded them as spawn
        /// actions would replay them twice on top of the ones the rebuilt state generates.
        ///
        /// Unlike the opening squad this is NOT keyed on absolute CurrentTick, so it runs
        /// normally in the league mode's timeSkip games. It still produces nothing there,
        /// because the time machine hands out free investments and repairs but deliberately
        /// no auto-spawner levels (see PlayerState's timeSkip constructor).
        /// </summary>
        private void TickAutoSpawn()
        {
            // Sides written literally, not read from PlayerState.Side -- a plain
            // `new GameState()` leaves Side at 0 and SpawnUnit maps anything that is not 1
            // to player 2, which would hand both streams to the same player. Same trap the
            // opening squad documents.
            for (int side = 1; side <= 2; side++)
            {
                var player = side == 1 ? _state.Player1 : _state.Player2;
                int perSecond = PlayerState.AutoSpawnUnitsPerSecond(player.AutoSpawnLevel);
                if (perSecond <= 0) continue;

                var roster = GameDataManager.Teams.Find(x => x.Color == player.Team)?.Roster;
                if (roster == null || roster.Count == 0) continue;

                player.AutoSpawnAccumulator += perSecond;

                // A loop rather than a single check: at 6/s over 30 ticks one tick can only
                // ever owe one unit, but that is a property of the current table, not of
                // this code, and a future rate above the tick rate should not silently drop
                // the remainder.
                while (player.AutoSpawnAccumulator >= TICKS_PER_SECOND)
                {
                    player.AutoSpawnAccumulator -= TICKS_PER_SECOND;

                    int tier = player.NextAutoSpawnTier();
                    player.AutoSpawnCycleIndex++;
                    // Tier N is Roster[N-1], the same mapping ApplyAction uses for the
                    // unit-buy actions. A team whose roster is short simply skips.
                    if (tier < 1 || tier > roster.Count) continue;

                    SpawnUnit(side, roster[tier - 1].Id, ignoreCost: true);
                }
            }
        }

        public bool SpawnUnit(int side, string unitId, bool ignoreCost = false, float position = -1, int yposition = -1)
        {
            // 1. Validation
            var player = side == 1 ? _state.Player1 : _state.Player2;
            if (!_unitCache.ContainsKey(unitId)) return false;

            var def = _unitCache[unitId];

            // 2. Check Cooldowns & Money
            // (Assuming you implement CheckCooldown logic here similar to your TickCooldowns)
            if (!ignoreCost)
            {
                if (player.Money < def.Cost) return false;

                // CHARGES GATE THE PURCHASE ALONGSIDE MONEY. Checked before anything is
                // deducted, so a spawn refused for want of a charge costs the player nothing.
                //
                // INSIDE the !ignoreCost branch on purpose: the opening squad, the
                // auto-spawner and the reinforcements gadget all spawn with ignoreCost, and
                // none of them is a purchase the player made -- charging them would let a
                // free unit eat the budget for a bought one.
                if (!player.HasUnitCharge(unitId)) return false;

                // 3. Deduct Cost
                player.Money -= def.Cost;

                // Spend the charge, and start the regeneration clock if it is not already
                // running. Re-arming only when the timer is idle is what makes the wait a
                // steady one second per charge rather than something that restarts every
                // time the player buys again.
                int remaining = player.GetUnitCharges(unitId) - 1;
                player.UnitCharges[unitId] = remaining;
                if (!player.CooldownTimers.TryGetValue(unitId, out long t) || t <= 0)
                    player.CooldownTimers[unitId] = PlayerState.UnitChargeRegenMs / (1000 / TICKS_PER_SECOND);

                // PURCHASE COUNTERS (diagnostic only, added 2026-08-07 for the Stage 0 macro
                // decomposition). Incremented here rather than at the end of the method
                // because this line IS the purchase -- the money is gone whatever happens to
                // the unit afterwards. Deliberately excludes ignoreCost spawns, so gadget
                // reinforcements are not counted as attack spending.
                //
                // Lives on GameEngine, not GameState: it must not enter GetStateVector, and
                // rollout clones keeping their own counts is exactly what is wanted, since
                // only the live game's spending is being measured.
                if (side >= 1 && side <= 2)
                {
                    UnitsPurchased[side]++;
                    MoneySpentOnUnits[side] += def.Cost;
                }

                // Track action ID for recording (tier maps directly to action ID 1-8)
                if (def.Tier >= 1 && def.Tier <= 8)
                {
                    if (side == 1) LastActionP1 = def.Tier;
                    else LastActionP2 = def.Tier;
                    if (side >= 1 && side <= 2) ActionsThisTick[side]++;
                }
            }

            Random random = Rng;   // was `new Random()` — see the Rng property above

            // ── RANDOM-STAT UNIT (weirdo) ────────────────────────────────────────────────
            // Rolled HERE, before anything reads a size, because the spawn position and the
            // y-offset below are both computed from the unit's width/height -- a roll made
            // after them would place the unit as though it were its base size.
            //
            // DRAWN FROM Rng, NOT `new Random()`. That stream is seeded per game and is what
            // makes a replay reconstructable and a search rollout's clone deterministic;
            // an unseeded roll here would silently break both. See the replay determinism
            // note in CLAUDE.md.
            //
            // The three draws happen ONLY for this unit id, so the RNG stream is untouched
            // for every other unit and any game without a weirdo in it stays byte-identical
            // to before this feature existed.
            // EVERY OTHER UNIT TAKES THE DEFINITION'S VALUES VERBATIM -- no multiply, no
            // rounding, no clamping. That is deliberate rather than incidental: it makes
            // "this feature cannot have changed any other unit" true by construction instead
            // of true by an argument about floating-point identity.
            //
            // It also dodges a real trap. A blanket Math.Max(1, ...) floor on damage would
            // have handed every WALL 1 damage, since GameDataManager.WallDefinition sets
            // Damage = 0 -- turning a piece of defensive scenery into an attacker.
            int   spawnHealth = def.MaxHealth;
            int   spawnDamage = def.Damage;
            float spawnSpeed  = def.MoveSpeed;
            float visualScale = 1f;

            if (unitId == RandomStatUnitId)
            {
                float hpScale     = RollStatScale(random);
                float damageScale = RollStatScale(random);
                float speedScale  = RollStatScale(random);

                // Floored at 1 so a bad roll cannot produce a unit with no health or no
                // bite. Only reachable here, where the base values are non-zero.
                spawnHealth = Math.Max(1, (int)MathF.Round(def.MaxHealth * hpScale));
                spawnDamage = Math.Max(1, (int)MathF.Round(def.Damage    * damageScale));
                spawnSpeed  = def.MoveSpeed * speedScale;

                // APPEARANCE ONLY -- see Unit.VisualScale. The mean of the three rolls, so
                // a unit that rolled well everywhere looks big and a bad one looks small.
                visualScale = (hpScale + damageScale + speedScale) / 3f;
            }

            // ── MAP EFFECTS ──────────────────────────────────────────────────────────────
            // THIS IS THE ONLY PLACE spawn-time map effects are applied, and that is the
            // whole point: every unit that reaches the field goes through SpawnUnit, so the
            // Reinforcements squad, a Wall gadget's wall and the free opening squad all pick
            // the effect up here without any of them knowing map effects exist.
            //
            // Applied AFTER the weirdo roll so the two compose: a weirdo on Calm Hills gets
            // its rolled health and then the map's +10% on top, rather than the map buffing
            // a base value the unit was never going to have.
            //
            // Health and damage are whole numbers, so they round (see MapEffects.ScaleStat,
            // which also protects a wall's zero damage). SPEED IS DELIBERATELY NOT ROUNDED:
            // it is a float everywhere in the engine and is already scaled fractionally on
            // every tick by Slow/Speed statuses, so keeping it float is the existing
            // behaviour rather than a new class of value. Rounding it would also make the
            // effect wildly uneven -- speeds run 1 to 23, so +-10% rounds to no change at all
            // for a speed-1 or speed-2 unit while moving a speed-5 unit by a full 20%.
            var mapMods = MapEffects.For(_state);
            spawnHealth = MapEffects.ScaleStat(spawnHealth, mapMods.Health);
            spawnDamage = MapEffects.ScaleStat(spawnDamage, mapMods.Damage);
            spawnSpeed  = spawnSpeed * mapMods.Speed;

            if (position == -1)
            {
                // Both sides must start with their LEADING edge the same distance from the
                // enemy castle wall, so a spawn is worth the same tempo whichever seat you
                // are in. Leading edges are placed at x=100 and x=MAP_WIDTH-100, a mirror
                // pair, giving both 1700px of ground to cover.
                //
                // Position is the sprite's LEFT edge (see GetDistanceToEnemyCastle), so a
                // side-2 unit already leads with Position and needs no adjustment, while a
                // side-1 unit leads with Position+Width and must be set back by its own
                // width. Previously both were placed by their left edge at 100 / 1900,
                // which left P1's leading edge Width pixels ahead of the mirror position
                // and handed P1 a shorter walk on every single spawn.
                //
                // Keeping side 2 fixed and moving side 1 back is the deliberate choice of
                // the LONGER of the two existing distances (1700, side 2's) so nothing
                // gets faster. The visible consequence is that wide units now start partly
                // off the left edge of the map, exactly as they already did off the right.
                position = (side == 1) ? 100 - def.Width : MAP_WIDTH - 100;
            }
            if (yposition == -1)
            {
                yposition = 360 - def.Height + random.Next(0, 51);
                if (def.Id == "wall_2")
                    yposition -= 120;
                if (def.Id == "wall_3")
                    yposition -= 350;
            }

            // 4. Create Unit
            var newUnit = new Unit
            {
                // --- IDENTITY & UI ---
                InstanceId = NextUnitId(),
                DefinitionId = unitId,
                Side = side,
                Tier = def.Tier,
                Height = def.Height,
                Width = def.Width,
                VisualScale = visualScale,
                PendingKnockback = 0,
                LastKnockbackTick = 0,
                AttacksWithoutKnockback = 0,

                // --- HEALTH & POSITION ---
                CurrentHealth = spawnHealth,
                MaxHealth = spawnHealth,
                CurrentShield = def.MaxShield,
                Position = position,
                YPosition = yposition,

                // --- COMBAT STATS ---
                BaseSpeed = spawnSpeed,
                CurrentSpeed = spawnSpeed,
                Damage = spawnDamage,
                Range = def.Range,
                AttackSpeed = def.AttackSpeed,

                // --- PHYSICS & MECHANICS ---
                Weight = def.Weight,
                PushForce = def.PushForce,
                EffectiveWeight = def.EffectiveWeight,
                AttackType = def.AttackType,
                ArmorType = def.ArmorType
            };

            // Give Monky a shield
            if (unitId == "monky")
            {
                newUnit.CurrentHealth /= 2;
                newUnit.MaxHealth /= 2;
                newUnit.CurrentShield = newUnit.CurrentHealth;
            }

            _state.Units.Add(newUnit);
            return true;
        }

        public bool Invest(int side)
        {
            // 1. Validation
            var player = side == 1 ? _state.Player1 : _state.Player2;

            if (player.Money < player.InvestmentPrice) return false;

            if (side == 1) LastActionP1 = 9; else LastActionP2 = 9;
            if (side >= 1 && side <= 2) ActionsThisTick[side]++;

            // ARMAGEDDON is a one-time purchase, so the button is dead afterwards.
            if (player.ArmageddonUsed) return false;

            // 2. Deduct cost
            player.Money -= player.InvestmentPrice;

            // 3a. At the top of the ladder the invest button is ARMAGEDDON instead of an
            // economy upgrade. Income and InvestmentPrice are left exactly where they are
            // -- this purchase buys the end of the game, not more income. Both players can
            // reach it independently; each gets their own cascade.
            if (player.InvestmentCount >= PlayerState.ArmageddonInvestmentCount)
            {
                player.ArmageddonUsed = true;
                TriggerArmageddon(side);
                return true;
            }

            // 3b. Increase income and next investment price -- single source of truth is
            // PlayerState.ApplyInvestmentStep (also used by the timeSkip "time machine"
            // constructor, so the two can never desync again; see its own comment).
            player.ApplyInvestmentStep();

            return true;
        }

        /// <summary>
        /// Kicks off the ARMAGEDDON cascade for <paramref name="side"/>.
        ///
        /// The whole cascade lives in ArmageddonEffect, reached through the same
        /// _gadgetCache lookup every other gadget uses, so its recurring phases survive
        /// Clone() as plain PendingEffect data like any other delayed effect.
        /// </summary>
        private void TriggerArmageddon(int side)
        {
            if (!_gadgetCache.TryGetValue(ArmageddonEffect.GadgetId, out var def)) return;

            // Untargeted: the cascade picks its own positions. Passing the enemy castle
            // only gives the opening screen-darken animation somewhere sensible to anchor.
            def.GadgetEffect.Execute(this, side, side == 1 ? MAP_WIDTH : 0);
        }

        /// <summary>
        /// Buys one auto-spawner level. Action 14 -- see the note in ApplyAction about why
        /// that id exists but is not in the action mask.
        /// </summary>
        public bool UpgradeAutoSpawn(int side)
        {
            // 1. Validation
            var player = side == 1 ? _state.Player1 : _state.Player2;

            if (player.AutoSpawnLevel >= PlayerState.MaxAutoSpawnLevel) return false;
            if (player.Money < player.AutoSpawnPrice) return false;

            if (side == 1) LastActionP1 = 14; else LastActionP2 = 14;
            if (side >= 1 && side <= 2) ActionsThisTick[side]++;

            // 2. Deduct cost
            player.Money -= player.AutoSpawnPrice;

            // 3. Raise the level and the next price -- single source of truth is
            // PlayerState.ApplyAutoSpawnStep.
            player.ApplyAutoSpawnStep();

            return true;
        }

        public bool Repair(int side)
        {
            // 1. Validation
            var player = side == 1 ? _state.Player1 : _state.Player2;

            if (player.Money < player.RepairPrice) return false;

            if (side == 1) LastActionP1 = 10; else LastActionP2 = 10;
            if (side >= 1 && side <= 2) ActionsThisTick[side]++;

            // 2. Deduct cost
            player.Money -= player.RepairPrice;

            // 3. Increase health and repair price -- single source of truth is
            // PlayerState.ApplyRepairStep (also used by the timeSkip "time machine"
            // constructor, so the two can never desync again; see its own comment).
            player.ApplyRepairStep();

            return true;
        }

        public bool UseGadget(int side, string gadgetId, int position)
        {
            // 1. Validation
            var player = side == 1 ? _state.Player1 : _state.Player2;

            if (!_gadgetCache.ContainsKey(gadgetId)) return false;

            if (gadgetId != player.OffensiveGadget.Id && gadgetId != player.DefensiveGadget.Id && gadgetId != player.SignatureGadget.Id) return false;

            var def = _gadgetCache[gadgetId];

            if (player.GadgetCooldowns.ContainsKey(gadgetId) && player.GadgetCooldowns[gadgetId] > 0)
            {
                return false; // Gadget is still on cooldown, do nothing!
            }

            if (player.Money < def.Cost) return false;

            // --- Bot Auto-Targeting Logic (-1) ---
            //
            // REPLACED 2026-08-21. This used to aim at the frontmost enemy plus a flat 300,
            // falling back to the enemy castle when no enemy existed, with no friendly-fire
            // check of any kind. Both halves of that rule pointed AT the caster's own army --
            // the +300 offset lands on the collision point where the two lines meet, and the
            // no-enemy fallback lands on the enemy castle, which is precisely where a winning
            // bot's siege is standing. Measured over Marc's 8 v3 replays, 12 of the 14 casts
            // that took this path hit his bot's own units, for 78 own against 42 enemy.
            //
            // It now routes through the SAME targeting HeuristicBot uses. That matters beyond
            // aim quality: every caller of ApplyAction(side, 11..13) -- search's raw action,
            // every ONNX/RL policy, HumanCloneBot, every harness driving the discrete action
            // space -- had no friendly-fire protection at all, because all of it lived one
            // layer up inside HeuristicBot. See GadgetTargeting for the full account.
            //
            // AutoTarget may return null, meaning "there is nothing here worth a blast, or
            // hitting it means hitting ourselves". That REFUSES the cast: no money spent, no
            // cooldown started, ApplyAction returns false.
            if (position == -1)
            {
                int? aim = Gadgets.GadgetTargeting.AutoTarget(this, side, def);
                if (!aim.HasValue) return false;
                // Already clamped to the legal window by AutoTarget, deliberately BEFORE its
                // friendly-fire test rather than after it -- see GadgetTargeting.ClampToMap.
                position = aim.Value;
            }

            // 2. Deduct Cost
            player.Money -= def.Cost;

            // Track action ID for recording (11=off, 12=def, 13=sig)
            int gadgetActionId = gadgetId == player.OffensiveGadget?.Id ? 11 :
                                 gadgetId == player.DefensiveGadget?.Id  ? 12 : 13;
            if (side == 1) LastActionP1 = gadgetActionId; else LastActionP2 = gadgetActionId;
            if (side >= 1 && side <= 2) ActionsThisTick[side]++;

            // 3. Apply gadget cooldown (converting ms to game ticks)
            player.GadgetCooldowns[gadgetId] = def.CooldownMs / (1000 / TICKS_PER_SECOND);

            // 4. Activate Gadget Effect
            def.GadgetEffect.Execute(this, side, position);

            // RECORDING HOOK. Deliberately here and NOT on OnGadgetAnimation: that event is
            // raised by the individual effects, and five of them (Reinforcements, Heal, Rage,
            // Speed, Wall) never raise it at all. Recording through it therefore dropped every
            // cast of those families -- 28 of 52 casts in game 0C7A5B, all of them action 12 --
            // from both the replay's target list AND the DB's gadget_uses table. This point is
            // reached by every successful cast, and `position` here is already resolved, so a
            // -1 auto-target is recorded as the coordinate the blast actually used.
            OnGadgetCast?.Invoke(side, gadgetId, position);

            return true;
        }

        private void ProcessHazards()
        {
            // Iterate backwards so we can safely remove them
            for (int i = _state.Hazards.Count - 1; i >= 0; i--)
            {
                var hazard = _state.Hazards[i];

                if (hazard.ExpiresAtTick <= _state.CurrentTick)
                {
                    hazard.OnExpire(_state);
                    _state.Hazards.RemoveAt(i);
                }
                else
                {
                    hazard.ProcessEffect(_state);
                }
            }
        }

        /// <summary>
        /// The Red map's heal pulse: every 10-30 seconds, every unit on the field is healed
        /// for 10-50% of its maximum health, capped at full.
        ///
        /// ONE roll per pulse, shared by every unit -- this is a single event happening to
        /// the whole forest, not each unit rolling its own dice. Walls are included, because
        /// they are units standing on the field like anything else.
        ///
        /// Both draws come from <see cref="Rng"/>, the engine's seeded stream, NEVER from an
        /// unseeded `new Random()`: that stream is what makes a replay reconstructable and a
        /// search rollout deterministic, and CLAUDE.md records this exact mistake twice. The
        /// draws happen ONLY on the Red map, so every other map's RNG sequence -- and so
        /// every existing benchmark on it -- is untouched.
        /// </summary>
        private void ProcessMapHealPulse()
        {
            if (!MapEffects.For(_state).HealPulse) return;

            // First sighting of this board: schedule and wait. Tick 0 can never be a pulse
            // time anyway (the minimum delay is ten seconds), so 0 is safe as "unscheduled".
            if (_state.NextHealPulseTick == 0)
            {
                _state.NextHealPulseTick = _state.CurrentTick + RollHealPulseDelay();
                return;
            }

            if (_state.CurrentTick < _state.NextHealPulseTick) return;
            _state.NextHealPulseTick = _state.CurrentTick + RollHealPulseDelay();

            float fraction = MapEffects.HealPulseMinFraction +
                (float)Rng.NextDouble() * (MapEffects.HealPulseMaxFraction - MapEffects.HealPulseMinFraction);

            foreach (var unit in _state.Units)
            {
                // Floored at 1 so the pulse always does something: 10% of a 5 HP tier-1
                // rounds to 1 rather than to nothing at all.
                int amount = Math.Max(1, (int)MathF.Round(unit.MaxHealth * fraction));
                unit.CurrentHealth = Math.Min(unit.MaxHealth, unit.CurrentHealth + amount);

                // A ZERO-VALUE Heal status, purely as the visual marker. The client spawns
                // its heal particles from a status's NAME and never reads the Value, and the
                // healing itself has already been applied above -- a non-zero Value here
                // would be applied AGAIN by ProcessStatuses on every pass for a whole
                // second, healing several times over. Zero is inert on that path: ApplyDamage
                // with amount 0 changes no health and does not touch the knockback counters.
                unit.Statuses.Add(new ActiveStatus(
                    "Heal",
                    _state.CurrentTick + MapEffects.HealPulseStatusTicks,
                    0f));
            }
        }

        private int RollHealPulseDelay() =>
            Rng.Next(MapEffects.HealPulseMinDelayTicks, MapEffects.HealPulseMaxDelayTicks + 1);

        private void ProcessStatuses()
        {
            // Blue damps fire and Orange feeds it. Scaling the damage HERE, as the tick
            // lands, rather than where the Burn status is created, means it covers every
            // source of fire at once -- the firebomb's zone, the meteor's ignite, and
            // anything added later -- without each of them having to remember.
            float burnMod = MapEffects.For(_state).BurnDamage;

            foreach (var unit in _state.Units)
            {
                // Remove expired effects
                unit.Statuses.RemoveAll(s => s.ExpiresAtTick <= _state.CurrentTick);

                // Apply DoT (Damage over Time)
                var burns = unit.Statuses.Where(s => s.Name == "Burn" || s.Name == "Poison" || s.Name == "Heal" || s.Name == "Blackhole");
                foreach (var burn in burns)
                {
                    int healthBefore = unit.CurrentHealth;

                    // Only Burn is fire. Poison, Heal and Blackhole take the original
                    // truncating cast untouched, so every map other than Blue and Orange
                    // produces byte-identical damage to before map effects existed.
                    int amount = (burn.Name == "Burn" && burnMod != 1f)
                        ? (int)MathF.Round(burn.Value * burnMod)
                        : (int)burn.Value;

                    // Change to AttackType.Magic later?
                    ApplyDamage(unit, amount, AttackType.Melee, 0);
                }
            }

            // Invulnerability check:
            if (_state.Player1.IsInvulnerable)
            {
                if (_state.CurrentTick > _state.Player1.InvulnerableUntilTick)
                    _state.Player1.IsInvulnerable = false;
            }
            if (_state.Player2.IsInvulnerable)
            {
                if (_state.CurrentTick > _state.Player2.InvulnerableUntilTick)
                    _state.Player2.IsInvulnerable = false;
            }
        }

        private void MoveAndFight()
        {
            // 1. Sort units by Position (X-coordinate) for ultra-fast spatial lookups
            _state.Units.Sort((a, b) => a.Position.CompareTo(b.Position));

            // ROOT-CAUSE FIX, re-applied 2026-07-26 for re-validation against asymmetric
            // matchups (spam/models), not just the mirror-match test -- see
            // TRAINING_CAMPAIGN_LOG.md's "Seat-bias root-cause investigation" section for
            // the full history. Defers both combat damage AND movement so every unit's
            // action this tick is decided from the identical start-of-tick state; nothing
            // this tick can be affected by another unit's action earlier in the SAME tick,
            // removing the order-dependent P1/P2 advantage (proven via reversing iteration
            // order and watching a ~90/10 bias flip completely).
            var pendingUnitDamage = new List<(Unit target, int amount, AttackType type, float force)>();
            var pendingCastleDamage = new List<(PlayerState enemyPlayer, int damage)>();
            var pendingMoves = new List<(Unit unit, float newPosition)>();
            var toRemove = new List<Unit>();
            int unitCountSnapshot = _state.Units.Count;

            for (int i = 0; i < unitCountSnapshot; i++)
            {
                var unit = _state.Units[i];
                if (!_unitCache.ContainsKey(unit.DefinitionId)) continue;
                var def = _unitCache[unit.DefinitionId];

                // --- 1. Calculate Stats (NO LINQ!) ---
                float speedMod = 1.0f;
                float dmgMod = 1.0f;
                bool isHardCcd = false;

                // One single fast loop to check all statuses without allocating memory
                for (int s = 0; s < unit.Statuses.Count; s++)
                {
                    var status = unit.Statuses[s];
                    if (status.Name == "Slow" || status.Name == "Speed") speedMod *= status.Value;
                    else if (status.Name == "Rage") dmgMod *= status.Value;
                    else if (status.Name == "Freeze" || status.Name == "Stun" || status.Name == "Knockback" || status.Name == "Blackhole") isHardCcd = true;
                }

                // Decrement Attack Cooldown
                if (unit.AttackCooldown > 0) unit.AttackCooldown -= (1000f / GameEngine.TICKS_PER_SECOND);

                // Check for hard CC -- no death-check needed here: health can't have
                // changed yet this tick (damage is deferred), so the single death-check
                // pass after all damage is applied already covers hard-CC'd units too.
                if (isHardCcd) continue;

                // --- 2. Target Acquisition ---
                // Pass the index 'i' so we can look left and right instantly
                var enemies = FindTargetsFast(unit, i, def);

                float distToCastle = GetDistanceToEnemyCastle(unit);
                bool castleInRange = distToCastle <= def.Range;

                // --- 3. Combat Logic (decide only -- applied simultaneously after the loop) ---
                if (enemies.Count > 0)
                {
                    unit.CurrentSpeed = 0;
                    if (unit.AttackCooldown <= 0 && def.AttackSpeed > 0)
                    {
                        unit.AttackCooldown = (1000f / def.AttackSpeed);

                        float impactForce = def.PushForce;
                        if (def.AttackType == AttackType.Siege) impactForce *= 2;
                        if (def.AttackType == AttackType.Ranged || def.AttackType == AttackType.Magic) impactForce /= 2;

                        for (int e = 0; e < enemies.Count; e++)
                        {
                            // unit.Damage, not def.Damage: identical for every ordinary unit
                            // (it is initialised from the definition), but the random-stat
                            // unit's roll lives on the instance. Same substitution below for
                            // castle damage, movement speed and width.
                            pendingUnitDamage.Add((enemies[e], (int)(unit.Damage * dmgMod), def.AttackType, impactForce));
                        }
                    }
                }
                else if (castleInRange)
                {
                    unit.CurrentSpeed = 0;

                    // `def.AttackSpeed > 0` mirrors the unit branch above, and is load
                    // bearing rather than defensive: a wall has AttackSpeed 0, so without
                    // it this computed 1000f/0 = float.PositiveInfinity and stored that on
                    // the unit. Infinity has no JSON representation, so the next
                    // SendAsync("GameStateUpdate", engine._state) in GameHostingService
                    // threw ArgumentException, the game-loop catch-all dropped the game,
                    // and the player got "The game ended unexpectedly."
                    //
                    // A wall could reach a castle at all because wall_3 is 450 wide and was
                    // placed by its LEFT edge (see WallEffect), so targeting anywhere past
                    // ~1350 pushed its right edge over P1's wall at x=1800. That placement
                    // is fixed too, but the guard stays: a unit that cannot attack has no
                    // business attacking a castle, and nothing should be able to write a
                    // non-finite value into serialisable game state.
                    if (unit.AttackCooldown <= 0 && def.AttackSpeed > 0)
                    {
                        // THE ONLY PLACE A UNIT DAMAGES A CASTLE. This was inlined from a
                        // separate AttackCastle() method so castle damage could be deferred
                        // alongside unit damage; that method lingered with zero callers until
                        // it was deleted on 2026-08-29. It was worth deleting rather than
                        // leaving: its copy of this line never got the `def.AttackSpeed > 0`
                        // guard above, so calling it on a wall (AttackSpeed 0) produced
                        // float.PositiveInfinity and crashed live games by making the state
                        // unserialisable, and it read damage off the DEFINITION, which would
                        // now miss the weirdo's per-instance roll.
                        unit.AttackCooldown = (1000f / def.AttackSpeed);
                        var enemyPlayer = unit.Side == 1 ? _state.Player2 : _state.Player1;
                        float castleDamage = unit.Damage;
                        if (def.AttackType == AttackType.Siege) castleDamage *= 2;
                        foreach (var status in unit.Statuses)
                            if (status.Name == "Rage") castleDamage *= status.Value;
                        pendingCastleDamage.Add((enemyPlayer, (int)castleDamage));
                    }
                }
                else
                {
                    // --- 4. Movement Logic (decided now, applied after the loop) ---
                    unit.CurrentSpeed = unit.BaseSpeed * speedMod;
                    if (speedMod > 0)
                    {
                        float direction = (unit.Side == 1) ? 1f : -1f;
                        float desired = unit.Position + (unit.BaseSpeed * speedMod * direction);
                        pendingMoves.Add((unit, ClampToContact(unit, i, def, desired)));
                    }
                }
            }

            // Apply all movement simultaneously -- no unit's target-acquisition this tick
            // can be affected by another unit having already moved earlier in the pass.
            foreach (var (unit, newPosition) in pendingMoves)
                unit.Position = newPosition;

            // Apply all combat damage simultaneously -- processing order can no longer
            // change who successfully lands a hit.
            foreach (var (target, amount, type, force) in pendingUnitDamage)
                ApplyDamage(target, amount, type, force);
            foreach (var (enemyPlayer, damage) in pendingCastleDamage)
                DamageCastle(enemyPlayer, damage);

            // --- 5. Death Check (once, after all this tick's damage has landed) ---
            for (int i = 0; i < _state.Units.Count; i++)
            {
                if (_state.Units[i].CurrentHealth <= 0) toRemove.Add(_state.Units[i]);
            }
            foreach (var dead in toRemove) _state.Units.Remove(dead);

            // --- 6. Apply Knockback ---
            // Black's low gravity lands here, and this is the one place displacement is
            // turned into movement, so it covers every source at once -- a melee impact, a
            // wave, a black hole collapsing -- exactly as the wall rule below does.
            //
            // Multiplying HERE rather than in ApplyDamage is deliberate: it comes after the
            // anti-stunlock clamps (the 25f/10f plateaus and the tier-8 cap), so low gravity
            // makes units fly farther without reopening the stunlock those clamps close.
            var mapMods = MapEffects.For(_state);

            for (int i = _state.Units.Count - 1; i >= 0; i--)
            {
                var unit = _state.Units[i];
                if (unit.PendingKnockback != 0)
                {
                    // A wall never moves. This is the ONE point where knockback is turned
                    // into displacement, so enforcing it here covers every source at once
                    // -- melee impacts, a wave sweeping through, a black hole collapsing --
                    // including any added later, rather than relying on each callsite to
                    // remember. The pending impulse is discarded, not banked, so it cannot
                    // accumulate and lurch the wall the moment some future code path
                    // exempts it. Walls also skip the "Knockback" hard-CC status below,
                    // which only exists to model being staggered by the hit.
                    if (unit.IsWall)
                    {
                        unit.PendingKnockback = 0f;
                        continue;
                    }

                    unit.Position += unit.PendingKnockback * mapMods.Knockback;
                    unit.PendingKnockback = 0f;

                    // The stagger IS the flight time -- the unit is moved instantly and the
                    // client animates the arc over the same window, so a unit that hangs in
                    // the air twice as long is one that cannot act for twice as long.
                    unit.Statuses.Add(new ActiveStatus("Knockback", _state.CurrentTick + mapMods.KnockbackStaggerTicks, 0f));
                    unit.AttacksWithoutKnockback = 0;

                    // The re-knockback immunity window is deliberately NOT doubled with the
                    // stagger. It exists to stop a unit being juggled, and at 2s it already
                    // covers low gravity's 2s flight -- doubling it to 4s would change the
                    // stunlock economics of the whole map rather than its gravity.
                    unit.LastKnockbackTick = _state.CurrentTick + 2 * GameEngine.TICKS_PER_SECOND;
                }
            }
        }

        /// <summary>
        /// Trims a move so the unit comes to rest exactly where something stops it -- the
        /// enemy castle wall, or the nearest enemy it could attack -- instead of stepping
        /// past that point and settling whereever its final stride happened to land.
        ///
        /// Added 2026-07-31. Movement advances in whole strides of MoveSpeed and the unit
        /// only halts on the tick AFTER it is already in contact, so its resting position
        /// used to depend on its speed: against the wall at x=1800 a 9-speed tier 1 came to
        /// rest at 1751 while a 14-speed tier 4 ended at 1758. A pileup of mixed tiers
        /// therefore settled into several ranks a few pixels apart rather than one, and an
        /// attacker arriving at it was only ever in contact with the front rank -- so a
        /// swing into a stack of 24 mixed units hit 12, and because the ranks' sprites
        /// overlap almost entirely, the rank that died was the one hidden behind the other.
        /// That is the "phantom hit": a real swing, real damage, killing units the player
        /// could not see. With this clamp every unit in a pileup shares one position and a
        /// single swing hits all of them.
        ///
        /// Safe against two units closing head-on in the same tick, which both clamp
        /// against the other's START-of-tick position: crossing over would need the closing
        /// gap to exceed the mover's own width (else the two just overlap, preserving
        /// order), and a stride that large would need a MoveSpeed several times the
        /// roster's maximum of 14.
        /// </summary>
        private float ClampToContact(Unit unit, int myIndex, UnitDefinition def, float desired)
        {
            if (unit.Side == 1)
            {
                // Rightmost position that still leaves the unit short of the enemy wall.
                float limit = MAP_WIDTH - 200 - def.Width;

                // Sorted ascending by Position, so the first valid enemy ahead is the
                // nearest, and its constraint (which does not involve its own width) is
                // the tightest -- nothing further right can bind harder.
                for (int i = myIndex + 1; i < _state.Units.Count; i++)
                {
                    var other = _state.Units[i];
                    if (other.Position < unit.Position) continue;
                    if (other.Side == unit.Side || other.CurrentHealth <= 0) continue;
                    var otherDef = _unitCache[other.DefinitionId];
                    if (otherDef.ArmorType == ArmorType.Flying && def.AttackType != AttackType.Ranged)
                        continue;   // cannot be attacked by this unit, so does not stop it
                    limit = Math.Min(limit, other.Position - def.Width - def.Range);
                    break;
                }
                return Math.Min(desired, Math.Max(unit.Position, limit));
            }
            else
            {
                float limit = 200f;

                // Mirror image, except the constraint DOES involve the target's width, and
                // widths are not sorted -- so keep scanning until no unit further back
                // could bind harder even if it were the widest in the game.
                for (int i = myIndex - 1; i >= 0; i--)
                {
                    var other = _state.Units[i];
                    if (other.Position + _maxUnitWidth + def.Range <= limit) break;
                    if (other.Position > unit.Position) continue;
                    if (other.Side == unit.Side || other.CurrentHealth <= 0) continue;
                    var otherDef = _unitCache[other.DefinitionId];
                    if (otherDef.ArmorType == ArmorType.Flying && def.AttackType != AttackType.Ranged)
                        continue;
                    limit = Math.Max(limit, other.Position + otherDef.Width + def.Range);
                }
                return Math.Max(desired, Math.Min(unit.Position, limit));
            }
        }

        /// <summary>
        /// Every enemy the attacker can reach this tick.
        ///
        /// Distances are measured sprite-edge to sprite-edge, which means treating
        /// Position as the sprite's LEFT edge -- the renderer's convention, and the same
        /// one GetDistanceToEnemyCastle follows. This used to read
        /// `|a.Position - b.Position| - a.Width/2 - b.Width/2`, i.e. it took the two
        /// positions to be CENTRES. That is exact when both units are the same width but
        /// off by `(attackerWidth - targetWidth)/2` otherwise, and -- because the error is
        /// signed -- it went the opposite way for each seat: a wide P1 attacker stopped
        /// with a visible gap in front of a narrow P2 defender, while the same pairing
        /// mirrored had the attacker's sprite swallow the defender. Same-width pairs are
        /// unaffected by the fix; mismatched pairs now engage at the true sprite gap and
        /// do so identically on both sides.
        /// </summary>
        // Note: We now pass in 'myIndex' from the MoveAndFight loop!
        private List<Unit> FindTargetsFast(Unit attacker, int myIndex, UnitDefinition attackerDef)
        {
            List<Unit> validTargets = new List<Unit>();

            // --- 1. Look RIGHT (Forward in the sorted list) ---
            for (int i = myIndex + 1; i < _state.Units.Count; i++)
            {
                var other = _state.Units[i];

                // Original Direction Check
                if (attacker.Side == 1 && other.Position < attacker.Position) continue;
                if (attacker.Side == 2 && other.Position > attacker.Position) continue;

                // The list is sorted ascending by Position, so everything from here on
                // lies at or to the right of the attacker: the gap runs from the
                // attacker's RIGHT edge to the target's LEFT edge. Note it does not
                // involve the target's width at all, so unlike the backward scan below
                // this bound is exact and needs no safety pad -- and it grows
                // monotonically down the list, so once it clears Range every later unit
                // does too. A negative value means the sprites overlap.
                float edgeToEdgeDist = other.Position - (attacker.Position + attackerDef.Width);

                // OPTIMIZATION: too far, and so is every subsequent unit. Break the loop.
                if (edgeToEdgeDist > attackerDef.Range) break;

                if (other.Side == attacker.Side) continue; // Friend
                if (other.CurrentHealth <= 0) continue; // Dead (Extra safety check)

                var otherDef = _unitCache[other.DefinitionId];
                if (otherDef.ArmorType == ArmorType.Flying && attackerDef.AttackType != AttackType.Ranged)
                    continue;

                validTargets.Add(other); // in range: the break above already proved it
            }

            // --- 2. Look LEFT (Backward in the sorted list) ---
            for (int i = myIndex - 1; i >= 0; i--)
            {
                var other = _state.Units[i];

                // Original Direction Check
                if (attacker.Side == 1 && other.Position < attacker.Position) continue;
                if (attacker.Side == 2 && other.Position > attacker.Position) continue;

                // Mirror of the forward scan: everything from here back lies at or to the
                // left of the attacker, so the gap runs from the target's RIGHT edge to
                // the attacker's LEFT edge -- which does depend on the target's width.
                // Positions are sorted but widths are not, so the break has to assume the
                // widest unit that could still be out there.
                if ((attacker.Position - other.Position) - _maxUnitWidth > attackerDef.Range)
                    break;

                if (other.Side == attacker.Side) continue; // Friend
                if (other.CurrentHealth <= 0) continue; // Dead

                var otherDef = _unitCache[other.DefinitionId];
                if (otherDef.ArmorType == ArmorType.Flying && attackerDef.AttackType != AttackType.Ranged)
                    continue;

                float edgeToEdgeDist = attacker.Position - (other.Position + otherDef.Width);

                if (edgeToEdgeDist <= attackerDef.Range)
                {
                    validTargets.Add(other);
                }
            }

            return validTargets;
        }

        /// <summary>
        /// Gap between the attacker's LEADING edge and the enemy castle's wall.
        ///
        /// Position is the sprite's LEFT edge, not its centre -- that is the renderer's
        /// convention (view.js drawUnit derives centreX as `position + width/2`, so the
        /// sprite occupies [position, position + width]) and both branches below must
        /// follow it. A side-1 unit therefore leads with `Position + Width` and a side-2
        /// unit leads with `Position` alone.
        ///
        /// Fixed 2026-07-31: the side-2 branch used to read `(Position - Width) - 200`.
        /// An earlier pass had mirrored the side-1 branch's `+ Width` by sign instead of
        /// by geometry, which is only correct if Position means the centre. The cost was
        /// that every P2 unit opened fire on P1's castle a full unit-width early --
        /// measured at exactly 50 / 100 / 200px of extra standoff for 50 / 100 / 200-wide
        /// units, against ~0px for P1 -- and that width-sized gap is also what let P2's
        /// units shoot over P1's blockers: a defender could stand inside the gap while
        /// still being too far from the attacker to be a melee target, so the attacker saw
        /// no target and hit the wall through it. In 40 bot-vs-bot games, 74.6% of the
        /// unit-attacks on P1's castle were fired over a defender standing in the way,
        /// versus 0.0% against P2's castle. This fix alone took that to 0.9%, and the
        /// FindTargetsFast edge-distance fix took it to 0.0% on both castles.
        ///
        /// Practical balance impact is below noise: over 400 HeuristicBot-vs-HeuristicBot
        /// games with random non-mirrored loadouts, P1's win share moved 48.2% -> 48.5%.
        /// A same-loadout mirror match is NOT a magnitude test -- see CLEANUP_BACKLOG.md.
        /// </summary>
        public float GetDistanceToEnemyCastle(Unit attacker)
        {
            // If Player 1 (Left), enemy castle wall is at MAP_WIDTH - 200
            if (attacker.Side == 1)
            {
                float dist = MAP_WIDTH - 200 - (attacker.Position + attacker.Width);
                return Math.Max(0f, dist);
            }
            // If Player 2 (Right), enemy castle wall is at 200
            else
            {
                float dist = attacker.Position - 200;
                return Math.Max(0f, dist);
            }
        }

        public void DamageCastle(PlayerState player, int damage)
        {
            // Invulnerability check:
            if (player.IsInvulnerable)
            {
                return; // No damage dealt
            }

            // CASTLE SHIELD (divine). Absorbs first and bleeds the remainder through to
            // health, mirroring the unit shield in ApplyDamage. It sits BEFORE the
            // one-shot floor below on purpose: the floor is a rule about the last of the
            // castle's REAL health, so it must see the damage that actually reaches the
            // castle, not the raw incoming hit. A blow fully eaten by the shield leaves
            // CastleHealth untouched and cannot end the game, so it returns early.
            if (player.CastleShield > 0 && damage > 0)
            {
                int absorbed = Math.Min(player.CastleShield, damage);
                player.CastleShield -= absorbed;
                damage -= absorbed;
                if (damage <= 0) return;
            }

            // Prevent 1-shots
            if (player.CastleHealth == player.CastleMaxHealth && damage >= player.CastleMaxHealth)
                player.CastleHealth = 1;
            else
                player.CastleHealth -= damage;

            // 5. Game Over Check
            if (player.CastleHealth <= 0)
            {
                player.CastleHealth = 0;

                // A genuine simultaneous double-KO (e.g. a nuke, which always damages
                // Player1 then Player2 in that fixed order via NukeEffect) is a draw, not
                // a win for whichever DamageCastle call happens to run second. But
                // multiple units/effects landing overkill hits on the SAME already-dead
                // castle within the same tick is the ordinary common case (several
                // attackers in contact at once) -- that must NOT be mistaken for a
                // double-KO, or every decisive win with more than one attacker in range
                // gets silently downgraded to a "draw" the instant a second hit lands on
                // the loser. The distinguishing test: is THIS hit against the side that
                // was about to be recorded as the winner (i.e. their castle, not the
                // loser's, just ALSO reached 0)? Only that is a genuine double-KO.
                // WinnerSide=0 already means "draw" elsewhere (see the MAX_TICKS
                // timeout-tie check in Tick()).
                if (_state.IsGameOver)
                {
                    if (player.Side == _state.WinnerSide)
                        _state.WinnerSide = 0;
                }
                else
                {
                    _state.IsGameOver = true;
                    _state.WinnerSide = player.Side == 1 ? 2 : 1;
                }
            }
        }

        // Negative damage => heal
        public void ApplyDamage(Unit target, int amount, AttackType type, float impactForce)
        {
            // Check if unit is invulnerable
            if (target.Statuses.Any(s => s.Name == "Invulnerable"))
            {
                // Invulnerable units take no damage or knockback
                return;
            }

            // HEALS RESTORE HEALTH ONLY, NEVER SHIELD. A heal arrives here as a NEGATIVE
            // amount, and the shield branch below is written for damage: `CurrentShield -=
            // amount` with a negative amount GREW the shield, without any cap, and then
            // returned before touching CurrentHealth. So healing a divine-shielded unit
            // used to inflate its shield without limit and never heal the thing the
            // gadget is for. Split heals off before the shield ever sees them.
            if (amount < 0)
            {
                target.CurrentHealth = Math.Min(target.MaxHealth, target.CurrentHealth - amount);
                return; // heals carry no knockback
            }

            // Shield Logic
            if (target.CurrentShield > 0)
            {
                // Siege deals double to shields
                if (type == AttackType.Siege) amount *= 2;

                target.CurrentShield -= amount;
                if (target.CurrentShield < 0)
                {
                    // Bleed over to health
                    target.CurrentHealth += target.CurrentShield; // (CurrentShield is negative here)
                    target.CurrentShield = 0;
                }
                else
                {
                    // Shielded units don't get knocked back
                    return;
                }
            }
            else
            {
                target.CurrentHealth -= amount;

                // Handle overheal
                if (target.CurrentHealth > target.MaxHealth)
                {
                    target.CurrentHealth = target.MaxHealth;
                }
            }

            // --- PHYSICS & MOMENTUM ---
            if (_state.CurrentTick < target.LastKnockbackTick)
            {
                return;
            }

            var enemyDef = _unitCache[target.DefinitionId];
            float resistance = Math.Max(1f, enemyDef.EffectiveWeight);
            float knockbackDist = impactForce / resistance;
            knockbackDist = Math.Min(knockbackDist, 3000f);

            if (target.AttacksWithoutKnockback >= 50 && target.Tier < 8)
            {
                knockbackDist = 25f;
            }
            else if (target.AttacksWithoutKnockback >= 250 && target.Tier == 8)
            {
                knockbackDist = 10f;
            }

            if (knockbackDist > 10f)
            {
                // (Walls used to be clamped to 10f here. They are now immovable outright,
                // enforced where knockback is applied in MoveAndFight -- see the IsWall
                // check there. Clamping again here would just be a second, weaker copy of
                // the rule.)

                // Tier 8 units can only be knocked back a small amount
                if (target.Tier == 8)
                    knockbackDist = Math.Min(knockbackDist, 10f);

                float direction = (target.Side == 1) ? -1f : 1f;
                target.PendingKnockback += (knockbackDist * direction);
            }
            else
            {
                if (amount > 0)
                    target.AttacksWithoutKnockback++;
            }
        }

        public void AddGadgetXp(int side, string gadgetId, int amount)
        {
            var player = side == 1 ? _state.Player1 : _state.Player2;

            player.AddGadgetXp(gadgetId, amount);
        }

        private void GiveIncome(PlayerState player)
        {
            player.Money += player.Income;
        }

        private void TickCooldowns(PlayerState player)
        {
            // UNIT CHARGE REGENERATION -- one charge per second per unit, up to
            // PlayerState.UnitMaxCharges. The timer is armed by SpawnUnit when a charge is
            // spent and re-arms itself here while the unit is still short, so a unit drained
            // to zero refills over five seconds rather than all at once.
            //
            // REWRITTEN 2026-09-01. The previous version of this loop read
            // UnitDefinition.MaxCharges/CooldownMs (the price-scaled formula) and was DEAD
            // CODE regardless: nothing ever spent a charge or seeded the dictionaries, so
            // CooldownTimers was permanently empty and the whole block never executed. It
            // also did a SelectMany over every team's roster per key per tick to find the
            // definition, which the flat rule removes the need for entirely.
            foreach (var key in player.CooldownTimers.Keys.ToList())
            {
                if (player.CooldownTimers[key] <= 0) continue;

                player.CooldownTimers[key]--;
                if (player.CooldownTimers[key] > 0) continue;

                int charges = player.GetUnitCharges(key);
                if (charges >= PlayerState.UnitMaxCharges) continue;

                charges++;
                player.UnitCharges[key] = charges;

                // Still short: go round again. Full: leave the timer at 0 so the entry stops
                // being ticked and stops being sent to the client.
                if (charges < PlayerState.UnitMaxCharges)
                    player.CooldownTimers[key] = PlayerState.UnitChargeRegenMs / (1000 / TICKS_PER_SECOND);
            }

            // Tick gadget cooldowns
            foreach (var key in player.GadgetCooldowns.Keys.ToList())
            {
                if (player.GadgetCooldowns[key] > 0)
                {
                    player.GadgetCooldowns[key]--;
                }
            }
        }

        // ------------- RECORDING HOOKS ---------------
        /// <summary>Every successful gadget cast, with its RESOLVED target position.
        /// Use this for recording; OnGadgetAnimation is for client visuals and is not
        /// raised by all effects.</summary>
        public event Action<int, string, int> OnGadgetCast;

        public int LastActionP1 { get; private set; }
        public int LastActionP2 { get; private set; }

        /// <summary>
        /// Paid unit purchases per side, indexed 1/2 (index 0 unused). Diagnostic only —
        /// nothing in the engine or the observation reads these. See SpawnUnit.
        /// </summary>
        public long[] UnitsPurchased { get; private set; } = new long[3];

        /// <summary>Money spent on paid unit purchases per side, indexed 1/2.</summary>
        public double[] MoneySpentOnUnits { get; private set; } = new double[3];

        /// <summary>
        /// Successful actions applied to this engine since the last ResetLastActions, per
        /// side (indexed 1/2). The replay format stores ONE action id per side per tick, so
        /// anything above 1 here is an action that cannot be recorded -- and, since 2026-08-20,
        /// an action no human could have issued either. The bots pace themselves to keep this
        /// at most 1; --multi-action-check is the regression test.
        ///
        /// A plain int pair, so a shallow GameEngine.Clone copying or not copying it is
        /// harmless -- deliberately not a mutable reference (see the clone hazard note).
        /// </summary>
        public int[] ActionsThisTick { get; private set; } = new int[3];

        public void ResetLastActions()
        {
            LastActionP1 = 0;
            LastActionP2 = 0;
            ActionsThisTick[1] = 0;
            ActionsThisTick[2] = 0;
        }

        // ------------- AI VIBE CODE ---------------
        // We track previous health to calculate immediate rewards!
        private int _prevP1CastleHealth;
        private double _prevP1Income;
        private double _prevP1Money;
        private int _prevP1MaxHealth;
        private int _prevP1UnitCount;
        private int _prevP2CastleHealth;
        private double _prevP2Income;
        private double _prevP2Money;
        private int _prevP2MaxHealth;
        private int _prevP2UnitCount;
        private int _prevP1OffGadgetLevel;
        private int _prevP1DefGadgetLevel;
        private int _prevP1SigGadgetLevel;
        private int _prevP2OffGadgetLevel;
        private int _prevP2DefGadgetLevel;
        private int _prevP2SigGadgetLevel;
        private float _currentDenseWeight = 1.0f;
        private bool _initialStateRewarded = false;

        public StepResult Step(int actionP1, int actionP2, float denseRewardWeight = 1.0f)
        {
            // 1. Record the "Before" state for reward calculation
            _prevP1CastleHealth = _state.Player1.CastleHealth;
            _prevP1Income = _state.Player1.Income;
            _prevP1Money = _state.Player1.Money;
            _prevP1MaxHealth = _state.Player1.CastleMaxHealth;
            _prevP1UnitCount = _state.Units.Where(u => u.Side == 1).ToList().Count();
            _prevP2CastleHealth = _state.Player2.CastleHealth;
            _prevP2Income = _state.Player2.Income;
            _prevP2Money = _state.Player2.Money;
            _prevP2MaxHealth = _state.Player2.CastleMaxHealth;
            _prevP2UnitCount = _state.Units.Where(u => u.Side == 2).ToList().Count();
            _prevP1OffGadgetLevel = _state.Player1.OffensiveGadget?.Level ?? 1;
            _prevP1DefGadgetLevel = _state.Player1.DefensiveGadget?.Level ?? 1;
            _prevP1SigGadgetLevel = _state.Player1.SignatureGadget?.Level ?? 1;
            _prevP2OffGadgetLevel = _state.Player2.OffensiveGadget?.Level ?? 1;
            _prevP2DefGadgetLevel = _state.Player2.DefensiveGadget?.Level ?? 1;
            _prevP2SigGadgetLevel = _state.Player2.SignatureGadget?.Level ?? 1;
            _currentDenseWeight = denseRewardWeight;

            // 2. Decode the AI's chosen action and execute it
            bool p1ActionSucceeded = ApplyAction(1, actionP1);
            bool p2ActionSucceeded = ApplyAction(2, actionP2);

            // 3. Advance the simulation by EXACTLY one tick
            Tick();

            // 4. Calculate how well the AI did on this specific tick
            float p1Reward = CalculateReward(1, p1ActionSucceeded && actionP1 > 10);
            float p2Reward = CalculateReward(2, p2ActionSucceeded && actionP2 > 10);

            // 5. Flatten the new game state for the AI's neural network
            float[] p1State = _state.GetStateVector(1);
            float[] p2State = _state.GetStateVector(2);

            // 6. Get action masks for both players
            int[] p1Mask = _state.GetActionMask(1);
            int[] p2Mask = _state.GetActionMask(2);

            return new StepResult
            {
                P1State = p1State,
                P2State = p2State,
                P1ActionMask = p1Mask,
                P2ActionMask = p2Mask,
                P1Reward = p1Reward,
                P2Reward = p2Reward,
                IsDone = _state.IsGameOver,
                WinnerSide = _state.WinnerSide
            };
        }

        public bool ApplyAction(int side, int actionId)
        {
            if (side == 1) LastActionP1 = actionId;
            else LastActionP2 = actionId;

            var player = side == 1 ? _state.Player1 : _state.Player2;
            bool actionSucceeded = false;

            // --- Actions 1 through 8: Dynamic Unit Spawning ---
            if (actionId >= 1 && actionId <= 8)
            {
                // Arrays are 0-indexed, so Action 1 looks at Roster[0] (Tier 1)
                int rosterIndex = actionId - 1;

                // Grab the roster for this specific player's team
                var teamRoster = GameDataManager.Teams.Find(t => t.Color == player.Team)?.Roster;
                if (teamRoster == null) return actionSucceeded;

                if (rosterIndex < teamRoster.Count)
                {
                    actionSucceeded = SpawnUnit(side, teamRoster[rosterIndex].Id);
                }
                return actionSucceeded;
            }

            // --- Actions 9 through 13: Abilities and Economy ---
            switch (actionId)
            {
                case 0:
                    // Do Nothing (Crucial so the AI can save up money!)
                    break;
                case 9:
                    actionSucceeded = Invest(side);
                    break;
                case 10:
                    actionSucceeded = Repair(side);
                    break;
                case 14:
                    // THE AUTO-SPAWNER IS REACHABLE HERE BUT IS NOT IN THE ACTION MASK.
                    // GetActionMask still returns 14 slots (0..13), so no policy can select
                    // this and every trained ONNX model, its observation vector and every
                    // pinned bot-vs-bot benchmark are untouched by the feature existing.
                    //
                    // The id exists anyway because recordings store one action byte per
                    // tick: without it, a human game in which the auto-spawner was bought
                    // could not be replayed, and every tool that rebuilds a game by
                    // resimulating actions would silently diverge from the real one.
                    //
                    // Giving bots the auto-spawner is a deliberate, separate change: it
                    // means widening the mask and the policy head, which invalidates the
                    // models.
                    actionSucceeded = UpgradeAutoSpawn(side);
                    break;
                case 11:
                    if (player.OffensiveGadget != null) actionSucceeded = UseGadget(side, player.OffensiveGadget.Id, -1);
                    break;
                case 12:
                    if (player.DefensiveGadget != null) actionSucceeded = UseGadget(side, player.DefensiveGadget.Id, -1);
                    break;
                case 13:
                    if (player.SignatureGadget != null) actionSucceeded = UseGadget(side, player.SignatureGadget.Id, -1);
                    break;
            }

            return actionSucceeded;
        }

        private float CalculateReward(int side, bool gadgetUsedThisTick)
        {
            float reward = 0f;

            // Determine who is who
            var myPlayer = side == 1 ? _state.Player1 : _state.Player2;
            var enemyPlayer = side == 1 ? _state.Player2 : _state.Player1;

            int myPrevHealth = side == 1 ? _prevP1CastleHealth : _prevP2CastleHealth;
            double myPrevIncome = side == 1 ? _prevP1Income : _prevP2Income;
            double myPrevMoney = side == 1 ? _prevP1Money : _prevP2Money;
            int myPrevMaxHealth = side == 1 ? _prevP1MaxHealth : _prevP2MaxHealth;
            int prevAllyCount = side == 1 ? _prevP1UnitCount : _prevP2UnitCount;
            int enemyPrevHealth = side == 1 ? _prevP2CastleHealth : _prevP1CastleHealth;
            int prevEnemyCount = side == 1 ? _prevP2UnitCount : _prevP1UnitCount;

            // --- 0. INITIAL STATE REWARD (first tick only) ---
            // When the time machine drops the AI into a mid-game state, credit it for the
            // economy/upgrades already in place — the same rewards it would have earned by
            // reaching that state organically. This teaches the model that progressing through
            // early-game stages is inherently valuable, not just a means to an end.
            if (!_initialStateRewarded && side == 1)
            {
                _initialStateRewarded = true;
                for (int i = 0; i < _state.Player1.InvestmentCount; i++)
                    reward += _rewardParams.InvestReward + (11 - i) * _rewardParams.InvestDecay;
                for (int i = 0; i < _state.Player2.InvestmentCount; i++)
                    reward += _rewardParams.InvestReward + (11 - i) * _rewardParams.InvestDecay;
                reward += ((_state.Player1.OffensiveGadget?.Level ?? 1) - 1) * _rewardParams.GadgetUpgrade;
                reward += ((_state.Player1.DefensiveGadget?.Level  ?? 1) - 1) * _rewardParams.GadgetUpgrade;
                reward += ((_state.Player1.SignatureGadget?.Level  ?? 1) - 1) * _rewardParams.GadgetUpgrade;
                reward += ((_state.Player2.OffensiveGadget?.Level ?? 1) - 1) * _rewardParams.GadgetUpgrade;
                reward += ((_state.Player2.DefensiveGadget?.Level  ?? 1) - 1) * _rewardParams.GadgetUpgrade;
                reward += ((_state.Player2.SignatureGadget?.Level  ?? 1) - 1) * _rewardParams.GadgetUpgrade;
                for (int i = 1; i <= _state.Player1.RepairCount; i++)
                    reward += Math.Max((5 - i) * 5f, 0f);
                for (int i = 1; i <= _state.Player2.RepairCount; i++)
                    reward += Math.Max((5 - i) * 5f, 0f);
            }

            // --- 1. THE TIME PENALTY ---
            // A tiny negative incentive every single tick.
            // At 30 ticks a second, -0.01f is -0.3f points per second, or -90f over 5 minutes.
            // This pushes them to end the game fast without making them want to instantly die.
            reward -= 0.011f;

            // --- 2. COMBAT REWARDS ---

            // + Points per enemy slain
            int enemiesSlain = prevEnemyCount - _state.Units.Where(u => u.Side == enemyPlayer.Side).ToList().Count();
            reward += (float)Math.Max(enemiesSlain, 0) * 6f * _rewardParams.CombatScale;
            // - Points per ally lost
            int myUnitsLost = prevAllyCount - _state.Units.Where(u => u.Side == myPlayer.Side).Count();
            reward -= Math.Max(myUnitsLost, 0) * 5f * _rewardParams.CombatScale;

            // + Points for damaging the enemy castle (Percentage based!)
            int damageDealt = enemyPrevHealth - enemyPlayer.CastleHealth;
            if (damageDealt > 0)
            {
                float pctDamageDealt = (float)damageDealt / enemyPlayer.CastleMaxHealth;
                reward += (float)(Math.Pow(pctDamageDealt, 2f) * 300f * _rewardParams.CombatScale);
            }
            // - Points for taking castle damage
            int myDamageTaken = myPrevHealth - myPlayer.CastleHealth;
            if (myDamageTaken > 0)
            {
                float pctMyDamage = (float)myDamageTaken / myPlayer.CastleMaxHealth;
                float dangerMultiplier = Math.Min(3.0f, 5.0f * (1.0f - (float)myPlayer.CastleHealth / myPlayer.CastleMaxHealth));
                reward -= pctMyDamage * 600f * dangerMultiplier * _rewardParams.CombatScale;
            }

            // --- 3. ECONOMY & UPGRADE REWARDS ---

            // Reward them heavily for successfully increasing their income
            if (myPlayer.Income - myPrevIncome > 0)
            {
                reward += _rewardParams.InvestReward + (11 - myPlayer.InvestmentCount) * _rewardParams.InvestDecay;
            }
            // Anti-spend: penalises any unit purchase while HP is high and economy isn't maxed.
            // Fades naturally as the model builds its economy (full penalty at count=0, zero at count=8).
            float hpRatio = (float)myPlayer.CastleHealth / myPlayer.CastleMaxHealth;
            float savingsProgress = Math.Min(1.0f, (float)(myPrevMoney / myPlayer.InvestmentPrice));
            float hpPenaltyFactor = Math.Max(0.0f, Math.Min(1.0f, (hpRatio - 0.5f) / 0.4f));
            if (hpPenaltyFactor > 0f && myPlayer.InvestmentCount < 8
                && savingsProgress > 0.6f && myPlayer.Money < myPrevMoney && myPrevIncome == myPlayer.Income)
            {
                float penaltyScale = (savingsProgress - 0.6f) / 0.4f;
                reward -= penaltyScale * _rewardParams.AntiSpend * hpPenaltyFactor;
            }
            // Survival bonus: reward spending at low HP to encourage fighting back when cornered.
            // Capped at zero — negative urgency (HP > 90%) was causing action-0 dominance where the
            // penalty gradient overwhelmed the policy from the first update, freezing greedy inference.
            // Doesn't apply to investing.
            if (hpRatio < 0.9f && myPlayer.Money < myPrevMoney && myPrevIncome == myPlayer.Income)
            {
                float urgency = (0.9f - hpRatio) / 0.9f; // 0 at 90% HP, 1 at 0% HP
                reward += urgency * _rewardParams.AntiSpend;
            }
            // Reward saving up to invest, if we're at a high investment tier, we don't want to worry about saving any more
            if (myPlayer.InvestmentCount <= 7)
            {
                // Tent shape: linearly increases 0→1 from 0% to 100% of invest price,
                // then linearly decreases 1→0 from 100% to 120%, and stays at 0 beyond that.
                // No discontinuity at the threshold, and no incentive to accumulate excess savings.
                float savingsFraction = (float)(myPlayer.Money / myPlayer.InvestmentPrice);
                if (savingsFraction > 1.0f)
                    savingsFraction = Math.Max(0f, (1.2f - savingsFraction) / 0.2f);
                float savingsBoost = Math.Max(1.0f, 4.0f - myPlayer.InvestmentCount * 0.5f);
                reward += savingsFraction * _rewardParams.SavingsWeight * savingsBoost;
            }

            // Reward them for successfully upgrading their base health
            float healthDelta = myPlayer.CastleHealth - myPrevHealth; // min: 11,000 (+10)
            if (healthDelta > 0)
            {
                reward += healthDelta / 1100f + Math.Max((5 - myPlayer.RepairCount), 0) * 5f;
            }

            // Reward gadget upgrades (reaching the next tier after enough uses)
            int prevOffLevel = side == 1 ? _prevP1OffGadgetLevel : _prevP2OffGadgetLevel;
            int prevDefLevel = side == 1 ? _prevP1DefGadgetLevel : _prevP2DefGadgetLevel;
            int prevSigLevel = side == 1 ? _prevP1SigGadgetLevel : _prevP2SigGadgetLevel;
            if ((myPlayer.OffensiveGadget?.Level ?? 1) > prevOffLevel) reward += _rewardParams.GadgetUpgrade;
            if ((myPlayer.DefensiveGadget?.Level ?? 1) > prevDefLevel) reward += _rewardParams.GadgetUpgrade;
            if ((myPlayer.SignatureGadget?.Level ?? 1) > prevSigLevel) reward += _rewardParams.GadgetUpgrade;

            // Reward successfully activating a gadget (dense phase only)
            if (gadgetUsedThisTick)
                reward += _rewardParams.GadgetUse * _currentDenseWeight;

            // --- 4. ENDGAME MULTIPLIERS ---
            if (_state.IsGameOver)
            {
                float winRew  = _state.IsTimeLimit ? _rewardParams.WinReward * 0.45f : _rewardParams.WinReward;
                float lossRew = _state.IsTimeLimit ? _rewardParams.WinReward * 0.55f : _rewardParams.WinReward;

                if (_state.WinnerSide == side)
                    reward += winRew;
                else if (_state.WinnerSide != 0)
                    reward -= lossRew;
            }

            return reward / 1000f;
        }
    }
}