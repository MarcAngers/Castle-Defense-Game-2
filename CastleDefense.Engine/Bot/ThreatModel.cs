using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;
using System;

namespace CastleDefense.Engine.Bot
{
    /// <summary>
    /// The enemy attack, reduced to the four numbers the survival law needs, plus whatever
    /// relief the bot's gadgets have already bought this decision.
    ///
    /// THE LAW (measured 2026-08-21 over 22,700 scripted games; see
    /// CastleDefense.BotArena/stall/FINDINGS.md and the fitting scripts beside it). A body in
    /// contact absorbs one enemy swing, because MoveAndFight makes a unit with ANY enemy in
    /// range attack that unit instead of the castle -- reaching the castle needs
    /// FindTargetsFast to come back empty, so blocking is a hard stop, not a damage race.
    /// With the enemy delivering S swings/sec and bodies arriving at r/sec, castle-bound
    /// swings leak at (S - r) and the castle needs K of them:
    ///
    ///     t(r) = T_walk + K / (S - r)          survival at block rate r
    ///     r    = S - K / (T - T_walk)          rate needed to survive T seconds
    ///
    /// Fitted on unescorted single-tier runs: median R^2 0.972, recovered S within 4% and K
    /// within 6% of roster values, median survival-prediction error -4.5%. Out of sample and
    /// never fitted: treating a mid-tier "anchor" as nothing but a body arriving at 1/cadence
    /// moved the error from -17.5% to -3.7%.
    ///
    /// TWO CONSEQUENCES THE CALLER SHOULD ACT ON, both of which fall out of the algebra:
    ///  - Returns ACCELERATE. dt/dr = K/(S-r)^2 grows as r approaches S, so spending a little
    ///    is nearly worthless. Match the swing rate or save the money; do not spend
    ///    proportionally.
    ///  - Per unit of blocking, only PRICE matters, because a body absorbs one swing whatever
    ///    the body is. Measured cost per absorbed swing: tier 4 is 10x a tier 1, tier 5 is
    ///    30x, tier 6 is 166x. Buy something bigger only for what blocking does not do --
    ///    killing an escort that is eating the line.
    ///
    /// WHAT THE LAW DOES NOT MODEL, and where it is therefore wrong:
    ///  - It is BLOCKING ONLY. Cheap bodies also out-damage low-tier attackers outright, so
    ///    against tier 5 the law is conservative -- less defence is needed than it says.
    ///  - It predicts the survival TIME much better than it predicts the exact critical rate.
    ///    Observed r_crit/S ran 0.11-0.14 against tier 5 and 1.9-2.6 against tier 7 in
    ///    numbers. Aim with a margin rather than exactly at S.
    ///  - S is assumed constant over the window. It is not when the enemy is still
    ///    reinforcing, which is why the caller waits for the force to settle before reading.
    /// </summary>
    public sealed class ThreatModel
    {
        /// <summary>S -- total enemy swings per second currently aimed at our castle.</summary>
        public float SwingRate { get; private set; }

        /// <summary>D -- castle damage per second if nothing blocks at all.</summary>
        public float UnblockedDps { get; private set; }

        /// <summary>K -- castle-bound swings needed to finish us, = HP / mean damage per swing.</summary>
        public float SwingsToKill { get; private set; }

        /// <summary>T_walk -- seconds until the FIRST attacker can strike the castle.</summary>
        public float WalkSeconds { get; private set; }

        /// <summary>Seconds of the attack already neutralised by a gadget this decision.</summary>
        public float ReliefSeconds { get; private set; }

        /// <summary>Enemy units that can actually swing (walls and hard-CC'd units excluded).</summary>
        public int ActiveAttackers { get; private set; }

        /// <summary>Total roster price of the attacking force -- what the enemy has committed.</summary>
        public double ForceValue { get; private set; }

        /// <summary>Largest single castle hit in the force. Drives the one-shot floor in K.</summary>
        public float BiggestSwing { get; private set; }

        /// <summary>True when nothing on the board can currently hurt the castle.</summary>
        public bool Idle => SwingRate <= 0.0001f || UnblockedDps <= 0.0001f;

