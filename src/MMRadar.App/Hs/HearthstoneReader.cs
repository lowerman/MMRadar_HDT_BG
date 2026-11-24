using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HackF5.UnitySpy;
using MMRadar.Util;

namespace MMRadar.Hs
{
    /// <summary>One Battlegrounds contestant as the game itself sees it.</summary>
    public sealed class BgPlayer
    {
        /// <summary>PLAYER_ID tag of the leaderboard hero (lobby slot id space).</summary>
        public int PlayerId { get; set; }
        public string Name { get; set; }
        public string HeroCardId { get; set; }
        public int Health { get; set; }
        public int Damage { get; set; }
        public int Place { get; set; }
        /// <summary>1-based duos team (leaderboard team index); 0 in solo.</summary>
        public int TeamId { get; set; }

        public bool IsDead => Health > 0 && Damage >= Health;
    }

    /// <summary>Immutable picture of the game at one moment.</summary>
    public sealed class GameSnapshot
    {
        public static readonly GameSnapshot Disconnected = new GameSnapshot();

        public bool Connected { get; set; }
        public int Scene { get; set; }
        public int GameType { get; set; }
        /// <summary>NA / EU / AP / CN, null while unknown.</summary>
        public string Region { get; set; }
        /// <summary>The user's own battletag ("Name#1234"), null while unknown.</summary>
        public string LocalBattleTag { get; set; }
        public IReadOnlyList<BgPlayer> Players { get; set; } = new BgPlayer[0];
        public DateTime TakenUtc { get; set; }

        public bool InGameplay => Scene == SceneGameplay;
        public bool IsBattlegrounds => GameType >= 23 && (GameType <= 24 || (GameType >= 35 && GameType <= 41));
        public bool IsDuos => GameType >= 37 && GameType <= 41;
        public bool IsFriendly => GameType == 24 || GameType == 39;

        internal const int SceneGameplay = 4;
    }

    /// <summary>
    /// Reads Battlegrounds state straight from Hearthstone's Mono heap with the
    /// vendored UnitySpy reader — the data HDT used to hand the plugin. Field
    /// chains follow Zero-to-Heroes' UnitySpy.HearthstoneLib (MIT).
    ///
    /// Runs on its own background thread and publishes immutable snapshots:
    /// memory reads are slow-ish (many small ReadProcessMemory calls) and must
    /// never stall the overlay's UI thread.
    /// </summary>
    public sealed class HearthstoneReader : IDisposable
    {
        private const int PlayerIdTag = 30;

        private readonly Thread _thread;
        private volatile bool _stop;
        private volatile GameSnapshot _latest = GameSnapshot.Disconnected;

        private IAssemblyImage _image;
        private int _pid;
        private DateTime _nextConnectUtc = DateTime.MinValue;
        private readonly Dictionary<string, int> _serviceSlotHints = new Dictionary<string, int>();

        // Per-game facts: read once the match starts, cleared when it ends.
        private int _gameType;
        private string _region;
        private string _battleTag;
        private int _lastScene = -1;

