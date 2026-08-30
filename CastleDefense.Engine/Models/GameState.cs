using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models.Hazards;
using System.Numerics;

namespace CastleDefense.Engine.Models
{
    public partial class GameState
    {
        public Guid GameId { get; set; }
        public string GameMode { get; set; }
        public TeamColour Map { get; set; }
        public bool ShadowMap { get; set; }
        public bool IsGameOver { get; set; }
        public bool IsTimeLimit { get; set; } // true when the game ended by tick limit rather than castle destruction
        public int WinnerSide { get; set; } // 0 = Playing, 1 = Player 1 (Left), 2 = Player 2 (Right)

        public long CurrentTick { get; set; }

        /// <summary>
        /// Tick the Red map's next heal pulse is due. 0 means "not scheduled yet", which is
        /// how a fresh board and a board loaded from an older save both start; the engine
        /// rolls the first delay on the tick it first notices. Unused on every other map.
        ///
        /// A plain long, so GameState.Clone's MemberwiseClone copies it correctly and a
        /// search rollout inherits the pulse the real game is already committed to rather
        /// than inventing its own -- see the clone hazard note in CLAUDE.md.
        /// </summary>
        public long NextHealPulseTick { get; set; }

        public PlayerState Player1 { get; set; }
        public PlayerState Player2 { get; set; }

        public List<Unit> Units { get; set; } = new List<Unit>();
        public List<Hazard> Hazards { get; set; } = new List<Hazard>();

        public GameState() : this(GameDataManager.GetRandomTeam())
        {
        }

        public GameState(TeamColour map) : this(map, new Random())
        {
        }

        /// <summary>
        /// Seeded overload. Added 2026-07-28 so benchmarks can be reproducible: the map
        /// roll below (shadow-map selection) is real gameplay-affecting randomness that
        /// previously came from an unseedable `new Random()`, which made identical
        /// benchmark setups diverge before the first tick. Pass a seeded Random to make
        /// setup deterministic; the parameterless paths keep the old behaviour exactly.
        /// </summary>
        public GameState(TeamColour map, Random rng)
        {
            GameId = Guid.NewGuid();
            Units = new List<Unit>();
            ShadowMap = false;

            Player1 = new PlayerState();
            Player2 = new PlayerState();

            Map = map;

            if (Map == TeamColour.Black)
            {
                Random rand = rng;

                // 50/50 for regular black map, or shadow version of a different map
                if (rand.Next(2) == 0)
                {
                    ShadowMap = true;
                    Map = RandomTeam(rng);

                    // 1/8 chance to still get the black map
                    if (Map == TeamColour.Black)
                    {
                        ShadowMap = false;
                    }
                }
            }
        }

        /// <summary>
        /// Deep copy of the whole board. Scalars and enums come across via
        /// MemberwiseClone; the four reference members are copied explicitly.
        ///
        /// GameId is deliberately preserved — a clone is a hypothetical continuation of
        /// the same game, not a new one, and keeping the id makes rollout traces
        /// attributable back to the position they branched from.
        /// </summary>
        public GameState Clone()
        {
            var copy = (GameState)MemberwiseClone();
            copy.Player1 = Player1?.Clone();
            copy.Player2 = Player2?.Clone();

            copy.Units = new List<Unit>(Units.Count);
            foreach (var u in Units) copy.Units.Add(u.Clone());

            copy.Hazards = new List<Hazard>(Hazards.Count);
            foreach (var h in Hazards) copy.Hazards.Add(h.Clone());

            return copy;
        }

        /// <summary>
        /// Seeded equivalent of GameDataManager.GetRandomTeam(), which draws from
        /// Random.Shared and therefore cannot be made reproducible.
        /// </summary>
        private static TeamColour RandomTeam(Random rng)
        {
            var values = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            return values[rng.Next(values.Length)];
        }

