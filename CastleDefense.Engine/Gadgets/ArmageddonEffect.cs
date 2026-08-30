using CastleDefense.Engine.Models;
using CastleDefense.Engine.Models.Hazards;
using System.Linq;

namespace CastleDefense.Engine.Gadgets
{
    /// <summary>
    /// ARMAGEDDON — what the invest button becomes once a player reaches
    /// <see cref="PlayerState.ArmageddonInvestmentCount"/> investments.
    ///
    /// WHY IT EXISTS: with both players on max income the late game degenerated into a
    /// spam stalemate, because neither side could convert an economic lead into a win.
    /// ARMAGEDDON is the conversion: it is deliberately not balanced, it escalates on a
    /// timer, and it is meant to close the game out within a few seconds in favour of
    /// whoever reached the threshold first.
    ///
    /// HOW IT RUNS. Every recurring piece is a self-rescheduling <see cref="PendingEffect"/>
    /// rather than a timer or a coroutine. That is what keeps the engine cloneable — a
    /// search rollout branched mid-ARMAGEDDON inherits the queue as plain data and keeps
    /// raining meteors on its own board, not the real one. See PendingEffect.cs.
    ///
    /// Each phase re-arms itself at the END of its handler and every handler bails on
    /// IsGameOver. That guard is load bearing, not defensive padding: GameEngine.Tick
    /// drains the scheduled-effect list BEFORE it checks IsGameOver, so without it the
    /// cascade would keep firing nukes into a finished game forever. The one exception is
    /// the shield phase, which stops re-arming at the end of its own window.
    ///
    /// THE SHIELD IS THE PART THAT DECIDES WHO WINS when both players get here. It runs for
    /// <see cref="ShieldDuration"/> over the castle and every allied unit, present or bought
    /// during the window; a SECOND ARMAGEDDON inherits the first one's expiry rather than
    /// starting a fresh window, so the two shields drop together. See
    /// <see cref="ClaimShieldWindow"/> for why arriving second used to be the winning move.
    ///
    /// The individual gadget values are read back out of the real definitions (meteor_3,
    /// nuke_3, wave_3, …) via GameEngine.GetGadgetDefinition, so rebalancing those rows in
    /// master_gadgets.csv rebalances ARMAGEDDON too. Only the TIMELINE lives here.
    /// </summary>
    public class ArmageddonEffect : IGadgetEffect
    {
        /// <summary>
        /// Id under which GameEngine.BuildCache registers this. Not a master_gadgets.csv
        /// row — see the comment at that callsite for why it must stay out of the CSV.
        /// Also used as the SourceGadgetId tag on every status the cascade applies, which
        /// is how re-application finds and refreshes its own statuses instead of stacking
        /// new ones (see <see cref="RefreshAllyStatus"/>).
        /// </summary>
        public const string GadgetId = "armageddon";

        // No GadgetDefinition is taken in the constructor, unlike every sibling effect:
        // there is no CSV row to carry per-level values, the timeline constants below are
        // the only tuning this effect owns, and everything else is read from the real
        // gadgets it chains into.

        // ── Timeline (30 ticks per second) ────────────────────────────────────────
        private const int PhaseTwoDelay   = 90;   // +3s: ally buffs + recurring tsunami
        private const int PhaseThreeDelay = 180;  // +6s: recurring nukes on the enemy castle

        private const int MeteorMinInterval = 3;   // 100ms
        private const int MeteorMaxInterval = 16;  // exclusive -> up to 500ms
        private const int FirebombInterval  = 30;  // 1s
        private const int WaveInterval      = 90;  // 3s
        private const int NukeInterval      = 30;  // 1s

        /// <summary>
        /// How long the divine shield lasts: 10s, castle and every allied unit alike.
        ///
        /// Deliberately NOT divine_3's StatusDuration, which is what this used to read.
        /// The shield window is a TIMELINE value now — it is the thing two competing casts
        /// have to agree on (see <see cref="ClaimShieldWindow"/>), and the timeline is the
        /// one thing this class owns. Rebalancing the divine_3 CSV row should not silently
        /// move it. The status VALUE still comes from that definition, as before.
        /// </summary>
        private const int ShieldDuration = 10 * GameEngine.TICKS_PER_SECOND;

