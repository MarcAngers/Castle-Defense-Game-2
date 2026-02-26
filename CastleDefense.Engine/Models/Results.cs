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
}
