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
        private readonly OfficialBoardClient _board;

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

        public WalliiService(WalliiApi api, OfficialBoardClient board = null)
        {
            _api = api;
            _board = board;
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

            var summaries = lobbyNames
                .Select(n => result.TryGetValue(n, out var s) ? s : NotFound(n))
                .ToList();

            // wallii only tracks the top of the ladder; fill in plain ratings for the
            // rest from the full official leaderboard mirror.
            await FillFallbackRatingsAsync(summaries, preferredRegion, gameMode).ConfigureAwait(false);

            return summaries;
        }

        private async Task FillFallbackRatingsAsync(
            List<PlayerSummary> summaries, string region, string gameMode)
        {
            if (_board == null || region == null)
                return;
            if (!summaries.Any(s => !s.OnLeaderboard && s.FallbackRating == null))
                return;
            try
            {
                var board = await _board.GetBoardAsync(region, gameMode == "1").ConfigureAwait(false);
                if (board == null)
                    return;
                foreach (var s in summaries)
                {
                    if (s.OnLeaderboard || s.FallbackRating != null)
                        continue;
                    if (board.Ratings.TryGetValue(s.LobbyName, out var rating))
                    {
                        s.FallbackRating = rating;
                        s.FallbackRank = board.RankOf(rating);
                    }
                    else
                    {
                        s.BelowCutoff = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Util.Logger.Debug("Official-board rating fill failed: " + ex.Message);
            }
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

            // Replace wallii's server-side day/week aggregates (their calendar windows)
            // with values computed from the same snapshots the dossier uses, so the
            // lobby chip always matches the popup. wallii numbers remain the fallback.
            await RecomputeAveragesAsync(
                result.Values.Where(s => s.OnLeaderboard).ToList(), gameMode).ConfigureAwait(false);

            return result;
        }

        private async Task RecomputeAveragesAsync(List<PlayerSummary> found, string gameMode)
        {
            if (found.Count == 0)
                return;
            var tasks = found.Select(async s =>
            {
                try
                {
                    var snapshots = await _api
                        .GetSnapshotsAsync(s.PlayerId, s.Region, gameMode, limit: 200)
                        .ConfigureAwait(false);
                    var records = PlacementEstimator.BuildGameRecords(snapshots);
                    var localToday = DateTime.Now.Date;
                    var today = records.Where(r => r.At.ToLocalTime().Date == localToday).ToList();
                    var week = records.Where(r => r.At >= DateTimeOffset.UtcNow.AddDays(-7)).ToList();
                    s.DayAvg = PlacementEstimator.Average(today);
                    s.GamesToday = today.Count;
                    s.WeekAvg = PlacementEstimator.Average(week);
                    s.GamesWeek = week.Count;
                }
                catch (Exception ex)
                {
                    Util.Logger.Debug($"Recompute averages failed for {s.LobbyName}: {ex.Message}");
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
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
                    TodayCount = hit.Value.TodayCount,
                    TodayAvg = hit.Value.TodayAvg,
                    Week7Count = hit.Value.Week7Count,
                    Week7Avg = hit.Value.Week7Avg,
                };
            }

            // 200 snapshots ≈ a week of games even for very active players.
            var snapshots = await _api.GetSnapshotsAsync(summary.PlayerId, summary.Region, gameMode, limit: 200)
                .ConfigureAwait(false);
            var records = PlacementEstimator.BuildGameRecords(snapshots);
            var recent = records.Take(recentGames).ToList();

            // Compute today / last-7-days from the same derived games the popup lists,
            // so the numbers can never contradict each other (wallii's own weekly
            // aggregates reset on their server's schedule and can look wrong).
            var localToday = DateTime.Now.Date;
            var today = records.Where(r => r.At.ToLocalTime().Date == localToday).ToList();
            var week = records.Where(r => r.At >= DateTimeOffset.UtcNow.AddDays(-7)).ToList();

            var details = new PlayerDetails
            {
                Summary = summary,
                RecentGames = recent,
                RecentAvg = PlacementEstimator.Average(recent),
                RatingHistory = snapshots
                    .OrderBy(s => s.SnapshotTime)
                    .Select(s => s.Rating)
                    .ToList(),
                TodayCount = today.Count,
                TodayAvg = PlacementEstimator.Average(today),
                Week7Count = week.Count,
                Week7Avg = PlacementEstimator.Average(week),
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
            FallbackRating = s.FallbackRating,
            FallbackRank = s.FallbackRank,
            BelowCutoff = s.BelowCutoff,
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
