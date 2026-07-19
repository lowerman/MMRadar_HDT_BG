using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MMRadar.Util;
using ScryDotNet;

namespace MMRadar.Game
{
    /// <summary>
    /// Spectate-only fallback for lobby names. While spectating, Hearthstone
    /// never sends the lobby roster to the client — but the in-game leaderboard
    /// tiles hold each player's name once the SPECTATOR mouses over the portrait,
    /// and those names persist in game memory for the rest of the match.
    ///
    /// Reads the tiles with the untapped-scry-dotnet reader that ships with HDT
    /// itself, so no extra files are distributed. Adapted from HDT_BGrank by
    /// IBM5100 (MIT); the tile traversal originates from
    /// Zero-to-Heroes/unity-spy-.net4.5.
    /// </summary>
    public class SpectateNameReader
    {
        /// <summary>Hearthstone's Unity engine version, required by the reader
        /// to locate Mono metadata. Breaks (gracefully) on engine upgrades.</summary>
        private const string UnityVersion = "2021.3.25.61228";

        private MonoImage _root;
        private DateTime _lastAttempt = DateTime.MinValue;
        private bool _connectFailed;

        private MonoImage Root
        {
            get
            {
                if (_root != null)
                    return _root;
                if (_connectFailed)
                    return null; // do not hammer a broken reader every tick
                using (var proc = Process.GetProcessesByName("Hearthstone").FirstOrDefault())
                {
                    if (proc == null)
                        return null;
                    using (var view = new MonoScry(Scry.connect(proc.Id)))
                        _root = view.getImage(new List<string> { "Blizzard.T5.ServiceLocator" }, UnityVersion);
                }
                return _root;
            }
        }

        /// <summary>
        /// Names revealed so far (hovered tiles), or null when nothing new could
        /// be read. Throttled internally — safe to call every tick.
        /// </summary>
        public IReadOnlyList<string> TryReadNames()
        {
            if ((DateTime.UtcNow - _lastAttempt).TotalSeconds < 2)
                return null;
            _lastAttempt = DateTime.UtcNow;
            try
            {
                dynamic manager = Root?["PlayerLeaderboardManager"]?["s_instance"];
                if (manager == null)
                    return null;
                var names = new List<string>();
                dynamic teams = manager["m_teams"]?["_items"];
                if (teams == null)
                    return null;
                for (uint i = 0; i < teams.size(); i++)
                {
                    dynamic team = teams[i];
                    if (team == null)
                        continue;
                    dynamic tiles = team["m_playerLeaderboardCards"]?["_items"];
                    if (tiles == null)
                        continue;
                    for (uint j = 0; j < tiles.size(); j++)
                    {
                        dynamic tile = tiles[j];
                        // Empty until the spectator mouses over this portrait.
                        string name = tile?["m_overlay"]?["m_heroActor"]?["m_playerNameText"]?["m_Text"];
                        if (string.IsNullOrWhiteSpace(name))
                            continue;
                        name = LobbyTracker.StripTag(name);
                        if (name != null && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                            names.Add(name);
                    }
                }
                return names;
            }
            catch (Exception ex)
            {
                // Typical causes: game closed mid-read, or an HS engine update
                // changed the memory layout — the spectate fallback simply stays
                // silent then, everything else keeps working.
                _connectFailed = _root == null;
                Logger.Debug("SpectateNameReader: " + ex.Message);
                return null;
            }
        }

        public void Clean()
        {
            try { _root?.Dispose(); } catch { /* best effort */ }
            _root = null;
            _connectFailed = false;
        }
    }
}
