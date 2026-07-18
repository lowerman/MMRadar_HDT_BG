using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MMRadar.Util;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Enums;
using HdtCore = Hearthstone_Deck_Tracker.API.Core;

namespace MMRadar.Game
{
    /// <summary>
    /// Resolves the 8 Battlegrounds lobby players from HDT.
    ///
    /// Primary source: HdtCore.Game.MetaData.BattlegroundsLobbyInfo - HDT polls the game's
    /// memory (HearthMirror) for the full lobby list (names + hero card ids).
    /// Fallback: the "GameState.DebugPrintGame() - PlayerID=N, PlayerName=X" lines that
    /// Hearthstone writes to Power.log at game creation, exposed via HdtCore.Game.PowerLog.
    /// </summary>
    public class LobbyTracker
    {
        private static readonly Regex PlayerLineRegex = new Regex(
            @"PlayerID=(?<id>\d+), PlayerName=(?<name>.+?)\s*$",
            RegexOptions.Compiled);

        private const string UnknownPlayer = "UNKNOWN HUMAN PLAYER";

        private int _powerLogIndex;
        private readonly Dictionary<int, string> _namesByPlayerId = new Dictionary<int, string>();

        /// <summary>
        /// GameUuid of the last lobby we resolved via BattlegroundsLobbyInfo. HDT's watcher
        /// may briefly serve the previous game's lobby at the start of a new match, so a
        /// UUID we have already consumed is treated as stale. Deliberately NOT cleared in
        /// <see cref="Reset"/> — it must survive across games within the plugin's lifetime.
        /// </summary>
        private string _consumedGameUuid;

        /// <summary>
        /// Roster of the last lobby resolved via BattlegroundsLobbyInfo, kept so that a
        /// reconnect into the SAME game (same uuid, which is otherwise treated as stale)
        /// can restore the full player list instead of limping on a partial Power.log.
        /// </summary>
        private string _lastLobbyUuid;
        private List<LobbyPlayerInfo> _lastLobbyPlayers;

        public void Reset()
        {
            _powerLogIndex = 0;
            _namesByPlayerId.Clear();
        }

        /// <summary>
        /// Marks whatever BattlegroundsLobbyInfo HDT currently holds as belonging to a
        /// finished game. HDT never clears GameMetaData between matches, so at the start
        /// of the next game the watcher may still serve the previous lobby.
        /// </summary>
        public void MarkCurrentLobbyInfoStale()
        {
            try
            {
                var uuid = HdtCore.Game?.MetaData?.BattlegroundsLobbyInfo?.GameUuid;
                if (!string.IsNullOrEmpty(uuid))
                    _consumedGameUuid = uuid;
            }
            catch (Exception ex)
            {
                Logger.Debug("MarkCurrentLobbyInfoStale: " + ex.Message);
            }
        }

        /// <summary>
        /// Attempts to resolve the full lobby. Returns null until enough information exists.
        /// </summary>
        public LobbyState TryResolveLobby()
        {
            try
            {
                var region = MapRegion();
                var gameMode = HdtCore.Game.IsBattlegroundsDuosMatch ? "1" : "0";
                var localName = StripTag(HdtCore.Game.Player?.Name);

                ScanPowerLog();

                var players = TryFromLobbyInfo(localName) ?? TryFromPowerLog(localName);
                if (players == null)
                    return null;

                AttachHeroEntities(players);

                return new LobbyState
                {
                    Players = players,
                    Region = region,
                    GameMode = gameMode,
                };
            }
            catch (Exception ex)
            {
                Logger.Error("LobbyTracker.TryResolveLobby failed", ex);
                return null;
            }
        }

        private List<LobbyPlayerInfo> TryFromLobbyInfo(string localName)
        {
            var lobbyInfo = HdtCore.Game.MetaData?.BattlegroundsLobbyInfo;
            var lobbyPlayers = lobbyInfo?.Players;
            if (lobbyPlayers == null || lobbyPlayers.Count < 8)
                return null;

            // Guard against HDT's watcher still serving the previous game's lobby —
            // but a reconnect into the SAME game restores the remembered roster.
            // HDT never clears MetaData, so a stale uuid can also mean "a brand new
            // game (e.g. spectating) whose lobby info has not surfaced yet": only
            // trust the cached roster once the current Power.log confirms its names.
            if (lobbyInfo.GameUuid != null && lobbyInfo.GameUuid == _consumedGameUuid)
            {
                if (lobbyInfo.GameUuid == _lastLobbyUuid && _lastLobbyPlayers != null &&
                    PowerLogConfirmsCachedRoster())
                    return _lastLobbyPlayers.Select(CopyPlayer).ToList();
                return null;
            }

            var result = new List<LobbyPlayerInfo>();
            foreach (var p in lobbyPlayers)
            {
                var name = StripTag(p.Name);
                if (string.IsNullOrWhiteSpace(name) || name == UnknownPlayer)
                    return null; // wait until every slot has resolved
                if (result.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                result.Add(new LobbyPlayerInfo
                {
                    Name = name,
                    HeroCardId = p.HeroCardId,
                    IsLocalPlayer = localName != null &&
                                    string.Equals(name, localName, StringComparison.OrdinalIgnoreCase),
                    PlayerId = FindPlayerId(name),
                });
            }
            if (result.Count == 0)
                return null;
            _consumedGameUuid = lobbyInfo.GameUuid;
            _lastLobbyUuid = lobbyInfo.GameUuid;
            _lastLobbyPlayers = result.Select(CopyPlayer).ToList();
            return result;
        }

        private static LobbyPlayerInfo CopyPlayer(LobbyPlayerInfo p) => new LobbyPlayerInfo
        {
            Name = p.Name,
            PlayerId = p.PlayerId,
            HeroCardId = p.HeroCardId,
            IsLocalPlayer = p.IsLocalPlayer,
        };

        /// <summary>
        /// True when the names printed to the CURRENT game's Power.log overlap the
        /// remembered roster — i.e. this really is a reconnect into the same game and
        /// not a new game (spectating, next match) with stale metadata.
        /// </summary>
        private bool PowerLogConfirmsCachedRoster()
        {
            if (_namesByPlayerId.Count < 2)
                return false; // too early to tell — wait for the log instead of guessing
            var overlap = _namesByPlayerId.Values.Count(n =>
                _lastLobbyPlayers.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)));
            return overlap >= 2;
        }