        public HearthstoneReader()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "MMRadar.HearthstoneReader" };
        }

        public GameSnapshot Latest => _latest;

        public void Start() => _thread.Start();

        public void Dispose()
        {
            _stop = true;
            Disconnect();
        }

        private void Loop()
        {
            while (!_stop)
            {
                var interval = 3000;
                try
                {
                    var snap = Poll();
                    _latest = snap;
                    LogChange(snap);
                    if (snap.Connected && snap.InGameplay)
                        interval = 1000;
                }
                catch (Exception ex)
                {
                    // A read can fail mid-scene-change or as the game exits; the
                    // next poll starts over (and reconnects when needed).
                    Logger.Debug("Reader poll failed: " + ex.Message);
                    if (!ProcessAlive())
                        Disconnect();
                }
                Thread.Sleep(interval);
            }
        }

        private GameSnapshot Poll()
        {
            if (!EnsureConnected())
                return GameSnapshot.Disconnected;

            var scene = ReadScene();
            if (scene != _lastScene)
            {
                // Scene change = game boundary: per-game facts must be re-read.
                _lastScene = scene;
                _gameType = 0;
                _battleTag = null;
                _serviceSlotHints.Clear();
            }

            var snap = new GameSnapshot
            {
                Connected = true,
                Scene = scene,
                TakenUtc = DateTime.UtcNow,
            };
            if (scene != GameSnapshot.SceneGameplay)
            {
                snap.Region = _region;
                return snap;
            }

            if (_gameType == 0)
                _gameType = SafeInt(() => GetService("GameMgr")?["m_gameType"]);
            snap.GameType = _gameType;
            if (!snap.IsBattlegrounds)
                return snap;

            // Region is fixed for the session but may read as 0 early on — retry
            // until known, then keep it.
            if (_region == null)
                _region = MapRegion(SafeInt(() =>
                    GetService("Network")?["m_state"]?["<CachedRegion>k__BackingField"]));
            if (_battleTag == null)
                _battleTag = ReadLocalBattleTag();

            snap.Region = _region;
            snap.LocalBattleTag = _battleTag;
            snap.Players = ReadPlayers(snap.IsDuos);
            return snap;
        }

        private string _lastLogged;

        /// <summary>One log line per state change — enough to diagnose a test
        /// match from the log alone, without flooding it every second.</summary>
        private void LogChange(GameSnapshot s)
        {
            var named = s.Players.Count(p => !string.IsNullOrWhiteSpace(p.Name));
            var dead = s.Players.Count(p => p.IsDead);
            var line = !s.Connected
                ? "not connected"
                : $"scene={s.Scene} gameType={s.GameType} region={s.Region ?? "?"} " +
                  $"you={(s.LocalBattleTag != null ? "known" : "?")} tiles={s.Players.Count} named={named} dead={dead}";
            if (line == _lastLogged)
                return;
            _lastLogged = line;
            Logger.Info("Game state: " + line);
        }

        // --- connection ---

        private bool EnsureConnected()
        {
            if (_image != null)
            {
                if (ProcessAlive())
                    return true;
                Logger.Info("Hearthstone exited");
                Disconnect();
            }
            if (DateTime.UtcNow < _nextConnectUtc)
                return false;
            _nextConnectUtc = DateTime.UtcNow.AddSeconds(5);

            var proc = Process.GetProcessesByName("Hearthstone").FirstOrDefault();
            if (proc == null)
                return false;
            try
            {
                // Fails while the game is still booting (Mono not ready yet) —
                // simply retried on the next attempt.
                _image = AssemblyImageFactory.Create(proc.Id, m => Logger.Debug("[UnitySpy] " + m));
                _pid = proc.Id;
                _region = null;
                _lastScene = -1;
                _serviceSlotHints.Clear();
                Logger.Info($"Attached to Hearthstone (pid {_pid})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug("Attach to Hearthstone failed (will retry): " + ex.Message);
                _image = null;
                return false;
            }
            finally
            {
                proc.Dispose();
            }
        }

        private bool ProcessAlive()
        {
            if (_pid == 0)
                return false;
            try
            {
                using (var p = Process.GetProcessById(_pid))
                    return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private void Disconnect()
        {
            try { (_image as IDisposable)?.Dispose(); } catch { /* best effort */ }
            _image = null;
            _pid = 0;
            _region = null;
            _gameType = 0;
            _battleTag = null;
            _lastScene = -1;
            _serviceSlotHints.Clear();
            _latest = GameSnapshot.Disconnected;
        }

        // --- readers ---

        private int ReadScene() =>
            SafeInt(() => _image["SceneMgr"]?["s_instance"]?["m_mode"]);

        private string ReadLocalBattleTag()
        {
            try
            {
                var myPlayer = _image["BnetPresenceMgr"]?["s_instance"]?["m_myPlayer"];
                var tag = Try(() => myPlayer?["m_hsGameAccount"]?["m_battleTag"])
                          ?? Try(() => myPlayer?["m_account"]?["m_battleTag"]);
                if (tag == null)
                    return null;
                string name = AsString(Try(() => tag["m_name"]));
                string number = AsString(Try(() => tag["m_number"]));
                return name == null ? null : number == null ? name : name + "#" + number;
            }
            catch (Exception ex)
            {
                Logger.Debug("Battletag read failed: " + ex.Message);
                return null;
            }
        }

        private List<BgPlayer> ReadPlayers(bool duos)
        {
            var result = new List<BgPlayer>();
            var manager = Try(() => _image["PlayerLeaderboardManager"]?["s_instance"]);
            if (manager == null)
                return result;

            // Tiles: one flat list in solo; per-team lists in duos (the team
            // index is the team id). Field names differ across game versions.
            var tiles = new List<(dynamic Tile, int Team)>();
            var flat = Try(() => manager["m_playerTiles"]);
            if (flat != null)
            {
                foreach (var t in Items(flat))
                    tiles.Add((t, 0));
            }
            else
            {
                var teams = Try(() => manager["m_teams"]);
                var teamIndex = 0;
                foreach (var team in Items(teams))
                {
                    teamIndex++;
                    var cards = Try(() => team["m_playerLeaderboardCards"]);
                    foreach (var t in Items(cards))
                        tiles.Add((t, duos ? teamIndex : 0));
                }
            }
            if (tiles.Count == 0)
                return result;

            var names = ReadPlayerInfoNames();
            var lastNames = ReadLastDisplayedNames();

            foreach (var (tile, team) in tiles)
            {
                try
                {
                    var entity = Try(() => tile["m_entity"]);
                    if (entity == null)
                        continue;
                    int playerId = ReadTag((object)entity, PlayerIdTag);
                    if (playerId <= 0 || result.Any(p => p.PlayerId == playerId))
                        continue;

                    names.TryGetValue(playerId, out var name);
                    if (string.IsNullOrWhiteSpace(name))
                        lastNames.TryGetValue(playerId, out name);
                    if (string.IsNullOrWhiteSpace(name))
                        name = AsString(Try(() => tile["m_overlay"]?["m_heroActor"]?["m_playerNameText"]?["m_Text"]));

                    result.Add(new BgPlayer
                    {
                        PlayerId = playerId,
                        Name = name,
                        HeroCardId = AsString(Try(() => entity["m_cardIdInternal"])),
                        Health = SafeInt(() => entity["m_realTimeHealth"]),
                        Damage = SafeInt(() => entity["m_realTimeDamage"]),
                        Place = SafeInt(() => entity["m_realTimePlayerLeaderboardPlace"]),
                        TeamId = team,
                    });
                }
                catch (Exception ex)
                {
                    Logger.Debug("Tile read failed: " + ex.Message);
                }
            }
            return result;
        }

        /// <summary>GameState.m_playerInfoMap → SharedPlayerInfo.m_name: every
        /// contestant's name, available without hovering the leaderboard.</summary>
        private Dictionary<int, string> ReadPlayerInfoNames()
        {
            var result = new Dictionary<int, string>();
            try
            {
                object map = _image["GameState"]?["s_instance"]?["m_playerInfoMap"];
                ReadIntMap(map, result, v => AsString(Try(() => ((dynamic)v)["m_name"])));
            }
            catch (Exception ex)
            {
                Logger.Debug("Player info names: " + ex.Message);
            }
            return result;
        }

        /// <summary>GameMgr.m_lastDisplayedPlayerNames — filled as presence names resolve.</summary>
        private Dictionary<int, string> ReadLastDisplayedNames()
        {
            var result = new Dictionary<int, string>();
            try
            {
                object map = GetService("GameMgr")?["m_lastDisplayedPlayerNames"];
                ReadIntMap(map, result, v => AsString(v));
            }
            catch (Exception ex)
            {
                Logger.Debug("Last displayed names: " + ex.Message);
            }
            return result;
        }

        private static void ReadIntMap(object mapObject, Dictionary<int, string> into, Func<object, string> value)
        {
            dynamic map = mapObject;
            if (mapObject == null)
                return;
            int count = map["count"] ?? 0;
            if (count <= 0)
                return;
            var keys = map["keySlots"];
            var values = (object[])map["valueSlots"];
            for (var i = 0; i < count && i < values.Length; i++)
            {
                object key = keys[i];
                if (!(key is int id))
                    continue;
                var text = value(values[i]);
                if (!string.IsNullOrWhiteSpace(text) && !into.ContainsKey(id))
                    into[id] = text;
            }
        }

        private static int ReadTag(object entityObject, int tag)
        {
            dynamic entity = entityObject;
            var tagValues = Try(() => entity["m_tags"]?["m_values"]);
            if (tagValues == null)
                return 0;
            int count = Try(() => tagValues["_count"]) ?? 0;
            if (count <= 0)
                return 0;
            var entries = (object[])tagValues["_entries"];
            for (var j = 0; j < count && j < entries.Length; j++)
            {
                if (entries[j] is IManagedObjectInstance e && e.GetValue<int>("key") == tag)
                    return e.GetValue<int>("value");
            }
            return 0;
        }

        /// <summary>The game's service locator (Hearthstone.HearthstoneJobs) — the
        /// slot of each service is remembered and re-validated on every use.</summary>
        private dynamic GetService(string name)
        {
            var services = _image["Hearthstone.HearthstoneJobs"]?["s_dependencyBuilder"]?["_items"]?[0]
                ?["m_serviceLocator"]?["m_services"];
            if (services == null)
                return null;

            if (_serviceSlotHints.TryGetValue(name, out var hint))
            {
                try
                {
                    var hinted = ((IManagedObjectInstance)services).GetArrayValue<dynamic>("_entries", hint);
                    if (hinted?["value"]?["<ServiceTypeName>k__BackingField"] == name)
                        return hinted["value"]["<Service>k__BackingField"];
                }
                catch
                {
                    // fall through to the scan
                }
                _serviceSlotHints.Remove(name);
            }

            var entries = services["_entries"];
            if (entries == null)
                return null;
            int length = entries.Length;
            for (var i = 0; i < length; i++)
            {
                var entry = entries[i];
                if (entry?["value"]?["<ServiceTypeName>k__BackingField"] == name)
                {
                    _serviceSlotHints[name] = i;
                    return entry["value"]["<Service>k__BackingField"];
                }
            }
            return null;
        }

        // --- helpers ---

        private static IEnumerable<dynamic> Items(dynamic list)
        {
            if (list == null)
                yield break;
            int size = Try(() => list["_size"]) ?? -1;
            var items = Try(() => list["_items"]);
            if (items == null)
                yield break;
            int length = items.Length;
            if (size >= 0 && size < length)
                length = size;
            for (var i = 0; i < length; i++)
            {
                var item = items[i];
                if (item != null)
                    yield return item;
            }
        }

        private static dynamic Try(Func<dynamic> read)
        {
            try { return read(); }
            catch { return null; }
        }

        private static int SafeInt(Func<dynamic> read)
        {
            try
            {
                object v = read();
                return v == null ? 0 : Convert.ToInt32(v);
            }
            catch
            {
                return 0;
            }
        }

        private static string AsString(object value)
        {
            if (value == null)
                return null;
            var text = value as string ?? value.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static string MapRegion(int bnetRegion)
        {
            switch (bnetRegion)
            {
                case 1: return "NA";
                case 2: return "EU";
                case 3:
                case 4: return "AP"; // KR / TW play on the Asia-Pacific ladder
                case 5: return "CN";
                default: return null;
            }
        }
    }
}