        /// <summary>
        /// Converts the complex game state into a flat array of floats for Reinforcement Learning.
        /// Flips the perspective so the AI always feels like it is moving "forward".
        /// </summary>
        public float[] GetStateVector(int side)
        {
            // Neural Networks REQUIRE a fixed input size. 
            // If we allow the AI to "see" up to 20 of its own units and 20 enemy units:
            const int MAX_UNITS = 50;
            const float MAP_WIDTH = 2000f;
            const float MAX_CASTLE_HP = 100000f; // Using the cap we established earlier
            const float MAX_UNIT_HP = 100000f;    // Set this to roughly the max HP a Tier 8 unit can have
            const float MAX_TIER = 8f;
            const float MAX_UPGRADES = 10f;      // The hard cap we put on the economy buttons

            List<float> state = new List<float>();

            var me = side == 1 ? Player1 : Player2;
            var enemy = side == 1 ? Player2 : Player1;

            // MY TEAM (Enums are already 0 or 1, perfect for Neural Nets)
            Array teamValues = Enum.GetValues(typeof(TeamColour));
            foreach (TeamColour team in teamValues)
            {
                state.Add(me.Team == team ? 1f : 0f);
            }
            // ENEMY TEAM
            foreach (TeamColour team in teamValues)
            {
                state.Add(enemy.Team == team ? 1f : 0f);
            }

            // GADGETS (Booleans are perfectly normalized)
            var OGadgets = GameDataManager.Gadgets.Where(g => g.Slot == GadgetSlot.Offense).ToList();
            foreach (GadgetDefinition OGadget in OGadgets)
            {
                state.Add(me.OffensiveGadget.Id == OGadget.Id ? 1f : 0f);
            }
            var DGadgets = GameDataManager.Gadgets.Where(g => g.Slot == GadgetSlot.Defense).ToList();
            foreach (GadgetDefinition DGadget in DGadgets)
            {
                state.Add(me.DefensiveGadget.Id == DGadget.Id ? 1f : 0f);
            }

            // --- 1. MY ECONOMY & CASTLE ---
            // Scaled to a 0.0 - 1.0 range
            state.Add(me.CastleHealth / MAX_CASTLE_HP);
            state.Add(me.CastleMaxHealth / MAX_CASTLE_HP);

            // Log10 perfectly squashes exponential economies. (0 gold = 0, 1M gold = 6)
            // We add 1 so we don't accidentally calculate Log10(0) which throws an error!
            state.Add((float)Math.Log10(me.Money + 1) / 6.0f);
            state.Add((float)Math.Log10(me.Income + 1) / 4.0f);

            // InvestmentPrice (log10) replaces InvestmentCount — directly actionable for savings decisions.
            // InvestmentCount is derivable from InvestmentPrice + Income, which are both in the state.
            state.Add((float)Math.Log10(me.InvestmentPrice + 1) / 4.0f);
            state.Add(me.RepairCount / MAX_UPGRADES);

            // --- 2. ENEMY CASTLE ---
            // (We hide the enemy's money/income so the AI doesn't cheat!)
            state.Add(enemy.CastleHealth / MAX_CASTLE_HP);
            state.Add(enemy.CastleMaxHealth / MAX_CASTLE_HP);

            // --- 3. MY UNITS ---
            var myUnits = Units.Where(u => u.Side == side)
                               .OrderBy(u => side == 1 ? (MAP_WIDTH - u.Position) : u.Position)
                               .Take(MAX_UNITS)
                               .ToList();

            for (int i = 0; i < MAX_UNITS; i++)
            {
                if (i < myUnits.Count)
                {
                    var u = myUnits[i];

                    // Normalize position: 0.0 is My Castle, 1.0 is Enemy Castle
                    float relativePos = side == 1 ? u.Position / MAP_WIDTH : (MAP_WIDTH - u.Position) / MAP_WIDTH;

                    state.Add(relativePos);

                    // Normalize Unit Health and Tier
                    state.Add(u.CurrentHealth / MAX_UNIT_HP);
                    state.Add(u.Tier / MAX_TIER);
                }
                else
                {
                    // PAD WITH ZEROS
                    state.Add(0f);
                    state.Add(0f);
                    state.Add(0f);
                }
            }

            // --- 4. ENEMY UNITS ---
            var enemyUnits = Units.Where(u => u.Side != side)
                                  .OrderBy(u => side == 1 ? u.Position : (MAP_WIDTH - u.Position))
                                  .Take(MAX_UNITS)
                                  .ToList();

            for (int i = 0; i < MAX_UNITS; i++)
            {
                if (i < enemyUnits.Count)
                {
                    var u = enemyUnits[i];

                    // Normalize position from MY perspective!
                    float relativePos = side == 1 ? u.Position / MAP_WIDTH : (MAP_WIDTH - u.Position) / MAP_WIDTH;

                    state.Add(relativePos);

                    // Normalize Unit Health and Tier
                    state.Add(u.CurrentHealth / MAX_UNIT_HP);
                    state.Add(u.Tier / MAX_TIER);
                }
                else
                {
                    state.Add(0f);
                    state.Add(0f);
                    state.Add(0f);
                }
            }

            return state.ToArray();
        }

