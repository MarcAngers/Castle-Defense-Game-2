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
    /// cascade would keep firing nukes into a finished game forever.
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

        // Ally buffs are re-applied on a cadence rather than cast once with a giant
        // duration, so units bought AFTER the cascade starts are swept in too. The status
        // lasts 3x the refresh interval purely so a missed refresh cannot make buffs
        // visibly flicker off.
        private const int BuffRefreshInterval = 30;
        private const int BuffDuration        = 90;

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
            engine.ScheduleEffect(divine?.Delay ?? 0, Effect(PhaseShieldFirst, side));
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
                    // The castle half of divine_3 fires ONCE, for its normal duration.
                    // Refreshing it forever would make the caster's castle permanently
                    // immune, including to their own phase-three nukes, and ARMAGEDDON is
                    // meant to stay capable of backfiring on a caster who is behind on HP.
                    var player = side == 1 ? engine._state.Player1 : engine._state.Player2;
                    var divine = engine.GetGadgetDefinition("divine_3");
                    if (divine != null)
                    {
                        player.IsInvulnerable = true;
                        player.InvulnerableUntilTick = engine._state.CurrentTick + divine.StatusDuration;
                    }
                    goto case PhaseShield;

                case PhaseShield:
                    ApplyShield(engine, side);
                    engine.ScheduleEffect(BuffRefreshInterval, Effect(PhaseShield, side));
                    break;

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

        private static void ApplyShield(GameEngine engine, int side)
        {
            var divine = engine.GetGadgetDefinition("divine_3");
            if (divine == null) return;

            RefreshAllyStatus(engine, side, "Invulnerable", divine.BaseValue);
        }

        private static void ApplyBuffs(GameEngine engine, int side)
        {
            var heal  = engine.GetGadgetDefinition("heal_3");
            var speed = engine.GetGadgetDefinition("speed_3");
            var rage  = engine.GetGadgetDefinition("rage_3");

            // Heal is stored negative because ProcessStatuses runs it through ApplyDamage.
            if (heal  != null) RefreshAllyStatus(engine, side, "Heal",  -1f * heal.BaseValue);
            if (speed != null) RefreshAllyStatus(engine, side, "Speed", speed.BaseValue);
            if (rage  != null) RefreshAllyStatus(engine, side, "Rage",  rage.BaseValue);
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
        private static void RefreshAllyStatus(GameEngine engine, int side, string statusName, float value)
        {
            long expires = engine._state.CurrentTick + BuffDuration;

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
