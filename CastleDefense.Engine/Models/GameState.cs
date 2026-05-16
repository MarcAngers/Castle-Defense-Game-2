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