        // ── Evaluator weights ─────────────────────────────────────────────────────
        // Recalibrated 2026-07-28 (audit_evaluator.py, fit "D"). Weights are
        // normalised internally, so the sum need not equal 1.0.
        //
        // WHY THESE CHANGED: the previous values came from train_evaluator.py, which
        // fit a NO-INTERCEPT logistic on features that are all sigmoid outputs — every
        // component equals 0.5 in an even position, so the model can only score an even
        // game at 50% when sum(w) == 0. That forces a zero-sum split in which some
        // coefficients must come out negative; the script then clamped negatives to zero
        // (train_evaluator.py, `np.maximum(raw_w, 0.0)`), deleting half the solution.
        // The resulting zeros meant "clamped negative", NOT "no predictive value" — and
        // which components got zeroed moved around with the L2 strength, i.e. the fit was
        // never identified. That is where "castle HP does not affect win probability"
        // came from, and it is not a real property of this game.
        //
        // These weights are fit against the functional form EvaluateBoard() actually
        // evaluates — p = (w·x) / sum(w), constrained to w >= 0 — rather than against a
        // logistic that is never used at runtime. On held-in data they beat both the old
        // values and the raw calibration output on accuracy and log-loss.
        //
        // Money/Army/Gadget/Repair land at zero here honestly: given HP and Income they
        // add nothing *to a linear weighted average*. A logistic on the same six features
        // does use all of them and calibrates ~11% better (log-loss 0.491 vs 0.551).
        //
        // THESE WEIGHTS ARE NOT DEPLOYED. The sigmoid switch this block used to describe
        // as "a real available upgrade — deliberately not done here" WAS done, later the
        // same day: EvaluateBoard() below is the logistic, over LogitWeight*, and it is
        // what RolloutSearchBot and the RL reward shaping both call. Everything here feeds
        // EvaluateBoardLinear() only, which is reachable solely via search-test's
        // --linear-eval. Read the zeros as a fact about the retired linear form — search
        // is NOT blind to army; LogitWeightArmy is 2.96.
        public static float EvalWeightHp     = 0.2476f;
        public static float EvalWeightIncome = 0.7524f;
        public static float EvalWeightMoney  = 0.0000f;
        public static float EvalWeightArmy   = 0.0000f;
        public static float EvalWeightGadget = 0.0000f;
        public static float EvalWeightRepair = 0.0000f;

