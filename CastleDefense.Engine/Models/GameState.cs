using CastleDefense.Engine.Models.Hazards;

namespace CastleDefense.Engine.Models
{
    public class GameState
    {
        public Guid GameId { get; set; }
        public TeamColour Map { get; set; }
        public bool ShadowMap { get; set; }
        public bool IsGameOver { get; set; }
        public int WinnerSide { get; set; } // 0 = Playing, 1 = Player 1 (Left), 2 = Player 2 (Right)

        public long CurrentTick { get; set; }

        public PlayerState Player1 { get; set; }
        public PlayerState Player2 { get; set; }

        public List<Unit> Units { get; set; } = new List<Unit>();
        public List<Hazard> Hazards { get; set; } = new List<Hazard>();

        public GameState() : this(GetRandomMap())
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
                    Map = GetRandomMap();

                    // 1/8 chance to still get the black map
                    if (Map == TeamColour.Black)
                    {
                        ShadowMap = false;
                    }
                }
            }
        }

        private static TeamColour GetRandomMap()
        {
            Array values = Enum.GetValues(typeof(TeamColour));

            return (TeamColour)values.GetValue(Random.Shared.Next(values.Length));
        }
    }
}