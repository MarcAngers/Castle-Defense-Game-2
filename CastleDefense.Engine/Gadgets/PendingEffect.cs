using System;

namespace CastleDefense.Engine.Gadgets
{
    /// <summary>
    /// A delayed gadget effect, represented as DATA rather than as a closure.
    ///
    /// WHY THIS EXISTS: GameEngine used to hold pending effects as `List&lt;Action&gt;` —
    /// lambdas created at 14 gadget-effect callsites, each capturing `engine`, the
    /// GadgetDefinition, and locals. Closures cannot be deep-copied, so a cloned engine
    /// would share its pending effects with the original and silently mutate the
    /// original's state when they fired. That single fact is what made the engine
    /// un-cloneable, and therefore blocked any form of search or lookahead — the
    /// technique this project needs to reach superhuman play.
    ///
    /// A PendingEffect contains only primitives, a string and a Guid, so copying one is
    /// a memberwise copy and copying the list is a shallow List copy. It is also
    /// serialisable, which gives deterministic replay and save/load for free.
    ///
    /// The GadgetDefinition itself does NOT need to be copied: definitions (and the
    /// IGadgetEffect instances hanging off them) are process-wide singletons built once
    /// by GameDataManager, and are immutable during play. GadgetId is enough to find the
    /// right one again, and because each upgrade level is a separate definition with its
    /// own id ("snipe", "snipe_2", "snipe_3"), the id also pins the level.
    /// </summary>
    public struct PendingEffect
    {
        /// <summary>Absolute tick at which this fires. Set by GameEngine.ScheduleEffect.</summary>
        public int ExecuteAtTick;

        /// <summary>Which gadget definition owns this effect — resolves back to the IGadgetEffect.</summary>
        public string GadgetId;

        /// <summary>
        /// Which delayed step of that gadget this is. Several gadgets schedule more than
        /// one distinct thing (Freeze applies the freeze, then later a slow; Meteor
        /// schedules an animation and a damage pulse), so the effect needs to know which
        /// one is firing. Numbering is private to each effect class — there is
        /// deliberately no global enum, so adding a phase touches exactly one file.
        /// </summary>
        public int Phase;

        public int Side;

        /// <summary>
        /// Impact position. Note this is not always the position the player clicked —
        /// Meteor, for instance, stores each individual meteor's scattered drop point.
        /// </summary>
        public int Position;

        /// <summary>
        /// Target unit, for effects that lock onto one (Snipe). Stored as the unit's
        /// stable InstanceId rather than a Unit reference — a reference would point into
        /// the wrong game after a clone, which is precisely the bug this type exists to
        /// prevent. Guid.Empty when unused.
        /// </summary>
        public Guid TargetId;

        /// <summary>Unit definition id, for effects that spawn (Reinforcements). Null when unused.</summary>
        public string UnitId;
    }
}