        // Ally buffs are re-applied on a cadence rather than cast once with a giant
        // duration, so units bought AFTER the cascade starts are swept in too. The status
        // lasts 3x the refresh interval purely so a missed refresh cannot make buffs
        // visibly flicker off.
        private const int BuffRefreshInterval = 30;
        private const int BuffDuration        = 90;

        // The SHIELD sweep runs ten times more often than the other buffs. Its job is to
        // cover units bought DURING the window, and unlike a missed rage tick a missed
        // shield tick is a unit that can be killed while it is supposed to be immortal, so
        // the gap between a unit appearing and being shielded is held to 100ms. Unlike the
        // buffs it does NOT re-arm forever: it stops at the window end, and every status it
        // writes carries that same absolute expiry rather than a rolling one.
        private const int ShieldRefreshInterval = 3;

        private const int PhaseMeteor      = 0;
        private const int PhaseFirebomb    = 1;
        private const int PhaseShieldFirst = 2;
        private const int PhaseShield      = 3;
        private const int PhaseBuffs       = 4;
        private const int PhaseWave        = 5;
        private const int PhaseNuke        = 6;

        public void Execute(GameEngine engine, int side, int position)
        {
            // Screen goes dark and stays dark. Its own animator rather than blackhole_3's,
            // so there is no stray black hole sprite sitting on the field with no hazard
            // under it.
            engine.TriggerGadgetAnimation(GadgetId, side, position);

            // Divine shield keeps its normal animation lead-in.
            var divine = engine.GetGadgetDefinition("divine_3");
            int shieldLeadIn = divine?.Delay ?? 0;

            // Decide when the shield ENDS right now, at cast time, rather than when it
            // lands. Two ARMAGEDDONs bought on the same tick have to agree on that tick,
            // and they only can if the second one sees the first one's claim before either
            // shield is actually applied.
            ClaimShieldWindow(engine, side, shieldLeadIn);

            engine.ScheduleEffect(shieldLeadIn, Effect(PhaseShieldFirst, side));
            if (divine != null) engine.TriggerGadgetAnimation(divine.Id, side, position);

            // Phase one's two bombardments start immediately.
            FireMeteor(engine, side);
            engine.ScheduleEffect(engine.Rng.Next(MeteorMinInterval, MeteorMaxInterval), Effect(PhaseMeteor, side));

            FireFirebomb(engine, side);
            engine.ScheduleEffect(FirebombInterval, Effect(PhaseFirebomb, side));

            engine.ScheduleEffect(PhaseTwoDelay, Effect(PhaseBuffs, side));
            engine.ScheduleEffect(PhaseTwoDelay, Effect(PhaseWave, side));
            engine.ScheduleEffect(PhaseThreeDelay, Effect(PhaseNuke, side));
        }

        public void ExecuteScheduled(GameEngine engine, in PendingEffect e)
        {
            // Stop the cascade dead once the game is decided. Nothing below re-arms.
            if (engine._state.IsGameOver) return;

            int side = e.Side;

            switch (e.Phase)
            {
                case PhaseMeteor:
                    FireMeteor(engine, side);
                    engine.ScheduleEffect(engine.Rng.Next(MeteorMinInterval, MeteorMaxInterval), Effect(PhaseMeteor, side));
                    break;

                case PhaseFirebomb:
                    FireFirebomb(engine, side);
                    engine.ScheduleEffect(FirebombInterval, Effect(PhaseFirebomb, side));
                    break;

                case PhaseShieldFirst:
                {
                    // The castle half fires ONCE, for the window claimed at cast time.
                    // Refreshing it forever would make the caster's castle permanently
                    // immune, including to their own phase-three nukes, and ARMAGEDDON is
                    // meant to stay capable of backfiring on a caster who is behind on HP.
                    var castle = side == 1 ? engine._state.Player1 : engine._state.Player2;

                    // The window can already be spent: an ARMAGEDDON bought inside the last
                    // few frames of the enemy's shield inherits whatever is left of it,
                    // which may be nothing. Arriving second never buys shield time the first
                    // player does not also get.
                    if (castle.ArmageddonShieldUntilTick <= engine._state.CurrentTick) break;

                    castle.IsInvulnerable = true;
                    castle.InvulnerableUntilTick = castle.ArmageddonShieldUntilTick;
                    goto case PhaseShield;
                }

                case PhaseShield:
                {
                    var shielded = side == 1 ? engine._state.Player1 : engine._state.Player2;
                    long until = shielded.ArmageddonShieldUntilTick;

                    // Window over: stop re-arming. This is the one recurring phase that
                    // ends on its own rather than running until the game does.
                    if (engine._state.CurrentTick >= until) break;

                    ApplyShield(engine, side, until);
                    engine.ScheduleEffect(ShieldRefreshInterval, Effect(PhaseShield, side));
                    break;
                }

                case PhaseBuffs:
                    ApplyBuffs(engine, side);
                    engine.ScheduleEffect(BuffRefreshInterval, Effect(PhaseBuffs, side));
                    break;

                case PhaseWave:
                    FireWave(engine, side);
                    engine.ScheduleEffect(WaveInterval, Effect(PhaseWave, side));
                    break;

                case PhaseNuke:
                    FireNuke(engine, side);
                    engine.ScheduleEffect(NukeInterval, Effect(PhaseNuke, side));
                    break;
            }
        }

