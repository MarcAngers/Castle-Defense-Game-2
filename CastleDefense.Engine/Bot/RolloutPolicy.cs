using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Bot
{
    /// <summary>
    /// The seam that makes <see cref="RolloutSearchBot"/>'s rollout policy swappable.
    ///
    /// WHY THIS EXISTS (Probe A, 2026-08-07). Flat one-ply search cannot discover a line its
    /// rollout policy would not play out, so the rollout policy IS the ceiling. The standard
    /// route past that ceiling is to distil the search into a fast policy and feed it back in
    /// as the rollout policy — but that only compounds if a stronger rollout policy actually
    /// produces a stronger search, and on this game that is NOT obvious. The search's own
    /// documented failure mode is that the rollout policy "erases the very difference being
    /// measured" (see RolloutSearchBot's _overrideMargin comment): after the candidate action
    /// HeuristicBot drives our side too, so spawn-now and wait converge. A rollout policy that
    /// already saves could erase the save-macro's advantage in exactly the same way, and the
    /// search would lose signal on the one axis where it currently has any.
    ///
    /// So the premise gets measured before anything is built on it. This interface plus
    /// <see cref="SavingHeuristicBot"/> is the whole apparatus: a strictly stronger drop-in
    /// rollout policy, and a flag to turn it on.
    ///
    /// Deliberately narrow — one method, the same signature HeuristicBot already had, so
    /// HeuristicBot satisfies it without a wrapper and the default path is unchanged.
    /// </summary>
    public interface IRolloutPolicy
    {
        void Update(GameEngine engine);
    }

    /// <summary>
    /// MEASUREMENT ONLY. Which parts of gadget usage to suppress, split so the +5.8 points
    /// that suppressing the defence gadget bought (2026-08-11, n=600, p=0.0019) can be
    /// attributed between "search stopped choosing it" and "the bot stopped casting it".
    /// </summary>
    [System.Flags]
    public enum GadgetSuppression
    {
        None = 0,
        /// <summary>Remove action 12 from the search candidate list only.</summary>
        DefenceCandidate = 1,
        /// <summary>Stop the prior and the rollout policy casting the defence gadget.</summary>
        DefenceCasting = 2,
        /// <summary>Remove action 11 from the search candidate list only.</summary>
        OffenceCandidate = 4,
        /// <summary>Stop the prior and the rollout policy casting the offence gadget.</summary>
        OffenceCasting = 8,
    }

    /// <summary>Which policy drives a side inside a rollout.</summary>
    public enum RolloutPolicyKind
    {
        /// <summary>HeuristicBot. The committed behaviour — this is the control arm.</summary>
        Heuristic,

        /// <summary>
        /// HeuristicBot plus a scripted save-and-invest commitment. See
        /// <see cref="SavingHeuristicBot"/>.
        /// </summary>
        Saving,
    }

    /// <summary>
    /// HeuristicBot with the save-invest commitment written in as a rule instead of being
    /// discovered by search.
    ///
    /// WHAT IT APPROXIMATES. The shipped composite bot is HeuristicBot making ~92% of the
    /// moves with search stepping in on ~8% to commit to saving, and that composite beats
    /// HeuristicBot 75%. Nesting a real search inside the rollout is not affordable — search
    /// already costs 231x a plain game, so nesting it would cost 231x that again — but the
    /// BEHAVIOUR it injects is cheap to script, and behaviour is what a rollout consumes.
    ///
    /// THREE RULES, in priority order:
    ///   1. If the next investment is affordable, take it. HeuristicBot does not necessarily
    ///      invest the instant it can; the macro does, and that is its whole point.
    ///   2. Otherwise, if we are at least <see cref="_commitFraction"/> of the way to
    ///      affording it, we are COMMITTED — defend, but buy nothing offensive, so the bank
    ///      keeps growing. This is the "spend only what survival requires" half of the plan.
    ///   3. Otherwise play normal HeuristicBot.
    ///
    /// RULE 2 IS WHY IT DEFENDS RATHER THAN SIMPLY HOLDING THE PURSE. MacroSaveInvest does
    /// nothing at all on decisions where it cannot afford the investment, which is survivable
    /// only because search picks it ~10% of the time. A policy that drives a side for a whole
    /// horizon cannot skip defence or it just dies holding its bank — the same reasoning that
    /// made the Armageddon macro delegate to a defence-only HeuristicBot rather than idle.
    ///
    /// It reuses HeuristicBot's existing AttackGateMinInvestment = 99 profile for the
    /// defence-only mode, so "defend but never attack" is expressed with a setting that is
    /// already tested rather than a second code path.
    ///
    /// NOT TUNED, AND NOT MEANT TO BE. This exists to answer one binary question. If Probe A
    /// comes back positive the real policy is a distilled net, not this.
    /// </summary>
    public class SavingHeuristicBot : IRolloutPolicy
    {
        private readonly int _side;
        private readonly double _commitFraction;
        private readonly HeuristicBot _normal;
        private readonly HeuristicBot _defence;

        /// <summary>Defence-only profile: never opens the attack gate.</summary>
        private static readonly HeuristicBotSettings DefenceOnly =
            new HeuristicBotSettings { AttackGateMinInvestment = 99 };

        /// <param name="commitFraction">
        /// Share of the next InvestmentPrice that must already be banked before offensive
        /// spending stops. 1.0 is the mildest form (invest on sight, otherwise play normally);
        /// 0.0 is defence-only for the entire game.
        /// </param>
        public SavingHeuristicBot(int side, double commitFraction = 0.5)
        {
            _side = side;
            _commitFraction = commitFraction;
            _normal = new HeuristicBot(side);
            _defence = new HeuristicBot(side, DefenceOnly);
        }

        public void Update(GameEngine engine)
        {
            var state = engine._state;
            if (state.IsGameOver) return;
            var me = _side == 1 ? state.Player1 : state.Player2;

            // Rule 1. Guarded exactly as MacroSaveInvest guards it. Invest() itself refuses
            // past investment 8, so the ArmageddonUsed check only avoids a wasted call.
            if (!me.ArmageddonUsed && me.Money >= me.InvestmentPrice)
            {
                var mask = state.GetActionMask(_side);
                if (9 < mask.Length && mask[9] == 1) { engine.ApplyAction(_side, 9); return; }
            }

            // Rule 2. InvestmentPrice is strictly positive in every reachable state, but
            // guard the divide anyway rather than depend on that.
            double progress = me.InvestmentPrice > 0 ? me.Money / me.InvestmentPrice : 1.0;
            if (progress >= _commitFraction) { _defence.Update(engine); return; }

            // Rule 3.
            _normal.Update(engine);
        }
    }

    /// <summary>Builds the policy driving one side of a rollout.</summary>
    public static class RolloutPolicyFactory
    {
        public static IRolloutPolicy Make(RolloutPolicyKind kind, int side, double saveCommitFraction,
                                          bool disableDefenceGadget = false,
                                          bool disableOffenceGadget = false)
            => kind switch
            {
                RolloutPolicyKind.Saving => new SavingHeuristicBot(side, saveCommitFraction),
                // Measurement switch only. A rollout that casts a gadget the live arm has
                // been forbidden simulates the wrong continuation, so the suppression has
                // to reach in here as well as into the prior and the candidate list.
                _ when disableDefenceGadget || disableOffenceGadget =>
                    new HeuristicBot(side, new HeuristicBotSettings {
                        DisableDefenseGadget = disableDefenceGadget,
                        DisableOffenseGadget = disableOffenceGadget }),
                _ => new HeuristicBot(side),
            };
    }
}
