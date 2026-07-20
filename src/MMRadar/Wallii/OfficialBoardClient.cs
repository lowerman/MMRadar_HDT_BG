using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using MMRadar.Util;

namespace MMRadar.Wallii
{
    /// <summary>A region's full official leaderboard with rank lookups.</summary>
    public class OfficialBoard
    {
        private readonly int[] _ratingsAscending;
        private readonly Dictionary<string, int> _exact;
        private readonly Dictionary<string, int> _folded;
        private readonly HashSet<string> _caseAmbiguous;

        public int Count => _ratingsAscending.Length;

        /// <summary>False when this board was read from the offline disk copy —
        /// evidence-grade decisions (the identity gate) require a live fetch.</summary>
        public bool FromLiveFetch { get; }

        public OfficialBoard(
            Dictionary<string, int> exact, Dictionary<string, int> folded,
            HashSet<string> caseAmbiguous, bool fromLiveFetch)
        {
            _exact = exact;
            _folded = folded;
            _caseAmbiguous = caseAmbiguous;
            FromLiveFetch = fromLiveFetch;
            _ratingsAscending = exact.Values.ToArray();
            Array.Sort(_ratingsAscending);
        }

        /// <summary>
        /// Exact-case match first: distinct players can differ by letter case
        /// alone ("Pacific" 10356 vs "pacific" 8007 coexist on the EU board),
        /// and the in-game lobby name carries the canonical casing. The folded
        /// lookup is only a fallback for sources that lost the original case.
        /// </summary>
        public bool TryGetRating(string name, out int rating) =>
            TryGetRating(name, out rating, out _);

        public bool TryGetRating(string name, out int rating, out bool exactCase)
        {
            exactCase = _exact.TryGetValue(name, out rating);
            return exactCase || _folded.TryGetValue(name, out rating);
        }

        /// <summary>True when the board holds two or more case-variants of this
        /// name — a folded match then cannot say WHICH player it found.</summary>
        public bool IsCaseAmbiguous(string name) => _caseAmbiguous.Contains(name);

        /// <summary>1-based rank for a rating; ties share the better rank.</summary>
        public int RankOf(int rating)
        {
            // first index whose value is strictly greater than `rating`
            int lo = 0, hi = _ratingsAscending.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (_ratingsAscending[mid] <= rating)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return _ratingsAscending.Length - lo + 1;
        }
    }

    /// <summary>
    /// Full official Battlegrounds leaderboard (every player above the ~8000 cutoff),
    /// served by IBM5100's public BGrank mirror (github.com/IBM5100o/BGrank_bot) —
    /// the same source the original HDT_BGrank plugin uses. wallii only tracks the
    /// very top of the ladder, so this fills in ratings for everyone else.
    /// </summary>
    public class OfficialBoardClient
    {
        public const string DefaultBaseUrl = "https://bgrank.fly.dev";

        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

        /// <summary>
        /// How long the on-disk board copy stays trustworthy. It exists to bridge
        /// short mirror outages; without an age cap a copy taken before a season
        /// reset would resurface last season's ratings as current data.
        /// </summary>
        private static readonly TimeSpan OfflineCopyMaxAge = TimeSpan.FromHours(48);

        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _cacheDir;

        private class BoardEntry
        {
            public OfficialBoard Board;
            public DateTime At;
        }

        private readonly ConcurrentDictionary<string, BoardEntry> _cache =
            new ConcurrentDictionary<string, BoardEntry>(StringComparer.OrdinalIgnoreCase);

        public OfficialBoardClient(string baseUrl = null, string cacheDir = null)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
            _cacheDir = cacheDir;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.Add("User-Agent", "MMRadar-HDT-Plugin");
        }

        /// <summary>
        /// The whole official leaderboard of a region (name → rating + rank lookups).
        /// Returns null when the board cannot be fetched and no offline copy exists.
        /// </summary>
        public async Task<OfficialBoard> GetBoardAsync(string region, bool duos)
        {
            var key = MapRegion(region);
            if (key == null)
                return null;
            if (duos)
                key += "_duo";

            if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTtl)
                return hit.Board;

            string text = null;
            var live = true;
            try
            {
                text = await _http.GetStringAsync($"{_baseUrl}/{key}/").ConfigureAwait(false);
                SaveOfflineCopy(key, text);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Official board fetch failed for {key}: {ex.Message}");
                live = false;
                text = LoadOfflineCopy(key);
                if (text == null)
                {
                    // Keep serving a stale in-memory board rather than nothing.
                    return _cache.TryGetValue(key, out var stale) ? stale.Board : null;
                }
            }

            var board = Parse(text, live);
            if (board == null || board.Count == 0)
                return _cache.TryGetValue(key, out var stale) ? stale.Board : null;

            _cache[key] = new BoardEntry { Board = board, At = DateTime.UtcNow };
            return board;
        }

        private static OfficialBoard Parse(string text, bool fromLiveFetch)
        {
            // Format (see BGrank_bot): lines of "playerName rating" separated by
            // "\n<br />", sorted by rating descending. Names are kept CASE-EXACT
            // ("Pacific" and "pacific" are different players); each dictionary
            // keeps the first (highest-rated) entry per key.
            var exact = new Dictionary<string, int>(StringComparer.Ordinal);
            var folded = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var caseAmbiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in text.Split(new[] { "\n<br />" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = line.LastIndexOf(' ');
                if (idx <= 0)
                    continue;
                var name = line.Substring(0, idx).Trim();
                if (name.Length == 0)
                    continue;
                if (!int.TryParse(line.Substring(idx + 1).Trim(), out var rating))
                    continue;
                if (!exact.ContainsKey(name))
                {
                    exact[name] = rating;
                    // A second distinct case-variant of an already-folded name means
                    // a folded lookup can no longer identify which player it hits.
                    if (folded.ContainsKey(name))
                        caseAmbiguous.Add(name);
                }
                if (!folded.ContainsKey(name))
                    folded[name] = rating;
            }
            return new OfficialBoard(exact, folded, caseAmbiguous, fromLiveFetch);
        }

        private static string MapRegion(string region)
        {
            switch (region?.ToUpperInvariant())
            {
                case "NA": return "US";
                case "EU": return "EU";
                case "AP": return "AP";
                case "CN": return "CN";
                default: return null;
            }
        }

        private string OfflinePath(string key) =>
            _cacheDir == null ? null : Path.Combine(_cacheDir, $"leaderboard_{key}.txt");

        private void SaveOfflineCopy(string key, string text)
        {
            try
            {
                var path = OfflinePath(key);
                if (path == null)
                    return;
                Directory.CreateDirectory(_cacheDir);
                File.WriteAllText(path, text);
            }
            catch (Exception ex)
            {
                Logger.Debug("Failed to save offline board copy: " + ex.Message);
            }
        }

        private string LoadOfflineCopy(string key)
        {
            try
            {
                var path = OfflinePath(key);
                if (path == null || !File.Exists(path))
                    return null;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > OfflineCopyMaxAge)
                    return null;
                return File.ReadAllText(path);
            }
            catch
            {
                return null;
            }
        }
    }
}
