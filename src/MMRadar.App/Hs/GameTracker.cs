using System;
using System.Collections.Generic;
using System.Linq;
using MMRadar.Game;

namespace MMRadar.Hs
{
    /// <summary>
    /// Standalone stand-in for the plugin's LobbyTracker: same surface for the
    /// core, but every fact comes from <see cref="HearthstoneReader"/> snapshots
    /// (the game's own memory) instead of HDT.
    /// </summary>
    public sealed class GameTracker
    {
        private readonly HearthstoneReader _reader;

        public GameTracker(HearthstoneReader reader)
        {
            _reader = reader;
        }

        private GameSnapshot Snap => _reader.Latest;

        public bool HearthstoneRunning => Snap.Connected;

        /// <summary>True while a Battlegrounds match is on screen.</summary>
        public bool InBattlegroundsMatch
        {
            get
            {
                var s = Snap;
                return s.Connected && s.InGameplay && s.IsBattlegrounds;
            }
        }

        /// <summary>
        /// The game state reader sees every contestant directly, so spectated
        /// games need no special roster path. While spectating, the user's own
        /// name is simply not in the lobby and no row turns into "you".
        /// </summary>
        public bool IsSpectating => false;

        public void Reset()
        {
        }

        public void MarkCurrentLobbyInfoStale()
        {
        }

        /// <summary>
        /// The lobby once enough contestants are named: all 8 in matchmade games
        /// (the names surface during hero select), 2+ in a private lobby, which
        /// legitimately runs short — the core's roster loop picks up late names.
        /// </summary>
        public LobbyState TryResolveLobby()
        {
            var s = Snap;
            if (!(s.Connected && s.InGameplay && s.IsBattlegrounds))
                return null;

            var named = s.Players.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
            var needed = s.IsFriendly ? 2 : 8;
            if (named.Count < needed)
                return null;

            var localName = StripTag(s.LocalBattleTag);
            var localAssigned = false;
            var players = new List<LobbyPlayerInfo>();
            foreach (var p in named)
            {
                var name = StripTag(p.Name);
                if (name == null)
                    continue;
                // Namesakes are indistinguishable by base name — the gold "you"
                // goes to the first occurrence only.
                var isLocal = !localAssigned && localName != null &&
                              string.Equals(name, localName, StringComparison.OrdinalIgnoreCase);
                if (isLocal)
                    localAssigned = true;
                players.Add(new LobbyPlayerInfo
                {
                    Name = name,
                    PlayerId = p.PlayerId,
                    HeroCardId = p.HeroCardId,
                    IsLocalPlayer = isLocal,
                    IsDead = p.IsDead,
                    LeaderboardPlace = p.Place,
                    TeamId = p.TeamId,
                });
            }
            if (players.Count < needed)
                return null;

            return new LobbyState
            {
                Players = players,
                Region = s.Region,
                GameMode = s.IsDuos ? "1" : "0",
                GameUuid = null,
            };
        }

        /// <summary>Refreshes hero, place, team and death state for the roster.</summary>
        public void AttachHeroEntities(List<LobbyPlayerInfo> players, string gameUuid)
        {
            var s = Snap;
            if (!s.InGameplay)
                return;
            foreach (var info in players)
            {
                var p = s.Players.FirstOrDefault(x => x.PlayerId == info.PlayerId && info.PlayerId > 0);
                if (p == null)
                    continue;
                if (!string.IsNullOrEmpty(p.HeroCardId))
                    info.HeroCardId = p.HeroCardId;
                if (p.Place > 0)
                    info.LeaderboardPlace = p.Place;
                if (p.TeamId > 0)
                    info.TeamId = p.TeamId;
                // One-way latch: BG players never come back within a game.
                if (p.IsDead)
                    info.IsDead = true;
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
