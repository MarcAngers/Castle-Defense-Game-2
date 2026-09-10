using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Gadgets
{
    public class SnipeEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        /// <summary>
        /// How far either side of the click the shot will look for something worth hitting.
        ///
        /// DELIBERATELY GENEROUS -- 600 units wide against a 2,000-unit map, roughly half of
        /// what a landscape phone has on screen at once. Snipe exists to delete expensive
        /// units, and the moment a unit is worth deleting it is standing in a crowd of tier-1
        /// bodies. Asking the player to click a 50px sprite inside that crowd, on a
        /// touchscreen, was the gadget fighting its own purpose.
        ///
        /// NOT read from the CSV's `Radius` column, which is 0 for snipe and means something
        /// else entirely: `GadgetTargeting.FindBestAoeTarget` uses `def.Radius` as the BLAST
        /// radius when scoring where an area gadget should land. Giving snipe a non-zero
        /// Radius to feed this would silently change how the bots aim it as well, and would
        /// tell the Collection screen the shot has an area of effect. Two different meanings,
        /// so two different numbers.
        ///
        /// ONLY SHOT 0 USES IT. The follow-up shots of an upgraded snipe are not aimed by the
        /// player at all -- see <see cref="SelectTargets"/>.
        /// </summary>
        public const int AutoTargetRadius = 300;

        /// <summary>
        /// How close a real (non-wall) enemy has to be to the click before it is read as the
        /// thing the player aimed at, overriding a direct hit on the wall behind it.
        ///
        /// Roughly one unit sprite. See the carve-out in <see cref="SelectTarget"/> for why a
        /// bare "is the click inside the wall" test is not enough on its own.
        /// </summary>
        public const int DirectClickPrecision = 40;

        public SnipeEffect(GadgetDefinition def)
        {
            _def = def;
        }

        /// <summary>
        /// Shots one cast fires, and the gap between them.
        ///
        /// HARDCODED, not a CSV column (Marc, 2026-09-09: "we can hard-code these safely, I
        /// don't foresee changing them"). They briefly lived in `master_gadgets.csv` so the
        /// level-3 count could be swept without rebuilding; the sweep is done -- 6/8/10 climbs
        /// and 10 through 20 sits inside +/-0.25pp -- so the columns were removed rather than
        /// left bloating a file that is read by the client, the engine and every arena.
        ///
        /// THIS IS THE SOURCE OF TRUTH FOR THE CLIENT TOO. `snipe-animator.js` mirrors both
        /// tables; see the timing contract in its header.
        /// </summary>
        public static int ShotsFor(GadgetDefinition def) => def.Level switch
        {
            3 => 10,
            2 => 2,
            _ => 1,
        };

        /// <summary>Ticks between consecutive shots. Meaningless when ShotsFor is 1.</summary>
        public static int ShotIntervalFor(GadgetDefinition def) => def.Level switch
        {
            3 => 5,
            2 => 15,
            _ => 0,
        };

        /// <summary>
        /// Is <paramref name="position"/> a DIRECT hit on this wall, i.e. inside its own
        /// footprint rather than merely near it?
        ///
        /// A wall is far wider than a unit, which is what makes this a usable distinction for
        /// the player: clicking on the wall sprite is unambiguous in a way that clicking a
        /// 50px unit inside a crowd is not. Marc's rule, 2026-09-08 -- "walls are wide enough
        /// that it should be evident when the Snipe is targeted on the wall."
        ///
        /// Uses the INSTANCE width. Every spawned unit satisfies `unit.Width == def.Width` by
        /// construction (see the random-stat unit notes -- scaling a unit's logical size broke
        /// combat and was reverted to sprite-only), so this is the definition's width in
        /// practice; reading it off the instance is simply what the hit test means.
        /// </summary>
        private static bool IsDirectHit(Unit wall, int position)
            => Math.Abs(wall.Position - position) <= Math.Max(1, wall.Width) / 2f;

        /// <summary>
        /// The unit a shot aimed at <paramref name="position"/> will hit: the BEEFIEST enemy
        /// within <see cref="AutoTargetRadius"/>, not the nearest one.
        ///
        /// WHAT THIS REPLACED, because the old rule looked like it did this and did not. It
        /// took the enemy NEAREST the click, with a max-health tiebreak that only applied when
        /// two units were at *exactly* the same distance -- a float `==` between two positions
        /// that move by fractional amounts every tick, so in practice it never fired. Nearest
        /// always won. Against the one situation snipe is for -- a tier 7 escorted by a screen
        /// of chumps -- any chump a single pixel closer to the click stole the shot, and the
        /// gadget read as broken.
        ///
        /// HIGHEST MaxHealth, NOT CurrentHealth. The question is "which of these is the
        /// expensive one", and that does not change as the unit takes damage: a tier 7 down to
        /// its last 100 HP is still the thing worth spending a snipe on, and CurrentHealth
        /// would steer the shot onto whichever bystander happened to be undamaged. It also
        /// prices Black's random-stat weirdo honestly, since a big roll really is a bigger
        /// unit.
        ///
        /// ── WALLS, REVISED 2026-09-08 ────────────────────────────────────────────────────
        /// A wall's MaxHealth is 400 / 6,000 / 48,000, an order of magnitude above any real
        /// unit, so on raw health it out-ranks the entire field and a health-first rule would
        /// send every nearby shot into scenery that neither moves nor attacks. But a wall must
        /// stay snipeable, because breaking one is a legitimate play. Marc's resolution, and
        /// the rule now implemented:
        ///
        ///   1. A DIRECT hit on a wall -- the click inside the wall's own footprint -- takes
        ///      the wall, outright and before anything else. Aiming at it is unambiguous.
        ///   2. Otherwise the auto-targeter ignores walls entirely and takes the beefiest real
        ///      unit in range, however much health the wall has.
        ///   3. A wall in range is still taken as a LAST RESORT when there is no real unit in
        ///      range at all, so a lone wall is not immune to a shot pointed at it.
        ///   4. Follow-up shots NEVER take a wall under any of the above -- see SelectTargets.
        /// </summary>
        public static Unit SelectTarget(IReadOnlyList<Unit> enemies, int position)
        {
            if (enemies == null || enemies.Count == 0) return null;

            Unit best = null, bestWall = null, nearest = null, directWall = null, nearestReal = null;
            float bestDist = 0, bestWallDist = 0, nearestDist = 0, directWallDist = 0, nearestRealDist = 0;

            foreach (var enemy in enemies)
            {
                float dist = Math.Abs(enemy.Position - position);

                // The out-of-range fallback, so a click into empty space still fires rather
                // than fizzling. This is also the path every BOT cast used to take and still
                // resolves the same way for them: GadgetTargeting.AutoTarget hands back a
                // position sitting on a unit, so that unit is always inside the radius.
                if (nearest == null || dist < nearestDist) { nearest = enemy; nearestDist = dist; }

                if (GadgetTargeting.IsWall(enemy))
                {
                    if (IsDirectHit(enemy, position)
                        && (directWall == null || dist < directWallDist))
                    { directWall = enemy; directWallDist = dist; }

                    // Rule 3: remembered only as the last resort.
                    if (dist > AutoTargetRadius) continue;
                    if (bestWall == null || dist < bestWallDist) { bestWall = enemy; bestWallDist = dist; }
                    continue;
                }

                if (nearestReal == null || dist < nearestRealDist) { nearestReal = enemy; nearestRealDist = dist; }

                if (dist > AutoTargetRadius) continue;

                // Beefiest wins; ties go to whichever is nearer the click, so the choice is
                // deterministic and matches where the player actually pointed.
                if (best == null
                    || enemy.MaxHealth > best.MaxHealth
                    || (enemy.MaxHealth == best.MaxHealth && dist < bestDist))
                {
                    best = enemy;
                    bestDist = dist;
                }
            }

            // Rule 1, with the one carve-out a 450-wide wall_3 forces. "Inside the wall's
            // footprint" is a much larger region than a unit sprite -- wall_3's half-width is
            // 225, most of AutoTargetRadius -- so a bare direct-hit test would hand the shot
            // to the wall whenever an enemy stood IN FRONT of it, which is precisely where
            // enemies stand. A unit within DirectClickPrecision of the click is therefore read
            // as the player aiming at that unit, not at the wall behind it. Outside that, a
            // click inside the wall is a click on the wall and takes it outright.
            if (directWall != null && (nearestReal == null || nearestRealDist > DirectClickPrecision))
                return directWall;

            return best ?? bestWall ?? nearest;
        }

        /// <summary>
        /// The full firing solution for one cast: up to <paramref name="shots"/> DISTINCT
        /// targets, in the order they will be fired.
        ///
        /// Shot 0 is the player's aim, resolved by <see cref="SelectTarget"/> above and
        /// therefore able to be a wall. Every later shot takes the highest-MaxHealth enemy not
        /// already on the list, **map-wide rather than within AutoTargetRadius** -- Marc's
        /// spec is "the next highest HP unit on the screen", and the radius exists to
        /// interpret a click, which the follow-ups do not have.
        ///
        /// FOLLOW-UPS NEVER TAKE A WALL. A wall out-ranks the entire roster on MaxHealth, so
        /// without this a level-3 cast would put nine of its ten shots into two walls while
        /// the army it was fired at walked past. The player can still spend a cast on a wall
        /// deliberately; they cannot have the gadget spend itself on one.
        ///
        /// TARGETS ARE CHOSEN AT CAST TIME, NOT AT FIRE TIME, and the list is DISTINCT.
        /// Choosing per shot would re-aim onto whatever survived, which sounds better and is
        /// worse: at 55,000 damage a level-3 shot kills essentially anything it touches, so
        /// re-aiming would concentrate the whole burst on the few units that happened to live
        /// through the previous one. Distinct-at-cast-time is what makes ten shots hit ten
        /// units. A target killed by something else in the meantime simply fizzles, which is
        /// the behaviour a single-shot snipe has had since 2026-07-28.
        ///
        /// Fewer targets than shots means fewer shots. The surplus is NOT re-aimed at an
        /// already-listed unit: overkill on a corpse is not worth hiding, and a visibly
        /// wasted shot against a thin field is the honest cost of a burst weapon.
        /// </summary>
        public static List<Unit> SelectTargets(IReadOnlyList<Unit> enemies, int position, int shots)
        {
            var picked = new List<Unit>();
            if (enemies == null || enemies.Count == 0 || shots <= 0) return picked;

            var first = SelectTarget(enemies, position);
            if (first == null) return picked;
            picked.Add(first);

            for (int i = 1; i < shots; i++)
            {
                Unit next = null;
                foreach (var enemy in enemies)
                {
                    if (GadgetTargeting.IsWall(enemy)) continue;
                    bool already = false;
                    foreach (var p in picked)
                        if (p.InstanceId == enemy.InstanceId) { already = true; break; }
                    if (already) continue;

                    // Position is the tiebreak purely so the list is deterministic -- two
                    // units of the same type have identical MaxHealth constantly, and a
                    // reproducible order is what lets --snipe-check and replay
                    // reconstruction agree.
                    if (next == null
                        || enemy.MaxHealth > next.MaxHealth
                        || (enemy.MaxHealth == next.MaxHealth && enemy.Position < next.Position))
                        next = enemy;
                }
                if (next == null) break;      // nothing left to shoot
                picked.Add(next);
            }

            return picked;
        }

        public void Execute(GameEngine engine, int side, int position)
        {
            var enemies = engine._state.Units.Where(u => u.Side != side).ToList();

            if (enemies.Count == 0)
                return;

            int shots = ShotsFor(_def);
            var targets = SelectTargets(enemies, position, shots);
            if (targets.Count == 0) return;

            // ONE animation event for the whole cast, carrying shot 0's target. The client
            // re-derives the follow-up targets with the same rule as SelectTargets -- see the
            // contract note in wwwroot/src/animators/snipe-animator.js. Kept to one event on
            // purpose: OnGadgetAnimation is what GameRecorder writes a cast from, so firing it
            // per shot would record one cast as several.
            engine.TriggerGadgetAnimation(_def.Id, side, position, targets[0].InstanceId);

            engine.AddGadgetXp(side, "snipe", 100);

            int interval = ShotIntervalFor(_def);
            for (int i = 0; i < targets.Count; i++)
            {
                // Scheduled rather than applied inline so the damage lands with the bullet.
                // The target is stored by InstanceId rather than as an object reference --
                // a captured reference would point into the original game after a clone.
                engine.ScheduleEffect(_def.Delay + i * interval, new PendingEffect
                {
                    GadgetId = _def.Id,
                    Phase = PhaseFire,
                    Side = side,
                    Position = position,
                    TargetId = targets[i].InstanceId,
                });
            }
        }

        private const int PhaseFire = 0;

        public void ExecuteScheduled(GameEngine engine, in PendingEffect e)
        {
            if (e.Phase != PhaseFire) return;

            // BEHAVIOUR CHANGE, deliberate (2026-07-28, agreed with Marc): if the target
            // died during the delay it is gone from Units, so the shot now fizzles. The
            // old closure held the Unit object directly and applied damage to a corpse
            // that was no longer on the board. Fizzling is the correct reading of "the
            // target got sniped" and the old behaviour was a latent bug.
            // Copy out of the `in` parameter first — CS1628: an in/ref/out parameter
            // cannot be captured by a lambda.
            var targetId = e.TargetId;
            var target = engine._state.Units.FirstOrDefault(u => u.InstanceId == targetId);
            if (target == null) return;

            engine.ApplyDamage(target, (int)_def.BaseValue, Models.AttackType.Melee, _def.PushForce);
        }
    }
}
