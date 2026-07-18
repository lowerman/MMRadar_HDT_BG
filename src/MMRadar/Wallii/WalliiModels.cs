using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace MMRadar.Wallii
{
    // Raw rows from the wallii.gg Supabase (PostgREST) API.

    public class WalliiPlayerRow
    {
        [JsonProperty("player_id")]
        public int PlayerId { get; set; }

        [JsonProperty("player_name")]
        public string PlayerName { get; set; }

        [JsonProperty("display_name")]
        public string DisplayName { get; set; }
    }

    public class DailyStatsRow
    {
        [JsonProperty("player_id")]
        public int PlayerId { get; set; }

        [JsonProperty("game_mode")]
        public string GameMode { get; set; }

        [JsonProperty("region")]
        public string Region { get; set; }

        [JsonProperty("day_start")]
        public string DayStart { get; set; }

        [JsonProperty("rating")]
        public int Rating { get; set; }

        [JsonProperty("rank")]
        public int Rank { get; set; }

        [JsonProperty("games_played")]
        public int GamesPlayed { get; set; }

        [JsonProperty("weekly_games_played")]
        public int WeeklyGamesPlayed { get; set; }

        [JsonProperty("day_avg")]
        public double? DayAvg { get; set; }

        [JsonProperty("weekly_avg")]
        public double? WeeklyAvg { get; set; }
    }

    /// <summary>daily_leaderboard_stats row with the embedded players join.</summary>
    public class TopStatsRow : DailyStatsRow
    {
        [JsonProperty("players")]
        public WalliiPlayerRef Player { get; set; }
    }

    public class WalliiPlayerRef
    {
        [JsonProperty("player_name")]
        public string PlayerName { get; set; }

        [JsonProperty("display_name")]
        public string DisplayName { get; set; }
    }

    public class SnapshotRow
    {
        [JsonProperty("rating")]
        public int Rating { get; set; }

        [JsonProperty("snapshot_time")]
        public DateTimeOffset SnapshotTime { get; set; }

        [JsonProperty("region")]
        public string Region { get; set; }

        [JsonProperty("game_mode")]
        public string GameMode { get; set; }
    }

    public class ChannelRow
    {
        [JsonProperty("channel")]
        public string Channel { get; set; }

        [JsonProperty("player")]
        public string Player { get; set; }

        [JsonProperty("live")]
        public bool Live { get; set; }

        [JsonProperty("youtube")]
        public string Youtube { get; set; }
    }

    // Aggregated results consumed by the UI.

    public class PlayerSummary
    {
        /// <summary>Name as seen in the game lobby (battletag suffix stripped).</summary>
        public string LobbyName { get; set; }

        /// <summary>True if the player was found on the wallii leaderboard.</summary>
        public bool OnLeaderboard { get; set; }

        public int PlayerId { get; set; }
        public string DisplayName { get; set; }
        public string Region { get; set; }
        public int Rating { get; set; }
        public int Rank { get; set; }
        public double? DayAvg { get; set; }
        public double? WeekAvg { get; set; }
        public int GamesToday { get; set; }
        public int GamesWeek { get; set; }
        public bool IsLive { get; set; }
        public string TwitchChannel { get; set; }

        public bool IsLocalPlayer { get; set; }
        public string HeroCardId { get; set; }
        public int GamePlayerId { get; set; }

        /// <summary>Duos team id (0 = unknown / solo game).</summary>
        public int TeamId { get; set; }

        /// <summary>
        /// Rating from the full official leaderboard for players wallii does not track
        /// (wallii covers only the very top of the ladder).
        /// </summary>
        public int? FallbackRating { get; set; }

        /// <summary>Rank computed from the full official leaderboard (≈, mirror lags a bit).</summary>
        public int? FallbackRank { get; set; }

        /// <summary>
        /// The official board was checked and the player is not on it — i.e. they are
        /// below the ~8000 leaderboard cutoff (as opposed to "board unavailable").
        /// </summary>
        public bool BelowCutoff { get; set; }

        /// <summary>Best available average placement (day, falling back to week).</summary>
        public double? BestAvg => WeekAvg ?? DayAvg;
    }

    public class GameRecord
    {
        public DateTimeOffset At { get; set; }
        public double Placement { get; set; }
        public int DeltaMmr { get; set; }
        public int EndingMmr { get; set; }
    }

    public class PlayerDetails
    {
        public PlayerSummary Summary { get; set; }

        /// <summary>Most recent first.</summary>
        public List<GameRecord> RecentGames { get; set; } = new List<GameRecord>();

        /// <summary>Average estimated placement over <see cref="RecentGames"/>.</summary>
        public double? RecentAvg { get; set; }

        /// <summary>Chronological ratings used for the sparkline (oldest first).</summary>
        public List<int> RatingHistory { get; set; } = new List<int>();

        // Computed from the snapshot-derived games (self-consistent with the list
        // above), rather than trusting wallii's daily/weekly aggregate columns.
        public int TodayCount { get; set; }
        public double? TodayAvg { get; set; }
        public int Week7Count { get; set; }
        public double? Week7Avg { get; set; }
    }
}
