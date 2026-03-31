using System;
using System.Collections.Generic;
using System.Text;

namespace CastleDefense.Engine.Models
{
    public record GameListResult
    {
        public IEnumerable<string> ActiveGames { get; init; }
        public IEnumerable<string> LobbyGames { get; init; }
    }

    public class StepResult
    {
        public float[] P1State { get; set; }
        public float[] P2State { get; set; }

        public int[] P1ActionMask { get; set; }
        public int[] P2ActionMask { get; set; }

        public float P1Reward { get; set; }
        public float P2Reward { get; set; }

        public bool IsDone { get; set; }
    }
}