        /// <summary>
        /// Computes the six raw sigmoid component scores that feed into EvaluateBoard().
        /// All outputs are in [0, 1] where 0.5 = perfectly even on that dimension.
        /// </summary>
        public (float Hp, float Income, float Money, float Army, float Gadget, float Repair) GetEvalComponents()
        {
            const float MAP_WIDTH = 2000f;

            static float Sig(float x) => 1f / (1f + MathF.Exp(-x));

            float GadgetReadiness(PlayerState ps)
            {
                float score = 0f;
                void Check(GadgetDefinition g)
                {
                    if (g == null) return;
                    bool available = !ps.GadgetCooldowns.ContainsKey(g.Id) || ps.GadgetCooldowns[g.Id] <= 0;
                    if (available) score += g.Level;
                }
                Check(ps.OffensiveGadget);
                Check(ps.DefensiveGadget);
                Check(ps.SignatureGadget);
                return score;
            }

            var p1 = Player1;
            var p2 = Player2;

            // ── 1. Castle HP ─────────────────────────────────────────────────
            float p1HpPct = p1.CastleHealth / MathF.Max(p1.CastleMaxHealth, 1f);
            float p2HpPct = p2.CastleHealth / MathF.Max(p2.CastleMaxHealth, 1f);
            float hpScore = Sig(3.0f * (p1HpPct - p2HpPct));

            // ── 2. Economy ───────────────────────────────────────────────────
            float incomeScore = Sig(2.0f * (MathF.Log((float)p1.Income + 1f) - MathF.Log((float)p2.Income + 1f)));
            float moneyScore  = Sig(0.5f * (MathF.Log((float)p1.Money  + 1f) - MathF.Log((float)p2.Money  + 1f)));

            // ── 3. Army threat ───────────────────────────────────────────────
            // Threat = sum of (tier² × proximity² × hpPct) across all units.
            // Proximity is 0 at own castle, 1 at enemy castle.
            float p1Pressure = 0f, p2Pressure = 0f;
            foreach (var u in Units)
            {
                float hpPct = u.CurrentHealth / MathF.Max(u.MaxHealth, 1f);
                float tier2 = u.Tier * u.Tier;
                if (u.Side == 1)
                {
                    float prox  = u.Position / MAP_WIDTH;
                    p1Pressure += tier2 * prox * prox * hpPct;
                }
                else
                {
                    float prox  = (MAP_WIDTH - u.Position) / MAP_WIDTH;
                    p2Pressure += tier2 * prox * prox * hpPct;
                }
            }
            float armyScore = Sig(0.008f * (p1Pressure - p2Pressure));

            // ── 4. Gadget readiness ──────────────────────────────────────────
            float gadgetScore = Sig(0.3f * (GadgetReadiness(p1) - GadgetReadiness(p2)));

            // ── 5. Repair accessibility ──────────────────────────────────────
            float p1Repair = MathF.Min(1f, (float)p1.Money / MathF.Max((float)p1.RepairPrice, 1f)) * (1f - p1HpPct);
            float p2Repair = MathF.Min(1f, (float)p2.Money / MathF.Max((float)p2.RepairPrice, 1f)) * (1f - p2HpPct);
            float repairScore = Sig(1.5f * (p1Repair - p2Repair));

            return (hpScore, incomeScore, moneyScore, armyScore, gadgetScore, repairScore);
        }

        // ── Logistic evaluator weights (fit "C" from audit_evaluator.py, 2026-07-28) ──
        // These are NOT normalised and must not be — in a logistic their magnitude sets
        // how sharply the evaluation responds, not just the relative mix.
        //
        // WHY THE FORM CHANGED. The previous deployed evaluator was a linear weighted
        // average, and fitting one honestly drove Money, Army, Gadget and Repair all to
        // zero: given HP and Income, a *linear* combination gains nothing from them. That
        // left an evaluator that could not see an enemy army approaching an undamaged
        // castle — it read full HP plus rising income and concluded that saving was always
        // correct. Rollout search built on it waited 87.4% of the time, saved through
        // incoming attacks, and lost 95% of its games; 63.1% of its decisions were exact
        // ties because only two slow-moving terms were live.
        //
        // A logistic on the same six features uses all of them and calibrates materially
        // better (log-loss 0.4914 vs 0.5510). Army recovers a real weight, which is the
        // "am I about to die" signal the whole thing was missing. Marc's own read of the
        // game — spend only what you must to survive, then save, then overwhelm — needs
        // exactly that term to be expressible.
        //
        // Weights are from a 263k-row subsample with a numpy solver; refit on the full
        // dataset with train_evaluator.py when convenient.
        public static float LogitWeightHp     = 5.53f;
        public static float LogitWeightIncome = 5.26f;
        public static float LogitWeightMoney  = 2.96f;
        public static float LogitWeightArmy   = 2.96f;
        public static float LogitWeightGadget = 0.13f;
        public static float LogitWeightRepair = 2.39f;

