using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MMRadar.Wallii
{
    /// <summary>
    /// High-level, cached access to wallii data keyed by in-game lobby names.
    /// </summary>
    public class WalliiService
    {
        private readonly WalliiApi _api;

        private class CacheEntry<T>
        {
            public T Value;
            public DateTime At;
        }

        private static readonly TimeSpan SummaryTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DetailsTtl = TimeSpan.FromMinutes(2);

        private readonly ConcurrentDictionary<string, CacheEntry<PlayerSummary>> _summaryCache =
            new ConcurrentDictionary<string, CacheEntry<PlayerSummary>>();
        private readonly ConcurrentDictionary<string, CacheEntry<PlayerDetails>> _detailsCache =
            new ConcurrentDictionary<string, CacheEntry<PlayerDetails>>();

        public WalliiService(WalliiApi api)
        {
            _api = api;
        }

        /// <summary>
        /// Resolves stats for a set of lobby names in one round of batched queries.
        /// Returned list preserves the order of <paramref name="lobbyNames"/>.
        /// Players that are not on the leaderboard come back with OnLeaderboard=false;
        /// API/network failures throw (and are never cached).
        /// </summary>
        public async Task<List<PlayerSummary>> GetLobbyStatsAsync(
            IReadOnlyList<string> lobbyNames, string preferredRegion, string gameMode)
        {
            var result = new Dictionary<string, PlayerSummary>(StringComparer.OrdinalIgnoreCase);
            var toFetch = new List<string>();

            foreach (var name in lobbyNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = CacheKey(name, preferredRegion, gameMode);
                if (_summaryCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < SummaryTtl)
                    result[name] = Clone(hit.Value);
                else
                    toFetch.Add(name);
            }

            if (toFetch.Count > 0)
            {
                // Let failures propagate — a network error must not be cached as "not found".
                var fetched = await FetchSummariesAsync(toFetch, preferredRegion, gameMode).ConfigureAwait(false);

                foreach (var pair in fetched)
                {
                    var key = CacheKey(pair.Key, preferredRegion, gameMode);
                    _summaryCache[key] = new CacheEntry<PlayerSummary> { Value = Clone(pair.Value), At = DateTime.UtcNow };
                    result[pair.Key] = pair.Value;
                }
            }

            return lobbyNames
                .Select(n => result.TryGetValue(n, out var s) ? s : NotFound(n))
                .ToList();
        }

        private async Task<Dictionary<string, PlayerSummary>> FetchSummariesAsync(
            List<string> names, string preferredRegion, string gameMode)
        {
            var lower = names.ToDictionary(n => n, n => n.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);

            var playersTask = _api.LookupPlayersAsync(lower.Values);
            var channelsTask = _api.GetChannelsAsync(lower.Values);
            await Task.WhenAll(WrapNonCritical(channelsTask), playersTask).ConfigureAwait(false);

            var players = playersTask.Result;
            var channels = channelsTask.Status == TaskStatus.RanToCompletion
                ? channelsTask.Result
                : new List<ChannelRow>();

            var byName = players
                .GroupBy(p => p.PlayerName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var stats = players.Count > 0
                ? await _api.GetDailyStatsAsync(players.Select(p => p.PlayerId), gameMode).ConfigureAwait(false)
                : new List<DailyStatsRow>();

            // Latest row per (player, region); then pick the preferred region if present,
            // otherwise the region where the player is rated highest.
            var latestPerRegion = stats
                .GroupBy(s => new { s.PlayerId, s.Region })
                .Select(g => g.OrderByDescending(s => s.DayStart, StringComparer.Ordinal).First())
                .GroupBy(s => s.PlayerId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var channelByPlayer = channels
                .GroupBy(c => c.Player, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var result = new Dictionary<string, PlayerSummary>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (!byName.TryGetValue(lower[name], out var player) ||
                    !latestPerRegion.TryGetValue(player.PlayerId, out var regionRows) ||
                    regionRows.Count == 0)
                {
                    result[name] = NotFound(name);
                    continue;
                }

                var row = regionRows.FirstOrDefault(r =>
                              string.Equals(r.Region, preferredRegion, StringComparison.OrdinalIgnoreCase))
                          ?? regionRows.OrderByDescending(r => r.Rating).First();

                channelByPlayer.TryGetValue(lower[name], out var channel);

                result[name] = new PlayerSummary
                {
                    LobbyName = name,
                    OnLeaderboard = true,
                    PlayerId = player.PlayerId,
                    DisplayName = string.IsNullOrWhiteSpace(player.DisplayName) ? name : player.DisplayName,
                    Region = row.Region,
                    Rating = row.Rating,
                    Rank = row.Rank,
                    DayAvg = row.DayAvg,
                    WeekAvg = row.WeeklyAvg,
                    GamesToday = row.GamesPlayed,
                    GamesWeek = row.WeeklyGamesPlayed,
                    IsLive = channel?.Live ?? false,
                    TwitchChannel = channel?.Channel,
                };
            }
            return result;
        }

        private List<PlayerSummary> _topCache;
        private string _topCacheMode;
        private DateTime _topCacheAt = DateTime.MinValue;

        /// <summary>The current highest-rated players across all regions (for the preview).</summary>
        public async Task<List<PlayerSummary>> GetTopLobbyAsync(string gameMode, int count = 8)
        {
            if (_topCache != null && _topCacheMode == gameMode &&
                DateTime.UtcNow - _topCacheAt < SummaryTtl)
                return _topCache;

            var rows = await _api.GetTopPlayersAsync(gameMode).ConfigureAwait(false);

            var top = rows
                .Where(r => r.Player != null && !string.IsNullOrWhiteSpace(r.Player.PlayerName))
                .GroupBy(r => new { r.PlayerId, r.Region })
                .Select(g => g.OrderByDescending(r => r.DayStart, StringComparer.Ordinal).First())
                .OrderByDescending(r => r.Rating)
                .Take(count)
                .ToList();

            var channels = new List<ChannelRow>();
            try
            {
                channels = await _api.GetChannelsAsync(top.Select(r => r.Player.PlayerName.ToLowerInvariant()))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Util.Logger.Debug("Top-lobby channels query failed: " + ex.Message);
            }
            var channelByPlayer = channels
                .GroupBy(c => c.Player, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var result = top.Select(r =>
            {
                channelByPlayer.TryGetValue(r.Player.PlayerName, out var channel);
                var name = string.IsNullOrWhiteSpace(r.Player.DisplayName)
                    ? r.Player.PlayerName
                    : r.Player.DisplayName;
                return new PlayerSummary
                {
                    LobbyName = name,
                    DisplayName = name,
                    OnLeaderboard = true,
                    PlayerId = r.PlayerId,
                    Region = r.Region,
                    Rating = r.Rating,
                    Rank = r.Rank,
                    DayAvg = r.DayAvg,
                    WeekAvg = r.WeeklyAvg,
                    GamesToday = r.GamesPlayed,
                    GamesWeek = r.WeeklyGamesPlayed,
                    IsLive = channel?.Live ?? false,
                    TwitchChannel = channel?.Channel,
                };
            }).ToList();

            _topCache = result;
            _topCacheMode = gameMode;
            _topCacheAt = DateTime.UtcNow;
            return result;
        }

        /// <summary>Recent inferred games + averages for the popup. Throws on API failure.</summary>
        public async Task<PlayerDetails> GetPlayerDetailsAsync(PlayerSummary summary, string gameMode, int recentGames = 10)
        {
            if (summary == null || !summary.OnLeaderboard)
                return null;

            var cacheKey = $"{summary.PlayerId}|{summary.Region}|{gameMode}";
            if (_detailsCache.TryGetValue(cacheKey, out var hit) &&
                DateTime.UtcNow - hit.At < DetailsTtl)
            {
                // Re-attach the caller's (fresher) summary instead of the cached one.
                return new PlayerDetails
                {
                    Summary = summary,
                    RecentGames = hit.Value.RecentGames,
                    RecentAvg = hit.Value.RecentAvg,
                    RatingHistory = hit.Value.RatingHistory,
                };
            }

            var snapshots = await _api.GetSnapshotsAsync(summary.PlayerId, summary.Region, gameMode).ConfigureAwait(false);
            var records = PlacementEstimator.BuildGameRecords(snapshots);
            var recent = records.Take(recentGames).ToList();

            var details = new PlayerDetails
            {
                Summary = summary,
                RecentGames = recent,
                RecentAvg = PlacementEstimator.Average(recent),
                RatingHistory = snapshots
                    .OrderBy(s => s.SnapshotTime)
                    .Select(s => s.Rating)
                    .ToList(),
            };
            _detailsCache[cacheKey] = new CacheEntry<PlayerDetails> { Value = details, At = DateTime.UtcNow };
            return details;
        }

        private static string CacheKey(string name, string region, string mode) =>
            $"{name.ToLowerInvariant()}|{region}|{mode}";

        private static PlayerSummary NotFound(string name) =>
            new PlayerSummary { LobbyName = name, OnLeaderboard = false };

        private static PlayerSummary Clone(PlayerSummary s) => new PlayerSummary
        {
            LobbyName = s.LobbyName,
            OnLeaderboard = s.OnLeaderboard,
            PlayerId = s.PlayerId,
            DisplayName = s.DisplayName,
            Region = s.Region,
            Rating = s.Rating,
            Rank = s.Rank,
            DayAvg = s.DayAvg,
            WeekAvg = s.WeekAvg,
            GamesToday = s.GamesToday,
            GamesWeek = s.GamesWeek,
            IsLive = s.IsLive,
            TwitchChannel = s.TwitchChannel,
        };

        private static async Task WrapNonCritical(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Util.Logger.Debug("Non-critical wallii query failed: " + ex.Message);
            }
        }
    }
}