        /// <summary>
        /// Survival in seconds if we feed bodies at <paramref name="blockRate"/> per second.
        /// Infinite once the block rate covers the swing rate -- every swing is absorbed.
        /// </summary>
        public float SurvivalSeconds(float blockRate)
        {
            if (Idle) return float.PositiveInfinity;
            float leak = SwingRate - blockRate;
            if (leak <= 0.0001f) return float.PositiveInfinity;
            return WalkSeconds + ReliefSeconds + SwingsToKill / leak;
        }

        /// <summary>
        /// Bodies per second needed to survive <paramref name="targetSeconds"/>. Zero when the
        /// target is already met for free; <see cref="SwingRate"/> when it cannot be met at all
        /// (the caller decides whether to pay that, or to reach for a gadget instead).
        /// </summary>
        public float RequiredBlockRate(float targetSeconds)
        {
            if (Idle) return 0f;
            float budget = targetSeconds - WalkSeconds - ReliefSeconds;
            if (budget <= 0.0001f) return SwingRate;          // already out of time
            float r = SwingRate - SwingsToKill / budget;
            if (r <= 0f) return 0f;                            // free -- we outlast it anyway
            return r > SwingRate ? SwingRate : r;
        }

        /// <summary>
        /// Records relief a gadget has already bought, so the spawn logic pays only for the
        /// residual. This is the whole point of the shared model: the two systems never have
        /// to know about each other, they just both write to and read from this.
        ///
        /// <paramref name="suppressSeconds"/> is for DELAYERS (freeze, goo, blackhole, a wall
        /// body) -- the attack simply stops for that long. <paramref name="killedSwingRate"/>
        /// and <paramref name="killedDps"/> are for ELIMINATORS (nuke, firebomb, snipe) --
        /// they permanently remove attackers, so both S and D drop and K is refitted.
        /// </summary>
        public void ApplyRelief(float suppressSeconds, float killedSwingRate, float killedDps)
        {
            if (suppressSeconds > 0f) ReliefSeconds += suppressSeconds;

            if (killedSwingRate > 0f || killedDps > 0f)
            {
                SwingRate = System.Math.Max(0f, SwingRate - killedSwingRate);
                UnblockedDps = System.Math.Max(0f, UnblockedDps - killedDps);
                RefitSwingsToKill(_castleHealth);
            }
        }

        private int _castleHealth;

        private void RefitSwingsToKill(int castleHealth)
        {
            _castleHealth = castleHealth;
            if (SwingRate <= 0.0001f || UnblockedDps <= 0.0001f) { SwingsToKill = 0f; return; }

            // K = HP / (mean damage per swing), and the mean is D/S. Equivalently HP*S/D.
            float meanSwingDamage = UnblockedDps / SwingRate;
            float k = castleHealth / meanSwingDamage;

            // ONE-SHOT FLOOR. DamageCastle refuses to kill a FULL-HP castle in a single blow --
            // it floors that hit at 1 HP -- so anything that would one-shot us actually needs
            // two swings, not the fraction the division above produces. At 23,000 HP this is
            // exactly why every tier 8 is a two-swing kill.
            if (BiggestSwing >= castleHealth && k < 2f) k = 2f;

            SwingsToKill = k < 1f ? 1f : k;
        }