        // ── TIME-TO-TERMINAL-STATE TERMS, added 2026-08-19 ───────────────────────────
        //
        // BOTH DEFAULT TO ZERO WEIGHT, and EvaluateBoard skips the computation entirely at
        // zero, so the deployed path is byte-for-byte unchanged and every recorded benchmark
        // still reproduces. Verified against the horizon-1600 n=20 fingerprint (9370
        // decisions / 116,369 rollouts / 160,162,670 simulated ticks).
        //
        // Screened on 800 HeuristicBot self-play games (210,104 frames, thinned to one per
        // 300 ticks, held out BY GAME) before any of this was wired up:
        //
        //   + t_arma    d-logloss -0.0184   d-AUC +0.0159   fitted weight +8.01 (largest)
        //   + t_death   d-logloss +0.0000   d-AUC -0.0014   fitted weight +0.27
        //
        // For scale, the gadget-level feature that was correctly REJECTED scored -0.0052 /
        // +0.0027, so t_arma is 3.5x that on logloss and t_death is a clean null. t_death is
        // kept anyway because the screen is demonstrably blind to army-like terms: dropping
        // the army term costs only +0.0011 logloss here, yet the linear evaluator that zeroed
        // Army made search wait 87.4% of the time and lose 95% of its games.
        //
        // The fitted +8.01 is NOT used as a default. Fitting the deployed six on this same
        // data puts MONEY's share at 0.354 -- the refit's failure zone, which scored 34.2% in
        // play -- so weights fit here are known to be pathological and the arms sweep the
        // t_arma weight instead.
        // ── MEASURED 2026-08-19: BOTH NEGATIVE OR NULL IN PLAY. SHIPPED OFF. ────────
        //
        // n=200 paired, seed 4242, horizon 1600, margin 0.10, against the deployed-six
        // control at 77.0% (154/200). McNemar exact, two-sided:
        //
        //   arm                      rate     delta    b/c      p
        //   t_arma w=1              77.0%    +0.00    6/6     1.000
        //   t_arma w=2              79.5%    +2.50    9/4     0.267
        //   t_arma w=4              76.5%    -0.50    9/10    1.000
        //   t_arma w=8              79.0%    +2.00   17/13    0.585
        //   t_death replaces army   57.0%   -20.00   11/51   <0.0001
        //
        // t_arma: NULL, and the sweep is NON-MONOTONE (w=2 and w=8 up, w=1 and w=4 flat or
        // down), which is the signature of noise rather than a dose-response. Pooled +1.0,
        // p=0.416. This is the cleanest demonstration yet of the calibration/play decoupling
        // this project keeps rediscovering: t_arma had the STRONGEST logloss signal of any
        // evaluator feature ever tried here (-0.0184, 3.5x the rejected gadget-level term)
        // and the largest fitted weight in the vector (+8.01), and it changed nothing.
        // It did move behaviour in the intended direction -- earned invests 6.62 -> 6.88
        // monotonically in weight -- so the feature works and the extra investing simply
        // does not convert. That is the eighth evaluator direction to fail.
        //
        // t_death: -20 points, decisive. Money's share was held at the deployed 0.154 by
        // giving t_death army's own 2.96, so this is NOT a money-share artefact.
        // CONFOUND, stated because it was not separated: the arm both DROPPED army and
        // ADDED t_death, so -20 is the net of the swap. It licenses "t_death is not an
        // adequate substitute for army", not "t_death is harmful". Separating it needs an
        // army-off-only arm and an army-on-plus-t_death arm; neither was run.
        //
        // THE SCREEN WAS CONFIRMED BLIND TO ARMY-LIKE TERMS, as predicted before the run.
        // Dropping the army term costs +0.0011 incremental logloss on held-out self-play
        // data -- i.e. nothing -- and 20 points in play. Do not screen a defensive/army
        // feature on calibration data; the instrument cannot see what those terms do.
        //
        // SIDE FINDING ON THE MONEY-SHARE CURVE, which is worth more than either feature.
        // The t_arma arms diluted money's share to 0.146 / 0.139 / 0.127 / 0.109 and win
        // rate was FLAT across all of them. The old ablation only sampled 0.000 (49.5-59.5%)
        // and 0.154 (74-76%), so "0.154 is the peak" was inferred from one side. It now
        // looks FLAT from 0.109 to 0.154, meaning the fall-off happens somewhere below
        // 0.109, not just under 0.154.
        public static float LogitWeightTArma = 0f;
        // ── t_death ADDED ALONGSIDE ARMY, probed 2026-08-20 ──────────────────────────
        //
        // This is the arm the 2026-08-19 note said was never run: the -20 result there came
        // from an arm that DROPPED army and ADDED t_death, and was recorded as confounded.
        // Here UseArmyTerm stays true. Decision-level probe via --nuke-repro, 12 seeds,
        // horizon 1600, on the three positions this investigation established:
        //
        //   position                     w=0            w=1              w=3
        //   7A385A t781 (0 enemy units)  spawnT3 12/12  NOTHING 0/12     defGadget 12/12
        //   FC1462 t781 (4 doggos in)    spawnT4 12/12  nuke 12/12       nuke 12/12
        //   FC1462 t916                  spawnT1 12/12  spawnT1 12/12    MACRO arma 12/12
        //
        // IT RE-RANKS RATHER THAN COMPRESSING, which is the thing the economy blend failed
        // to do -- spawning falls (7A385A spawnT3 0.6367 -> 0.5393 -> 0.3432) while REPAIR
        // rises (0.1896 -> 0.2454 -> 0.3856). That is exactly the absolute-HP sense the
        // evaluator was missing, working as intended.
        //
        // TWO WARNINGS, both visible already at probe scale:
        //
        //  1. THE DEFENCE GADGET RUNS AWAY above w=1. At w=3 it scores 0.7795 and at w=6
        //     0.9313 -- a single reinforcements cast reading as a near-certain win, because
        //     friendly HP enters t_death as an interception delay. Any weight that makes
        //     defensive casts dominate will produce gadget spam.
        //  2. THE PRIOR'S SCORE FALLS AS w RISES, so search overrides MORE, not less:
        //     FC1462 t781 prior 0.5012 -> 0.3905 -> 0.2066, with four candidates beating it
        //     at w=3. t_death systematically dislikes HeuristicBot's continuation. The
        //     project's own history says a high override rate is dangerous -- the first
        //     version of search replaced HeuristicBot entirely and lost 95% of its games.
        //
        // So only w~1 is worth measuring in play, and even there the result is mixed: it
        // fixes 7A385A t781 (the unprompted spawn stops, 0/12 overrides) but redirects
        // FC1462 t781 to firing the nuke instead. Not yet measured in play.
        //
        // NOTE TDeathScale (GameStateTimeFeatures) is still an UNFITTED 1.0 sitting inside
        // the sigmoid, so it controls curvature independently of this weight and has never
        // been swept. A disappointing play result at w~1 should sweep that before concluding
        // the feature is dead.
        //
        // ── MEASURED IN PLAY 2026-08-20: MONOTONE NEGATIVE. SHIPPED OFF. ────────────
        //
        // search-test 200, seed 4242, horizon 1600, margin 0.10, interval 15, army term ON
        // throughout. Control re-measured in THIS build because RepairHpThreshold moved to
        // 0.60 the same day, which changes both search's prior and its rollout policy:
        //
        //   w      win rate vs HeuristicBot     override   earned invests   units bought
        //   0      70.0% [63.3, 75.9]  140/60    13.8%         6.24            189.5
        //   0.5    69.0% [62.3, 75.0]  138/62    12.3%         6.20            180.9
        //   1      66.5% [59.7, 72.7]  133/67    12.4%         6.04            168.4
        //   2      60.0% [53.1, 66.5]  120/80    13.9%         5.95            178.4
        //
        // Win rate falls monotonically across all four arms and earned invests fall with it,
        // 6.24 -> 5.95. Only w=2 is individually significant (two-proportion z = 2.10,
        // p = 0.036); the case does not rest on that single arm but on the DOSE-RESPONSE,
        // which is what distinguishes this from the non-monotone t_arma sweep that was
        // correctly read as noise.
        //
        // ONE PREDICTION WAS WRONG AND SHOULD BE RECORDED AS SUCH. The probe warned that a
        // falling prior score would send the override rate up; it did not move (13.8, 12.3,
        // 12.4, 13.9). The damage is not search overriding more often, it is search
        // overriding TOWARD more defensive lines: spend-on-units drops 21,154 -> 16,786 and
        // the economy goes with it. The bot buys safety it does not need and arrives at
        // investment 8 later.
        //
        // THE DECISION-LEVEL PROBE WAS RIGHT ABOUT MECHANISM AND WRONG ABOUT VALUE. t_death
        // really does supply the absolute-HP sense the evaluator lacks -- it re-ranks rather
        // than compresses, it makes repair look better and reckless spawning look worse. That
        // is simply not what wins games here, and fixing the three hand-picked positions this
        // investigation started from made overall play worse. A decision-level probe can
        // show a term is DOING something; only play can show it HELPS.
        //
        // This is the ninth evaluator direction to fail, and both t_arma and t_death are now
        // measured null-or-negative in play in both the differential and (for t_arma) the
        // absolute search-side form. Do not re-run either without a genuinely new angle;
        // the untried knob is TDeathScale's curvature, not this weight.
        public static float LogitWeightTDeath = 0f;

