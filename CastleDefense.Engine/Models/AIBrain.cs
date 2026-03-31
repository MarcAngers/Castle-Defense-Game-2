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
            // 1. Convert our flat float array into a 2D Tensor of shape [1, 348]
            // (1 batch, 348 features)
            var inputTensor = new DenseTensor<float>(stateVector, new[] { 1, 348 });

            // 2. Map the tensor to the exact input name we defined in Python
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("observation", inputTensor)
            };

            // 3. Run the neural network! This takes fractions of a millisecond.
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);

            // 4. Extract the output logits (the 14 raw confidence scores)
            var output = results.First(r => r.Name == "action_logits").AsEnumerable<float>().ToArray();

            // 5. Find the index of the highest score (ArgMax). This is the AI's chosen action!
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

        public void Dispose()
        {
            _session?.Dispose();
        }
    }
}