        /// <summary>
        /// Reads the live board into a model. Uses each unit's DEFINITION for damage and attack
        /// speed because that is what MoveAndFight actually reads when it resolves a castle hit
        /// (it recomputes from `def`, not from the Unit's own copy), and the unit's live
        /// Statuses for the modifiers that do apply -- Rage multiplies castle damage, and the
        /// hard-CC set (Freeze/Stun/Knockback/Blackhole) makes a unit skip its turn entirely,
        /// which is exactly how a freeze shows up here as a lower S.
        /// </summary>
        public static ThreatModel Build(GameEngine engine, int mySide,
                                        System.Collections.Generic.List<Unit> enemyUnits,
                                        int castleHealth)
        {
            var m = new ThreatModel();
            if (enemyUnits == null || enemyUnits.Count == 0)
            {
                m.RefitSwingsToKill(castleHealth);
                return m;
            }

            float nearestWalk = float.PositiveInfinity;

            foreach (var u in enemyUnits)
            {
                var def = LookupDefinition(u);
                if (def == null) continue;

                // A wall has AttackSpeed 0 and never swings; it is scenery, not a threat.
                if (def.AttackSpeed <= 0f) continue;

                m.ForceValue += def.Cost;

                bool hardCcd = false;
                float rage = 1f;
                for (int i = 0; i < u.Statuses.Count; i++)
                {
                    var st = u.Statuses[i];
                    if (st.Name == "Rage") rage *= st.Value;
                    else if (st.Name == "Freeze" || st.Name == "Stun"
                          || st.Name == "Knockback" || st.Name == "Blackhole") hardCcd = true;
                }

                // Hard-CC'd units are skipped by MoveAndFight outright, so they contribute
                // nothing to S right now. That is deliberate and is how an already-landed
                // freeze is accounted for without any special case.
                if (hardCcd) continue;

                float swing = def.Damage * (def.AttackType == AttackType.Siege ? 2f : 1f) * rage;
                if (swing <= 0f) continue;

                m.SwingRate += def.AttackSpeed;
                m.UnblockedDps += def.AttackSpeed * swing;
                m.ActiveAttackers++;
                if (swing > m.BiggestSwing) m.BiggestSwing = swing;

                // Time for this one to come into range of our castle. GetDistanceToEnemyCastle
                // is written from the unit's own point of view, so for an ENEMY unit the
                // "enemy castle" it measures to is ours.
                float dist = engine.GetDistanceToEnemyCastle(u);
                float speed = def.MoveSpeed;
                for (int i = 0; i < u.Statuses.Count; i++)
                {
                    var st = u.Statuses[i];
                    if (st.Name == "Slow" || st.Name == "Speed") speed *= st.Value;
                }
                float walk = (dist <= def.Range || speed <= 0.01f)
                    ? 0f
                    : (dist - def.Range) / (speed * GameEngine.TICKS_PER_SECOND);
                if (walk < nearestWalk) nearestWalk = walk;
            }

            m.WalkSeconds = float.IsPositiveInfinity(nearestWalk) ? 0f : nearestWalk;
            m.RefitSwingsToKill(castleHealth);
            return m;
        }


