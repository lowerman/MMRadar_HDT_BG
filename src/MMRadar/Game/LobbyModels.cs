using System.Collections.Generic;

namespace MMRadar.Game
{
    public class LobbyPlayerInfo
    {
        /// <summary>Battletag name without the #1234 suffix.</summary>
        public string Name { get; set; }

        /// <summary>In-game PLAYER_ID (0 when unknown).</summary>
        public int PlayerId { get; set; }

        public string HeroCardId { get; set; }
        public bool IsLocalPlayer { get; set; }
        public bool IsDead { get; set; }

        /// <summary>Current in-game leaderboard place (1-8, 0 when unknown).</summary>
        public int LeaderboardPlace { get; set; }
    }

    public class LobbyState
    {
        public List<LobbyPlayerInfo> Players { get; set; } = new List<LobbyPlayerInfo>();

        /// <summary>Leaderboard region: NA / EU / AP / CN (null when unknown).</summary>
        public string Region { get; set; }

        /// <summary>wallii game_mode: "0" = solo, "1" = duos.</summary>
        public string GameMode { get; set; }
    }
}
