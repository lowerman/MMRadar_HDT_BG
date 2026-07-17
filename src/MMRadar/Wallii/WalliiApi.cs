using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace MMRadar.Wallii
{
    /// <summary>
    /// Thin client for the public wallii.gg Supabase (PostgREST) API — the same backend
    /// the wallii.gg site queries directly from the browser (see github.com/JimLiu0/wall-lii-app).
    /// The anon key is public by design (shipped in their client bundle, read-only via RLS).
    /// </summary>
    public class WalliiApi
    {
        public const string DefaultBaseUrl = "https://xtivasurpzvcbomieuba.supabase.co/rest/v1";
        public const string DefaultAnonKey =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Inh0aXZhc3VycHp2Y2JvbWlldWJhIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NDQzMTUzODgsImV4cCI6MjA1OTg5MTM4OH0.Opd3c-esvzBd-CWBDSSV7XFB2JCF2LlyevrE2Yr054U";

        private readonly HttpClient _http;
        private readonly string _baseUrl;

        public WalliiApi(string baseUrl = null, string anonKey = null)
        {
            // HDT targets net472 where TLS 1.2 is not always on by default.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
            var key = string.IsNullOrWhiteSpace(anonKey) ? DefaultAnonKey : anonKey;

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.Add("apikey", key);
            _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + key);
            _http.DefaultRequestHeaders.Add("User-Agent", "MMRadar-HDT-Plugin");
        }

        public async Task<List<WalliiPlayerRow>> LookupPlayersAsync(IEnumerable<string> lowercaseNames)
        {
            var names = lowercaseNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            if (names.Count == 0)
                return new List<WalliiPlayerRow>();
            var inList = BuildQuotedInList(names);
            var url = $"{_baseUrl}/players?select=player_id,player_name,display_name&player_name=in.{inList}";
            return await GetAsync<List<WalliiPlayerRow>>(url).ConfigureAwait(false) ?? new List<WalliiPlayerRow>();
        }

        /// <summary>
        /// Latest daily leaderboard rows for the given players (any region), recent days only.
        /// </summary>
        public async Task<List<DailyStatsRow>> GetDailyStatsAsync(IEnumerable<int> playerIds, string gameMode, int daysBack = 10)
        {
            var ids = playerIds.Distinct().ToList();
            if (ids.Count == 0)
                return new List<DailyStatsRow>();
            // InvariantCulture is essential: default calendars like Thai Buddhist would
            // produce a future "year" and silently empty the result set.
            var since = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var url = $"{_baseUrl}/daily_leaderboard_stats" +
                      "?select=player_id,game_mode,region,day_start,rating,rank,games_played,weekly_games_played,day_avg,weekly_avg" +
                      $"&player_id=in.({string.Join(",", ids)})" +
                      $"&game_mode=eq.{Uri.EscapeDataString(gameMode)}" +
                      $"&day_start=gte.{since}" +
                      "&order=day_start.desc" +
                      "&limit=1000";
            return await GetAsync<List<DailyStatsRow>>(url).ConfigureAwait(false) ?? new List<DailyStatsRow>();
        }

        /// <summary>Recent daily rows for the highest-rated players (all regions), names embedded.</summary>
        public async Task<List<TopStatsRow>> GetTopPlayersAsync(string gameMode, int limit = 60)
        {
            var since = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var url = $"{_baseUrl}/daily_leaderboard_stats" +
                      "?select=player_id,region,game_mode,day_start,rating,rank,games_played,weekly_games_played,day_avg,weekly_avg,players!inner(player_name,display_name)" +
                      $"&game_mode=eq.{Uri.EscapeDataString(gameMode)}" +
                      $"&day_start=gte.{since}" +
                      "&order=rating.desc" +
                      $"&limit={limit}";
            return await GetAsync<List<TopStatsRow>>(url).ConfigureAwait(false) ?? new List<TopStatsRow>();
        }

        public async Task<List<SnapshotRow>> GetSnapshotsAsync(int playerId, string region, string gameMode, int limit = 60)
        {
            var url = $"{_baseUrl}/leaderboard_snapshots" +
                      "?select=rating,snapshot_time,region,game_mode" +
                      $"&player_id=eq.{playerId}" +
                      $"&region=eq.{Uri.EscapeDataString(region)}" +
                      $"&game_mode=eq.{Uri.EscapeDataString(gameMode)}" +
                      "&order=snapshot_time.desc" +
                      $"&limit={limit}";
            return await GetAsync<List<SnapshotRow>>(url).ConfigureAwait(false) ?? new List<SnapshotRow>();
        }

        public async Task<List<ChannelRow>> GetChannelsAsync(IEnumerable<string> lowercaseNames)
        {
            var names = lowercaseNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            if (names.Count == 0)
                return new List<ChannelRow>();
            var inList = BuildQuotedInList(names);
            var url = $"{_baseUrl}/channels?select=channel,player,live,youtube&player=in.{inList}";
            return await GetAsync<List<ChannelRow>>(url).ConfigureAwait(false) ?? new List<ChannelRow>();
        }

        private async Task<T> GetAsync<T>(string url) where T : class
        {
            Exception last = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var response = await _http.GetAsync(url).ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                            throw new WalliiAuthException(
                                "wallii Supabase anon key was rejected (401). The key may have rotated — " +
                                "update WalliiAnonKey in the plugin settings file.");
                        response.EnsureSuccessStatusCode();
                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return JsonConvert.DeserializeObject<T>(json);
                    }
                }
                catch (WalliiAuthException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
            throw new WalliiApiException($"wallii API request failed: {url}", last);
        }

        /// <summary>Builds a PostgREST quoted in-list: ("a","b") — URL-encoded.</summary>
        private static string BuildQuotedInList(IEnumerable<string> values)
        {
            var sb = new StringBuilder("(");
            var first = true;
            foreach (var v in values)
            {
                if (!first)
                    sb.Append(",");
                first = false;
                sb.Append('"').Append(v.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }
            sb.Append(")");
            return Uri.EscapeDataString(sb.ToString());
        }
    }

    public class WalliiApiException : Exception
    {
        public WalliiApiException(string message, Exception inner) : base(message, inner) { }
    }

    public class WalliiAuthException : Exception
    {
        public WalliiAuthException(string message) : base(message) { }
    }
}