        private static PendingEffect Effect(int phase, int side)
            => new PendingEffect { GadgetId = GadgetId, Phase = phase, Side = side };

        // ── The individual bombardments ──────────────────────────────────────────
        // Each one reproduces the two lines the real gadget's Execute would run, minus its
        // AddGadgetXp call. Going through Execute would quietly level up whatever the
        // player happens to have equipped, once per meteor, for the rest of the game.

        /// <summary>One meteor_3 impact at a random point on the map.</summary>
        private static void FireMeteor(GameEngine engine, int side)
        {
            var def = engine.GetGadgetDefinition("meteor_3");
            if (def == null) return;

            int pos = engine.Rng.Next(0, GameEngine.MAP_WIDTH + 1);

            // "meteor" not def.Id, matching MeteorEffect: the animator has no level-3 art.
            engine.TriggerGadgetAnimation("meteor", side, pos);
            engine.ScheduleEffect(def.Delay, new PendingEffect
            {
                GadgetId = def.Id,
                Phase = MeteorEffect.PhaseDamage,
                Side = side,
                Position = pos,
            });
        }

        /// <summary>A level-1 firebomb at a random point on the map.</summary>
        private static void FireFirebomb(GameEngine engine, int side)
        {
            var def = engine.GetGadgetDefinition("firebomb");
            if (def == null) return;

            int pos = engine.Rng.Next(0, GameEngine.MAP_WIDTH + 1);

            engine.TriggerGadgetAnimation(def.Id, side, pos);
            engine.ScheduleEffect(def.Delay, new PendingEffect
            {
                GadgetId = def.Id,
                Phase = FirebombEffect.PhaseSpawnHazard,
                Side = side,
                Position = pos,
            });
        }

        /// <summary>A wave_3 tsunami rolling from the caster's castle.</summary>
        private static void FireWave(GameEngine engine, int side)
        {
            var def = engine.GetGadgetDefinition("wave_3");
            if (def == null) return;

            // Same spawn points WaveEffect uses.
            int pos = side == 1 ? -100 : GameEngine.MAP_WIDTH + 100;

            engine.TriggerGadgetAnimation(def.Id, side, pos);
            engine._state.Hazards.Add(new WaveHazard
            {
                Type = "Wave",
                SourceGadgetId = def.Id,
                Side = side,
                Position = pos,
                Width = def.Radius * 2,
                ExpiresAtTick = (int)engine._state.CurrentTick + def.HazardDuration
            });
        }

        /// <summary>A nuke_3 on the enemy castle. Damages BOTH castles, as nukes always do.</summary>
        private static void FireNuke(GameEngine engine, int side)
        {
            var def = engine.GetGadgetDefinition("nuke_3");
            if (def == null) return;

            int pos = side == 1 ? GameEngine.MAP_WIDTH : 0;

            engine.TriggerGadgetAnimation(def.Id, side, pos);
            engine.ScheduleEffect(def.Delay, new PendingEffect
            {
                GadgetId = def.Id,
                Phase = NukeEffect.PhaseDetonate,
                Side = side,
                Position = pos,
            });
        }

