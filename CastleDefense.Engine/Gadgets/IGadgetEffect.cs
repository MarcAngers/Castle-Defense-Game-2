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
    }
}
