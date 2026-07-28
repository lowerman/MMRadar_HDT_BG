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

            // A fresh instance per position: lobby namesakes (same base name, different
            // battletags) each get their own summary object, so the in-game decoration
            // (hero, team, "you") of one can never bleed into the other. Each clone
            // keeps its position's EXACT casing — "Pacific" and "pacific" are distinct
            // players on the official board, and the board lookup is case-exact.
            var summaries = lobbyNames
                .Select(n =>
                {
                    if (!result.TryGetValue(n, out var s))
                        return NotFound(n);
                    var clone = Clone(s);
                    clone.LobbyName = n;
                    return clone;
                })
                .ToList();

            // NOTE: official-board data (rating authority, fallback ratings, the
            // namesake gate) is deliberately NOT applied here — the caller layers
            // it on via TryFillOfficialRatingsAsync as a SECOND paint, so a slow
            // or unreachable board can never delay the first render.

            return summaries;
        }

        /// <summary>
        /// True when the last board fill could not obtain a board for a KNOWN
        /// region — the caller may retry later in the game (dashes are curable).
        /// </summary>
        public bool LastBoardMissing { get; private set; }

        /// <summary>
        /// Fills official-board data into the summaries: ratings for untracked
        /// players, rating authority + the identity gate for tracked ones. Public
        /// so the wallii-outage path can degrade to board-only ratings.
        /// </summary>
        public async Task TryFillOfficialRatingsAsync(
            List<PlayerSummary> summaries, string region, string gameMode)
        {
            LastBoardMissing = false;
            if (_board == null || region == null || summaries.Count == 0)
                return;
            try
            {
                var board = await _board.GetBoardAsync(region, gameMode == "1").ConfigureAwait(false);
                LastBoardMissing = board == null;
                if (board == null)
                    return;

                var namesakeCandidates = new List<(PlayerSummary S, int Official)>();
                foreach (var s in summaries)
                {
                    if (s.OnLeaderboard)
                    {
                        // The official board outranks wallii for the CURRENT rating:
                        // once a player slips out of wallii's tracked top, wallii
                        // keeps carrying their last seen value forward for days.
                        // Positive evidence only — a name absent from the board must
                        // never downgrade a tracked player (name spaces can differ).
                        if (board.TryGetRating(s.LobbyName, out var official, out var exactCase))
                        {
                            // A case-folded hit on a case-AMBIGUOUS name cannot say
                            // which of the case-twins it found — leave wallii's value.
                            var caseTrusted = exactCase || !board.IsCaseAmbiguous(s.LobbyName);

                            // Shared identity evidence: how far the board value sits
                            // from the wallii identity's recent trajectory, and how
                            // fresh that trajectory actually is (snapshot times — the
                            // daily-row dates lie for frozen players).
                            var hasEnvelope = s.Envelope10Min != null && s.Envelope10Max != null;
                            var distance = !hasEnvelope ? 0
                                : official < s.Envelope10Min.Value
                                    ? s.Envelope10Min.Value - official
                                    : official > s.Envelope10Max.Value
                                        ? official - s.Envelope10Max.Value
                                        : 0;
                            var staleDays = s.LastSnapshotUtc == null
                                ? 10.0
                                : Math.Min(10.0, Math.Max(0.0,
                                    (DateTimeOffset.UtcNow - s.LastSnapshotUtc.Value).TotalDays));
                            var slack = 1000 + (int)(150 * staleDays);

                            // Freshest observation of the RIGHT person wins. wallii and
                            // the board mirrors all watch the same official ladder, so
                            // for the same player the source that saw them LATER holds
                            // the truer number: a live fetch beats everything, a fresh
                            // wallii snapshot beats an hours-old mirror copy, and a
                            // frozen wallii identity loses to any newer board data.
                            // The 30-min grace lets the board win near-ties, so the
                            // shown value does not flip between sources on refreshes.
                            // Namesakes (an impossible gap) always take the board value.
                            // Unknown wallii freshness (that one snapshot query
                            // failed): only a LIVE board may outrank it then — a
                            // stale copy must not blindly displace a value that
                            // may well be fresher.
                            var walliiNewer = s.LastSnapshotUtc != null
                                ? s.LastSnapshotUtc.Value - board.ObservedAtUtc > TimeSpan.FromMinutes(30)
                                : !board.FromLiveFetch;
                            var keepWallii = s.RegionIsCurrent && walliiNewer &&
                                (!hasEnvelope || distance <= slack);

                            if (caseTrusted && !keepWallii)
                            {
                                s.Rating = official;
                                s.Rank = board.RankOf(official);
                            }

                            // Identity gate: a huge gap means the stats belong to a
                            // namesake. Evidence-grade inputs only: exact-case hit,
                            // live-fetched board, same region, an envelope to compare.
                            // The LOCAL player is exempt — their identity is the one
                            // thing this plugin knows for certain.
                            if (exactCase && board.FromLiveFetch && s.RegionIsCurrent &&
                                !s.IsLocalPlayer && hasEnvelope && distance > slack)
                            {
                                namesakeCandidates.Add((s, official));
                            }
                        }
                        continue;
                    }
                    if (s.FallbackRating != null)
                        continue;
                    if (board.TryGetRating(s.LobbyName, out var rating))
                    {
                        s.FallbackRating = rating;
                        s.FallbackRank = board.RankOf(rating);
                    }
                    else
                    {
                        s.BelowCutoff = true;
                    }
                }

                // Lobby-wide guard: three or more simultaneous "namesakes" is not a
                // coincidence — it is a systemic divergence (a season reset, a stale
                // data source). Trust nothing the gate says in that case.
                if (namesakeCandidates.Count >= 3)
                {
                    Util.Logger.Info(
                        $"Identity gate suppressed: {namesakeCandidates.Count} simultaneous mismatches (systemic)");
                    return;
                }
                foreach (var (s, official) in namesakeCandidates)
                {
                    Util.Logger.Info(
                        $"Identity gate: '{s.LobbyName}' board {official} vs wallii envelope " +
                        $"[{s.Envelope10Min}..{s.Envelope10Max}] — stats hidden as a suspected namesake");
                    // Demote to the existing rating-only row state: every consumer
                    // (sort, header average, click gating, live dot) follows along.
                    s.NamesakeSuspected = true;
                    s.NamesakeWalliiRating = s.Envelope10Max;
                    s.OnLeaderboard = false;
                    s.FallbackRating = official;
                    s.FallbackRank = board.RankOf(official);
                    s.IsLive = false;
                    s.TwitchChannel = null;
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

                // A lobby is played on exactly ONE ladder, so only that region's row
                // describes the player sitting in it. Another region's row is either
                // a different person with the same name (wallii lowercases names and
                // keeps a single identity per name — the official boards prove those
                // collisions exist) or the same person's unrelated ladder; either way
                // its rating, rank and averages are not this lobby's. The old "else
                // take the region where they are rated highest" fallback is exactly
                // what printed an NA profile for an EU lobby.
                var row = regionRows.FirstOrDefault(r =>
                    string.Equals(r.Region, preferredRegion, StringComparison.OrdinalIgnoreCase));
                if (row == null)
                {
                    if (!string.IsNullOrWhiteSpace(preferredRegion))
                    {
                        // Untracked on THIS ladder: the row is left to the official
                        // board of the current region (rating only) or to the
                        // "<8 000" state — never to a foreign region's numbers.
                        var elsewhere = string.Join("/", regionRows
                            .Select(r => r.Region)
                            .Where(r => !string.IsNullOrWhiteSpace(r))
                            .Distinct(StringComparer.OrdinalIgnoreCase));
                        Util.Logger.Debug(
                            $"'{name}': wallii tracks this name only on {elsewhere} — " +
                            $"ignored for this {preferredRegion} lobby");
                        result[name] = NotFound(name, elsewhere);
                        continue;
                    }
                    // Region still unknown (HDT has not resolved it yet): best effort,
                    // and RegionIsCurrent stays false so no trust decision leans on it.
                    row = regionRows.OrderByDescending(r => r.Rating).First();
                }

                // Identity-envelope evidence: the wallii rating range over the fetched
                // ~10 days in the CHOSEN region (later widened by snapshots). Costs
                // nothing — the daily rows are already in hand.
                var chosenRegionRatings = stats
                    .Where(r => r.PlayerId == player.PlayerId &&
                                string.Equals(r.Region, row.Region, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Rating)
                    .ToList();

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
                    RegionIsCurrent = string.Equals(row.Region, preferredRegion, StringComparison.OrdinalIgnoreCase),
                    Envelope10Min = chosenRegionRatings.Count > 0 ? chosenRegionRatings.Min() : (int?)null,
                    Envelope10Max = chosenRegionRatings.Count > 0 ? chosenRegionRatings.Max() : (int?)null,
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
                    if (snapshots.Count > 0)
                    {
                        // Snapshot times are the honest staleness signal, and the
                        // snapshot ratings widen the identity envelope (same region
                        // as the summary — the query is keyed by s.Region).
                        s.LastSnapshotUtc = snapshots.Max(x => x.SnapshotTime);
                        var lo = snapshots.Min(x => x.Rating);
                        var hi = snapshots.Max(x => x.Rating);
                        s.Envelope10Min = s.Envelope10Min == null ? lo : Math.Min(s.Envelope10Min.Value, lo);
                        s.Envelope10Max = s.Envelope10Max == null ? hi : Math.Max(s.Envelope10Max.Value, hi);
                    }
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

        /// <summary>Independent copy for the second (board) render pass, so the
        /// background fill can never race the rows already handed to the UI.</summary>
        public static PlayerSummary CloneSummary(PlayerSummary s) => Clone(s);

        private static string CacheKey(string name, string region, string mode) =>
            $"{name.ToLowerInvariant()}|{region}|{mode}";

        private static PlayerSummary NotFound(string name, string trackedOnRegions = null) =>
            new PlayerSummary
            {
                LobbyName = name,
                OnLeaderboard = false,
                TrackedOnOtherRegions = trackedOnRegions,
            };

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
            // Identity-envelope evidence persists through the cache; the per-call
            // display flags (NamesakeSuspected etc.) deliberately do NOT — every
            // call re-evaluates them against the current board.
            Envelope10Min = s.Envelope10Min,
            Envelope10Max = s.Envelope10Max,
            LastSnapshotUtc = s.LastSnapshotUtc,
            RegionIsCurrent = s.RegionIsCurrent,
            TrackedOnOtherRegions = s.TrackedOnOtherRegions,
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