        // ── Ally buffs ───────────────────────────────────────────────────────────

        /// <summary>
        /// Sweeps every allied unit onto the shield with an ABSOLUTE expiry rather than a
        /// rolling one. That is what makes a unit bought nine seconds into the window lose
        /// its shield at the same instant as one that was on the field when it started —
        /// and, when both players have cast, at the same instant as the enemy's units.
        /// </summary>
        private static void ApplyShield(GameEngine engine, int side, long until)
        {
            var divine = engine.GetGadgetDefinition("divine_3");
            if (divine == null) return;

            RefreshAllyStatus(engine, side, "Invulnerable", divine.BaseValue, until);
        }

        /// <summary>
        /// Fixes the tick this side's shield expires on and writes it to the player.
        ///
        /// THE SECOND ARMAGEDDON DOES NOT GET ITS OWN TEN SECONDS. If the enemy's shield is
        /// still live when this one is bought, this one ends on the enemy's tick instead.
        /// Without that rule, reaching ARMAGEDDON second was the WINNING move: survive the
        /// first player's cascade for a few seconds, buy your own, and your shield outlasts
        /// theirs by exactly the gap between the two purchases — so their castle is exposed
        /// to your nukes while yours is still immune. Ending together puts both castles back
        /// on the board at the same moment and lets the two cascades decide it.
        ///
        /// An enemy shield that has ALREADY expired is not inherited, so a player who
        /// reaches ARMAGEDDON long after the first cascade burned out still gets a full
        /// window.
        /// </summary>
        private static void ClaimShieldWindow(GameEngine engine, int side, int animationLeadIn)
        {
            var self  = side == 1 ? engine._state.Player1 : engine._state.Player2;
            var enemy = side == 1 ? engine._state.Player2 : engine._state.Player1;

            long naturalEnd = engine._state.CurrentTick + animationLeadIn + ShieldDuration;

            self.ArmageddonShieldUntilTick =
                enemy.ArmageddonShieldUntilTick > engine._state.CurrentTick
                    ? enemy.ArmageddonShieldUntilTick
                    : naturalEnd;
        }

        private static void ApplyBuffs(GameEngine engine, int side)
        {
            var heal  = engine.GetGadgetDefinition("heal_3");
            var speed = engine.GetGadgetDefinition("speed_3");
            var rage  = engine.GetGadgetDefinition("rage_3");

            long expires = engine._state.CurrentTick + BuffDuration;

            // Heal is stored negative because ProcessStatuses runs it through ApplyDamage.
            if (heal  != null) RefreshAllyStatus(engine, side, "Heal",  -1f * heal.BaseValue, expires);
            if (speed != null) RefreshAllyStatus(engine, side, "Speed", speed.BaseValue, expires);
            if (rage  != null) RefreshAllyStatus(engine, side, "Rage",  rage.BaseValue, expires);
        }

        /// <summary>
        /// Extends this cascade's own status of that name on every ally, adding it only to
        /// units that do not have it yet.
        ///
        /// REFRESHING RATHER THAN RE-ADDING IS NOT OPTIONAL. MoveAndFight combines these
        /// multiplicatively (`speedMod *= status.Value`, `dmgMod *= status.Value`), so
        /// blindly re-adding rage_3 once a second would give a 10^n damage multiplier and
        /// overflow within seconds, and Heal ticks once per copy so it would compound the
        /// same way. Matching on SourceGadgetId keeps this separate from a rage_3 the
        /// player casts themselves, which is still allowed to stack on top as it always has.
        /// </summary>
        private static void RefreshAllyStatus(GameEngine engine, int side, string statusName, float value, long expires)
        {
            foreach (var ally in engine._state.Units)
            {
                if (ally.Side != side) continue;

                var existing = ally.Statuses.FirstOrDefault(
                    s => s.Name == statusName && s.SourceGadgetId == GadgetId);

                if (existing != null)
                {
                    existing.ExpiresAtTick = expires;
                    existing.Value = value;
                }
                else
                {
                    ally.Statuses.Add(new ActiveStatus(statusName, expires, value, side, GadgetId));
                }
            }
        }
    }
}
