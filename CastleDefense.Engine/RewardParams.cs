using System.IO;
using System.Text.Json;

namespace CastleDefense.Engine
{
    public class RewardParams
    {
        // === ENDGAME ===
        /// <summary>Terminal reward magnitude for a win or loss. Time-limit outcomes use 0.45× (win) and 0.55× (loss).</summary>
        public float WinReward     { get; set; } = 54000f;

        // === ECONOMY ===
        /// <summary>Base reward granted each time the player successfully invests.</summary>
        public float InvestReward  { get; set; } = 3000f;

        /// <summary>Extra reward per invest that tapers as the economy matures: bonus = (11 - investCount) × InvestDecay.</summary>
        public float InvestDecay   { get; set; } = 800f;

        /// <summary>Max penalty applied when spending money while already close to the next invest threshold.</summary>
        public float AntiSpend     { get; set; } = 700f;

        /// <summary>Per-tick weight for the tent-shape savings progress reward toward the invest threshold.</summary>
        public float SavingsWeight { get; set; } = 0.1f;

        // === COMBAT ===
        /// <summary>Multiplier on all combat rewards: kills, deaths, and castle damage dealt/taken. Internal ratios stay fixed.</summary>
        public float CombatScale   { get; set; } = 1.0f;

        // === UPGRADES ===
        /// <summary>Reward granted each time a gadget levels up.</summary>
        public float GadgetUpgrade { get; set; } = 1700f;

        /// <summary>Reward for successfully activating a gadget (dense-phase only, pre-divisor). Currently 45f ≈ 0.05 normalized.</summary>
        public float GadgetUse     { get; set; } = 45f;

        public static RewardParams Default { get; } = new RewardParams();

        public static RewardParams LoadFromJson(string path)
        {
            if (!File.Exists(path))
                return Default;
            try
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<RewardParams>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? Default;
            }
            catch
            {
                return Default;
            }
        }
    }
}
