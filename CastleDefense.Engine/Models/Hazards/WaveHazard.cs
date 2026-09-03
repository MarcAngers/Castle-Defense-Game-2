namespace CastleDefense.Engine.Models.Hazards
{
    /// <summary>
    /// The travelling wall of water the Wave gadget sends across the map.
    ///
    /// IT NOW HAS A BUDGET (2026-09-03). Before, a wave swept the whole map and launched
    /// everything it touched for its full HazardDuration, so its power scaled with however
    /// many units happened to be on the field -- against a big army it was unbounded. It now
    /// carries <see cref="MaxKnockbacks"/>, spends one from it per DISTINCT unit it launches,
    /// and collapses the moment the budget is gone.
    ///
    /// DISTINCT UNITS, NOT HITS. A unit inside the wave for several ticks is pushed on each
    /// of them, exactly as before; only the FIRST of those ticks costs budget. Counting hits
    /// would make the cap depend on the wave's width and speed rather than on how many units
    /// it caught, which is not what a "maximum units knocked back" cap means.
    /// </summary>
    public class WaveHazard : Hazard
    {
        /// <summary>
        /// Distinct enemy units this wave may launch before it collapses. Set by
        /// <see cref="Gadgets.WaveEffect"/> from the gadget's level; see WaveEffect.CapFor.
        /// </summary>
        public int MaxKnockbacks { get; set; } = int.MaxValue;

        /// <summary>
        /// How far a TIER 8 unit is launched, regardless of the wave's level. They are the
        /// heaviest thing on the field, so a wave shoves them rather than throwing them.
        /// </summary>
        private const float Tier8KnockbackDist = 25f;

        /// <summary>
        /// Units already charged against <see cref="MaxKnockbacks"/>.
        ///
        /// THE ONLY REFERENCE-TYPED FIELD ON ANY HAZARD, which is exactly the case the base
        /// class's Clone note warns about: a MemberwiseClone would share this set between a
        /// search rollout and the live game, so one rollout's waves would silently spend the
        /// real wave's budget. <see cref="Clone"/> is overridden below for that reason and
        /// `clone-check` is the guard.
        /// </summary>
        private HashSet<Guid> _launched = new HashSet<Guid>();

        /// <summary>How much of the budget has been spent. Diagnostic, and read by checks.</summary>
        public int LaunchedCount => _launched.Count;

        public override Hazard Clone()
        {
            var copy = (WaveHazard)base.Clone();
            copy._launched = new HashSet<Guid>(_launched);
            return copy;
        }

        public override void ProcessEffect(GameState state)
        {
            float direction = (this.Side == 1) ? 1f : -1f;
            var enemies = state.Units.Where(u => u.Side != this.Side).ToList();

            int level = 1;
            if (!string.IsNullOrEmpty(this.SourceGadgetId))
            {
                var parts = this.SourceGadgetId.Split('_');
                if (parts.Length > 1 && int.TryParse(parts[1], out int parsedLevel))
                {
                    level = parsedLevel;
                }
            }

            // Calculate wave speed and knockback strength
            float durationSeconds = 5f; // Level 1 defaults to 5 seconds
            float knockbackDist = 500f;
            if (level == 2)
            {
                durationSeconds = 7f;
                knockbackDist = 1500f;
            }
            if (level == 3)
            {
                durationSeconds = 10f;
                knockbackDist = 3000f;
            }

            float speed = GameEngine.MAP_WIDTH / (durationSeconds * GameEngine.TICKS_PER_SECOND);

            foreach (var enemy in enemies)
            {
                // 1D Hitbox overlap check:
                // Unit's right edge > Hazard's left edge AND Unit's left edge < Hazard's right edge
                if (enemy.Position + enemy.Width >= this.Position && enemy.Position <= this.Position + this.Width)
                {
                    if (enemy.DefinitionId == "wall")
                    {
                        continue;
                    }
                    if (enemy.Tier == 8 && enemy.Statuses.Exists(s => s.Name == "Knockback"))
                    {
                        continue;
                    }

                    // THE BUDGET GATE. A unit already on the list keeps being pushed for free
                    // -- it has been paid for. A NEW unit is skipped outright once the budget
                    // is gone, so the wave stops affecting anything the instant it is spent
                    // rather than carrying on for one more tick.
                    if (!_launched.Contains(enemy.InstanceId))
                    {
                        if (_launched.Count >= MaxKnockbacks) continue;
                        _launched.Add(enemy.InstanceId);
                    }

                    // Only launch tier 8 units a little bit.
                    //
                    // PER-UNIT, and that is the whole point. This used to assign to
                    // `knockbackDist` itself -- the variable holding the LEVEL's distance for
                    // the whole loop -- so the first tier 8 the wave touched permanently cut
                    // every unit processed after it in the same tick down to 25 as well, and
                    // it stayed cut for every later tick of the same wave. A level-3 tsunami
                    // that clipped one tier 8 early stopped launching anything.
                    //
                    // Order-dependent and invisible without a tier 8 on the field, which is
                    // why it survived: the enemies list is in spawn order, so whether a wave
                    // "worked" depended on where the tier 8 happened to sit in it.
                    float unitKnockback = enemy.Tier == 8 ? Tier8KnockbackDist : knockbackDist;

                    // Launch the unit
                    enemy.PendingKnockback += (unitKnockback * direction);
                }
            }

            // SPENT: collapse instead of finishing the crossing. Expiring rather than being
            // removed here keeps the single removal path in GameEngine.ProcessHazards (which
            // is mid-iteration over this list), and the client already ends its animation
            // when the hazard stops appearing in the broadcast state -- so "the wave falls
            // away early" needs no new message, only this line.
            if (_launched.Count >= MaxKnockbacks)
            {
                this.ExpiresAtTick = (int)state.CurrentTick;
                return;
            }

            // Move the wave
            this.Position += speed * direction;
        }
    }
}
