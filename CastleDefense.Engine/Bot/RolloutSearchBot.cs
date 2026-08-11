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
                                     int deepCommitTicks = 225)
        {
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
            _prior = new HeuristicBot(side);
        }

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
        /// Margin 0.0 is the strongest: +1.9 points, and it wins 34 of the 57 games where
        /// the two arms disagree. But McNemar puts that at p = 0.185 -- consistently
        /// positive across every configuration tried, never negative, and never proven.
        ///
        /// It ships at 0.10 anyway, on Marc's call, for PLAYABILITY rather than strength.
        /// At 7.3% the bot spends a large share of the game in defence-only banking, which
        /// makes it a markedly less interactive opponent to play against. He would rather
        /// have the behaviour present but rare than trade the feel of the game for ~2
        /// unproven points. Do not "fix" this to 0.0 on the strength of the table above.
        ///
        /// ONE UNEXPLAINED THING, worth chasing before trusting the mechanism: at 7.3%
        /// firing, earned investments moved only 6.82 -> 6.86. A macro whose stated purpose
        /// is committing to the investment race should move that far more. The likely
        /// reading is that it mostly fires when it CANNOT afford the investment and so
        /// delegates to the defence-only bot -- meaning any real gain may come from playing
        /// more defensively, not from racing the economy. If so, a simpler defence-only
        /// macro would capture the same thing. Untested.
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
        private const int DeferToPrior = -2;

        public void Update(GameEngine engine)
        {
            if (!_asyncDecisions) { UpdateBlocking(engine); return; }

            var st = engine._state;
            if (st.IsGameOver) return;

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
                var snapshot = engine.Clone(rngSeed: _rng.Next());
                _pending = System.Threading.Tasks.Task.Run(() => Decide(snapshot));
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
                if (state.CurrentTick < _next) return;
                _next = state.CurrentTick + _decisionInterval;
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
            var candidates = new List<int> { PriorBaselineAction, 0 };
            if (_useMacro && !mePlayer.ArmageddonUsed) candidates.Add(MacroSaveInvest);
            if (_usePressMacro && mePlayer.InvestmentCount >= 2) candidates.Add(MacroPressAdvantage);
            if (_useArmageddonMacro && !mePlayer.ArmageddonUsed) candidates.Add(MacroArmageddon);
            for (int a = 8; a >= 1; a--) if (mask[a] == 1) candidates.Add(a);
            foreach (int a in new[] { 9, 10, 11, 12, 13 }) if (a < mask.Length && mask[a] == 1) candidates.Add(a);

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

            var mine = RolloutPolicyFactory.Make(_ownRolloutPolicy, _side, _saveCommitFraction);
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
                if (tier > 0) CommitWave(engine, _side, tier, _pressWaveUnits);
                else WaitDecisions++;
                return;
            }

            if (action == 0) { WaitDecisions++; return; }
            engine.ApplyAction(_side, action);
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
            var mine = RolloutPolicyFactory.Make(_ownRolloutPolicy, _side, _saveCommitFraction);
            // Defence-only stand-in used for the whole Armageddon rollout.
            var mineDefence = arma ? new HeuristicBot(_side, DefenceOnly) : null;
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
            return _side == 1 ? eval : 1.0 - eval;
        }
    }
}
