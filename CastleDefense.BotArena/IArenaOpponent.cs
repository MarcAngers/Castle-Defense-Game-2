using CastleDefense.Engine;

namespace CastleDefense.BotArena
{
    // Common shape for anything that can drive a side in a headless simulation.
    public interface IArenaOpponent
    {
        void Update(GameEngine engine);
    }
}