        /// <summary>
        /// Set false to drop the army term, for the arm where t_death REPLACES it rather
        /// than adding to it. Giving t_death army's own 2.96 makes that swap weight-neutral,
        /// so money's share stays at the deployed 0.154 and the arm is not confounded with a
        /// money-share change -- which is the mechanism that has explained three surprising
        /// evaluator results in this project.
        /// </summary>
        public static bool UseArmyTerm = true;

        // ── Refit candidate, 2026-08-05 (fit_evaluator.py mix "B") ────────────────
        // Fit on 800 HeuristicBot self-play + 400 SearchBot-vs-HeuristicBot games —
        // the first calibration data on this project that contains HeuristicBot at
        // all. Simulation's --collect-calibration pool is league ONNX brains plus
        // Random/AntiSpam/Spam bots, and cannot include HeuristicBot because the pool
        // is typed Func<GameState,int,int> while HeuristicBot drives the engine
        // directly. So the deployed weights above were fit entirely on games between
        // players HeuristicBot beats 83-100% of the time.
        //
        // Measured on HELD-OUT games (split by game_id, never by row):
        //
        //                      heur holdout        search holdout
        //                      AUC    signal       AUC    signal
        //   deployed          0.691   0.0568      0.827   0.0778
        //   this refit        0.720   0.0863      0.842   0.0769
        //
        // 52% more signal on HeuristicBot positions. The direction moved rather than
        // just the scale (cosine 0.908 with the deployed vector), which matters: search
        // takes an argmax, and a pure rescaling would reorder nothing. Money roughly
        // doubles in weight while Income and Army fall — consistent with an agent whose
        // win condition is banking cash for the next investment.
        //
        // NOT ENABLED BY DEFAULT. EvaluateBoard() feeds RL reward shaping
        // (Simulation/Program.cs:446), so switching it changes training as well as
        // search. Gate the swap on a search-test A/B, not on calibration alone.
        //
        // CAVEAT worth keeping: on 112 recorded human games this refit ranks WORSE
        // than the deployed weights (AUC 0.597 vs 0.653), while the broad-pool fit
        // ranks best (0.685). That holdout has only ~145 losing frames, so the
        // differences are within noise — but the direction is a warning that fitting
        // tightly to HeuristicBot's manifold may generalise badly to human play.
        public static float RefitWeightHp     = 4.2072f;
        public static float RefitWeightIncome = 3.2847f;
        public static float RefitWeightMoney  = 4.8438f;
        public static float RefitWeightArmy   = 1.3432f;
        public static float RefitWeightGadget = 0.3977f;
        public static float RefitWeightRepair = 0.6353f;

