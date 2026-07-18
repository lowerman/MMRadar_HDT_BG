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

                var players = TryFromLobbyInfo(localName, out var gameUuid);
                if (players == null)
                {
                    gameUuid = null;
                    players = TryFromPowerLog(localName);
                }
                if (players == null)
                    return null;

                AttachHeroEntities(players, gameUuid);

                return new LobbyState
                {
                    Players = players,
                    Region = region,
                    GameMode = gameMode,
                    GameUuid = gameUuid,
                };
            }
            catch (Exception ex)
            {
                Logger.Error("LobbyTracker.TryResolveLobby failed", ex);
                return null;
            }
        }

        private List<LobbyPlayerInfo> TryFromLobbyInfo(string localName, out string gameUuid)
        {
            gameUuid = null;
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
                    (PowerLogConfirmsCachedRoster() || EntitiesConfirmCachedRoster()))
                {
                    gameUuid = _lastLobbyUuid;
                    return _lastLobbyPlayers.Select(CopyPlayer).ToList();
                }
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
            gameUuid = lobbyInfo.GameUuid;
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
            // The local player appears in EVERY game's log (and in premade duos so
            // does their partner), so only OTHER lobby members are real evidence
            // that the log belongs to the remembered game and not a brand-new one.
            var overlap = _namesByPlayerId.Values.Count(n =>
                _lastLobbyPlayers.Any(p => !p.IsLocalPlayer &&
                    string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)));
            return overlap >= 2;
        }

        /// <summary>
        /// Same-game check that works in duos, where the log names never confirm the
        /// roster (only the local player is printed): the CURRENT game's hero
        /// entities must overlap the remembered roster's hero cards.
        /// </summary>
        private bool EntitiesConfirmCachedRoster()
        {
            try
            {
                var cachedCards = new HashSet<string>(
                    _lastLobbyPlayers.Select(p => NormalizeHeroCardId(p.HeroCardId)).Where(c => c != null),
                    StringComparer.OrdinalIgnoreCase);
                if (cachedCards.Count < 4)
                    return false;
                var hits = HdtCore.Game.Entities.Values.ToList()
                    .Where(e => e.IsHero && e.HasTag(GameTag.PLAYER_ID))
                    .Select(e => NormalizeHeroCardId(e.CardId))
                    .Where(c => c != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(cachedCards.Contains);
                return hits >= 4;
            }
            catch (Exception ex)
            {
                Logger.Debug("EntitiesConfirmCachedRoster: " + ex.Message);
                return false;
            }
        }

        private List<LobbyPlayerInfo> TryFromPowerLog(string localName)
        {
            // In duos the log only ever prints the local player plus the tavern
            // entity (which carries a bot-like name) — a "lobby" built from that
            // would be a fake 2-player list, so duos trusts lobby info only.
            if (HdtCore.Game.IsBattlegroundsDuosMatch && _namesByPlayerId.Count < 8)
                return null;

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
        /// <param name="gameUuid">Uuid of the lobby the roster belongs to (null when
        /// resolved from Power.log) — gates the lobby-info hero refresh below.</param>
        public void AttachHeroEntities(List<LobbyPlayerInfo> players, string gameUuid)
        {
            try
            {
                // The roster is resolved as soon as all 8 NAMES are known — usually
                // during hero select, when BattlegroundsLobbyInfo still has empty
                // hero card ids for most players. The card ids fill in as players
                // pick, and card-id matching below depends on them, so re-read them
                // on every refresh (only from the same game's lobby info).
                RefreshHeroCardIds(players, gameUuid);

                // PlayerIds are unknown when the roster came from BattlegroundsLobbyInfo
                // before Power.log was parsed — keep filling them in on every refresh,
                // otherwise no entity (hero/team/dead) data can ever be matched.
                ScanPowerLog();
                foreach (var p in players)
                    if (p.PlayerId == 0)
                        p.PlayerId = FindPlayerId(p.Name);

                var entities = HdtCore.Game.Entities.Values.ToList();

                // In duos the ids Power.log prints ("PlayerID=15") and the PLAYER_ID
                // tags on hero entities (1..8, the lobby slots) are DIFFERENT id
                // spaces, and bots never reach the log at all — the hero card id from
                // BattlegroundsLobbyInfo is the only anchor that works for all 8
                // players. Adopt the hero entity's PLAYER_ID whenever the cards match.
                foreach (var entity in entities)
                {
                    if (!entity.IsHero || !entity.HasTag(GameTag.PLAYER_ID))
                        continue;
                    var card = NormalizeHeroCardId(entity.CardId);
                    if (card == null)
                        continue;
                    var player = players.FirstOrDefault(p =>
                        card.Equals(NormalizeHeroCardId(p.HeroCardId), StringComparison.OrdinalIgnoreCase));
                    if (player == null)
                        continue;
                    // Ghost copies of hero entities exist; the one actually on the
                    // leaderboard (it has a place) is authoritative, any other copy
                    // may only fill a still-unknown id.
                    if (entity.HasTag(GameTag.PLAYER_LEADERBOARD_PLACE) || player.PlayerId == 0)
                        player.PlayerId = entity.GetTag(GameTag.PLAYER_ID);
                }

                foreach (var entity in entities)
                {
                    if (!entity.HasTag(GameTag.PLAYER_ID))
                        continue;
                    var playerId = entity.GetTag(GameTag.PLAYER_ID);
                    var player = players.FirstOrDefault(p => p.PlayerId == playerId);
                    if (player == null)
                        continue;

                    // Accept the duos team id from any entity carrying it, EXCEPT
                    // player-type entities for non-local players: their PLAYER_ID
                    // tag lives in the log id space (e.g. 15 for the tavern), which
                    // can collide with another player's hero-space id (1..8).
                    // The local player's own PLAYER entity is the one place their
                    // team id is guaranteed to appear — their hero may never get it.
                    if (entity.HasTag(GameTag.BACON_DUO_TEAM_ID) &&
                        (!entity.IsPlayer || player.IsLocalPlayer))
                        player.TeamId = entity.GetTag(GameTag.BACON_DUO_TEAM_ID);

                    if (entity.IsHero && entity.HasTag(GameTag.PLAYER_LEADERBOARD_PLACE))
                    {
                        if (string.IsNullOrEmpty(player.HeroCardId))
                            player.HeroCardId = entity.CardId;
                        player.LeaderboardPlace = entity.GetTag(GameTag.PLAYER_LEADERBOARD_PLACE);
                        player.IsDead = entity.Health <= 0;
                    }
                }

                // Note on duos: BACON_DUO_TEAM_ID sits on the hero entities of the
                // other seven players (revealed progressively in a fresh game) and,
                // for the LOCAL player only, on their PLAYER entity — the own hero
                // may never carry it. Both are covered by the loop above since team
                // ids are accepted from any entity with a PLAYER_ID tag.
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

        /// <summary>
        /// Re-reads hero card ids from BattlegroundsLobbyInfo for the CURRENT game
        /// (uuid-gated so a stale previous-game lobby can never donate hero ids).
        /// </summary>
        private static void RefreshHeroCardIds(List<LobbyPlayerInfo> players, string gameUuid)
        {
            if (gameUuid == null)
                return;
            try
            {
                var lobbyInfo = HdtCore.Game.MetaData?.BattlegroundsLobbyInfo;
                if (lobbyInfo?.Players == null || lobbyInfo.GameUuid != gameUuid)
                    return;
                foreach (var info in lobbyInfo.Players)
                {
                    if (string.IsNullOrEmpty(info.HeroCardId))
                        continue;
                    var name = StripTag(info.Name);
                    if (name == null)
                        continue;
                    var player = players.FirstOrDefault(p =>
                        string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (player != null)
                        player.HeroCardId = info.HeroCardId;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("RefreshHeroCardIds: " + ex.Message);
            }
        }

        /// <summary>
        /// Hero card ids differ between sources: lobby info may hold the base hero
        /// while the entity carries a skin (BG24_HERO_204 vs BG24_HERO_204_SKIN_E).
        /// HDT's own mapping runs first (it knows non-conventional skins once its
        /// remote data is loaded); the _SKIN_ suffix strip covers the rest.
        /// </summary>
        internal static string NormalizeHeroCardId(string cardId)
        {
            if (string.IsNullOrEmpty(cardId))
                return null;
            try
            {
                cardId = Hearthstone_Deck_Tracker.Hearthstone.BattlegroundsUtils
                             .GetOriginalHeroId(cardId) ?? cardId;
            }
            catch
            {
                // remote data not loaded yet — the suffix strip below still applies
            }
            var idx = cardId.IndexOf("_SKIN_", StringComparison.OrdinalIgnoreCase);
            return idx > 0 ? cardId.Substring(0, idx) : cardId;
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