        /// <summary>
        /// What one gadget cast takes off the threat, in the two currencies
        /// <see cref="ApplyRelief"/> understands.
        ///
        /// DELIBERATELY CONSERVATIVE, and the asymmetry is the reason. Under-claiming relief
        /// makes the bot buy a few more bodies than it strictly needed -- cheap. Over-claiming
        /// makes it stand down in front of an attack that is still coming -- fatal. Every
        /// judgement call below is therefore resolved downward.
        ///
        /// Keyed by gadget FAMILY rather than derived generically from Radius, because the
        /// generic reading is wrong for the most important case: freeze has Radius 0 but
        /// FreezeEffect hits every enemy on the board, and gives tier 8 units one second where
        /// everything else gets StatusDuration. A generic rule would have quietly mis-scored
        /// the single best delaying tool in the game.
        /// </summary>
        public static (float SuppressSeconds, float KilledSwingRate, float KilledDps)
            EstimateRelief(GadgetDefinition def, int position,
                           System.Collections.Generic.List<Unit> enemyUnits)
        {
            if (def == null || enemyUnits == null || enemyUnits.Count == 0) return (0f, 0f, 0f);
            string family = def.Id.Split('_')[0].ToLowerInvariant();

            switch (family)
            {
                // ---- DELAYERS -------------------------------------------------------
                case "freeze":
                {
                    // Global, and tier 8 is capped at 30 ticks by FreezeEffect. Take the
                    // SHORTEST freeze any live attacker will get: the attack resumes as soon
                    // as the first one thaws, so that is the honest suppression.
                    float shortest = def.StatusDuration / (float)GameEngine.TICKS_PER_SECOND;
                    foreach (var u in enemyUnits)
                        if (u.Tier == 8) { shortest = Math.Min(shortest, 1f); break; }
                    return (Math.Max(0f, shortest), 0f, 0f);
                }
                case "blackhole":
                {
                    // Hard CC, but only for what the hazard actually catches. Scale the
                    // duration by the share of the swing rate inside the radius.
                    float share = SwingShareInRadius(enemyUnits, position, def.Radius);
                    float seconds = def.HazardDuration / (float)GameEngine.TICKS_PER_SECOND;
                    return (seconds * share, 0f, 0f);
                }

                // ---- ELIMINATORS ----------------------------------------------------
                case "nuke":
                case "firebomb":
                case "meteor":
                case "wave":
                case "poison":
                case "snipe":
                {
                    // Only count a unit as removed when the hit alone finishes it -- shield
                    // first, then health, exactly as ApplyDamage resolves it. Burn and poison
                    // ticks are ignored on purpose: they are real, but counting them would be
                    // claiming a kill the cast has not yet made.
                    int radius = family == "snipe" ? 0 : def.Radius;
                    float killedSwings = 0f, killedDps = 0f;
                    foreach (var u in enemyUnits)
                    {
                        if (radius > 0 && Math.Abs(u.Position - position) > radius) continue;
                        var ud = LookupDefinition(u);
                        if (ud == null || ud.AttackSpeed <= 0f) continue;
                        if (def.BaseValue < u.CurrentHealth + u.CurrentShield) continue;

                        float swing = ud.Damage * (ud.AttackType == AttackType.Siege ? 2f : 1f);
                        killedSwings += ud.AttackSpeed;
                        killedDps += ud.AttackSpeed * swing;
                        if (radius <= 0) break;   // snipe removes exactly one thing
                    }
                    return (0f, killedSwings, killedDps);
                }

                // ---- NOTHING THE THREAT MODEL CAN BANK ------------------------------
                // wall  -- a real absorbing body, but it blocks rather than suppresses and
                //          modelling that properly means tracking its HP against the swings
                //          it eats. Worth doing; not worth guessing. Scores 0 for now, which
                //          errs the safe way.
                // heal / speed / rage / divine / cash / reinforcements -- act on OUR side, so
                //          they change survival without changing the threat. Any relief they
                //          give shows up in the next decision's board anyway.
                default:
                    return (0f, 0f, 0f);
            }
        }

        /// <summary>Share of the active swing rate standing within <paramref name="radius"/> of a point.</summary>
        private static float SwingShareInRadius(System.Collections.Generic.List<Unit> enemyUnits,
                                                int position, int radius)
        {
            float total = 0f, inside = 0f;
            foreach (var u in enemyUnits)
            {
                var ud = LookupDefinition(u);
                if (ud == null || ud.AttackSpeed <= 0f) continue;
                total += ud.AttackSpeed;
                if (radius <= 0 || Math.Abs(u.Position - position) <= radius) inside += ud.AttackSpeed;
            }
            return total <= 0.0001f ? 0f : inside / total;
        }

        /// <summary>
        /// Roster row for a live unit. Gadget-spawned units (reinforcements, walls) are on the
        /// board under ids that may not belong to the owner's team roster, so this searches all
        /// teams rather than assuming one.
        /// </summary>
        private static UnitDefinition LookupDefinition(Unit u)
        {
            foreach (var team in GameDataManager.Teams)
            {
                var roster = team.Roster;
                for (int i = 0; i < roster.Count; i++)
                    if (roster[i].Id == u.DefinitionId) return roster[i];
            }
            return null;
        }

        public override string ToString() =>
            Idle
                ? "threat=idle"
                : $"S={SwingRate:F2}/s D={UnblockedDps:F0}dps K={SwingsToKill:F1} " +
                  $"walk={WalkSeconds:F1}s relief={ReliefSeconds:F1}s n={ActiveAttackers} " +
                  $"value=${ForceValue:F0}";
    }
}
