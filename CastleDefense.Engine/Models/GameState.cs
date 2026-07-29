using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models.Hazards;
using System.Numerics;

namespace CastleDefense.Engine.Models
{
    public class GameState
    {
        public Guid GameId { get; set; }
        public string GameMode { get; set; }
        public TeamColour Map { get; set; }
        public bool ShadowMap { get; set; }
        public bool IsGameOver { get; set; }
        public bool IsTimeLimit { get; set; } // true when the game ended by tick limit rather than castle destruction
        public int WinnerSide { get; set; } // 0 = Playing, 1 = Player 1 (Left), 2 = Player 2 (Right)

        public long CurrentTick { get; set; }

        public PlayerState Player1 { get; set; }
        public PlayerState Player2 { get; set; }

        public List<Unit> Units { get; set; } = new List<Unit>();
        public List<Hazard> Hazards { get; set; } = new List<Hazard>();

        public GameState() : this(GameDataManager.GetRandomTeam())
        {
        }

        public GameState(TeamColour map)
        {
            GameId = Guid.NewGuid();
            Units = new List<Unit>();
            ShadowMap = false;

            Player1 = new PlayerState();
            Player2 = new PlayerState();

            Map = map;

            if (Map == TeamColour.Black)
            {
                Random rand = new Random();

                // 50/50 for regular black map, or shadow version of a different map
                if (rand.Next(2) == 0)
                {
                    ShadowMap = true;
                    Map = GameDataManager.GetRandomTeam();

                    // 1/8 chance to still get the black map
                    if (Map == TeamColour.Black)
                    {
                        ShadowMap = false;
                    }
                }
            }
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
        // does use all of them and calibrates ~11% better (log-loss 0.491 vs 0.551), so
        // switching EvaluateBoard() to sigmoid(w·(x−0.5)) is a real available upgrade —
        // deliberately not done here, since it changes live game behaviour.
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

        /// <summary>
        /// Heuristic board evaluation from P1's perspective.
        /// Returns a win-probability estimate in [0, 1] where 0.5 = even game.
        /// Symmetric inputs produce exactly 0.5 at game start.
        /// Update the EvalWeight* fields above after running train_evaluator.py.
        /// </summary>
        public float EvaluateBoard()
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

            if (me.Money < me.InvestmentPrice)
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