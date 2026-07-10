using CastleDefense.Engine;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    // Drives a side using one of the trained ONNX models, the same way
    // GameHostingService does for live "sp"/"watch"/"league" games: one action
    // pulled from the model every 3 ticks, applied through the same discrete
    // action-space interface (GetStateVector / GetActionMask / ApplyAction).
    public class AIModelOpponent : IArenaOpponent, IDisposable
    {
        private readonly int _side;
        private readonly AIBrain _brain;

        public AIModelOpponent(int side, string modelPath)
        {
            _side = side;
            _brain = new AIBrain(modelPath);
        }

        public void Update(GameEngine engine)
        {
            var state = engine._state;
            if (state.IsGameOver) return;
            if (state.CurrentTick % 3 != 0) return;

            var stateVector = state.GetStateVector(_side);
            var mask = state.GetActionMask(_side);
            int action = _brain.GetBestAction(stateVector, mask);
            if (action != 0) engine.ApplyAction(_side, action);
        }

        public void Dispose() => _brain.Dispose();
    }
}