        /// <summary>
        /// Same logistic form as <see cref="EvaluateBoard"/>, using the refit weights.
        /// Kept as a separate method rather than a mutable weight set so that enabling
        /// it for search cannot silently alter the training-facing evaluator.
        /// </summary>
        public float EvaluateBoardRefit()
        {
            var (hp, income, money, army, gadget, repair) = GetEvalComponents();
            float z = RefitWeightHp     * (hp     - 0.5f)
                    + RefitWeightIncome * (income - 0.5f)
                    + RefitWeightMoney  * (money  - 0.5f)
                    + RefitWeightArmy   * (army   - 0.5f)
                    + RefitWeightGadget * (gadget - 0.5f)
                    + RefitWeightRepair * (repair - 0.5f);
            return 1f / (1f + MathF.Exp(-z));
        }

        /// <summary>
        /// Board evaluation from P1's perspective, as a win probability in [0, 1].
        ///
        /// Logistic over the six components, centred so that an even position (every
        /// component 0.5) maps to exactly 0.5. Centring is what makes a zero intercept
        /// correct here — the mis-specification that wrecked the original calibration was
        /// fitting a no-intercept logistic to *uncentred* [0,1] features.
        ///
        /// NOTE: this feeds RL reward shaping as well as search
        /// (Simulation/Program.cs:446 -> batchEval), so changing it changes training.
        /// </summary>
        public float EvaluateBoard()
        {
            var (hp, income, money, army, gadget, repair) = GetEvalComponents();
            float z = LogitWeightHp     * (hp     - 0.5f)
                    + LogitWeightIncome * (income - 0.5f)
                    + LogitWeightMoney  * (money  - 0.5f)
                    + (UseArmyTerm ? LogitWeightArmy * (army - 0.5f) : 0f)
                    + LogitWeightGadget * (gadget - 0.5f)
                    + LogitWeightRepair * (repair - 0.5f);
            // Guarded on the weight, not merely multiplied by it: at zero weight the
            // component is never COMPUTED, so the default path costs nothing and cannot
            // perturb the result. See GameStateTimeFeatures.cs.
            if (LogitWeightTArma  != 0f) z += LogitWeightTArma  * (TArmaComponent()  - 0.5f);
            if (LogitWeightTDeath != 0f) z += LogitWeightTDeath * (TDeathComponent() - 0.5f);
            return 1f / (1f + MathF.Exp(-z));
        }