        private List<LobbyPlayerInfo> TryFromPowerLog(string localName)
        {
            // All 8 lobby names are printed at game creation; accept a partial lobby
            // only later in the game so we do not show an incomplete list at turn 1.
            var haveAll = _namesByPlayerId.Count >= 8;
            var lateEnough = SafeTurnNumber() >= 3 && _namesByPlayerId.Count >= 2;
            if (!haveAll && !lateEnough)
                return null;

            return _namesByPlayerId
                .OrderBy(kv => kv.Key)
                .Select(kv => new LobbyPlayerInfo
                {
                    Name = kv.Value,
                    PlayerId = kv.Key,
                    IsLocalPlayer = localName != null &&
                                    string.Equals(kv.Value, localName, StringComparison.OrdinalIgnoreCase),
                })
                .ToList();
        }

        private void ScanPowerLog()
        {
            var log = HdtCore.Game.PowerLog;
            if (log == null)
                return;
            if (_powerLogIndex > log.Count)
                _powerLogIndex = 0; // log was reset by a new game

            for (var i = _powerLogIndex; i < log.Count; i++)
            {
                var line = log[i];
                // Anchor to the exact game-creation print to avoid matching any other
                // log format that happens to contain "PlayerID=".
                if (line == null || !line.Contains("DebugPrintGame()") || !line.Contains("PlayerID="))
                    continue;
                var m = PlayerLineRegex.Match(line);
                if (!m.Success)
                    continue;
                var name = StripTag(m.Groups["name"].Value);
                if (string.IsNullOrWhiteSpace(name) || name == UnknownPlayer)
                    continue;
                var id = int.Parse(m.Groups["id"].Value);
                _namesByPlayerId[id] = name;
            }
            _powerLogIndex = log.Count;
        }

        private int FindPlayerId(string name)
        {
            foreach (var kv in _namesByPlayerId)
                if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase))
                    return kv.Key;
            return 0;
        }

        /// <summary>Fills hero card ids, duos team ids and live state from game entities.</summary>
        public void AttachHeroEntities(List<LobbyPlayerInfo> players)
        {
            try
            {
                // PlayerIds are unknown when the roster came from BattlegroundsLobbyInfo
                // before Power.log was parsed — keep filling them in on every refresh,
                // otherwise no entity (hero/team/dead) data can ever be matched.
                ScanPowerLog();
                foreach (var p in players)
                    if (p.PlayerId == 0)
                        p.PlayerId = FindPlayerId(p.Name);

                foreach (var entity in HdtCore.Game.Entities.Values.ToList())
                {
                    if (!entity.HasTag(GameTag.PLAYER_ID))
                        continue;
                    var playerId = entity.GetTag(GameTag.PLAYER_ID);
                    var player = players.FirstOrDefault(p => p.PlayerId == playerId);
                    if (player == null)
                        continue;

                    // The duos team id lives on player-type entities (heroes may not
                    // carry it) — accept it from whichever entity has the tag.
                    if (entity.HasTag(GameTag.BACON_DUO_TEAM_ID))
                        player.TeamId = entity.GetTag(GameTag.BACON_DUO_TEAM_ID);

                    if (entity.IsHero && entity.HasTag(GameTag.PLAYER_LEADERBOARD_PLACE))
                    {
                        if (string.IsNullOrEmpty(player.HeroCardId))
                            player.HeroCardId = entity.CardId;
                        player.LeaderboardPlace = entity.GetTag(GameTag.PLAYER_LEADERBOARD_PLACE);
                        player.IsDead = entity.Health <= 0;
                    }
                }

                // Note on duos: BACON_DUO_TEAM_ID lives on hero entities that are
                // revealed progressively during the first minutes of the game, so
                // team ids trickle in — the panel regroups as they become known.
                // (Leaderboard slots are NOT shared by teammates, so they cannot
                // be used to pair players.)
            }
            catch (Exception ex)
            {
                Logger.Debug("AttachHeroEntities: " + ex.Message);
            }
        }

        private static int SafeTurnNumber()
        {
            try { return HdtCore.Game.GetTurnNumber(); }
            catch { return 0; }
        }

        private static string MapRegion()
        {
            switch (HdtCore.Game.CurrentRegion)
            {
                case Region.US: return "NA";
                case Region.EU: return "EU";
                case Region.ASIA: return "AP";
                case Region.CHINA: return "CN";
                default: return null;
            }
        }

        internal static string StripTag(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            name = name.Trim();
            var idx = name.IndexOf('#');
            return idx > 0 ? name.Substring(0, idx) : name;
        }
    }
}

