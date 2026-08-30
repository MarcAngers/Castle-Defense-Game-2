using System;
using System.Collections.Generic;
using System.Text;

namespace CastleDefense.Engine.Gadgets
{
    public interface IGadgetEffect
    {
        void Execute(GameEngine engine, int side, int position);

        /// <summary>
        /// Runs a delayed phase previously queued with GameEngine.ScheduleEffect.
        ///
        /// Default no-op so the effects that have no delayed phase (Heal, Speed, Rage,
        /// Wall, Wave) don't have to implement it. Effects with delayed phases override
        /// this and switch on <see cref="PendingEffect.Phase"/>.
        ///
        /// The implementing class already holds its own GadgetDefinition from its
        /// constructor, so it does not need to re-resolve one — `e` carries only the
        /// per-invocation data (side, position, target, phase).
        /// </summary>
        void ExecuteScheduled(GameEngine engine, in PendingEffect e) { }

        /// <summary>
        /// How much CASTLE damage this ALREADY-QUEUED phase will deal to
        /// <paramref name="side"/> when it fires, without firing it.
        ///
        /// Exists so a bot can see a detonation coming and react inside the delay window
        /// (GameEngine.IncomingCastleDamage). Only nuke answers non-zero today; the
        /// default no-op keeps every other effect out of it, and puts the knowledge of
        /// "this gadget hurts a castle, by this much" in the same file as the code that
        /// actually applies it. Any future delayed castle-damage gadget must override
        /// this too or it will be invisible to the bot.
        /// </summary>
        int PendingCastleDamage(in PendingEffect e, int side) => 0;

        /// <summary>
        /// True when this gadget's damage does NOT filter by side, i.e. it hurts the caster's
        /// own units as readily as the enemy's. Drives the friendly-fire veto in
        /// <see cref="GadgetTargeting.AutoTarget"/>.
        ///
        /// Exactly three families answer true -- nuke (NukeEffect loops every unit in radius),
        /// firebomb (FireHazard has no side check) and blackhole (BlackholeHazard likewise).
        /// Meteor and poison filter to `u.Side != side`, goo heals its own side, and wave only
        /// pushes, so those are safe to drop on a friendly and must NOT be vetoed -- doing so
        /// would refuse good casts for nothing.
        ///
        /// Kept next to the code that applies the damage, for the same reason
        /// PendingCastleDamage is: a list of gadget ids somewhere else would drift the first
        /// time an effect's side filter changed.
        /// </summary>
        bool HarmsAllies => false;

        /// <summary>
        /// Whose line this gadget wants to be dropped on when the caster did not pick a
        /// point. Nearly everything aims at the enemy, so that is the default; the
        /// exceptions are the gadgets that do something FOR your own army where it stands.
        ///
        /// This exists because "aim at the best enemy cluster" is only correct for gadgets
        /// pointed at the enemy. wall_2 and wall_3 are TARGETED, and HeuristicBot places them
        /// at the average position of its own units -- a wall dropped into the middle of the
        /// enemy army tanks nothing. Goo is the same shape (it heals allies and only
        /// incidentally slows enemies).
        ///
        /// Untargeted gadgets ignore the position entirely, so their value here is moot.
        /// </summary>
        GadgetAim Aim => GadgetAim.Enemy;
    }

    /// <summary>Which army an unaimed cast should be anchored on. See IGadgetEffect.Aim.</summary>
    public enum GadgetAim
    {
        Enemy,
        Ally,
    }
}
