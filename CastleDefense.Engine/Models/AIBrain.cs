using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CastleDefense.Engine.Models
{
    public class AIBrain : IDisposable
    {
        private readonly InferenceSession _session;

        public AIBrain(string modelFilePath)
        {
            // Loads the ONNX file (and automatically finds the .data file next to it)
            _session = new InferenceSession(modelFilePath);
        }

        public int GetBestAction(float[] stateVector, int[] actionMask)
        {
            var output = GetRawLogits(stateVector);

            // Find the index of the highest score (ArgMax). This is the AI's chosen action!
            int bestAction = 0;
            float maxScore = float.MinValue;

            for (int i = 0; i < output.Length; i++)
            {
                if (actionMask[i] == 0) continue;

                if (output[i] > maxScore)
                {
                    maxScore = output[i];
                    bestAction = i;
                }
            }

            return bestAction;
        }

        // Exposes the raw (pre-mask, pre-argmax) action logits -- used by diagnostic
        // tooling to measure real policy entropy/confidence on real game states,
        // rather than just the argmax choice.
        public float[] GetRawLogits(float[] stateVector)
        {
            var inputTensor = new DenseTensor<float>(stateVector, new[] { 1, 348 });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("observation", inputTensor)
            };
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);
            return results.First(r => r.Name == "action_logits").AsEnumerable<float>().ToArray();
        }

        public void Dispose()
        {
            _session?.Dispose();
        }
    }
}