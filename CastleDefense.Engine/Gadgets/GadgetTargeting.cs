using System;
using System.Collections.Generic;
using System.Linq;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Gadgets
{
    /// <summary>
    /// WHERE A GADGET SHOULD LAND. One implementation, shared by everything that aims.
    ///
    /// WHY THIS EXISTS (Marc's report on game EE51FF, 2026-08-21: "the bot used the nuke on
    /// its own units... we have done work previously to prevent this, but it looks like it
    /// didn't work correctly"). The work was not broken -- it was BYPASSED, because there
    /// were two different ways to cast a gadget and only one of them aimed:
    ///
    ///   * HeuristicBot.TryUseOffenseGadget computed a target and passed it to UseGadget.
    ///     All the friendly-fire machinery lived here.
    ///   * ApplyAction(side, 11..13) -- RolloutSearchBot's raw action, every ONNX/RL policy,
    ///     HumanCloneBot, and any harness driving the discrete action space -- routed to
    ///     UseGadget(..., -1) and a targeter that had no friendly-fire logic at all.
    ///
    /// Measured over all 8 v3 singleplayer replays, P2 = the deployed search bot
    /// (`--nuke-audit`, which tells the paths apart by comparing the recorded target against
    /// the auto-target):
    ///
    ///   path   casts   casts hitting own   own units hit   enemy units hit
    ///   AUTO      14                  12              78                42
    ///   heur      50                   0               0               285
    ///
    /// Twelve own-goals, all of them on the auto path; and the only two clean AUTO casts were
    /// the two where the bot had no units on the board to hit. That is not an occasional miss.
    ///
    /// THE OLD RULE WAS ACTIVELY ATTRACTED TO THE CASTER'S OWN ARMY. It aimed at the
    /// frontmost enemy plus a flat 300 -- which in a fight is the point where the two armies
    /// are colliding -- and when there were no enemies at all it fell back to "drop it at the
    /// enemy castle", clamped to 300, which is exactly where a winning bot's siege is
    /// standing. EE51FF t2521 is that case in its purest form: 0 enemy units on the board, 13
    /// of the bot's own, 7 caught in its own blast.
    ///
    /// TWO SEPARATE THINGS ARE FIXED HERE, and they are worth keeping distinct:
    ///
    ///   1. AIM. <see cref="FindBestAoeTarget"/> replaces the flat +300 offset. It scores
    ///      every candidate impact point by the Power it catches, weighted by proximity to
    ///      the caster's own castle, over positions PROJECTED forward by the gadget's own
    ///      deployment delay and clamped at the castles and walls that actually stop units.
    ///      This applies to every gadget, because a real lead beats a fixed offset for all
    ///      of them.
    ///   2. FRIENDLY FIRE. Only the three families whose damage does not filter by side --
    ///      nuke, firebomb, blackhole (see <see cref="IGadgetEffect.HarmsAllies"/>) -- get
    ///      the veto. Meteor and poison filter to enemies, goo heals allies, and wave only
    ///      pushes, so vetoing those would cost casts and buy nothing.
    ///
    /// HeuristicBot delegates to this file rather than keeping its own copy, so the two can
    /// never drift apart again -- which is the whole failure mode being repaired.
    ///
    /// ── WHAT IT DOES TO THE RECORDED GAMES ───────────────────────────────────────────
    /// Re-asking the same 8 v3 replays what this targeter would have done on each board
    /// (`--nuke-audit`, whose NOW column is exactly this counterfactual):
    ///
    ///   path   casts | as played: hit-own  own  enemy | now: refused  hit-own  own  enemy
    ///   AUTO      14 |                12    78     42 |          12        0     0      7
    ///   heur      50 |                 0     0    285 |           2        0     0    283
    ///
    /// All twelve own-goals refused, zero own units hit, and both previously-clean AUTO casts
    /// preserved at the identical aim point. The two `heur` refusals are counterfactual only
    /// (those casts never took this path); they are boards where ClampToMap pulls the aim into
    /// our own line, where refusing is the right answer.
    ///
    /// ── WHAT IT COSTS ────────────────────────────────────────────────────────────────
    /// Measured against CD_LEGACY_AIM=1 inside ONE binary (see UseLegacyAutoTarget).
    ///
    /// LADDER, 250 setups x 2 sides, both modes, seed 12345: BYTE-IDENTICAL on every rung
    /// except HumanClone -- DoNothing, Tier1Spam, Tier4Spam, Investor, BalancedHuman and the
    /// HeuristicBot mirror all reproduce exactly. HumanClone is the only ladder opponent that
    /// drives the discrete action space including gadget ids, and it won 2 more games of 500
    /// with the new aim (494/6 -> 492/8 against the contender), i.e. very slightly stronger.
    /// The right sign, and far too small to call.
    ///
    /// SEARCH-TEST, 150 games paired, seed 4242: 27.3% -> 28.0%, one game in 150. FLAT. Read
    /// that with its caveat: search-test's overrideMargin DEFAULTS TO 0.01 and its
    /// reactiveOpeningGate to false, where the deployed bot uses 0.10 and true -- which is why
    /// the absolute rate sits at 27% against the flagship's ~75-82%. Both arms shared one
    /// binary and one config so the COMPARISON is sound, but it describes a search bot that is
    /// not the one that ships. A run at `--margin 0.10 --reactive-opening` was started and
    /// deliberately abandoned: the ladder evidence already bounds the risk, and Marc's call
    /// was that the compute was not worth it for a change whose downside is bounded by
    /// construction (it can only ever REMOVE a cast, never add one).
    /// </summary>
    public static class GadgetTargeting
    {
        public static bool IsWall(Unit u) => u.DefinitionId.StartsWith("wall");

        /// <summary>
        /// The legal impact window: at least 100 away from either castle, as the old
        /// auto-target rule required.
        ///
        /// APPLIED BEFORE THE FRIENDLY-FIRE TEST, NOT AFTER, and that ordering is the whole
        /// point. The first cut of the 2026-08-21 fix vetoed the raw aim point and let
        /// GameEngine clamp the survivor -- so an approved point below 300 was then MOVED to
        /// 300, which is inside our own siege, and 3225A7 t4906 still took 9 of its own units
        /// with it. A clamp that runs after the check invalidates the check.
        /// </summary>
        public static int ClampToMap(int position)
            => Math.Max(300, Math.Min(GameEngine.MAP_WIDTH - 300, position));

        /// <summary>
        /// How much a unit is worth as a blast target. Damage output dominates, with a
        /// smaller contribution from the effective HP that would be removed.
        /// </summary>
        public static float Power(Unit u)
        {
            float aps = u.AttackSpeed > 0 ? u.AttackSpeed : 0.3f;
            return u.Damage * aps + u.CurrentHealth * 0.04f + u.CurrentShield * 0.04f;
        }

        /// <summary>The one wall each side may have up, for ProjectedPosition's blocker clamp.</summary>
        public static (Unit side1Wall, Unit side2Wall) FindWalls(GameState state)
        {
            Unit s1 = null, s2 = null;
            foreach (var u in state.Units)
            {
                if (!IsWall(u)) continue;
                if (u.Side == 1) s1 = u; else s2 = u;
            }
            return (s1, s2);
        }

        /// <summary>
        /// Where a unit will actually BE when the gadget lands, `leadTicks` from now.
        ///
        /// CurrentSpeed is already 0 whenever a unit is engaged in combat or attacking a
        /// castle (GameEngine sets it that way every tick), so a stationary/fighting unit is
        /// correctly not led at all -- only units still actively marching get projected.
        ///
        /// A UNIT CANNOT WALK PAST THE CASTLE IT IS ATTACKING -- it stops on contact and
        /// starts hitting it. Extrapolating raw speed over the deployment delay ignores that,
        /// and Marc caught the consequence in live play: against a fast incoming wave already
        /// near our castle the lead put the aim point BEHIND our own castle, so the units
        /// crashed into it and dealt their damage while the gadget landed on empty ground.
        /// His words: "there's never any enemy units back there, so it's never a good idea to
        /// do that." A WALL STOPS UNITS TOO, so leading one through a wall is the same error
        /// further out.
        ///
        /// Clamped here rather than on the final target so the fix also reaches
        /// FindBestAoeTarget's CLUSTER SCORING, which compares projected positions to each
        /// other -- several units projected past the castle would otherwise score as a
        /// phantom cluster at a position nothing can occupy.
        /// </summary>
        public static float ProjectedPosition(Unit u, int leadTicks, bool clampToCastle,
                                              Unit side1Wall, Unit side2Wall)
        {
            if (leadTicks <= 0 || u.CurrentSpeed <= 0) return u.Position;
            float direction = u.Side == 1 ? 1f : -1f;
            float projected = u.Position + u.CurrentSpeed * leadTicks * direction;

            if (!clampToCastle) return projected;

            // Stop lines are exactly GetDistanceToEnemyCastle's: a side-1 unit halts when
            // Position + Width reaches MAP_WIDTH - 200, a side-2 unit when Position reaches 200.
            if (u.Side == 1)
            {
                float limit = GameEngine.MAP_WIDTH - 200 - u.Width;
                if (side2Wall != null && side2Wall.Position > u.Position)
                    limit = Math.Min(limit, side2Wall.Position - u.Width);
                return Math.Min(projected, limit);
            }
            else
            {
                float limit = 200f;
                if (side1Wall != null && side1Wall.Position < u.Position)
                    limit = Math.Max(limit, side1Wall.Position + side1Wall.Width);
                return Math.Max(projected, limit);
            }
        }

        /// <summary>
        /// Best impact point against `targets`: the projected position catching the most
        /// Power within `radius`, weighted toward whatever is closest to our own castle
        /// (or furthest, for the gadgets that want to strike the enemy's staging ground).
        /// </summary>
        public static int? FindBestAoeTarget(List<Unit> targets, int radius, int myCastlePos,
                                             int leadTicks, bool preferFarFromMyCastle,
                                             bool clampToCastle, Unit side1Wall, Unit side2Wall)
        {
            if (targets.Count == 0) return null;

            int bestPos = 0;
            float bestScore = -1f;
            foreach (var candidate in targets)
            {
                float candidatePos = ProjectedPosition(candidate, leadTicks, clampToCastle, side1Wall, side2Wall);
                float score = 0f;
                foreach (var other in targets)
                {
                    if (Math.Abs(ProjectedPosition(other, leadTicks, clampToCastle, side1Wall, side2Wall) - candidatePos) <= radius)
                        score += Power(other);
                }
                float distToMyCastle = Math.Abs(candidatePos - myCastlePos);
                float threatWeight = preferFarFromMyCastle
                    ? Math.Max(0.15f, distToMyCastle / 1200f)
                    : Math.Max(0.15f, 1200f / (distToMyCastle + 250f));
                score *= threatWeight;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPos = (int)candidatePos;
                }
            }
            return bestPos;
        }

        public static double UnitCost(GameEngine engine, Unit u)
        {
            var owner = u.Side == 1 ? engine._state.Player1 : engine._state.Player2;
            var roster = GameDataManager.Teams.FirstOrDefault(t => t.Color == owner.Team)?.Roster;
            return roster?.FirstOrDefault(d => d.Id == u.DefinitionId)?.Cost ?? 0;
        }

        /// <summary>Total $ value of `units` within `radius` of `position`.</summary>
        public static double ValueNear(GameEngine engine, List<Unit> units, float position, int radius)
            => units.Where(u => Math.Abs(u.Position - position) <= radius).Sum(u => UnitCost(engine, u));

        /// <summary>
        /// Friendly-fire TRADE test for the gadgets that damage both sides in the blast.
        /// True when the cast is worth taking: the enemy value caught is at least `margin`
        /// times our own. A margin of 0 or less degenerates to the strict "no ally in
        /// radius" rule, which is what the shipped HeuristicBot uses.
        /// </summary>
        public static bool AoeTradeOk(GameEngine engine, List<Unit> myUnits, List<Unit> enemyUnits,
                                      float target, int radius, double margin)
        {
            if (margin <= 0)
                return !myUnits.Any(u => Math.Abs(u.Position - target) <= radius);

            double allyValue = ValueNear(engine, myUnits, target, radius);
            if (allyValue <= 0) return true;                       // nothing of ours caught
            return ValueNear(engine, enemyUnits, target, radius) >= allyValue * margin;
        }

        /// <summary>
        /// The aim point for a cast that did not specify one — GameEngine.UseGadget's `-1`
        /// sentinel. Returns null to mean DO NOT CAST: there is no target worth a blast, or
        /// every target worth a blast would take our own army with it.
        ///
        /// Refusing is deliberate and is the safe direction. UseGadget returns false, so no
        /// money is spent and no cooldown starts — the caller has simply wasted a decision,
        /// which is strictly better than deleting its own army. It is also self-correcting
        /// for search: a candidate action that no-ops scores exactly like waiting, so the
        /// override margin keeps the prior and the bot stops proposing it.
        /// </summary>
        /// <summary>
        /// Restores the pre-2026-08-21 auto-targeter, so the fix can be measured against its
        /// own absence inside ONE binary rather than across two builds. Set from the
        /// CD_LEGACY_AIM environment variable, which every harness inherits without needing
        /// its own flag. Measurement only -- never set in the game.
        /// </summary>
        public static bool UseLegacyAutoTarget { get; set; }
            = Environment.GetEnvironmentVariable("CD_LEGACY_AIM") == "1";

        /// <summary>The old rule, kept verbatim as the control arm. See UseLegacyAutoTarget.</summary>
        private static int LegacyAutoTarget(GameEngine engine, int side)
        {
            var enemies = engine._state.Units.Where(u => u.Side != side).ToList();
            int position;
            if (enemies.Count > 0)
                position = side == 1
                    ? (int)enemies.OrderBy(e => e.Position).First().Position - 300
                    : (int)enemies.OrderByDescending(e => e.Position).First().Position + 300;
            else
                position = side == 1 ? GameEngine.MAP_WIDTH : 0;
            return ClampToMap(position);
        }

        public static int? AutoTarget(GameEngine engine, int side, GadgetDefinition def)
        {
            if (UseLegacyAutoTarget) return LegacyAutoTarget(engine, side);

            var state = engine._state;
            int myCastlePos = side == 1 ? 200 : GameEngine.MAP_WIDTH - 200;
            bool harmsAllies = def.GadgetEffect?.HarmsAllies ?? false;

            var enemies = state.Units.Where(u => u.Side != side).ToList();
            var mine = state.Units.Where(u => u.Side == side).ToList();

            // ALLY-ANCHORED gadgets (wall_2/3, goo) go where OUR army is, exactly as
            // HeuristicBot places them: the average position of our own units, or our castle
            // when we have none. Aiming these at the best enemy cluster would be a
            // regression, not a fix -- a wall in the middle of the enemy line tanks nothing.
            if ((def.GadgetEffect?.Aim ?? GadgetAim.Enemy) == GadgetAim.Ally)
                return ClampToMap(mine.Count > 0 ? (int)mine.Average(u => u.Position) : myCastlePos);

            // Walls are near-immune value sinks (Marc: "a freeze ray or a Nuke does basically
            // nothing to it"), so a lone enemy wall must not read as a legitimate cluster.
            var aimable = enemies.Where(u => !IsWall(u)).ToList();

            if (aimable.Count == 0)
            {
                // NOTHING TO HIT. The old fallback dropped it on the enemy castle, which is
                // where our own siege stands -- the EE51FF t2521 own-goal exactly. For an
                // ally-harming gadget that is the single worst place on the map, so refuse.
                // For the rest, keep the old fallback: it costs nothing and some gadgets
                // (wave, meteor) still want to be pointed downfield.
                if (harmsAllies) return null;
                return ClampToMap(side == 1 ? GameEngine.MAP_WIDTH : 0);
            }

            var (s1Wall, s2Wall) = FindWalls(state);
            int radius = def.Radius;

            if (!harmsAllies)
            {
                int? aim = FindBestAoeTarget(aimable, radius, myCastlePos, def.Delay,
                                             preferFarFromMyCastle: false, clampToCastle: true,
                                             s1Wall, s2Wall);
                return aim.HasValue ? ClampToMap(aim.Value) : (int?)null;
            }

            // ALLY-HARMING: score every candidate impact point as FindBestAoeTarget does, but
            // walk them best-first and take the best one that does not catch our own units.
            // RETARGETING rather than refusing outright is HeuristicBot's own answer for
            // firebomb ("still a valid burn, and this gadget wants to be cast"), and it
            // generalises: the densest cluster is often reachable from a slightly different
            // point that our line is not standing on.
            var scored = new List<(int pos, float score)>();
            foreach (var candidate in aimable)
            {
                float pos = ProjectedPosition(candidate, def.Delay, true, s1Wall, s2Wall);
                float score = 0f;
                foreach (var other in aimable)
                {
                    if (Math.Abs(ProjectedPosition(other, def.Delay, true, s1Wall, s2Wall) - pos) <= radius)
                        score += Power(other);
                }
                float distToMyCastle = Math.Abs(pos - myCastlePos);
                score *= Math.Max(0.15f, 1200f / (distToMyCastle + 250f));
                // Clamp HERE, so the position the veto below judges is the position the
                // blast will actually land on. See ClampToMap.
                scored.Add((ClampToMap((int)pos), score));
            }

            foreach (var (pos, _) in scored.OrderByDescending(c => c.score))
            {
                // Strict no-ally rule (margin 0), matching the shipped HeuristicBot default.
                if (AoeTradeOk(engine, mine, enemies, pos, radius, 0)) return pos;
            }

            // Every worthwhile aim point would catch our own army. Don't cast.
            return null;
        }
    }
}
