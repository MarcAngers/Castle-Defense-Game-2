using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Bot
{
    // RELOCATED 2026-07-28 from CastleDefense.BotArena so the web game can use it.
    // The benchmark harness (CastleDefense.BotArena.RolloutSearchOpponent) is now a thin
    // subclass of this, so there is exactly one implementation and the thing Marc plays
    // against is byte-for-byte the thing search-test measures.
    //
    // Only two changes from the BotArena original: it drives HeuristicBot directly rather
    // than through BotArena's HeuristicBotAdapter, and it does not implement
    // IArenaOpponent (that interface lives in the benchmark project). Update(GameEngine)
    // has the identical signature, so the subclass satisfies the interface implicitly.
    // <summary>
    // Flat one-ply rollout search.
    //
    // At each decision point: for every legal action, clone the engine, play that action,
    // then let HeuristicBot drive BOTH sides for a fixed horizon and score the resulting
    // position with GameState.EvaluateBoard(). Take the argmax.
    //
    // WHY NOT MCTS: both players act every tick, so this is a simultaneous-move game and
    // textbook MCTS does not apply cleanly. Flat rollout sidesteps the whole issue and is
    // the right first thing to measure — if one ply of lookahead over a strong rollout
    // policy doesn't beat that policy, deeper search is unlikely to rescue it.
    //
    // WHAT THIS INHERITS: the leaf evaluator was recalibrated on 2026-07-28 after its
    // previous weights were found to come from an unidentifiable fit. It now weights
    // castle HP 0.248 and income 0.752. Search amplifies whatever the evaluator believes,
    // so a miscalibrated evaluator produces a confidently bad bot — worth remembering if
    // the results look strange.
    //
    // COST: roughly (legal actions) x (horizon) engine ticks per decision. With ~6 legal
    // actions, a 300-tick horizon and a decision every 15 ticks, a ~4500-tick game costs
    // on the order of 500k simulated ticks — about 100x a plain game. Tune via the
    // constructor; `search-test` reports the real numbers.
    // </summary>
    public class RolloutSearchBot
    {
        private readonly int _side;
        private readonly int _decisionInterval;
        private readonly int _horizon;
        private readonly int _rolloutsPerAction;
        private readonly Random _rng;
        private long _next;

        // Instrumentation — throughput is the open question, so measure it by default.
        public long Decisions { get; private set; }

        // Backing fields, not auto-properties: rollouts now run in parallel, so these are
        // incremented from several threads and must go through Interlocked. A `count++` on
        // an auto-property would silently lose increments under contention.
        private long _rollouts;
        private long _simulatedTicks;
        public long Rollouts => System.Threading.Interlocked.Read(ref _rollouts);
        public long SimulatedTicks => System.Threading.Interlocked.Read(ref _simulatedTicks);

        // Latency, measured where it actually matters: one decision in one game.
        public double DecisionMsSum { get; private set; }
        public double SlowestDecisionMs { get; private set; }
        public long TruncatedDecisions { get; private set; }

        private readonly int _maxParallelism;

        // Diagnostics for "is search actually getting any signal?"
        //   ScoreSpreadSum / Decisions = average gap between the best and worst candidate.
        //   FlatDecisions             = decisions where every option scored identically,
        //                               i.e. the argmax was arbitrary.
        //   WaitDecisions             = how often doing nothing won, which is the tell for
        //                               an evaluator that punishes spending.
        public double ScoreSpreadSum { get; private set; }
        public long FlatDecisions { get; private set; }
        public long WaitDecisions { get; private set; }

        // HeuristicBot as a POLICY PRIOR. Search only overrides it when a candidate beats
        // simply-letting-HeuristicBot-play by more than this margin, measured in win
        // probability at the leaf.
        //
        // WHY: the first version replaced HeuristicBot entirely and lost 95% of games. Its
        // diagnostics showed 63% of decisions were exact ties, and because candidates are
        // scanned from action 0 upward with a strictly-greater test, every tie resolved to
        // action 0 — wait. So whenever search had no signal, the bot did nothing, and it
        // spent the game saving money it never lived to use.
        //
        // Ties are that common because the rollout policy erases the very difference being
        // measured: after the candidate action, HeuristicBot drives our side too, so
        // "spawn a defender now" and "wait" converge — in the wait line HeuristicBot just
        // spawns the defender a moment later.
        //
        // Deferring to the prior turns search into a refinement of the strongest agent
        // available rather than a replacement for it, so the floor is HeuristicBot's
        // strength instead of zero.
        private readonly double _overrideMargin;
        private readonly bool _usePrior;
        private readonly HeuristicBot _prior;

        // Rollout policy per side. NOTE these do not touch _prior: the PRIOR is what plays
        // the move when search declines to override, and changing it would confound "does a
        // better rollout policy help the search" with "does a better bot play better moves".
        // Probe A needs those two separated.
        private readonly RolloutPolicyKind _ownRolloutPolicy;
        private readonly RolloutPolicyKind _oppRolloutPolicy;
        private readonly double _saveCommitFraction;

        /// <summary>
        /// Separate, usually LOWER override margin for the two macros.
        ///
        /// WHY THE MARGINS SHOULD DIFFER (2026-08-04 sweep, n=200 per point, paired seeds).
        /// Raising the single shared margin monotonically improved play right up to the
        /// point where search stopped acting at all:
        ///
        ///     margin  overrides  earned invests (vs HeuristicBot)  win rate
        ///     0.00      57.4%      3.70  (-1.50)                    56.0%
        ///     0.01      26.4%      5.08  (-0.21)                    68.5%
        ///     0.10       8.0%      6.75  (+0.72)                    76.0%
        ///     0.20       3.9%      7.13  (+0.63)                    73.5%
        ///
        /// Two different things are being traded off against each other by one number.
        /// The search's PRIMITIVE suggestions are actively harmful — handing it more
        /// control costs 12.5 points and drives earned invests below HeuristicBot's own.
        /// Its MACRO suggestions are the entire source of strength: with the save-invest
        /// macro disabled the bot scores 44.0%, i.e. worse than not searching at all.
        ///
        /// CORRECTION (2026-08-07, Stage 0 decomposition, n=600 paired at seed 4242). The
        /// 44.0% above is real and reproduces to 43.8% — but it is a MARGIN 0.01 number, and
        /// nothing here ever said so. Both claims in the paragraph above are margin-specific,
        /// and at the SHIPPED margin the second one inverts:
        ///
        ///     margin   overrides   macros ON   macros OFF   macro worth
        ///      0.01    26.2/17.8%     68.5%       43.8%        +24.7
        ///      0.10     8.2/ 3.8%     74.8%       63.7%        +11.2   <- SHIPPED
        ///
        /// At the shipped margin, search with NO macros at all still beats HeuristicBot
        /// 63.7% against ~50% for HeuristicBot self-play. **The primitives are net positive
        /// here; they are harmful only when over-applied.** The right statement is not
        /// "primitives are harmful, macros are everything" but "search intervening rarely is
        /// good, search intervening often is bad, whichever kind of move it intervenes with".
        /// That is the same override-rate invariant Probe A found the evaluator and the
        /// rollout policy both obeying.
        ///
        /// WHAT THE MACRO'S 11.2 POINTS ACTUALLY ARE (Stage 0, and this is the useful part).
        /// Firing the IDENTICAL macro the identical number of times per game (25.78 vs 25.18)
        /// at RANDOM moments instead of search-chosen ones is worth NOTHING: 63.3% against
        /// the no-macro arm's 63.7%, McNemar p = 0.905. The behaviour is inert. All 11.2
        /// points are in WHICH decisions it fires on.
        ///
        /// And that choice is not a rule. Only 1.2% of decisions are ones where the
        /// investment is already affordable, while search fires the macro on 4.5% — so ~3/4
        /// of its firings are on decisions where it CANNOT yet buy, i.e. it is choosing when
        /// to HOLD money. Restricting random firing to affordable decisions recovers ~1 of
        /// the 11 points. Two hypotheses died here: the macro does not win by attacking less
        /// (it buys MORE units than the no-macro arm, 224.8 vs 210.5, at equal spend), and
        /// "invested more" is only a third of it (+0.81 vs +0.40 earned-invest differential).
        ///
        /// A single margin cannot express "fire the economic plan readily, but ignore
        /// tactical noise". This one can. Setting both equal reproduces the old behaviour
        /// exactly, so the previous configuration remains available.
        /// </summary>
        private readonly double _macroMargin;

        /// <summary>
        /// Below this investment count, search cannot propose a unit purchase; the prior
        /// owns that decision. 0 disables the guard (pre-2026-08-18 behaviour).
        /// </summary>
        private readonly int _earlySpendGuardMinInvest;

        /// <summary>
        /// Below this investment count, search may not propose a GADGET cast (11/12/13).
        /// 0 = off. Shipped at 1 for singleplayer: no gadget before the first investment.
        /// See the note at the candidate filter for the measurement behind it.
        /// </summary>
        private readonly int _earlyGadgetGuardMinInvest;

        // ── REACTIVE OPENING GATE (2026-08-20, Marc's design) ───────────────────────
        //
        // Until the opponent has put a unit on the board, search may not propose ANY
        // spending except investment, repair and the signature gadget. It is not allowed to
        // open the game by buying something.
        //
        // WHY THIS SHAPE RATHER THAN AN INVESTMENT COUNT. Two investment-count guards were
        // tried first and each just moved the leak: blocking gadgets before investment 1
        // made the bot open with a $12 Reinforcements -> $9 tier-3 unit instead, and
        // blocking units too made it buy the tier-3 the moment the first investment landed.
        // The count was never the thing that made those buys wrong. What made them wrong is
        // that there was nothing to buy them FOR -- an empty board cannot threaten anything,
        // so any purchase is pure economic loss with no defensive value. Gating on the
        // OPPONENT having committed encodes that directly, and it stops being restrictive
        // the moment a purchase could actually be justified.
        //
        // LATCHING, not momentary. Once the opponent has committed, the gate is open for the
        // rest of the game: this exists to shape the OPENING, not to make the bot permanently
        // reactive. A bot that could only ever spend while enemy units were on screen would
        // be unable to build a wave, which is a different and much worse failure.
        //
        // SIGNATURE GADGET IS EXEMPT because the signature slot is where the economy gadgets
        // live -- White's is `cash`, which converts money at a profit and is the opposite of
        // waste. NOTE this exemption is by SLOT, not by effect: a team whose signature is a
        // damage gadget gets a free early cast out of it. That is a known looseness, kept
        // because the alternative is hard-coding gadget ids here.
        //
        // Repair is exempt for the same reason it is exempt everywhere else: it is a
        // permanent castle upgrade, not a consumable.
        private readonly bool _reactiveOpeningGate;
        private bool _opponentCommitted;

        /// <summary>Whether the reactive opening gate has been released. Diagnostic.</summary>
        public bool OpponentCommitted => _opponentCommitted;

        /// <summary>
        /// Per-candidate override margin. Macros clear a different (normally lower) bar
        /// than primitive actions — see <see cref="_macroMargin"/>.
        /// </summary>
        private double MarginFor(int action, int investmentCount)
        {
            if (action == MacroPressAdvantage)
            {
                // INVESTMENT-GATED. Marc's read from play, corroborated by the replay
                // divergence analysis: investment 6 is exactly where a lethal tier-6 wave
                // first becomes affordable, and it is where his line and the bot's diverge
                // hardest (his mean tier 5.44 against the bot's 2.75, n=133). Before that a
                // blitz is premature -- he watched the bot throw tier-5 units away early --
                // and by investment 8 the game has usually moved to a different plan.
                //
                // This is a MARGIN, not a gate: the macro stays a candidate at every
                // investment level, it just needs much stronger evidence outside the
                // window. Early pressure is still reachable when the rollouts genuinely
                // favour it, which is what Marc asked for.
                return investmentCount >= _pressPeakMinInvest && investmentCount <= _pressPeakMaxInvest
                    ? _pressPeakMargin
                    : _pressOffPeakMargin;
            }
            if (action == MacroGadgetUpgrade) return _upgradeMargin;
            if (action == MacroArmageddon) return _armageddonMargin;
            return action == MacroSaveInvest ? _macroMargin : _overrideMargin;
        }

        // -- PRESS-ADVANTAGE (blitz) tunables ----------------------------------------
        // WHY THE MACRO WAS REBUILT (2026-08-06). The original bought the biggest
        // affordable unit every decision interval, forever. That is a TRICKLE, not a wave:
        // it spends as it earns, so it dribbles out one mid-tier unit at a time and never
        // masses anything able to break a defence. Marc plays the same strategy as
        // "bank ~$2,000, then commit 4-8 tier-6 units together with covering gadgets", and
        // the replay divergence data says the bot buys tier>=6 on 7.4% of shared decisions
        // against his 35.5%.
        //
        // This one is fixable where the gadget ladder was not, because the whole plan fits
        // INSIDE the search horizon: 6 x tier-6 is ~$2,000 and income at investment 6 is
        // ~$750/s, so the bank completes in under 3 seconds against a 10-second horizon.
        // The evaluator can see the payoff too -- army pressure is tier^2 x proximity^2,
        // so six tier-6 units advancing together score far above a trickle of tier-3s.
        private readonly int _pressWaveUnits;
        private readonly int _pressMinTier;
        private readonly double _pressPeakMargin;
        private readonly double _pressOffPeakMargin;
        private readonly int _pressPeakMinInvest;
        private readonly int _pressPeakMaxInvest;

        /// <summary>
        /// Opt-in switch for the bank-then-commit wave. DEFAULTS TO FALSE because the
        /// rebuild MEASURED WORSE against HeuristicBot, monotonically in how often it
        /// fires (n=200 each, paired seeds, margin 0.10):
        ///
        ///     press fires   win rate
        ///       0.4%          76.0%   <- committed trickle
        ///       0.9%          69.0%
        ///       2.1%          68.0%
        ///       2.8%          59.0%
        ///       4.5%          55.0%
        ///
        /// READ THIS BEFORE CONCLUDING THE BLITZ IS BAD. HeuristicBot plays SUSTAINED
        /// PRESSURE, and in Marc's rock/paper/scissors model sustained pressure is exactly
        /// what BEATS a blitz. So a blitz losing on this yardstick is what his model
        /// PREDICTS, not evidence against it -- search-test measures one opponent, and it
        /// happens to be the blitz's hard counter. Evaluating this fairly needs an
        /// economy/stall opponent (the ladder's Investor rung) where the model says the
        /// blitz should win. Until that measurement exists, this stays off.
        /// </summary>
        private readonly bool _pressWaveCommit;

        /// <summary>
        /// Highest tier at which a FULL wave is affordable right now, or 0 if none.
        /// Deliberately requires the whole wave up front rather than buying what fits --
        /// committing half a wave is the trickle behaviour this replaced.
        /// </summary>
        private static int AffordableWaveTier(GameState st, int side, int waveUnits, int minTier)
        {
            var me = side == 1 ? st.Player1 : st.Player2;
            var roster = GameDataManager.Teams.FirstOrDefault(t => t.Color == me.Team)?.Roster;
            if (roster == null) return 0;
            for (int tier = 8; tier >= minTier; tier--)
            {
                var def = roster.FirstOrDefault(u => u.Tier == tier && u.Cost > 0);
                if (def != null && me.Money >= (double)def.Cost * waveUnits) return tier;
            }
            return 0;
        }

        /// <summary>Commits the wave, then fires whatever gadgets are legal to cover it.</summary>
        private static void CommitWave(GameEngine engine, int side, int tier, int waveUnits)
        {
            for (int i = 0; i < waveUnits; i++)
            {
                var m = engine._state.GetActionMask(side);
                if (tier >= m.Length || m[tier] != 1) break;
                engine.ApplyAction(side, tier);
            }
            // Covering gadgets. WHICH gadget is right depends on the loadout, and that
            // judgement is deliberately not encoded here -- the rollout prices the whole
            // package, so a cover that wastes money just makes the macro score worse.
            foreach (int g in new[] { 11, 12, 13 })
            {
                var m = engine._state.GetActionMask(side);
                if (g < m.Length && m[g] == 1) engine.ApplyAction(side, g);
            }
        }

        /// <summary>
        /// Sentinel action id for the save-and-invest macro. Well clear of the 0-13
        /// primitive action space so it can never collide with a real action.
        /// </summary>
        public const int MacroSaveInvest = 100;

        /// <summary>
        /// Sentinel for the PRESS-ADVANTAGE macro: stop investing and convert accumulated
        /// economy into the biggest army the purse allows, every decision.
        ///
        /// Added 2026-07-28 to fix the failure the margin sweep exposed. The bot reliably
        /// executes the first two clauses of the winning plan — survive cheaply, save to
        /// invest — and builds a real economic lead (+0.53 invests over HeuristicBot at
        /// margin 0.03). It then never cashes it in: 33% of those games ran to the 600s
        /// tick cap and were awarded on castle HP rather than won outright.
        ///
        /// The cause is structural, not evaluative. HeuristicBot has no concept of pressing
        /// an advantage, and search overrides it on only ~3% of moves, almost all of them
        /// economic. Nobody was driving a counterattack. Saving needed a macro because it
        /// is a multi-decision commitment invisible to one-ply search; attacking needs one
        /// for exactly the same reason — a single expensive unit looks like wasted money,
        /// while six in a row is a breakthrough.
        /// </summary>
        public const int MacroPressAdvantage = 101;

        private readonly bool _useMacro;

        /// <summary>
        /// STAGE 0 DIAGNOSTIC (2026-08-07). Fires the save-invest macro on this share of
        /// decisions AT RANDOM, before any search runs. 0 disables it.
        ///
        /// WHY IT EXISTS. The save-invest macro is worth ~31 points (75.0% with it, 44.0%
        /// with `--no-macro`), and the standing explanation is that it builds an economic
        /// lead — search does out-invest HeuristicBot by +0.81 earned investments. But a
        /// SCRIPTED blanket saving rule with the same behaviour built a lead of only +0.06
        /// (Probe A, SavingHeuristicBot), so the behaviour alone does not reproduce the
        /// effect. That leaves two candidate explanations that imply opposite next moves:
        ///
        ///   SELECTION  — the value is in search choosing the ~4.5% of decisions where
        ///                banking pays. Then sharpening the option selection is the lever.
        ///   BEHAVIOUR  — the value is in the act of committing, and any firing pattern at
        ///                the right rate captures it. Then selection is irrelevant and the
        ///                lever is finding MORE behaviours worth committing to.
        ///
        /// Setting this to the macro's own measured firing rate WITH `--no-macro` (so the
        /// macro is not also selectable) reproduces the behaviour at the right rate with the
        /// selection deleted, which is the only construction I could find that separates the
        /// two. Setting it to 1.0 gives the saturation arm.
        ///
        /// Deliberately does NOT increment Overrides: that counter means "search overrode
        /// the prior", and a coin flip is not search. MacroChosen still counts it, so the
        /// realised firing rate stays measurable — which is the check that this arm actually
        /// matched the control's rate rather than merely being configured to.
        /// </summary>
        private readonly double _macroRandomRate;

        /// <summary>Restricts random macro firing to decisions where the investment is
        /// already affordable. See the gate in UpdateBlocking.</summary>
        private readonly bool _macroRandomAffordable;

        /// <summary>
        /// Separate stream from <see cref="_rng"/> on purpose: _rng supplies the rollouts'
        /// common random numbers, and drawing macro rolls from it would shift every
        /// subsequent rollout seed, so this arm would differ from its own control by more
        /// than the one thing under test.
        /// </summary>
        private readonly Random _macroRng;

        public long Overrides { get; private set; }

        /// <summary>
        /// DIAGNOSTIC ONLY. Snapshot of the last decision's candidate scores, the prior's
        /// score, and what was chosen -- so a trace can show WHY search overrode its prior
        /// rather than only that it did. Never read by the bot itself.
        /// </summary>
        /// <summary>Enables the snapshot below. Diagnostic only; leave false in the game.</summary>
        public static bool CaptureDecisionTrace { get; set; }

        public Dictionary<int, double> LastScores { get; private set; }
        public double LastPriorScore { get; private set; }
        public int LastChosenAction { get; private set; } = -1;
        public long MacroChosen { get; private set; }
        public long PressChosen { get; private set; }

        /// <summary>
        /// Process-wide switch to the old linear weighted-average evaluator, for A/B use.
        /// Static because it is a global experiment setting, not per-agent state — set it
        /// once before a run. Read-only during play, so parallel games are unaffected.
        /// </summary>
        public static bool UseLinearEval = false;

        /// <summary>
        /// Selects the 2026-08-05 refit weights (GameState.EvaluateBoardRefit) for the
        /// leaf. Same rationale as UseLinearEval: a process-wide A/B switch, set once
        /// before a run. Ignored when UseLinearEval is set.
        /// </summary>
        public static bool UseRefitEval = false;

        public RolloutSearchBot(int side, int decisionInterval = 15, int horizon = 300,
                                     int rolloutsPerAction = 1, int seed = 0,
                                     bool usePrior = true, double overrideMargin = 0.01,
                                     bool useMacro = true, bool usePressMacro = true,
                                     int maxDecisionMs = 0, int maxParallelism = 1,
                                     bool asyncDecisions = false, double macroMargin = double.NaN,
                                     int pressWaveUnits = 6, int pressMinTier = 6,
                                     double pressPeakMargin = double.NaN, double pressOffPeakMargin = double.NaN,
                                     int pressPeakMinInvest = 6, int pressPeakMaxInvest = 7,
                                     bool pressWaveCommit = false,
                                     bool useArmageddonMacro = true,
                                     double armageddonMargin = double.NaN,
                                     RolloutPolicyKind ownRolloutPolicy = RolloutPolicyKind.Heuristic,
                                     RolloutPolicyKind oppRolloutPolicy = RolloutPolicyKind.Heuristic,
                                     double saveCommitFraction = 0.5,
                                     double macroRandomRate = 0.0,
                                     bool macroRandomAffordable = false,
                                     bool deepMacroEval = false,
                                     int deepPlayouts = 2,
                                     int deepCommitTicks = 225,
                                     int staleTicks = 0,
                                     bool suppressDefenceGadget = false,
                                     GadgetSuppression gadgetSuppression = GadgetSuppression.None,
                                     bool useUpgradeMacro = false,
                                     double upgradeMargin = double.NaN,
                                     // 0 = off (committed behaviour). See the guard in Decide().
                                     int earlySpendGuardMinInvest = 0,
                                     // 0 = off. Ticks between searches while the board is
                                     // quiet. See DeferForQuietBoard.
                                     int idleDecisionInterval = 0,
                                     // 0 = off (shipped). See EconomyBlend.
                                     double economyBlend = 0.0,
                                     // 0 = off. Below this investment count search may not
                                     // propose a gadget cast. See the candidate filter.
                                     int earlyGadgetGuardMinInvest = 0,
                                     // See the ReactiveOpeningGate note by the field.
                                     bool reactiveOpeningGate = false)
        {
            // 0 = decide and act on the same tick, which is what every recorded
            // benchmark number describes. See UpdateStale.
            _earlySpendGuardMinInvest = Math.Max(0, earlySpendGuardMinInvest);
            _economyBlend = Math.Max(0.0, Math.Min(1.0, economyBlend));
            _earlyGadgetGuardMinInvest = Math.Max(0, earlyGadgetGuardMinInvest);
            _reactiveOpeningGate = reactiveOpeningGate;
            // Below the base interval it could never fire, so clamp it away rather than
            // silently accepting a setting that does nothing.
            _idleInterval = idleDecisionInterval > decisionInterval ? idleDecisionInterval : 0;
            _staleTicks = Math.Max(0, staleTicks);
            // suppressDefenceGadget is the original all-in-one switch, kept so existing
            // callers are unchanged; it means "candidate + casting", both for defence.
            _suppress = gadgetSuppression
                      | (suppressDefenceGadget
                         ? GadgetSuppression.DefenceCandidate | GadgetSuppression.DefenceCasting
                         : GadgetSuppression.None);
            // Defaults keep the shipped path byte-for-byte unchanged: deep evaluation is
            // opt-in, so the control arm in any A/B is the real committed bot.
            _deepMacroEval = deepMacroEval;
            _deepPlayouts = Math.Max(1, deepPlayouts);
            _deepCommitTicks = Math.Max(1, deepCommitTicks);
            _macroRandomRate = macroRandomRate;
            _macroRandomAffordable = macroRandomAffordable;
            _macroRng = new Random((seed == 0 ? 20260728 + side : seed) ^ 0x5A11);
            // Default to Heuristic on both sides so every existing caller — the web game, the
            // ladder, search-test's control arm — is byte-for-byte unaffected.
            _ownRolloutPolicy = ownRolloutPolicy;
            _oppRolloutPolicy = oppRolloutPolicy;
            _saveCommitFraction = saveCommitFraction;
            // MUST be assigned before the press margins below, which fall back to it.
            // It was not, and _macroMargin read as 0.0, silently making the press macro
            // fire on any improvement at all -- caught only because a "restored" run
            // scored 78.5% instead of reproducing the expected 76.0%.
            //
            // NaN means "not specified" -- fall back to the shared margin, which
            // reproduces the single-margin behaviour exactly. A sentinel is needed
            // because 0.0 is a meaningful setting (fire whenever it merely ties).
            _macroMargin = double.IsNaN(macroMargin) ? overrideMargin : macroMargin;
            _useArmageddonMacro = useArmageddonMacro;
            _useUpgradeMacro = useUpgradeMacro;
            _upgradeMargin = double.IsNaN(upgradeMargin) ? _macroMargin : upgradeMargin;
            _upgradePrior = new HeuristicBot(side, UpgradeFarming);
            _armageddonMargin = double.IsNaN(armageddonMargin) ? _macroMargin : armageddonMargin;
            _defencePrior = new HeuristicBot(side, DefenceOnly);
            _pressWaveUnits = Math.Max(1, pressWaveUnits);
            _pressMinTier = Math.Clamp(pressMinTier, 1, 8);
            // NaN => fall back to the shared macro margin, which reproduces the
            // committed single-margin behaviour exactly.
            _pressPeakMargin = double.IsNaN(pressPeakMargin) ? _macroMargin : pressPeakMargin;
            _pressOffPeakMargin = double.IsNaN(pressOffPeakMargin) ? _macroMargin : pressOffPeakMargin;
            _pressWaveCommit = pressWaveCommit;
            _pressPeakMinInvest = pressPeakMinInvest;
            _pressPeakMaxInvest = pressPeakMaxInvest;
            _asyncDecisions = asyncDecisions;
            _maxDecisionMs = maxDecisionMs;
            // Defaults to 1 so the BENCHMARK stays single-threaded per game — search-test
            // already parallelises across games, and nesting the two would oversubscribe
            // the box and make its latency numbers meaningless. The live game sets this
            // high instead, because there it is one game with a whole machine spare.
            _maxParallelism = Math.Max(1, maxParallelism);
            _side = side;
            _decisionInterval = decisionInterval;
            _horizon = horizon;
            _rolloutsPerAction = Math.Max(1, rolloutsPerAction);
            _rng = new Random(seed == 0 ? 20260728 + side : seed);
            _usePrior = usePrior;
            _overrideMargin = overrideMargin;
            _useMacro = useMacro;
            _usePressMacro = usePressMacro;
            // The prior must carry the same suppression as the candidate list, and so must
            // the rollout policy (RolloutPolicyFactory reads _suppressDefenceGadget too) --
            // otherwise the rollouts simulate a continuation that casts a gadget the live
            // arm never will, and every candidate is scored against the wrong future.
            _prior = (SuppressDefCasting || SuppressOffCasting)
                ? new HeuristicBot(side, new HeuristicBotSettings {
                    DisableDefenseGadget = SuppressDefCasting,
                    DisableOffenseGadget = SuppressOffCasting })
                : new HeuristicBot(side);
        }

        /// <summary>
        /// MEASUREMENT ONLY. Splits gadget suppression into its independent parts so the
        /// +5.8 points that `--no-def-gadget` bought can be attributed.
        ///
        /// That switch did THREE things at once: removed action 12 from the search
        /// candidate list, and disabled casting in the prior AND in the rollout policy.
        /// The override rate fell 8.2% -> 5.8% alongside the gain, and every lever measured
        /// on this project obeys "intervening less is better" — so the gain might be the
        /// CANDIDATE REMOVAL rather than anything about defensive play. These flags
        /// separate the two, and add the offence equivalents as a specificity control.
        /// </summary>
        private readonly GadgetSuppression _suppress;

        private bool SuppressDefCandidate => (_suppress & GadgetSuppression.DefenceCandidate) != 0;
        private bool SuppressDefCasting   => (_suppress & GadgetSuppression.DefenceCasting) != 0;
        private bool SuppressOffCandidate => (_suppress & GadgetSuppression.OffenceCandidate) != 0;
        private bool SuppressOffCasting   => (_suppress & GadgetSuppression.OffenceCasting) != 0;

        private readonly bool _usePressMacro;

        /// <summary>
        /// Sentinel for the ARMAGEDDON-COMMITMENT macro: race to investment 8 and spend
        /// only what survival requires on the way.
        ///
        /// WHY IT EXISTS (2026-08-06). The 3x3 strategy matrix found the game has a strict
        /// DOMINANCE ORDER, not the rock/paper/scissors Marc expected:
        ///
        ///                   Armageddon   Blitz   Pressure    row avg
        ///     Armageddon        55.0     71.7      53.3        60.0
        ///     Blitz             40.0     53.3      33.3        42.2
        ///     Pressure          46.7     65.0      45.0        52.2
        ///
        /// Armageddon wins every column, and it is the only one of the three the bot did
        /// not have. Marc's own play agrees: blitzing deliberately he converted ~20% of
        /// games, while twice turning a failed blitz into an Armageddon win.
        ///
        /// HOW THIS DIFFERS FROM MacroSaveInvest, which already existed: that macro banks
        /// for exactly ONE investment and then hands straight back to HeuristicBot, which
        /// resumes streaming units. It is one rung of the ladder, not a commitment to the
        /// race. This one keeps investing every time it can afford to, all the way to
        /// investment 8 (PlayerState.ArmageddonInvestmentCount), and never spends on
        /// offence in between.
        ///
        /// CRITICALLY, IT STILL DEFENDS. MacroSaveInvest does nothing on decisions where it
        /// cannot afford the investment, which is survivable only because it is chosen ~4%
        /// of the time. A macro meant to be chosen for most of a game cannot skip defence
        /// or it simply dies holding its bank -- so when it cannot invest, it delegates to
        /// a DEFENCE-ONLY HeuristicBot (AttackGateMinInvestment = 99 permanently closes the
        /// offensive spend block at HeuristicBot.cs:2284 while leaving the reactive
        /// in-danger path untouched). That is Marc's "spend only what you absolutely need
        /// to on defending and stalling", expressed with a setting that already exists.
        ///
        /// TUNING, AND A DELIBERATE DECISION NOT TO TAKE THE BEST NUMBER (2026-08-06).
        /// Measured against a control run in the SAME build (n=600 each, paired setups):
        ///
        ///     arma margin   fires   win rate
        ///       (off)        0.0%     73.8%
        ///        0.10        0.5%     ~73.5%   <- SHIPPED
        ///        0.00        7.3%     75.7%
        ///
        /// Margin 0.0 looked strongest at n=600: +1.9 points, winning 34 of the 57 games
        /// where the two arms disagree, but McNemar p = 0.185 -- positive everywhere,
        /// proven nowhere. It shipped at 0.10 anyway, on Marc's call, for PLAYABILITY:
        /// at 7.3% firing the bot spends much of the game in defence-only banking and
        /// becomes a markedly less interactive opponent.
        ///
        /// RE-MEASURED 2026-08-11 AT n=2400, AND THE EFFECT REVERSED. The 2026-08-10 goal
        /// statement drops playability as a constraint, so this was re-run with enough
        /// power to settle it (~4x the discordant pairs). Paired setups, seed 4242, no
        /// headstart, same build:
        ///
        ///     arma margin   fires   overrides   win rate
        ///        0.10        0.4%      7.9%      75.42%   <- SHIPPED
        ///        0.00        7.4%     14.5%      74.38%
        ///
        /// Paired delta **-1.04 points**, discordant b=129/c=104, McNemar p=0.116,
        /// 95% CI [-2.29, +0.20]. The +1.9 was small-sample noise; the interval now
        /// essentially excludes it. **Do not "fix" this to 0.0 -- there were never two
        /// points there to buy, and the playability choice cost nothing.**
        ///
        /// Note the override rate doubling alongside the loss: this is the same
        /// intervene-rarely invariant that the evaluator, the rollout policy and the deep
        /// estimator all obey.
        ///
        /// AND THE MACRO STILL DOES NOT MOVE THE ECONOMY. At 18x the firing rate, earned
        /// investments went 6.88 -> 6.86 and units bought 217.8 -> 198.2. This was logged
        /// as "one unexplained thing" at n=600 (6.82 -> 6.86); at n=2400 it is simply
        /// flat. Whatever this macro is, it is a play-defensively macro, not the commitment
        /// to the eight-investment race it is documented as. The strategy-matrix finding
        /// that motivated it still stands -- this implementation just is not delivering it.
        /// </summary>
        public const int MacroArmageddon = 102;

        private readonly bool _useArmageddonMacro;
        private readonly double _armageddonMargin;

        /// <summary>Defence-only profile: never opens the attack gate.</summary>
        private static readonly HeuristicBotSettings DefenceOnly =
            new HeuristicBotSettings { AttackGateMinInvestment = 99 };

        /// <summary>
        /// Persistent defence-only bot for the LIVE path. Persistent for the same reason
        /// _prior is: HeuristicBot carries HP-drain history and spend allowances, and a
        /// fresh instance each decision would reset them.
        /// </summary>
        private readonly HeuristicBot _defencePrior;

        public long ArmageddonChosen { get; private set; }

        /// <summary>
        /// Sentinel for the GADGET-UPGRADE macro: farm XP toward the next gadget tier.
        ///
        /// WHY IT IS A COMMITMENT AND NOT A ONE-SHOT CAST (2026-08-12). Gadget tiers are
        /// bought with USES -- 100 XP per cast, UpgradeCost per tier -- and a single cast
        /// buys 1/7th of a speed upgrade. **Gadget XP is not one of EvaluateBoard's six
        /// features**, so a one-shot XP cast is strictly invisible-to-negative at the leaf:
        /// search would see the money leave and nothing arrive, and would never choose it.
        /// The rollout has to run the whole commitment so the upgrade actually LANDS inside
        /// the horizon and the evaluator prices the stronger gadget's downstream effects.
        /// Exactly the reasoning behind MacroSaveInvest and the Armageddon macro.
        ///
        /// WHY THIS EXISTS AT ALL. As a RULE inside HeuristicBot the same behaviour is
        /// monotonically harmful: the k sweep (0.04/0.08/0.15/0.30) gave 87.3/86.7/86.5/83.4
        /// overall with earned investments tracking it exactly (6.94/6.82/6.56/6.11), so the
        /// optimum was the setting where it never fires -- gadget tiers were being bought
        /// with investments. But that is the third time on this project that a
        /// timing-sensitive action has failed as a threshold and worked when search picked
        /// the moment (save-invest macro, Stage 0; defensive casting, --sup def-cast +8.2).
        /// This tests whether the same holds here. If it does not, XP farming is closed.
        /// </summary>
        public const int MacroGadgetUpgrade = 103;

        private readonly bool _useUpgradeMacro;
        private readonly double _upgradeMargin;
        public long UpgradeChosen { get; private set; }

        /// <summary>HeuristicBot with XP farming ON, for the macro's own line only.</summary>
        private static readonly HeuristicBotSettings UpgradeFarming =
            new HeuristicBotSettings { GadgetUpgradeSpam = true };

        /// <summary>Persistent farming bot for the LIVE path, same reason as _prior.</summary>
        private readonly HeuristicBot _upgradePrior;

        /// <summary>
        /// Is any slot actually worth farming right now? Keeps the candidate out of the
        /// list entirely when it could only ever be a no-op, so it does not dilute the
        /// argmax or inflate the macro-selection diagnostics.
        /// </summary>
        private static bool UpgradeEligible(PlayerState me)
        {
            foreach (var d in new[] { me.OffensiveGadget, me.DefensiveGadget, me.SignatureGadget })
                if (d != null && !string.IsNullOrEmpty(d.NextTierId) && me.Money >= d.Cost) return true;
            return false;
        }

        /// <summary>
        /// Hard wall-clock ceiling for one decision, in milliseconds. 0 disables it.
        ///
        /// Added 2026-07-28 because cost is not constant across a game. Late on, incomes are
        /// high so nearly every action is affordable (candidates roughly triple) AND there
        /// are far more units on the board (each simulated tick costs more). Those multiply,
        /// so a decision that costs 20ms at minute one can cost seconds by minute five and
        /// the live game grinds to a halt.
        ///
        /// Candidates are evaluated in priority order and the search stops when the budget
        /// is spent, keeping the best answer found so far. Degrading to a slightly worse
        /// move is enormously better than stalling the game — and because the strategically
        /// decisive options are priced FIRST, what gets dropped under pressure is the
        /// marginal primitives, not the plan.
        /// </summary>
        private readonly int _maxDecisionMs;

        // ── Asynchronous decisions ────────────────────────────────────────────────────
        // Set for live play; left off for benchmarks, where blocking is harmless and
        // determinism matters more.
        //
        // WHY: however cheap the search is made, doing it on the game loop's thread freezes
        // the game for exactly as long as it takes. A tick is 33ms, so any budget above
        // that is a visible stutter no matter how much slack remains before the next
        // decision — three rounds of shrinking the budget did not fix that, because the
        // problem is structural rather than one of cost.
        //
        // Instead: snapshot the engine, think on a background thread, and apply the answer
        // whenever it is ready. The loop never waits. The bot acts on a state a fraction of
        // a second old, which is what a human does anyway, and it gets the full horizon
        // back rather than a truncated one.
        private readonly bool _asyncDecisions;
        private System.Threading.Tasks.Task<int> _pending;

        /// <summary>Sentinel meaning "the search found nothing better than the prior".</summary>
        // -- ECONOMY BLEND (2026-08-20) ---------------------------------------------
        //
        // WHY THIS IS NOT AN EVALUATOR TERM, which was the obvious first idea and is
        // structurally impossible. EvaluateBoard returns ONE P1-perspective scalar and side 2
        // scores 1-eval, so every term is antisymmetric by construction: add a component that
        // is high when BOTH players are rich and it raises P1's score while lowering P2's by
        // the same amount. "Both rich" is a SYMMETRIC property of a position and cannot be
        // expressed in an antisymmetric scalar at all.
        //
        // WHAT IT FIXES. --score-decomp on 7A385A tick 781 (zero enemy units on the board)
        // found spawnT4 scoring 0.6315 against wait 0.5223, and the leaf states explain it:
        // the wait line takes BOTH players to income 59.9 and investment 5, the spawn line
        // leaves BOTH on 19.7 and investment 4. Every evaluator component is a differential,
        // so 59.9-vs-59.9 and 19.7-vs-19.7 both score exactly 0.5, and dIncome between those
        // two lines is 0.0000. Mutually destructive play is free.
        //
        // The evaluator is not WRONG about that -- as a win probability both positions really
        // are near 50/50. It is being asked the wrong question. So this does not touch the
        // probability estimate; it blends an explicit PREFERENCE on top of it, on the search
        // side, where action ranking actually happens:
        //
        //     score = (1 - w) * winProb + w * ownProsperity
        //
        // ownProsperity is our OWN ABSOLUTE distance to the economic terminal state, which is
        // directional per side (so no symmetry problem) and bounded in [0,1] (so the score
        // stays a [0,1] quantity and the 0.10 override margin keeps its meaning).
        //
        // IT REUSES t_arma, AND THE IRONY IS THE POINT. TimeToArmageddonSeconds measured NULL
        // as an evaluator feature on 2026-08-19 -- but it was used there as
        // Sig(k*(log t2 - log t1)), a DIFFERENTIAL, which inherits exactly the blindness this
        // is trying to remove. The feature was right; the form was wrong. Used as an absolute
        // own-side quantity it is precisely the "am I actually getting rich" signal, and it is
        // continuous rather than the coarse integer InvestmentCount.
        //
        // TERMINAL ROLLOUTS ARE NOT BLENDED. A win is a win; diluting 1.0 toward a prosperity
        // score would let search prefer a rich loss to a poor win.
        //
        // ── MEASURED 2026-08-20: IT DOES NOT DO WHAT IT WAS BUILT TO DO. SHIPPED OFF. ──
        //
        // Swept 0 / 0.15 / 0.30 / 0.50 on 7A385A tick 781, 12 seeds, horizon 1600:
        //
        //   blend   spawnT3   prior     gap    gap/gap0   (1-w)
        //   0.00    0.6367   0.5223   0.1144     1.000    1.000
        //   0.15    0.5908   0.4948   0.0960     0.839    0.850
        //   0.30    0.5450   0.4673   0.0777     0.679    0.700
        //   0.50    0.4839   0.4306   0.0533     0.466    0.500
        //
        // It does flip the decision -- 12/12 overrides at w=0 become 0/12 at w=0.15. But the
        // RANKING IS IDENTICAL AT EVERY WEIGHT: spawnT3 stays top, and every candidate keeps
        // its place. All that happens is that gaps shrink, and they shrink almost exactly by
        // (1-w). The flip is the fixed 0.10 override margin biting a compressed scale, which
        // makes this knob a more expensive, less legible way of RAISING overrideMargin -- a
        // knob that already exists and is already tuned.
        //
        // The residual beyond pure compression is the only real signal, and it is tiny:
        // 0.839 against 0.850 at w=0.15. Implied prosperity at w=0.50 is 0.3389 for wait and
        // 0.3311 for spawnT3 -- a difference of 0.0078.
        //
        // WHY SO SMALL, AND WHY THAT IS THE INTERESTING PART. The premise was that the spawn
        // line ends "both poor" (income 19.7, investment 4) against wait's "both rich" (59.9,
        // investment 5). That reading ignored banked money: the spawn line holds $422 with
        // rung 4 priced at 474, i.e. one purchase from where the wait line already is. On the
        // absolute t_arma clock the wait line leads by about FOUR SECONDS out of ~243. The
        // two futures are economically near-identical, the evaluator's income differential of
        // 0.5-vs-0.5 is CORRECT rather than blind, and there was no large hidden economic
        // cost for this term to expose. See the correction in ScoreDecomp's header.
        //
        // Kept, default 0, because the STRUCTURAL argument above still stands -- a genuinely
        // symmetric change cannot be expressed in the evaluator's antisymmetric scalar, and
        // if such a case is ever demonstrated this is where the fix belongs. What is not
        // demonstrated is that tick 781 is such a case.
        private readonly double _economyBlend;

        // t_arma from a fresh game, the natural normaliser for prosperity. DERIVED rather than
        // transcribed so it cannot drift if the investment ladder is ever rebalanced -- the
        // same reason GameStateTimeFeatures drives PlayerState.ApplyInvestmentStep instead of
        // hardcoding the rungs. Oracle-checked at 242.8s.
        private static readonly double FreshGameTArma =
            GameState.TimeToArmageddonSeconds(new PlayerState());

        private const int DeferToPrior = -2;

        // ── ADAPTIVE DECISION CADENCE ────────────────────────────────────────────────
        //
        // Marc's observation: most of a game is spent with an empty board while both
        // players bank toward an investment, and searching every 15 ticks through that is
        // almost pure waste. `idleDecisionInterval` lets the bot search only every N ticks
        // while the board is QUIET, and snap back to the normal interval the moment
        // anything is happening.
        //
        // WHY THIS POLLS AT THE FAST INTERVAL AND SKIPS, rather than scheduling `_next`
        // far ahead. Scheduling ahead is the obvious implementation and it is wrong: set
        // `_next = tick + 150` while quiet and a unit spawning at tick+10 goes unanswered
        // for 140 ticks. Here the gate still opens every `_decisionInterval` ticks, and
        // each time it does the CURRENT board decides whether to search or skip. Worst-case
        // reaction latency to a spawn is therefore _decisionInterval, exactly as today --
        // the only thing given up is decisions taken DURING quiet, which is the point.
        // The skip test is O(1), so the polls it discards are free.
        //
        // WHAT COUNTS AS QUIET, and why each clause is there:
        //   - no units on the board, either side. This is the state being targeted.
        //   - no pending gadget effects. An empty board with a meteor inbound is not quiet.
        //   - THE NEXT INVESTMENT IS NOT YET AFFORDABLE. Without this clause the change
        //     would attack the bot's single strongest mechanism: the save-invest macro
        //     fires on ~4.5% of decisions, is worth +11.2 points, and does its work exactly
        //     during quiet stretches. Holding money needs no decision (not acting IS
        //     holding), but BUYING does -- so sitting on an affordable investment for up to
        //     150 ticks would directly cost earned investments, and earned-investment
        //     differential predicts win rate almost monotonically across ~20 configs.
        //
        // MEASURED 2026-08-19 AND NOT WORTH IT -- LEFT OFF. n=200 paired, seed 4242,
        // horizon 1600, margin 0.10, idle interval 150:
        //
        //   arm        win rate   decisions/game   sim ticks   ms/decision   wall
        //   idle off     77.0%         542          1.851B       184.4      1596s
        //   idle 150     73.5%         513          1.760B       197.9      1477s
        //
        // Paired delta -3.50 (b=12/c=19, p=0.281) to save 4.9% of simulated ticks. Not
        // significant, but the point estimate is negative and the saving is tiny, so the
        // trade is bad in expectation.
        //
        // WHY THE SAVING IS SO SMALL, AND IT GENERALISES. The board is completely empty
        // only ~7.5% of the time -- both players trickle units out continuously while they
        // bank, so "waiting" describes the MONEY, not the BOARD. Worse, note ms/decision
        // went UP (184.4 -> 197.9): an empty board means a rollout with no units to
        // simulate, i.e. the CHEAPEST decisions in the game. **Any "skip when nothing is
        // happening" heuristic targets the cheap end by construction.** Compute is
        // concentrated where units are dense, so a real saving has to make BUSY positions
        // cheaper -- fewer candidates, or a cheaper tick -- not skip quiet ones.
        //
        // Kept because it is inert at 0 and the mechanism above is worth not rediscovering.
        // Defaults to 0 = OFF, so every existing caller and every recorded benchmark
        // reproduces byte-for-byte. It must be switched on explicitly.
        private readonly int _idleInterval;
        private long _lastSearchTick;

        /// <summary>Polls skipped because the board was quiet. Diagnostic only.</summary>
        public long IdleDeferrals { get; private set; }

        /// <summary>
        /// True when this poll should be skipped. Must be called AFTER `_next` is re-armed
        /// and BEFORE anything draws from <see cref="_rng"/> -- a deferred poll must not
        /// perturb the random stream, or the arm stops being comparable to its control.
        /// </summary>
        private bool DeferForQuietBoard(GameEngine engine, long tick)
        {
            if (_idleInterval <= 0) return false;
            if (tick - _lastSearchTick >= _idleInterval) return false;
            var st = engine._state;
            if (st.Units.Count > 0) return false;
            if (engine.PendingEffectCount > 0) return false;
            var me = _side == 1 ? st.Player1 : st.Player2;
            if (me.Money >= me.InvestmentPrice) return false;
            IdleDeferrals++;
            return true;
        }

        public void Update(GameEngine engine)
        {
            if (!_asyncDecisions)
            {
                // _staleTicks == 0 routes to the ORIGINAL path, untouched, so every
                // existing benchmark number reproduces byte-for-byte. It is not merely
                // equivalent — UpdateStale clones and draws from _rng, so running it at
                // 0 would perturb the stream and the control would stop being the
                // control.
                if (_staleTicks > 0) { UpdateStale(engine); return; }
                UpdateBlocking(engine);
                return;
            }

            var st = engine._state;
            if (st.IsGameOver) return;
            // Queued wave actions play out before any new decision -- see QueueWave.
            if (DrainOne(engine)) return;

            // Apply a finished decision. Deliberately applied to the LIVE engine here on the
            // game-loop thread — the background task only ever touches its own clone.
            if (_pending != null && _pending.IsCompleted)
            {
                int chosen = _pending.Result;
                _pending = null;
                if (chosen == DeferToPrior) _prior.Update(engine);
                else Apply(engine, chosen);
            }

            // Start thinking about the next one. Never more than one in flight: if the
            // search is slower than the decision interval the bot simply acts less often,
            // which is far better than queueing up stale decisions.
            if (_pending == null && st.CurrentTick >= _next)
            {
                _next = st.CurrentTick + _decisionInterval;
                if (DeferForQuietBoard(engine, st.CurrentTick)) return;
                _lastSearchTick = st.CurrentTick;
                var snapshot = engine.Clone(rngSeed: _rng.Next());
                _pending = System.Threading.Tasks.Task.Run(() => Decide(snapshot));
            }
        }

        // ── ASYNC STALENESS, made measurable ─────────────────────────────────────────
        //
        // The live bot is asynchronous: it snapshots the engine, thinks on a background
        // thread, and applies the answer whenever it is ready — so it always acts on a
        // state some milliseconds old. The standing assumption has been that this costs
        // little because the game moves slowly. That assumption has never been tested,
        // and it cannot be tested by the live path, whose delay is wall-clock and
        // therefore not reproducible.
        //
        // This models the SAME structure with a deterministic delay measured in ticks:
        // decide from the state at tick T, commit the answer at tick T + _staleTicks.
        // It reproduces the two properties of the live path that matter —
        //
        //   1. the action is chosen against a state that no longer exists, and
        //      Apply() re-derives macro behaviour from the state it lands in, exactly
        //      as the live bot does;
        //   2. at most one decision is ever in flight, so if the delay exceeds the
        //      decision interval the bot simply acts LESS OFTEN rather than queueing
        //      stale answers behind each other.
        //
        // Converting a measurement here back to real time: one tick is 33 ms, so
        // _staleTicks 1 ~ 33 ms of thinking, 8 ~ 250 ms (the live maxDecisionMs cap),
        // 15 ~ one whole decision interval.
        //
        // MEASURED 2026-08-11, AND THE ASSUMPTION HOLDS WITH 15-30x OF MARGIN.
        // n=600 paired per arm, seed 4242, no headstart, same build:
        //
        //     D    ms    win rate   delta   p (exact)
        //     0     0     74.8%       --       --
        //     1    33     77.3%     +2.50    0.137
        //     2    67     77.2%     +2.33    0.141
        //     4   133     74.8%     +0.00    1.000
        //     8   266     73.3%     -1.50    0.417
        //    15   499     74.7%     -0.17    1.000
        //
        // Flat and non-monotone; nothing significant anywhere, including a FULL decision
        // interval of staleness. Real latency measured at 15.5 ms average / 34 ms worst
        // on ONE core, and the live game gives each decision 18 -- so the operating point
        // is under one tick, far left of anything that costs.
        //
        // DO NOT CHASE THE +2.5 AT D=1. The Armageddon margin showed +1.9 at n=600
        // (p=0.185) and came back -1.04 at n=2400 with the sign reversed. Same n, same
        // p-range. D=1 and D=2 are also near-identical policies, so their agreement is
        // not independent corroboration.
        //
        // LIMIT: measured against HeuristicBot, which does not exploit reaction delay.
        // A human can bait a cooldown or time a wave against known lag. This bounds the
        // cost against a non-exploiting opponent only.
        private readonly int _staleTicks;
        private long _staleApplyAt = long.MaxValue;
        private int _staleChoice = DeferToPrior;

        private void UpdateStale(GameEngine engine)
        {
            var st = engine._state;
            if (st.IsGameOver) return;
            if (DrainOne(engine)) return;

            if (st.CurrentTick >= _staleApplyAt)
            {
                int chosen = _staleChoice;
                _staleApplyAt = long.MaxValue;
                if (chosen == DeferToPrior) _prior.Update(engine);
                else Apply(engine, chosen);
            }

            if (_staleApplyAt == long.MaxValue && st.CurrentTick >= _next)
            {
                _next = st.CurrentTick + _decisionInterval;
                if (DeferForQuietBoard(engine, st.CurrentTick)) return;
                _lastSearchTick = st.CurrentTick;
                // Clone for the same reason the async path does: the decision must be
                // taken against a frozen copy, not against an engine that keeps moving.
                var snapshot = engine.Clone(rngSeed: _rng.Next());
                _staleChoice = Decide(snapshot);
                _staleApplyAt = st.CurrentTick + _staleTicks;
            }
        }

        /// <summary>
        /// Picks an action against <paramref name="engine"/> without mutating it.
        /// Returns an action id, or DeferToPrior.
        /// </summary>
        private int Decide(GameEngine engine)
        {
            int before = _pendingChoice;
            UpdateBlocking(engine, decideOnly: true);
            int chosen = _pendingChoice;
            _pendingChoice = before;
            return chosen;
        }

        private int _pendingChoice = DeferToPrior;

        private void UpdateBlocking(GameEngine engine, bool decideOnly = false)
        {
            var state = engine._state;
            if (state.IsGameOver) return;
            if (!decideOnly)
            {
                if (DrainOne(engine)) return;
                if (state.CurrentTick < _next) return;
                _next = state.CurrentTick + _decisionInterval;
                if (DeferForQuietBoard(engine, state.CurrentTick)) return;
                _lastSearchTick = state.CurrentTick;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var mask = state.GetActionMask(_side);
            var mePlayer = _side == 1 ? state.Player1 : state.Player2;

            // Stage 0 diagnostic — see _macroRandomRate. Gated on the same ArmageddonUsed
            // condition that makes the macro a candidate at all, so this arm cannot fire it
            // in states where the real macro never could.
            // AFFORDABILITY-MATCHED variant. A3 showed random firing captures none of the
            // macro's value, but a random firing lands mostly on decisions where the
            // investment is not affordable, where the macro degenerates to "do nothing".
            // Gating the roll on affordability asks the sharper question: is search's
            // selection anything MORE than "commit when you can afford it"? If this arm
            // recovers the control's win rate, the selection is a rule and can be written
            // down; if it does not, the selection is genuinely state-dependent.
            bool randomGateOpen = !_macroRandomAffordable || mePlayer.Money >= mePlayer.InvestmentPrice;
            if (_macroRandomRate > 0 && randomGateOpen && !mePlayer.ArmageddonUsed
                && _macroRng.NextDouble() < _macroRandomRate)
            {
                Decisions++;
                if (decideOnly) { _pendingChoice = MacroSaveInvest; return; }
                Apply(engine, MacroSaveInvest);
                return;
            }

            // COMMON RANDOM NUMBERS. Every candidate action in this decision is evaluated
            // against the SAME set of random futures, so differences in score are
            // attributable to the action rather than to luck.
            var seeds = new int[_rolloutsPerAction];
            for (int r = 0; r < seeds.Length; r++) seeds[r] = _rng.Next();

            // ── Candidates ─────────────────────────────────────────────────────────────
            // ONE list, evaluated in ONE parallel pass.
            //
            // The previous version split these into two sequential batches and checked the
            // clock between them. That was wrong twice over. The strategic batch holds only
            // 3-4 candidates, so while it ran at most 4 cores were busy — which is exactly
            // the ~20% of a 20-core box that showed up in practice. And the budget check
            // sat BETWEEN batches, so a single batch could overrun it without limit; the
            // cap bounded nothing. Both faults compounded: fewer cores made each batch
            // slower, and nothing stopped it.
            //
            // Now every candidate runs concurrently and the deadline is enforced INSIDE
            // each rollout. Under time pressure they all stop at roughly the same wall
            // clock, so the effective horizon shortens uniformly rather than some options
            // being dropped — comparisons stay fair, just shallower. Strongest-unit-first
            // ordering is kept for partitioner locality and because it puts expensive
            // options in front of the scheduler.
            // Release the reactive opening gate the first time the opponent has anything
            // on the board. Latches -- see the field note.
            if (_reactiveOpeningGate && !_opponentCommitted)
            {
                for (int i = 0; i < state.Units.Count; i++)
                    if (state.Units[i].Side != _side) { _opponentCommitted = true; break; }
            }
            bool openingLocked = _reactiveOpeningGate && !_opponentCommitted;

            var candidates = new List<int> { PriorBaselineAction, 0 };
            if (_useMacro && !mePlayer.ArmageddonUsed) candidates.Add(MacroSaveInvest);
            if (_usePressMacro && mePlayer.InvestmentCount >= 2) candidates.Add(MacroPressAdvantage);
            if (_useArmageddonMacro && !mePlayer.ArmageddonUsed) candidates.Add(MacroArmageddon);
            if (_useUpgradeMacro && UpgradeEligible(mePlayer)) candidates.Add(MacroGadgetUpgrade);
            // EARLY-SPEND GUARD (2026-08-18). Below this investment count, search may not
            // propose buying a unit at all; the prior decides all unit purchases.
            //
            // WHY THIS IS A CANDIDATE FILTER AND NOT A MARGIN. Traced from Marc's live-play
            // report that the bot buys a Tier-4 the moment it can after investment 2 and
            // spends its whole wallet. At that decision the scores are: spawnT4 0.6642 vs
            // prior = wait = MACRO-saveInvest = 0.5323 -- all three IDENTICAL. Saving is not
            // narrowly losing, it is completely INVISIBLE, so no margin on any option can
            // change the ranking.
            //
            // It is invisible for a structural reason, not a tuning one. Because
            // InvestmentPrice = Income * (InvestmentCount * 4 + 8), the time to afford the
            // next investment from an empty wallet is exactly (4 * count + 8) SECONDS
            // whatever the income: 8s at count 0, then 12s, 16s, 20s... The horizon is 300
            // ticks = 10s. So from investment 1 onward the payoff of saving can NEVER occur
            // inside the horizon, while a unit bought now deals damage inside it. Search is
            // therefore structurally guaranteed to prefer spending, and the cost it ignores
            // -- that the $18 pushes the next investment ~4s further out -- lands beyond the
            // horizon where it cannot be seen.
            //
            // RETRACTED 2026-08-19. This used to read "Raising the horizon is not the fix:
            // 900 was measured at 47.0% against 300's 68.5%, because long rollouts converge
            // to the same self-play continuation and the search scores noise." Both claims
            // are false. At the SHIPPED margin 0.10, horizon 900 scores 78.0% [71.8, 83.2]
            // and 600 beats 300 by +4.0 points (n=800 paired, p=0.0128); the best-worst
            // score spread GROWS with horizon (0.203 -> 0.341), so branches diverge rather
            // than converge. The 47.0% came from a coordinate-descent sweep that tuned
            // horizon at margin ~0.03 and never revisited it after margin moved to 0.10.
            //
            // Raising the horizon IS a fix for the structural problem described above, and
            // the shipped bot now runs horizon 1600 -- enough to span every investment rung
            // including the hand-tuned 7th (1600 ticks). This guard therefore addresses a
            // narrower case than it used to. See GameHostingService.SetupSearchOpponent and
            // FLAGSHIP_BASELINE.md section 5.
            //
            // Defence is NOT removed. When search declines to override, the prior runs, and
            // HeuristicBot's reactive branch still buys units whenever it is inDanger. What
            // this suppresses is only search's UNPROVOKED spending, which is the behaviour
            // HeuristicBot's own AttackGateMinInvestment=6 already forbids for itself and
            // which search was overriding.
            // THE GUARD DOES NOT COVER GADGETS, AND THAT IS WHERE THE OPENING LEAK IS.
            // It filters only the 1-8 unit candidates below; 9-13 are added unguarded. Marc
            // reported the bot opening with a Reinforcements cast, and 9A60C6 tick 181
            // confirms it: with $12.00, income 2.0, investment 0 and a $18 first rung, search
            // scores action 12 at 0.6197 against the prior's 0.5001 and overrides in 12/12
            // seeds, spending the ENTIRE wallet six seconds into the game.
            //
            // It is not ignoring the cost. --score-decomp shows the immediate money penalty
            // is +0.8373 against us -- the largest of any candidate -- and the rollout delays
            // investment 3 from tick 950 to 1130, exactly the 6.0 seconds $12 at $2/s buys.
            // What outweighs it is hp: -1.1071. Fifty-three seconds later the five free
            // tier-1 units from that cast have chewed the enemy castle to 71.7% while the
            // wait line leaves it untouched at 100%, and the $12 has long since been re-earned.
            //
            // THAT HP GAIN IS AN ARTEFACT OF THE OPPONENT MODEL. It is HeuristicBot that lets
            // five tier-1 units convert into 28% of a castle. Against Marc the same cast buys
            // nothing and hands him a six-second lead on the first rung, which is the lead he
            // says he snowballs. The wait line reads 0.5000 / 0.5000 / 0.5001 across the whole
            // horizon -- search sees "do nothing" as a dead-even position, when against a
            // human who plays the economy it is the WINNING line.
            bool earlySpendGuard = _earlySpendGuardMinInvest > 0
                                && mePlayer.InvestmentCount < _earlySpendGuardMinInvest;
            if (!earlySpendGuard && !openingLocked)
                for (int a = 8; a >= 1; a--) if (mask[a] == 1) candidates.Add(a);
            foreach (int a in new[] { 9, 10, 11, 12, 13 })
            {
                // Action 12 is the defence gadget. _suppressDefenceGadget is a MEASUREMENT
                // switch (see HeuristicBotSettings.DisableDefenseGadget) and must remove it
                // here as well as from the priors -- otherwise search can still cast the
                // gadget the probe is trying to withhold, and the arm measures nothing.
                if (a == 12 && SuppressDefCandidate) continue;
                if (a == 11 && SuppressOffCandidate) continue;
                // ── EARLY GADGET GUARD (2026-08-20) ─────────────────────────────────
                // No gadget cast before the first investment. Measured cause, 9A60C6 tick
                // 181: with $12.00 of a $12.00 wallet, income 2.0, investment 0 and an $18
                // first rung, search scored action 12 at 0.6197 against the prior's 0.5001
                // and overrode in 12/12 seeds -- spending everything six seconds into the
                // game and slipping the first rung by 6.0s.
                //
                // It was not a costing error. The immediate money penalty is +0.8373, the
                // largest of any candidate, and the rollout delays investment 3 by exactly
                // the 180 ticks the arithmetic predicts. What overrode it was hp = -1.1071:
                // 53 seconds later the five free tier-1 units from that cast had chewed the
                // enemy castle to 71.7% while the wait line left it at 100%.
                //
                // AND THAT IS AN ARTEFACT OF THE OPPONENT MODEL. HeuristicBot lets five
                // tier-1 units become 28% of a castle; Marc does not. Against him the cast
                // buys nothing and hands over a six-second lead on the first rung -- the
                // lead he reports snowballing into sustained pressure. Search cannot see
                // this: the wait line scores 0.5000/0.5000/0.5001 flat across the horizon,
                // so "do nothing" reads as dead even when against a human playing the
                // economy it is the WINNING line.
                //
                // A CANDIDATE FILTER, not a margin, for the same reason the early-SPEND
                // guard is one: at 0.6197 against 0.5001 no margin short of 0.12 removes it,
                // and a margin that large would suppress every legitimate override too.
                //
                // EXPECT THIS TO COST LADDER WIN RATE. The rollout genuinely believes the
                // cast is worth +0.12 against HeuristicBot, and against HeuristicBot it
                // probably is. It is removed because the opponent that matters is a human.
                if (a >= 11 && a <= 13 && _earlyGadgetGuardMinInvest > 0
                    && mePlayer.InvestmentCount < _earlyGadgetGuardMinInvest) continue;
                // Reactive opening gate: offence and defence gadgets are spending with
                // nothing to spend it on while the board is empty. 13 (signature) is exempt.
                if (openingLocked && (a == 11 || a == 12)) continue;
                if (a < mask.Length && mask[a] == 1) candidates.Add(a);
            }

            var scores = new Dictionary<int, double>();
            EvaluateBatch(engine, candidates, seeds, scores, clock);
            if (_maxDecisionMs > 0 && clock.ElapsedMilliseconds >= _maxDecisionMs) TruncatedDecisions++;

            double priorScore = scores[PriorBaselineAction];
            int bestAction = 0;
            // Best score AFTER subtracting each candidate's own override margin, which is
            // what the override test below compares against the prior. Kept separate from
            // the raw min/max used for the spread diagnostics, which must stay unadjusted.
            double bestAdjusted = double.NegativeInfinity;
            double minScore = double.PositiveInfinity, maxScore = double.NegativeInfinity;
            foreach (var kv in scores)
            {
                if (kv.Key == PriorBaselineAction) continue;
                if (kv.Value < minScore) minScore = kv.Value;
                if (kv.Value > maxScore) maxScore = kv.Value;
                double adjusted = kv.Value - MarginFor(kv.Key, mePlayer.InvestmentCount);
                if (adjusted > bestAdjusted) { bestAdjusted = adjusted; bestAction = kv.Key; }
            }

            Decisions++;
            DecisionMsSum += clock.Elapsed.TotalMilliseconds;
            if (clock.Elapsed.TotalMilliseconds > SlowestDecisionMs) SlowestDecisionMs = clock.Elapsed.TotalMilliseconds;
            if (maxScore > double.NegativeInfinity && minScore < double.PositiveInfinity)
            {
                double spread = maxScore - minScore;
                ScoreSpreadSum += spread;
                if (spread < 1e-6) FlatDecisions++;
            }

            // In decide-only mode nothing is committed here — the caller applies the result
            // to the LIVE engine on the game-loop thread. Mutating this clone would be
            // harmless but pointless, and it would make the two paths behave differently.
            if (!_usePrior)
            {
                if (decideOnly) { _pendingChoice = bestAction; return; }
                Apply(engine, bestAction);
                return;
            }

            bool override_ = bestAdjusted > priorScore;

            // Off by default: this copies a dictionary on the shipped bot's hot path
            // (~560 decisions per game) and exists only for traces.
            if (CaptureDecisionTrace)
            {
                LastScores = new Dictionary<int, double>(scores);
                LastPriorScore = priorScore;
                LastChosenAction = override_ ? bestAction : PriorBaselineAction;
            }

            if (OnMacroDecision != null && !decideOnly && scores.ContainsKey(MacroSaveInvest))
            {
                OnMacroDecision(engine, new MacroDecisionInfo
                {
                    Tick = state.CurrentTick,
                    Side = _side,
                    InvestmentCount = mePlayer.InvestmentCount,
                    Money = mePlayer.Money,
                    InvestmentPrice = mePlayer.InvestmentPrice,
                    MacroScore = scores[MacroSaveInvest],
                    PriorScore = priorScore,
                    ChosenAction = override_ ? bestAction : PriorBaselineAction,
                    Overrode = override_,
                });
            }

            if (decideOnly)
            {
                if (override_) Overrides++;
                _pendingChoice = override_ ? bestAction : DeferToPrior;
                return;
            }

            // ── STAGE 1b, second stage ───────────────────────────────────────────────────
            // The deep estimator has authority over exactly one question: macro or prior.
            // It cannot promote or demote a primitive, so a mixed-estimator comparison never
            // arises — the only two options it ranks are both scored by it.
            if (_deepMacroEval && scores.ContainsKey(MacroSaveInvest))
            {
                double dm = 0, dp = 0;
                for (int r = 0; r < _deepPlayouts; r++)
                {
                    // COMMON RANDOM NUMBERS across the two branches, same discipline as the
                    // shallow search: one seed per playout, used for both.
                    int s = _rng.Next();
                    dm += DeepRollout(engine, macro: true, seed: s, clock);
                    dp += DeepRollout(engine, macro: false, seed: s, clock);
                }
                dm /= _deepPlayouts; dp /= _deepPlayouts;

                bool deepWantsMacro = dm - _macroMargin > dp;
                bool shallowChoseMacro = override_ && bestAction == MacroSaveInvest;

                if (deepWantsMacro)
                {
                    if (!shallowChoseMacro) DeepPromotions++;
                    Overrides++;
                    Apply(engine, MacroSaveInvest);
                    return;
                }
                if (shallowChoseMacro)
                {
                    // Deep overruled the macro. Hand back to the prior rather than falling
                    // through to the next-best primitive: the shallow ranking that produced
                    // that primitive already lost to the macro, so promoting it now would be
                    // acting on the estimator we just declined to trust.
                    DeepVetoes++;
                    _prior.Update(engine);
                    return;
                }
            }

            if (override_)
            {
                Overrides++;
                Apply(engine, bestAction);
            }
            else
            {
                // No clear improvement — defer to the prior. Note this is a persistent
                // HeuristicBot instance, so its own cadence and observed-unit memory carry
                // across the game exactly as they would if it were playing unassisted.
                _prior.Update(engine);
            }
        }

        /// <summary>Sentinel for "let the prior play this decision" — the override baseline.</summary>
        private const int PriorBaselineAction = -1;

        // ── STAGE 1b: deep evaluation of the hold-money decision ─────────────────────────
        //
        // Stage 0 established that the save-invest macro's whole +11.2 points are in WHICH
        // decisions it fires on. Stage 1a measured that choice against play-to-completion
        // ground truth (n=1219, K=30) and found:
        //
        //   - the decision is BIMODAL: ~92% of the time macro-vs-prior makes literally no
        //     difference, ~8% of the time it is near game-deciding (p90 gap 1.0);
        //   - search gets ~94% right but concentrates its errors on the decisive ones (mean
        //     gap when wrong 0.38 vs 0.07 overall), leaving regret 0.0283/decision;
        //   - a BOUNDED-COMMITMENT play-to-completion estimator reaches ~0 regret with a
        //     SINGLE playout (held-out: 0.00022 vs 0.02860, a 131x gap), because the gaps
        //     are 0-or-1 rather than finely graded.
        //
        // This runs that estimator. Two properties matter and neither is optional:
        //
        //  1. BOUNDED. Committing to saving with no bound holds the purse to game end and
        //     dies with a full bank -- Stage 1a measured that framing implying a single
        //     decision swings 0.62 win probability, which is not credible. The live macro is
        //     re-priced every interval and abandoned; _deepCommitTicks models that.
        //
        //  2. BOTH SIDES OF THE COMPARISON GET IT. The prior baseline is re-evaluated deeply
        //     too. Scoring the macro by truth and the prior by a truncated evaluator would
        //     compare two different estimators -- the same error class as the --divergence
        //     phase bug, which this project has now hit twice.
        private readonly bool _deepMacroEval;
        private readonly int _deepPlayouts;
        private readonly int _deepCommitTicks;

        /// <summary>Deep said fire the macro when the shallow search would not have.</summary>
        public long DeepPromotions { get; private set; }
        /// <summary>Shallow chose the macro and deep overruled it.</summary>
        public long DeepVetoes { get; private set; }
        public long DeepRollouts => System.Threading.Interlocked.Read(ref _deepRolloutCount);
        private long _deepSimTicks;
        public long DeepSimTicks => System.Threading.Interlocked.Read(ref _deepSimTicks);

        /// <summary>
        /// Plays one branch of the hold-money decision to a REAL terminal outcome under a
        /// bounded saving commitment. Mirrors MacroTruth.PlayToEnd, which is the estimator
        /// Stage 1a measured — deliberately, so what ships is what was validated.
        /// </summary>
        private double DeepRollout(GameEngine engine, bool macro, int seed,
                                   System.Diagnostics.Stopwatch clock)
        {
            var clone = engine.Clone(rngSeed: seed);
            var cs = clone._state;
            System.Threading.Interlocked.Increment(ref _deepRolloutCount);

            var mine = RolloutPolicyFactory.Make(_ownRolloutPolicy, _side, _saveCommitFraction,
                                                 SuppressDefCasting, SuppressOffCasting);
            var theirs = RolloutPolicyFactory.Make(_oppRolloutPolicy, _side == 1 ? 2 : 1, _saveCommitFraction);
            var me = _side == 1 ? cs.Player1 : cs.Player2;

            long start = cs.CurrentTick;
            bool stillSaving = macro;
            int t = 0;
            bool truncated = false;
            while (!cs.IsGameOver)
            {
                // Same deadline discipline as the shallow rollout, masked to every 64th tick
                // because ElapsedMilliseconds is not free. A deep rollout runs to game end,
                // so without this the live game's budget would be unenforceable.
                if (_maxDecisionMs > 0 && (t & 63) == 0 && clock.ElapsedMilliseconds >= _maxDecisionMs)
                { truncated = true; break; }

                clone.Tick();
                t++;
                if (stillSaving)
                {
                    if (me.Money >= me.InvestmentPrice) { clone.ApplyAction(_side, 9); stillSaving = false; }
                    else if (cs.CurrentTick - start >= _deepCommitTicks) stillSaving = false;
                }
                else mine.Update(clone);
                theirs.Update(clone);
            }
            System.Threading.Interlocked.Add(ref _deepSimTicks, t);

            // Graceful degradation: if the budget cut the rollout short there is no terminal
            // result, so fall back to the evaluator rather than returning a fabricated one.
            if (truncated && !cs.IsGameOver)
            {
                float ev = UseLinearEval ? cs.EvaluateBoardLinear()
                         : UseRefitEval ? cs.EvaluateBoardRefit()
                                        : cs.EvaluateBoard();
                return _side == 1 ? ev : 1.0 - ev;
            }
            if (cs.WinnerSide == 0) return 0.5;
            return cs.WinnerSide == _side ? 1.0 : 0.0;
        }

        private long _deepRolloutCount;

        /// <summary>
        /// What the search believed at a decision where the save-invest macro was a
        /// candidate. Diagnostic only — see <see cref="OnMacroDecision"/>.
        /// </summary>
        public sealed class MacroDecisionInfo
        {
            public long Tick;
            public int Side;
            public int InvestmentCount;
            public double Money, InvestmentPrice;
            /// <summary>Shallow (horizon-truncated, evaluator-scored) leaf values.</summary>
            public double MacroScore, PriorScore;
            public int ChosenAction;
            public bool Overrode;
        }

        /// <summary>
        /// Fires at every decision where the save-invest macro was a candidate, BEFORE the
        /// chosen action is applied — so a listener can fork the engine from exactly the
        /// state the search judged.
        ///
        /// Null by default and never set by the game or the ladder: Stage 1a needs the
        /// search's own shallow beliefs to compare against ground truth, and re-deriving
        /// them outside would be a second implementation that could drift from this one.
        /// </summary>
        public Action<GameEngine, MacroDecisionInfo> OnMacroDecision;

        /// <summary>
        /// Scores a batch of candidates IN PARALLEL.
        ///
        /// Rollouts are independent by construction: each one clones the engine and
        /// simulates its own copy, and the live engine is only read (never mutated) until
        /// Apply() runs after the search. So this is safe without locking, and it was the
        /// single largest source of wasted capacity — the search ran on one core while
        /// nineteen sat idle.
        /// </summary>
        private void EvaluateBatch(GameEngine engine, List<int> candidates, int[] seeds,
                                   Dictionary<int, double> scores,
                                   System.Diagnostics.Stopwatch clock)
        {
            if (candidates.Count == 0) return;
            var results = new double[candidates.Count];

            if (_maxParallelism <= 1)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    double total = 0;
                    for (int r = 0; r < seeds.Length; r++) total += Rollout(engine, candidates[i], seeds[r], clock);
                    results[i] = total / seeds.Length;
                }
            }
            else
            {
                // ONE TASK PER CANDIDATE, not Parallel.For.
                //
                // Parallel.For over a small range uses a RANGE PARTITIONER: it hands each
                // worker a contiguous chunk of indices to process sequentially. With ~17
                // candidates that means a handful of workers each running several rollouts
                // back to back, so most cores sit idle — which is why CPU stayed near 20%
                // even after the batching fix.
                //
                // It is worse than merely inefficient here, because candidate costs are
                // wildly uneven. A rollout that opens with a gadget cast can be an order of
                // magnitude dearer than one that opens with a unit spawn (hazards iterate
                // every unit on every tick for their whole duration, and Reinforcements
                // spawns five units). Marc watched the stutter vanish the instant a gadget
                // stopped being a legal candidate. Chunked scheduling puts several of those
                // on one thread; one task each lets the pool spread them.
                var tasks = new System.Threading.Tasks.Task[candidates.Count];
                for (int i = 0; i < candidates.Count; i++)
                {
                    int idx = i;
                    tasks[idx] = System.Threading.Tasks.Task.Run(() =>
                    {
                        double total = 0;
                        for (int r = 0; r < seeds.Length; r++) total += Rollout(engine, candidates[idx], seeds[r], clock);
                        results[idx] = total / seeds.Length;
                    });
                }
                System.Threading.Tasks.Task.WaitAll(tasks);
            }

            for (int i = 0; i < candidates.Count; i++) scores[candidates[i]] = results[i];
        }

        /// <summary>
        /// Commits the chosen action to the real game.
        ///
        /// The macro needs translating: it is a multi-decision commitment, but each call
        /// only gets to act once. Invest if we can already afford it, otherwise hold the
        /// purse. Because the search re-runs every decision, repeatedly choosing the macro
        /// produces sustained saving — and it re-prices the decision each time, so the bot
        /// abandons saving the moment the rollouts say it is about to get killed. That is
        /// the "spend only what you need to survive" half of the plan, arrived at by
        /// evaluation rather than by a hand-written rule.
        /// </summary>
        private void Apply(GameEngine engine, int action)
        {
            if (action == MacroSaveInvest)
            {
                MacroChosen++;
                var p = _side == 1 ? engine._state.Player1 : engine._state.Player2;
                if (p.Money >= p.InvestmentPrice) engine.ApplyAction(_side, 9);
                else WaitDecisions++;
                return;
            }

            if (action == MacroGadgetUpgrade)
            {
                UpgradeChosen++;
                // One decision's worth of farming. Sustained farming happens by search
                // re-selecting this every interval, exactly as MacroSaveInvest does --
                // the commitment lives in the ROLLOUT, not in a latched flag here.
                _upgradePrior.Update(engine);
                return;
            }

            if (action == MacroArmageddon)
            {
                ArmageddonChosen++;
                var ap = _side == 1 ? engine._state.Player1 : engine._state.Player2;
                // Invest whenever affordable -- including the final rung, where
                // GameEngine.Invest turns the button into ARMAGEDDON itself. Otherwise
                // defend, never attack.
                if (!ap.ArmageddonUsed && ap.Money >= ap.InvestmentPrice) engine.ApplyAction(_side, 9);
                else _defencePrior.Update(engine);
                return;
            }

            if (action == MacroPressAdvantage)
            {
                PressChosen++;
                if (!_pressWaveCommit) { BuyBiggestAffordable(engine, _side); return; }
                int tier = AffordableWaveTier(engine._state, _side, _pressWaveUnits, _pressMinTier);
                // No full wave affordable yet -> BANK this decision. The search re-prices
                // every interval, so repeatedly choosing press produces sustained saving
                // and then one committed strike, exactly as repeatedly choosing the
                // save-invest macro produces sustained saving and then one investment.
                if (tier > 0) QueueWave(engine, tier);
                else WaitDecisions++;
                return;
            }

            if (action == 0) { WaitDecisions++; return; }
            engine.ApplyAction(_side, action);
        }

        // ── ONE ACTION PER TICK (2026-08-20) ────────────────────────────────────────
        //
        // CommitWave was the worst offender in the codebase: up to _pressWaveUnits (6) unit
        // spawns plus three covering gadget casts, all inside a SINGLE tick. No human can
        // issue nine actions in 33ms, and the replay format keeps only the last of them, so
        // eight vanished from every recording. See HeuristicBot's Act() for the full note.
        //
        // The real path now QUEUES the wave and plays it out one action per tick. Search
        // decides every _decisionInterval (15) ticks and the burst is at most 9, so it always
        // drains first. Action IDS are queued rather than closures because re-resolving the
        // mask at execution time IS the re-validation: a tier that is no longer affordable is
        // skipped instead of being force-spawned against a stale price.
        //
        // ROLLOUTS ARE DELIBERATELY NOT PACED. Rollout() still calls CommitWave directly on
        // its clone, so search continues to price the macro as an instantaneous wave while the
        // real bot spreads it over nine ticks. That is a real modelling gap, recorded rather
        // than hidden: pacing inside the rollout would mean threading a tick budget through
        // the whole simulated continuation, and the rollout's own HeuristicBot instances
        // already pace themselves.
        private readonly Queue<int> _pendingActions = new Queue<int>();

        /// <summary>Actions still queued from an earlier decision. Diagnostic.</summary>
        public int PendingActionCount => _pendingActions.Count;

        private void QueueWave(GameEngine engine, int tier)
        {
            for (int i = 0; i < _pressWaveUnits; i++) _pendingActions.Enqueue(tier);
            foreach (int g in new[] { 11, 12, 13 }) _pendingActions.Enqueue(g);
            DrainOne(engine);
        }

        /// <summary>
        /// Plays at most one queued action, re-checking the mask so a stale one is skipped
        /// cleanly. Returns true when the queue was non-empty, i.e. no new decision this tick.
        /// </summary>
        private bool DrainOne(GameEngine engine)
        {
            if (_pendingActions.Count == 0) return false;
            int a = _pendingActions.Dequeue();
            var mask = engine._state.GetActionMask(_side);
            if (a < mask.Length && mask[a] == 1) engine.ApplyAction(_side, a);
            return true;
        }

        /// <summary>
        /// Spends on the highest tier currently affordable. Action ids 1-8 map directly to
        /// tiers, and the action mask already encodes affordability, so walking it downward
        /// finds the strongest legal purchase without duplicating any cost logic.
        /// </summary>
        private static bool BuyBiggestAffordable(GameEngine engine, int side)
        {
            var mask = engine._state.GetActionMask(side);
            for (int tier = 8; tier >= 1; tier--)
            {
                if (mask[tier] == 1) { engine.ApplyAction(side, tier); return true; }
            }
            return false;
        }

        private double Rollout(GameEngine engine, int action, int seed,
                               System.Diagnostics.Stopwatch clock)
        {
            // Seed comes from the caller so every candidate action in a decision shares
            // the same random future — see the common-random-numbers note above.
            var clone = engine.Clone(rngSeed: seed);
            var cs = clone._state;
            System.Threading.Interlocked.Increment(ref _rollouts);

            bool macro = action == MacroSaveInvest;
            bool press = action == MacroPressAdvantage;
            bool arma = action == MacroArmageddon;
            bool upgrade = action == MacroGadgetUpgrade;

            // action == -1 means "force nothing" — used for the prior baseline, where the
            // rollout's own HeuristicBot decides this turn as well.
            if (action > 0 && !macro && !press && !arma) clone.ApplyAction(_side, action);

            // The rollout policy for both sides. HeuristicBot by default — it is by a wide
            // margin the strongest agent available, so it is the most informative
            // continuation — but SWAPPABLE, because it is also the ceiling: search cannot
            // discover a line its rollout policy would not play out. See IRolloutPolicy.
            //
            // The two sides are separate settings on purpose. Raising OUR side tests whether
            // a stronger rollout policy makes search stronger (Probe A); raising THEIRS is
            // opponent modelling, a different question with a different payoff.
            var mine = RolloutPolicyFactory.Make(_ownRolloutPolicy, _side, _saveCommitFraction,
                                                 SuppressDefCasting, SuppressOffCasting);
            // Defence-only stand-in used for the whole Armageddon rollout.
            var mineDefence = arma ? new HeuristicBot(_side, DefenceOnly) : null;
            // Farms XP for the WHOLE horizon so the upgrade lands inside it and the leaf
            // can price the upgraded gadget. A one-shot cast is invisible to the evaluator.
            var mineFarming = upgrade ? new HeuristicBot(_side, UpgradeFarming) : null;
            var theirs = RolloutPolicyFactory.Make(_oppRolloutPolicy, _side == 1 ? 2 : 1, _saveCommitFraction);
            var me = _side == 1 ? cs.Player1 : cs.Player2;

            // For the SAVE-AND-INVEST macro, our side buys nothing until it can afford the
            // next investment, takes it, and only then hands back to HeuristicBot.
            bool stillSaving = macro;
            bool waveCommitted = false;

            int t = 0;
            for (; t < _horizon && !cs.IsGameOver; t++)
            {
                // DEADLINE, checked here rather than between candidates. Simulated ticks
                // are where essentially all the time goes, and late-game ticks cost far
                // more than early ones (many more units), so a rollout's duration cannot
                // be predicted from its tick count. Checking only at candidate boundaries
                // left the budget unenforced in exactly the situation it existed for.
                //
                // Every candidate is running concurrently against the same clock, so they
                // all stop at about the same moment: the horizon shortens uniformly and
                // the comparison between actions stays fair, just shallower. That is much
                // better than abandoning some candidates unscored, which would bias the
                // argmax toward whichever ones happened to finish.
                //
                // Masked to every 64th tick because Stopwatch.ElapsedMilliseconds is not
                // free and this is the hottest loop in the program.
                if (_maxDecisionMs > 0 && (t & 63) == 0 && clock.ElapsedMilliseconds >= _maxDecisionMs)
                    break;

                clone.Tick();

                if (stillSaving)
                {
                    if (me.Money >= me.InvestmentPrice)
                    {
                        clone.ApplyAction(_side, 9); // invest
                        stillSaving = false;
                    }
                }
                else if (upgrade)
                {
                    mineFarming.Update(clone);
                }
                else if (arma)
                {
                    // Simulate the WHOLE commitment, not one rung: keep taking every
                    // investment we can afford for the full horizon, defending in between.
                    // That is what lets the evaluator price the economy actually compounding.
                    if (!me.ArmageddonUsed && me.Money >= me.InvestmentPrice) clone.ApplyAction(_side, 9);
                    else mineDefence.Update(clone);
                }
                else if (press && !_pressWaveCommit)
                {
                    if (cs.CurrentTick % _decisionInterval == 0) BuyBiggestAffordable(clone, _side);
                }
                else if (press && !waveCommitted)
                {
                    // Bank until a FULL wave is affordable, commit it in one burst with
                    // covering gadgets, then hand back to HeuristicBot for the remainder of
                    // the horizon. Simulating the follow-through is the point: what the
                    // evaluator has to be able to price is the breakthrough, not the
                    // purchase.
                    if (cs.CurrentTick % _decisionInterval == 0)
                    {
                        int tier = AffordableWaveTier(cs, _side, _pressWaveUnits, _pressMinTier);
                        if (tier > 0) { CommitWave(clone, _side, tier, _pressWaveUnits); waveCommitted = true; }
                    }
                }
                else
                {
                    mine.Update(clone);
                }
                theirs.Update(clone);
            }
            System.Threading.Interlocked.Add(ref _simulatedTicks, t);

            if (cs.IsGameOver)
            {
                if (cs.WinnerSide == 0) return 0.5;
                return cs.WinnerSide == _side ? 1.0 : 0.0;
            }

            // EvaluateBoard is from P1's perspective; flip it for side 2.
            // UseLinearEval selects the pre-2026-07-28 weighted-average form, so the
            // logistic switch can be A/B tested against the prior+macro configuration
            // instead of being credited on faith.
            float eval = UseLinearEval ? cs.EvaluateBoardLinear()
                       : UseRefitEval  ? cs.EvaluateBoardRefit()
                                       : cs.EvaluateBoard();
            double winProb = _side == 1 ? eval : 1.0 - eval;
            if (_economyBlend <= 0) return winProb;

            // Own absolute progress toward ARMAGEDDON: 0 = as far away as a fresh game,
            // 1 = able to fire it now. See _economyBlend for why this is a search-side
            // preference rather than an evaluator component.
            double tArma = GameState.TimeToArmageddonSeconds(me);
            double prosperity = 1.0 - tArma / FreshGameTArma;
            if (prosperity < 0) prosperity = 0;
            else if (prosperity > 1) prosperity = 1;
            return (1.0 - _economyBlend) * winProb + _economyBlend * prosperity;
        }
    }
}