        /// <summary>
        /// The previous linear weighted-average evaluator, kept for A/B comparison.
        /// Retained deliberately: the switch to a logistic changes both search behaviour
        /// and RL reward shaping, so being able to measure the old form against the new one
        /// is worth more than a tidy deletion.
        /// </summary>
        public float EvaluateBoardLinear()
        {
            var (hp, income, money, army, gadget, repair) = GetEvalComponents();
            float total = EvalWeightHp + EvalWeightIncome + EvalWeightMoney
                        + EvalWeightArmy + EvalWeightGadget + EvalWeightRepair;
            return (EvalWeightHp     * hp
                  + EvalWeightIncome * income
                  + EvalWeightMoney  * money
                  + EvalWeightArmy   * army
                  + EvalWeightGadget * gadget
                  + EvalWeightRepair * repair) / total;
        }

        public int[] GetActionMask(int side)
        {
            int[] mask = new int[14];
            for (int i = 0; i < 14; i++) mask[i] = 1; // Default all to valid (1)

            var me = side == 1 ? Player1 : Player2;
            
            TeamDefinition myTeam = new TeamDefinition();
            foreach (TeamDefinition team in GameDataManager.Teams)
            {
                if (me.Team == team.Color)
                    myTeam = team;
            }

            for (int i = 0; i < myTeam.Roster.Count; i++)
            {
                if (me.Money < myTeam.Roster[i].Cost)
                {
                    mask[myTeam.Roster[i].Tier] = 0;
                }
            }

            // ARMAGEDDON is a one-time purchase and it is the last thing action 9 can ever
            // buy, so the slot is dead for the rest of the game once it has been used.
            if (me.Money < me.InvestmentPrice || me.ArmageddonUsed)
            {
                mask[9] = 0;
            }
            if (me.Money < me.RepairPrice)
            {
                mask[10] = 0;
            }

            if (me.Money < me.OffensiveGadget.Cost || (me.GadgetCooldowns.ContainsKey(me.OffensiveGadget.Id) && me.GadgetCooldowns[me.OffensiveGadget.Id] > 0))
            {
                mask[11] = 0;
            }
            if (me.Money < me.DefensiveGadget.Cost || (me.GadgetCooldowns.ContainsKey(me.DefensiveGadget.Id) && me.GadgetCooldowns[me.DefensiveGadget.Id] > 0))
            {
                mask[12] = 0;
            }
            if (me.Money < me.SignatureGadget.Cost || (me.GadgetCooldowns.ContainsKey(me.SignatureGadget.Id) && me.GadgetCooldowns[me.SignatureGadget.Id] > 0))
            {
                mask[13] = 0;
            }
            
            return mask;
        }
    }
}