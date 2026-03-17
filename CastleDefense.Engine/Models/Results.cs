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
        public float[] State { get; set; }
        public float Reward { get; set; }
        public bool IsDone { get; set; }
    }
}
