using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using MMRadar.Config;
using MMRadar.Game;
using MMRadar.UI;
using MMRadar.Util;
using MMRadar.Wallii;
using MMRadar.Engine;
using MMRadar.Hs;

namespace MMRadar.Standalone
{
    /// <summary>
    /// Standalone orchestrator: game state (read from the game itself), wallii fetches, overlay UI.
    /// A deliberate copy of the plugin's PluginCore with the HDT touchpoints swapped for
    /// </summary>
    /// GameTracker (game memory) and OverlayHost (own transparent window) — the fetch,
    /// two-phase render and retry logic is kept identical.
    public class AppCore
    {
        private enum Phase
        {
            Idle,
            WaitingForLobby,
            Fetching,
            Loaded,
        }

        private readonly GameTracker _tracker;
        private readonly MMRadar.Overlay.OverlayHost _overlay;

        public AppCore(GameTracker tracker, MMRadar.Overlay.OverlayHost overlay)
        {
            _tracker = tracker;
            _overlay = overlay;
        }

        /// <summary>
        /// Test switch (--simulate-wallii-down): every wallii request goes to a
        /// closed local port and is refused at once, so the outage UI can be
        /// checked at will. The official leaderboard keeps working for real.
        /// </summary>
        public static bool SimulateWalliiDown { get; set; }

        private const string SimulatedDeadWalliiUrl = "http://127.0.0.1:9/rest/v1";

        /// <summary>%AppData%\MMRadar — settings, offline board copies, logs.</summary>
        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MMRadar");

        /// <summary>
        /// First run after the HDT plugin: adopt its settings (panel position,
        /// theme, scale, colors) so the panel shows up exactly where it was.
        /// </summary>
        private static void ImportPluginSettings(string dir)
        {
            try
            {
                var target = Path.Combine(dir, "settings.xml");
                if (File.Exists(target))
                    return;
                var plugin = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HearthstoneDeckTracker", "MMRadar", "settings.xml");
                if (!File.Exists(plugin))
                    return;
                Directory.CreateDirectory(dir);
                File.Copy(plugin, target);
                Logger.Info("Imported settings from the HDT plugin");
            }
            catch (Exception ex)
            {
                Logger.Debug("Plugin settings import skipped: " + ex.Message);
            }
        }
        private WalliiService _wallii;
        private PluginSettings _settings;
        private LobbyPanel _panel;
        private DossierPopup _popup;

        /// <summary>
        /// Games listed (and charted) in the dossier. Fixed for the app; fewer when
        /// the player has fewer detectable games in the fetched history.
        /// </summary>
        private const int DossierGames = 30;
        private SettingsCard _settingsCard;

        private Phase _phase = Phase.Idle;
        private LobbyState _lobby;
        private int _fetchGeneration;
        private int _popupGeneration;
        private DateTime _lastLiveRefresh = DateTime.MinValue;
        private bool _hiddenForThisGame;
        private bool _lastFetchFailed;
        private int _fetchRetries;
        private DateTime _nextRetryAtUtc = DateTime.MinValue;
        // Official-board retries run on their OWN budget: a mirror hiccup must not
        // consume the wallii retry allowance (and vice versa).
        private bool _boardMissing;
        private int _boardRetries;
        private DateTime _nextBoardRetryAtUtc = DateTime.MinValue;
        // The wallii-only baseline currently on screen (current generation). Board
        // retries re-run just the board layer over clones of it — no wallii
        // round-trip, no baseline repaint.
        private List<PlayerSummary> _baseline;
        // What the panel actually displays right now (baseline or board-corrected).
        // Board repaints compare against THIS, so a repeated fill that lands the
        // same values never churns the rows, while a fill whose predecessor was
        // lost to a generation bump still paints.
        private List<PlayerSummary> _lastPainted;
        private bool _previewActive;
        private int _previewGeneration;
        private SettingsWindow _settingsWindow;
        private AppSettings _appSettings;

        /// <summary>App-only settings, shared with the app window and the updater.</summary>
        public AppSettings AppSettings => _appSettings;

        public string Theme => _settings.Theme;
        public bool SortAscending => _settings.SortAscending;
        public string ChipStyle => _settings.ChipStyle;
        // Which player the details card currently belongs to (toggle on re-click).
        private string _popupKey;
        // Card position when a header press began — tells a drag from a click.
        private System.Windows.Point? _popupDragFrom;
        private Button _popupHomeButton;
        private System.Windows.Media.ScaleTransform _homeIconFlip;
        private DateTime _lastPartialLobbyRetry = DateTime.MinValue;
        private bool _spectateNoticeShown;
        private System.Windows.Threading.DispatcherTimer _saveDebounce;

        /// <summary>True once the user has chosen an explicit scale (mouse wheel).</summary>
        private bool _scaleTouched;

        public void Load()
        {

            var dir = DataDirectory;
            ImportPluginSettings(dir);
            _settings = PluginSettings.Load(dir);
            _appSettings = AppSettings.Load(dir);
            // Wheel-zoom raises LayoutChanged on every notch; an XML write per
            // notch can stutter on slow disks. Collect the burst, write once.
            _saveDebounce = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _saveDebounce.Tick += (s, e) =>
            {
                _saveDebounce.Stop();
                _settings.Save();
            };
            ThemeManager.Apply(_settings.Theme);
            ThemeManager.ApplyChipStyle(_settings.ChipStyle);
            _wallii = new WalliiService(
                new WalliiApi(SimulateWalliiDown ? SimulatedDeadWalliiUrl : _settings.WalliiBaseUrl,
                    _settings.WalliiAnonKey),
                new OfficialBoardClient(_settings.OfficialBoardUrl, dir));

            _panel = new LobbyPanel { Visibility = System.Windows.Visibility.Collapsed };
            _popup = new DossierPopup { Visibility = System.Windows.Visibility.Collapsed };
            _settingsCard = new SettingsCard(ToggleOverlay, ResetPosition, SetTheme, SetSortAscending, SetChipStyle)
            {
                Visibility = System.Windows.Visibility.Collapsed,
            };
            _settingsCard.CloseRequested += HideSettingsCard;

            Canvas.SetLeft(_panel, SafeCoord(_settings.PanelLeft, 40));
            Canvas.SetTop(_panel, SafeCoord(_settings.PanelTop, 130));
            InitWantedPosition(Canvas.GetLeft(_panel), Canvas.GetTop(_panel));
            // Provisional until the overlay window has a real size (see ShowPanel):
            // at plugin load HDT's overlay still has its design-time dimensions.
            _panel.PanelScale = _settings.Scale > 0 ? _settings.Scale : 1.0;
            _popup.PanelScale = _panel.PanelScale;
            _scaleTouched = _settings.Scale > 0;


            _panel.PlayerClicked += OnPlayerClicked;
            _panel.LayoutChanged += TrackWantedPosition; // before SaveLayout: it saves the wanted spot
            _panel.LayoutChanged += SaveLayout;
            _panel.LayoutChanged += UpdateHomeIconDirection; // the panel moved: re-aim the dock arrow
            _panel.SettingsRequested += ToggleSettingsCard;
            _panel.ScaleChangedByUser += () =>
            {
                _scaleTouched = true;
                _popup.PanelScale = _panel.PanelScale;
                _settingsCard.CardScale = _panel.PanelScale;
            };
            _panel.SortAscending = _settings.SortAscending;
            _panel.IsCollapsed = _settings.Collapsed;
            _panel.CollapsedChanged += collapsed =>
            {
                _settings.Collapsed = collapsed;
                _settings.Save();
            };
            _popup.CloseRequested += () => _popupGeneration++;
            // The card drags itself by its header; remember where it was dropped.
            _popup.Header.PreviewMouseLeftButtonDown += (s, e) =>
                _popupDragFrom = new System.Windows.Point(Canvas.GetLeft(_popup), Canvas.GetTop(_popup));
            _popup.Header.MouseLeftButtonUp += (s, e) => RememberPopupPosition();
            _popup.Header.LostMouseCapture += (s, e) => RememberPopupPosition();
            AddPopupHomeButton();

            // Attach to the overlay last so a failure above cannot orphan the controls.
            _overlay.Surface.Children.Add(_panel);
            _overlay.Surface.Children.Add(_popup);
            _overlay.Surface.Children.Add(_settingsCard);
            AddCollapsedBeacon();

            Logger.Info("Overlay ready");
        }

        private System.Windows.Shapes.Ellipse _beacon;

        /// <summary>
        /// The collapsed pill fades to ~30% so it stays out of the way, but over
        /// busy hero art it got hard to find. A full-brightness copy of the title
        /// dot sits exactly on top of the real one while the panel is collapsed:
        /// still barely noticeable, yet easy to spot when you know where to look.
        /// It lives on the app's overlay, so the shared panel stays untouched.
        /// </summary>
        private void AddCollapsedBeacon()
        {
            _beacon = new System.Windows.Shapes.Ellipse
            {
                IsHitTestVisible = false, // clicks and hover go to the panel beneath
                Visibility = System.Windows.Visibility.Collapsed,
            };
            Panel.SetZIndex(_beacon, 1000);
            _overlay.Surface.Children.Add(_beacon);
            _panel.LayoutUpdated += (s, e) => SyncCollapsedBeacon();
            _panel.IsVisibleChanged += (s, e) => SyncCollapsedBeacon();
        }

        private void SyncCollapsedBeacon()
        {
            try
            {
                var dot = _panel.TitleDot;
                var show = _panel.IsVisible && _panel.IsCollapsed && dot.IsVisible && dot.ActualWidth > 0;
                var v = show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                if (_beacon.Visibility != v)
                    _beacon.Visibility = v;
                if (!show)
                    return;
                var at = dot.TranslatePoint(new System.Windows.Point(0, 0), _overlay.Surface);
                // Only touch properties that changed: every set re-runs layout,
                // and this handler is driven by LayoutUpdated.
                if (Math.Abs(Canvas.GetLeft(_beacon) - at.X) > 0.01 || double.IsNaN(Canvas.GetLeft(_beacon)))
                    Canvas.SetLeft(_beacon, at.X);
                if (Math.Abs(Canvas.GetTop(_beacon) - at.Y) > 0.01 || double.IsNaN(Canvas.GetTop(_beacon)))
                    Canvas.SetTop(_beacon, at.Y);
                if (_beacon.Width != dot.ActualWidth)
                    _beacon.Width = dot.ActualWidth;
                if (_beacon.Height != dot.ActualHeight)
                    _beacon.Height = dot.ActualHeight;
                if (!ReferenceEquals(_beacon.Fill, dot.Fill))
                    _beacon.Fill = dot.Fill; // follows the theme and the coral "wallii down" state
            }
            catch (Exception ex)
            {
                Logger.Debug("Collapsed beacon: " + ex.Message);
            }
        }

        public void Unload()
        {
            try
            {
                // Flush synchronously — a pending debounced write would race
                // HDT's shutdown and could lose the last zoom or drag.
                _saveDebounce?.Stop();
                if (_panel != null && _settings != null)
                {
                    CaptureLayout();
                    _settings.Save();
                }
                ThemeManager.Reset();
                try { _settingsWindow?.Close(); } catch { }
                _settingsWindow = null;
                if (_panel != null)
                {
                    _overlay.Surface.Children.Remove(_panel);
                    _overlay.Surface.Children.Remove(_popup);
                    _overlay.Surface.Children.Remove(_settingsCard);
                    if (_beacon != null)
                        _overlay.Surface.Children.Remove(_beacon);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unload failed", ex);
            }
        }

        /// <summary>Called by the plugin host (throttled by the entry class).</summary>
        public void Tick()
        {
            try
            {
                if (!_settings.OverlayEnabled)
                    return;

                if (!_tracker.InBattlegroundsMatch)
                {
                    if (_phase != Phase.Idle)
                        ResetToIdle();
                    return;
                }

                // In a battlegrounds match.
                if (_phase == Phase.Idle)
                {
                    _phase = Phase.WaitingForLobby;
                    _tracker.Reset();
                    _hiddenForThisGame = false;
                    _previewActive = false; // a real match replaces the sample preview
                    HideSettingsCard();     // a stray card over hero-pick hurts most
                    ShowWaitingClean();
                    ShowPanel();
                }

                if (_hiddenForThisGame)
                    return;

                if (_phase == Phase.WaitingForLobby)
                {
                    var lobby = _tracker.TryResolveLobby();
                    if (lobby != null && lobby.Players.Count > 0)
                    {
                        _lobby = lobby;
                        _phase = Phase.Fetching;
                        _panel.ShowLoading(lobby.Players);
                        var generation = ++_fetchGeneration;
                        _ = FetchLobbyStatsAsync(lobby, generation);
                    }
                    else if (!_spectateNoticeShown && _tracker.IsSpectating)
                    {
                        // Spectated games never hand over the roster — but hovering
                        // the leaderboard portraits reveals names one by one (the
                        // hover reader picks them up and the panel fills in).
                        _spectateNoticeShown = true;
                        _panel.SetStatus("Spectating: hover the leaderboard portraits to reveal players");
                    }
                }
                else if (_phase == Phase.Loaded || _phase == Phase.Fetching)
                {
                    if ((DateTime.UtcNow - _lastLiveRefresh).TotalSeconds >= 2)
                    {
                        _lastLiveRefresh = DateTime.UtcNow;
                        if (_lobby != null)
                        {
                            _tracker.AttachHeroEntities(_lobby.Players, _lobby.GameUuid);
                            _panel.UpdateLiveState(_lobby.Players);
                        }
                    }

                    // Transient wallii failure: retry a few times over the course of the game.
                    if (_phase == Phase.Loaded && _lastFetchFailed && _fetchRetries < 3 &&
                        DateTime.UtcNow >= _nextRetryAtUtc && _lobby != null)
                    {
                        _lastFetchFailed = false;
                        _fetchRetries++;
                        _phase = Phase.Fetching;
                        var generation = ++_fetchGeneration;
                        _ = FetchLobbyStatsAsync(_lobby, generation, isRetry: true);
                    }

                    // Transient official-board failure: dash rows are curable, so the
                    // board gets its own in-game retries (30/60/120 s). Board-only:
                    // the baseline on screen is already the best wallii knowledge, so
                    // only the board layer re-runs — no wallii round-trip, no repaint
                    // unless the board actually delivers something.
                    if (_phase == Phase.Loaded && _boardMissing && _boardRetries < 3 &&
                        DateTime.UtcNow >= _nextBoardRetryAtUtc && _lobby != null &&
                        _baseline != null)
                    {
                        _boardMissing = false; // the refill below re-evaluates it
                        _boardRetries++;
                        _ = RetryBoardFillAsync(_lobby, _baseline, _fetchGeneration);
                    }

                    // A partial roster (e.g. right after a reconnect) keeps improving as
                    // more names surface, and a roster accepted from stale metadata is
                    // replaced once HDT surfaces the real lobby of a NEW game (its
                    // uuid differs from the one the current roster came from).
                    if (_phase == Phase.Loaded && _lobby != null &&
                        (DateTime.UtcNow - _lastPartialLobbyRetry).TotalSeconds >= 5)
                    {
                        _lastPartialLobbyRetry = DateTime.UtcNow;
                        var better = _tracker.TryResolveLobby();
                        if (better != null &&
                            (better.Players.Count > _lobby.Players.Count ||
                             // A late-resolving region unlocks the entire board
                             // layer (and region-correct wallii rows) — refetch.
                             (better.Region != null && _lobby.Region == null) ||
                             (better.GameUuid != null && better.GameUuid != _lobby.GameUuid)))
                        {
                            _lobby = better;
                            _phase = Phase.Fetching;
                            var generation = ++_fetchGeneration;
                            _ = FetchLobbyStatsAsync(better, generation);
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                Logger.Error("Tick failed", ex);
            }
        }

        private async Task FetchLobbyStatsAsync(LobbyState lobby, int generation, bool isRetry = false)
        {
            List<PlayerSummary> summaries;
            string statusMessage = null;
            try
            {
                var names = lobby.Players.Select(p => p.Name).ToList();
                summaries = await _wallii.GetLobbyStatsAsync(names, lobby.Region, lobby.GameMode)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error("Lobby stats fetch failed", ex);
                // Shown as the hover text of the coral title dot — the compact
                // outage state replaces the old full-width status line.
                statusMessage = ex is WalliiAuthException
                    ? "wallii.gg rejected the access key. Showing official leaderboard ratings only.\n" +
                      "Detailed stats return once the key is updated."
                    : "wallii.gg is unavailable. Showing official leaderboard ratings only.\n" +
                      "Detailed stats return automatically when it is back.";
                _lastFetchFailed = true;
                _nextRetryAtUtc = DateTime.UtcNow.AddSeconds(30);
                summaries = lobby.Players
                    .Select(p => new PlayerSummary { LobbyName = p.Name, OnLeaderboard = false })
                    .ToList();
            }

            // A retry that failed AGAIN has no new wallii data: repainting fresh
            // placeholders would wipe the board fills already on screen and blink
            // every row, so the baseline paint goes quiet. The BOARD layer below
            // still runs — during an outage these retries are exactly the chances
            // the board has to land (or to redo a fill that a generation bump cut
            // short mid-flight).
            var quietBaseline = isRetry && statusMessage != null;

            // PHASE 1 — the wallii-only baseline paints immediately (v1.0.7 timing
            // and semantics): a slow or unreachable leaderboard source must never
            // delay the first render.
            DecorateWithGameInfo(summaries, lobby);
            RunOnUi(() =>
            {
                if (generation != _fetchGeneration)
                    return;
                _phase = Phase.Loaded;
                // Compact outage state on failure; a successful fetch clears it.
                _panel.SetWalliiUnavailable(statusMessage);
                if (!quietBaseline)
                {
                    _baseline = summaries;
                    _lastPainted = summaries;
                    _panel.SetStats(summaries);
                }
            });

            // PHASE 2 — the official-board layer (rating authority, case-exact
            // namesakes, fallback ratings, wallii-outage degradation) lands as a
            // second paint once the board arrives; if it never does, the panel
            // simply stays on the baseline. Runs on independent clones so the
            // background fill can never race rows already handed to the UI, and
            // repaints only when the board actually changed something — an
            // identical second paint would just churn the rows.
            var corrected = summaries.Select(WalliiService.CloneSummary).ToList();
            // Decoration first: the identity gate exempts the LOCAL player, so
            // IsLocalPlayer must be known before the board layer runs.
            DecorateWithGameInfo(corrected, lobby);
            var boardMissing = await _wallii
                .TryFillOfficialRatingsAsync(corrected, lobby.Region, lobby.GameMode)
                .ConfigureAwait(false);
            RunOnUi(() =>
            {
                if (generation != _fetchGeneration)
                    return;
                // Arm (or disarm) the board retry loop; the backoff doubles per try.
                _boardMissing = boardMissing;
                if (boardMissing)
                {
                    _nextBoardRetryAtUtc = DateTime.UtcNow.AddSeconds(30 << _boardRetries);
                    return;
                }
                _boardRetries = 0;
                if (BoardChangedAnything(_lastPainted, corrected))
                {
                    _lastPainted = corrected;
                    _panel.SetStats(corrected);
                }
            });
        }

        /// <summary>
        /// Board-only retry: re-runs the official-board layer over clones of the
        /// baseline already on screen. The wallii data cannot have improved (its
        /// retries are separate), so nothing else is refetched or repainted.
        /// </summary>
        private async Task RetryBoardFillAsync(LobbyState lobby, List<PlayerSummary> baseline, int generation)
        {
            try
            {
                var corrected = baseline.Select(WalliiService.CloneSummary).ToList();
                DecorateWithGameInfo(corrected, lobby);
                var boardMissing = await _wallii
                    .TryFillOfficialRatingsAsync(corrected, lobby.Region, lobby.GameMode)
                    .ConfigureAwait(false);
                RunOnUi(() =>
                {
                    if (generation != _fetchGeneration)
                        return;
                    _boardMissing = boardMissing;
                    if (boardMissing)
                    {
                        _nextBoardRetryAtUtc = DateTime.UtcNow.AddSeconds(30 << _boardRetries);
                        return;
                    }
                    _boardRetries = 0;
                    if (BoardChangedAnything(_lastPainted, corrected))
                    {
                        _lastPainted = corrected;
                        _panel.SetStats(corrected);
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Board retry failed", ex);
            }
        }

        /// <summary>
        /// True when the board layer changed anything the panel actually shows.
        /// The compared fields are exactly the ones the fill mutates; everything
        /// else is identical by construction (clones of the same baseline).
        /// </summary>
        private static bool BoardChangedAnything(
            List<PlayerSummary> before, List<PlayerSummary> after)
        {
            if (before == null || before.Count != after.Count)
                return true;
            for (var i = 0; i < before.Count && i < after.Count; i++)
            {
                var a = before[i];
                var b = after[i];
                if (a.OnLeaderboard != b.OnLeaderboard || a.Rating != b.Rating ||
                    a.Rank != b.Rank || a.FallbackRating != b.FallbackRating ||
                    a.FallbackRank != b.FallbackRank || a.BelowCutoff != b.BelowCutoff ||
                    a.NamesakeSuspected != b.NamesakeSuspected)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Pairs stats with in-game info positionally — the stats list preserves
        /// roster order, and a name lookup would glue lobby namesakes (same base
        /// name, different battletags) to whichever of them comes first.
        /// </summary>
        private static void DecorateWithGameInfo(List<PlayerSummary> summaries, LobbyState lobby)
        {
            for (var i = 0; i < summaries.Count; i++)
            {
                var summary = summaries[i];
                var player = i < lobby.Players.Count &&
                             string.Equals(lobby.Players[i].Name, summary.LobbyName,
                                 StringComparison.OrdinalIgnoreCase)
                    ? lobby.Players[i]
                    : lobby.Players.FirstOrDefault(p =>
                        string.Equals(p.Name, summary.LobbyName, StringComparison.OrdinalIgnoreCase));
                if (player != null)
                {
                    summary.IsLocalPlayer = player.IsLocalPlayer;
                    summary.HeroCardId = player.HeroCardId;
                    summary.GamePlayerId = player.PlayerId;
                    summary.TeamId = player.TeamId;
                }
            }
        }

        private void OnPlayerClicked(PlayerSummary summary) => ShowPlayerDetails(summary, toggle: true);

        /// <summary>Identity of a lobby row: namesakes differ by their game player id.</summary>
        private static string PopupKey(PlayerSummary s) =>
            (s.LobbyName ?? "").ToLowerInvariant() + "|" + s.GamePlayerId;

        private async void ShowPlayerDetails(PlayerSummary summary, bool toggle)
        {
            // A second click on the player whose card is already open closes it.
            var key = PopupKey(summary);
            if (toggle && _popup.Visibility == System.Windows.Visibility.Visible && key == _popupKey)
            {
                _popupGeneration++; // cancel an in-flight fetch re-showing it
                _popup.Visibility = System.Windows.Visibility.Collapsed;
                _popupKey = null;
                return;
            }
            _popupKey = key;

            var generation = ++_popupGeneration;
            try
            {
                HideSettingsCard(); // dossier and settings share the panel-side slot
                PositionPopupNextToPanel();
                _popup.ShowLoading(summary);
                var details = await _wallii
                    .GetPlayerDetailsAsync(summary, _lobby?.GameMode ?? "0", DossierGames)
                    .ConfigureAwait(true);
                if (generation != _popupGeneration)
                    return; // a newer click superseded this one
                if (details == null)
                    _popup.ShowError(summary, "No wallii data for this player.");
                else
                    _popup.SetData(details);
            }
            catch (WalliiAuthException ex)
            {
                Logger.Error("wallii auth failed", ex);
                if (generation == _popupGeneration)
                    _popup.ShowError(summary, ex.Message);
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to load player details", ex);
                if (generation == _popupGeneration)
                    _popup.ShowError(summary, "wallii.gg request failed. Check your connection and try again.");
            }
        }

        /// <summary>
        /// Toggles the overlay. During a Battlegrounds match this hides or brings back
        /// the real panel (e.g. after the user closed it with ✕); outside of a match it
        /// shows a preview with the current top-8 wallii players for positioning.
        /// </summary>
        public void ToggleOverlay()
        {
            try
            {
                if (_panel.Visibility == System.Windows.Visibility.Visible)
                {
                    _previewActive = false;
                    _previewGeneration++;
                    if (_phase != Phase.Idle)
                        _hiddenForThisGame = true;
                    HidePanel();
                    return;
                }

                _hiddenForThisGame = false;

                if (_phase != Phase.Idle)
                {
                    // Mid-match: restore the real panel; Tick resumes lobby
                    // resolution / live updates from wherever it left off.
                    ShowPanel();
                    return;
                }

                // No match running: preview with the live global top-8.
                _previewActive = true;
                ShowWaitingClean();
                _panel.SetStatus("PREVIEW · loading top players…");
                ShowPanel();
                _ = ShowTopPreviewAsync(++_previewGeneration);
            }
            catch (Exception ex)
            {
                Logger.Error("ToggleOverlay failed", ex);
            }
        }

        private async Task ShowTopPreviewAsync(int generation)
        {
            List<PlayerSummary> lobby;
            string status;
            var live = true;
            try
            {
                lobby = await _wallii.GetTopLobbyAsync("0").ConfigureAwait(false);
                status = "PREVIEW · current top 8 · wallii.gg";
                if (lobby.Count == 0)
                    throw new WalliiApiException("empty top-players result", null);
            }
            catch (Exception ex)
            {
                Logger.Debug("Top-8 preview unavailable, using sample data: " + ex.Message);
                lobby = SampleData.Lobby();
                status = "PREVIEW · sample data (wallii.gg unavailable)";
                live = false;
            }

            // Simulation: preview exactly what an outage looks like in a match —
            // official ratings only, the compact outage state, no avg column.
            string outage = null;
            if (SimulateWalliiDown)
            {
                foreach (var s in lobby)
                {
                    if (s.OnLeaderboard)
                    {
                        s.FallbackRating = s.Rating;
                        s.FallbackRank = s.Rank;
                    }
                    s.OnLeaderboard = false;
                    s.IsLive = false;
                }
                outage = "Simulated wallii.gg outage. Showing official leaderboard ratings only.";
                status = "PREVIEW · wallii outage simulation";
            }

            RunOnUi(() =>
            {
                if (generation != _previewGeneration || !_previewActive)
                    return;
                _panel.SetWalliiUnavailable(outage);
                _panel.SetStats(lobby);
                _panel.SetStatus(status);
                if (live)
                {
                    PositionPopupNextToPanel();
                    var first = lobby.Find(p => p.OnLeaderboard);
                    if (first != null)
                        ShowPlayerDetails(first, toggle: false); // the preview always opens it
                }
            });
        }

        /// <summary>
        /// Toggles the in-overlay settings card next to the panel (gear button).
        /// Runs on the overlay, so the game never loses focus and a stray click
        /// costs one more click on the same gear. Mutually exclusive with the
        /// dossier popup — they share the space beside the panel.
        /// </summary>
        public void ToggleSettingsCard()
        {
            try
            {
                if (_settingsCard.Visibility == System.Windows.Visibility.Visible)
                {
                    HideSettingsCard();
                    return;
                }
                _popupGeneration++; // cancel an in-flight dossier fetch re-showing the popup
                _popup.Visibility = System.Windows.Visibility.Collapsed;
                _settingsCard.Sync(_settings.Theme, _settings.SortAscending, _settings.ChipStyle);
                _settingsCard.CardScale = _panel.PanelScale;
                PositionCardNextToPanel(_settingsCard, 292);
                _settingsCard.Visibility = System.Windows.Visibility.Visible;
            }
            catch (Exception ex)
            {
                Logger.Error("ToggleSettingsCard failed", ex);
            }
        }

        private void HideSettingsCard() =>
            _settingsCard.Visibility = System.Windows.Visibility.Collapsed;

        /// <summary>Opens (or focuses) the small settings dialog.</summary>
        public void OpenSettings()
        {
            try
            {
                if (_settingsWindow != null && _settingsWindow.IsLoaded)
                {
                    _settingsWindow.Activate();
                    return;
                }
                _settingsWindow = new SettingsWindow(
                    ToggleOverlay, ResetPosition, _settings.Theme, SetTheme,
                    _settings.SortAscending, SetSortAscending,
                    _settings.ChipStyle, SetChipStyle);
                try { /* no owner window outside HDT */ }
                catch { /* owner is optional */ }
                _settingsWindow.Show();
            }
            catch (Exception ex)
            {
                Logger.Error("OpenSettings failed", ex);
            }
        }

        public void SetTheme(string key)
        {
            _settings.Theme = key;
            _settings.Save();
            ThemeManager.Apply(key);
        }

        public void SetSortAscending(bool ascending)
        {
            _settings.SortAscending = ascending;
            _settings.Save();
            _panel.SortAscending = ascending; // re-sorts the current lobby in place
        }

        public void SetChipStyle(string key)
        {
            _settings.ChipStyle = key;
            _settings.Save();
            ThemeManager.ApplyChipStyle(key); // re-renders rows and the dossier live
        }

        public bool OverlayEnabled
        {
            get => _settings.OverlayEnabled;
            set
            {
                _settings.OverlayEnabled = value;
                _settings.Save();
                if (!value)
                {
                    _previewActive = false;
                    HidePanel();
                }
                else if (_phase != Phase.Idle && !_hiddenForThisGame)
                {
                    ShowPanel();
                }
            }
        }

        public void ResetPosition()
        {
            Canvas.SetLeft(_panel, 40);
            Canvas.SetTop(_panel, 130);
            _wantLeft = 40;
            _wantTop = 130;
            _settings.PanelLeft = 40;
            _settings.PanelTop = 130;
            // Back to resolution-based auto scale.
            _settings.Scale = 0;
            _scaleTouched = false;
            _panel.PanelScale = 1.0;
            _popup.PanelScale = 1.0;
            _settingsCard.CardScale = 1.0;
            _settings.Save();
            _hiddenForThisGame = false;
            ShowPanel(); // re-applies the auto scale when the overlay has a real size
            if (_settingsCard.Visibility == System.Windows.Visibility.Visible)
                PositionCardNextToPanel(_settingsCard, 292); // follow the panel home
            // The details card goes back to its default spot next to the panel too.
            ResetPopupPosition();
        }

        private void ResetToIdle()
        {
            _phase = Phase.Idle;
            _lobby = null;
            _fetchGeneration++;
            _popupGeneration++;
            _lastFetchFailed = false;
            _fetchRetries = 0;
            _boardMissing = false;
            _boardRetries = 0;
            _baseline = null;
            _lastPainted = null;
            _spectateNoticeShown = false;
            // Whatever lobby info HDT still holds now belongs to the finished game.
            _tracker.MarkCurrentLobbyInfoStale();
            _tracker.Reset();
            if (!_previewActive)
                HidePanel();
        }

        /// <summary>Raised (on the UI thread) whenever the panel appears or disappears.</summary>
        public event Action<bool> PanelShownChanged;

        public bool IsPanelShown => _panel != null && _panel.Visibility == System.Windows.Visibility.Visible;

        private void ShowPanel()
        {
            ApplyAutoScale();
            var was = IsPanelShown;
            _panel.Visibility = System.Windows.Visibility.Visible;
            _overlay.ShowWithoutGame = true; // the test panel needs the overlay even without the game
            KeepPanelOnScreen();
            if (!was)
                PanelShownChanged?.Invoke(true);
        }

        // --- keeping the panel reachable ---
        // The user's chosen spot is kept separately from where the panel is drawn:
        // a spot outside a smaller game window (or one picked on a bigger desktop
        // while previewing without the game) is pulled inside for display only,
        // and the panel returns to it once the game window is big enough again.

        private double _wantLeft = double.NaN;
        private double _wantTop = double.NaN;
        private double _shownLeft = double.NaN;
        private double _shownTop = double.NaN;

        private void InitWantedPosition(double left, double top)
        {
            _wantLeft = left;
            _wantTop = top;
            _overlay.Surface.SizeChanged += (s, e) => KeepPanelOnScreen();
            _panel.SizeChanged += (s, e) => KeepPanelOnScreen();
        }

        /// <summary>After a drag or zoom: a position the user dragged to becomes the wanted one.</summary>
        private void TrackWantedPosition()
        {
            var left = Canvas.GetLeft(_panel);
            var top = Canvas.GetTop(_panel);
            if (double.IsNaN(left) || double.IsNaN(top))
                return;
            // Unchanged from where we last drew it = a zoom, not a move: keep the
            // wanted spot (it may lie outside and be drawn clamped right now).
            var moved = double.IsNaN(_shownLeft) ||
                        Math.Abs(left - _shownLeft) > 0.5 || Math.Abs(top - _shownTop) > 0.5;
            if (moved)
            {
                _wantLeft = left;
                _wantTop = top;
            }
            KeepPanelOnScreen();
            if (moved && !double.IsNaN(_shownLeft))
            {
                // A drag past the edge settles where the panel actually landed.
                _wantLeft = _shownLeft;
                _wantTop = _shownTop;
            }
        }

        /// <summary>
        /// Draws the panel at the wanted spot, pulled inside the overlay when it
        /// would end up out of view: fully inside horizontally, header inside
        /// vertically (a tall panel may still run off the bottom edge).
        /// </summary>
        private void KeepPanelOnScreen()
        {
            try
            {
                if (_panel == null || double.IsNaN(_wantLeft))
                    return;
                var w = _overlay.Surface.ActualWidth;
                var h = _overlay.Surface.ActualHeight;
                if (w < 100 || h < 100)
                    return; // overlay not laid out yet
                var panelW = _panel.ActualWidth > 1 ? _panel.ActualWidth : 192 * _panel.PanelScale;
                var headerH = Math.Min(_panel.ActualHeight > 1 ? _panel.ActualHeight : 40, 40 * _panel.PanelScale);
                var left = Math.Max(0, Math.Min(_wantLeft, w - panelW));
                var top = Math.Max(0, Math.Min(_wantTop, h - headerH));
                _shownLeft = left;
                _shownTop = top;
                if (Math.Abs(Canvas.GetLeft(_panel) - left) > 0.5 || double.IsNaN(Canvas.GetLeft(_panel)))
                    Canvas.SetLeft(_panel, left);
                if (Math.Abs(Canvas.GetTop(_panel) - top) > 0.5 || double.IsNaN(Canvas.GetTop(_panel)))
                    Canvas.SetTop(_panel, top);
            }
            catch (Exception ex)
            {
                Logger.Debug("KeepPanelOnScreen: " + ex.Message);
            }
        }

        /// <summary>
        /// Auto mode: compensate for higher game-window resolutions with a DAMPED curve
        /// (1080p ×1.0, 1440p ×1.2, 4K ×1.6). Full proportionality made sense with the
        /// original small typography; the current 16px HUD type only needs to recover
        /// the physical size lost to pixel density, not the full resolution ratio.
        /// Only runs once the overlay window has been sized to the real game window,
        /// and is never persisted — the user's own wheel-zoom takes over for good.
        /// </summary>
        private void ApplyAutoScale()
        {
            if (_scaleTouched)
                return;
            try
            {
                var width = _overlay.ActualWidth;
                if (width < 1000)
                    return; // overlay not sized to the game yet
                var auto = 1.0 + (width / 1920.0 - 1.0) * 0.6;
                auto = Math.Max(1.0, Math.Min(auto, 1.6));
                if (Math.Abs(_panel.PanelScale - auto) < 0.01)
                    return;
                _panel.PanelScale = auto;
                _popup.PanelScale = auto;
            }
            catch (Exception ex)
            {
                Logger.Debug("ApplyAutoScale: " + ex.Message);
            }
        }

        /// <summary>
        /// "Waiting for lobby" with NO leftovers: ShowWaiting alone hides the header
        /// but keeps the previous lobby's context flag, so the next relayout (e.g. the
        /// auto-scale in ShowPanel) brought back last game's "avg · delta" line.
        /// An empty SetStats clears that state first.
        /// </summary>
        private void ShowWaitingClean()
        {
            _panel.SetWalliiUnavailable(null); // a new game starts with a clean slate
            _panel.SetStats(new List<PlayerSummary>());
            _panel.ShowWaiting();
        }
        private void HidePanel()
        {
            _popupGeneration++; // cancel any in-flight details fetch re-showing the popup
            var was = IsPanelShown;
            _panel.Visibility = System.Windows.Visibility.Collapsed;
            _overlay.ShowWithoutGame = false;
            if (was)
                PanelShownChanged?.Invoke(false);
            _popup.Visibility = System.Windows.Visibility.Collapsed;
            HideSettingsCard();
        }

        private void PositionPopupNextToPanel()
        {
            _popup.PanelScale = _panel.PanelScale;
            if (!TryPlacePopupAtSavedPosition())
                PositionCardNextToPanel(_popup, 280);
            UpdateHomeIconDirection();
        }

        /// <summary>
        /// Puts the card where the user last dropped it (stored as a fraction of
        /// the overlay size), clamped so it can never end up off screen.
        /// </summary>
        private bool TryPlacePopupAtSavedPosition()
        {
            var fx = _appSettings.PopupLeftFraction;
            var fy = _appSettings.PopupTopFraction;
            var w = _overlay.Surface.ActualWidth;
            var h = _overlay.Surface.ActualHeight;
            if (fx == null || fy == null || w < 100 || h < 100)
                return false;
            var cardWidth = _popup.ActualWidth > 50 ? _popup.ActualWidth : 280 * _panel.PanelScale;
            var left = Math.Max(0, Math.Min(fx.Value * w, w - Math.Min(cardWidth, w)));
            var top = Math.Max(0, Math.Min(fy.Value * h, h - 80));
            Canvas.SetLeft(_popup, left);
            Canvas.SetTop(_popup, top);
            return true;
        }

        /// <summary>
        /// A "back to its spot" button left of the card's ✕, injected here rather
        /// than in the shared XAML so the plugin stays untouched. Shown only while
        /// the card sits at a user-chosen position — otherwise it would do nothing.
        /// </summary>
        private void AddPopupHomeButton()
        {
            try
            {
                var grid = _popup.CloseButton.Parent as Grid;
                if (grid == null)
                    return;
                grid.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = System.Windows.GridLength.Auto });
                Grid.SetColumn(_popup.CloseButton, 2);
                _popupHomeButton = new Button
                {
                    Style = _popup.CloseButton.Style,
                    Content = BuildHomeIcon(),
                    VerticalAlignment = System.Windows.VerticalAlignment.Top,
                    Margin = new System.Windows.Thickness(0, 0, 2, 0),
                    ToolTip = "Move back next to the panel",
                };
                _popupHomeButton.Click += (s, e) => ResetPopupPosition();
                Grid.SetColumn(_popupHomeButton, 1);
                grid.Children.Add(_popupHomeButton);
                UpdatePopupHomeButton();
            }
            catch (Exception ex)
            {
                Logger.Error("AddPopupHomeButton failed", ex);
            }
        }

        /// <summary>
        /// "Dock back to the panel": an arrow running into a bar, drawn as
        /// vectors to match the ✕ glyph beside it (same optical size, ~1px
        /// weight). A circular arrow read as "refresh". Points left by default;
        /// <see cref="UpdateHomeIconDirection"/> mirrors it so it always points
        /// at the panel. Its colour follows the button, hover included.
        /// </summary>
        private System.Windows.UIElement BuildHomeIcon()
        {
            var fg = new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1),
            };
            var shape = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(
                    "M 1.5,1.5 L 1.5,8.5 M 8.5,5 L 3.3,5 M 5.6,2.7 L 3.3,5 L 5.6,7.3"),
                StrokeThickness = 1.0,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
            };
            shape.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, fg);
            _homeIconFlip = new System.Windows.Media.ScaleTransform(1, 1, 5, 5);
            var icon = new Canvas
            {
                Width = 10,
                Height = 10,
                IsHitTestVisible = false,
                RenderTransform = _homeIconFlip,
                // Axis-aligned 1px strokes land on whole pixels at full intensity,
                // while the ✕ glyph's diagonals are anti-aliased: at the same colour
                // the arrow carried ~1.55× the ink and looked brighter. Measured on
                // a 1:1 render, 0.65 evens them out (hover keeps the same ratio).
                Opacity = 0.65,
            };
            icon.Children.Add(shape);
            return icon;
        }

        /// <summary>Mirrors the dock arrow so it points toward the panel.</summary>
        private void UpdateHomeIconDirection()
        {
            if (_homeIconFlip == null)
                return;
            var panelLeft = Canvas.GetLeft(_panel);
            var cardLeft = Canvas.GetLeft(_popup);
            if (double.IsNaN(panelLeft) || double.IsNaN(cardLeft))
                return;
            var panelCenter = panelLeft + _panel.ActualWidth / 2;
            var cardCenter = cardLeft + _popup.ActualWidth / 2;
            _homeIconFlip.ScaleX = panelCenter > cardCenter ? -1 : 1;
        }

        private void UpdatePopupHomeButton()
        {
            if (_popupHomeButton == null)
                return;
            _popupHomeButton.Visibility = _appSettings.PopupLeftFraction != null
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            UpdateHomeIconDirection();
        }

        private void ResetPopupPosition()
        {
            _appSettings.PopupLeftFraction = null;
            _appSettings.PopupTopFraction = null;
            _appSettings.Save();
            UpdatePopupHomeButton();
            if (_popup.Visibility == System.Windows.Visibility.Visible)
                PositionPopupNextToPanel();
        }

        private void RememberPopupPosition()
        {
            try
            {
                if (_popupDragFrom == null)
                    return;
                var from = _popupDragFrom.Value;
                _popupDragFrom = null;
                var left = Canvas.GetLeft(_popup);
                var top = Canvas.GetTop(_popup);
                var w = _overlay.Surface.ActualWidth;
                var h = _overlay.Surface.ActualHeight;
                if (double.IsNaN(left) || double.IsNaN(top) || w < 100 || h < 100)
                    return;
                // A plain click on the header (or its close button) is not a move.
                if (Math.Abs(left - from.X) < 3 && Math.Abs(top - from.Y) < 3)
                    return;
                _appSettings.PopupLeftFraction = left / w;
                _appSettings.PopupTopFraction = top / h;
                _appSettings.Save();
                UpdatePopupHomeButton();
            }
            catch (Exception ex)
            {
                Logger.Error("RememberPopupPosition failed", ex);
            }
        }

        /// <summary>Places a card right of the panel, flipping left at the screen edge.</summary>
        private void PositionCardNextToPanel(System.Windows.FrameworkElement card, double baseWidth)
        {
            var left = Canvas.GetLeft(_panel);
            var top = Canvas.GetTop(_panel);
            if (double.IsNaN(left)) left = 40;
            if (double.IsNaN(top)) top = 130;

            var scale = _panel.PanelScale;
            // All cards scale via layout, so ActualWidth is already final.
            var measured = _panel.ActualWidth;
            var panelWidth = measured > 50 ? measured : 272 * scale;
            var cardWidth = card.ActualWidth > 50 ? card.ActualWidth : baseWidth * scale;
            var x = left + panelWidth + 8;

            var canvasWidth = _overlay.Surface.ActualWidth;
            if (canvasWidth > 100 && x + cardWidth > canvasWidth)
                x = Math.Max(0, left - cardWidth - 8);

            Canvas.SetLeft(card, x);
            Canvas.SetTop(card, top);
        }

        private void SaveLayout()
        {
            try
            {
                CaptureLayout();
                _saveDebounce.Stop();
                _saveDebounce.Start();
            }
            catch (Exception ex)
            {
                Logger.Error("SaveLayout failed", ex);
            }
        }

        private void CaptureLayout()
        {
            // The user's chosen spot, not a temporarily clamped one (see KeepPanelOnScreen).
            var left = double.IsNaN(_wantLeft) ? Canvas.GetLeft(_panel) : _wantLeft;
            var top = double.IsNaN(_wantTop) ? Canvas.GetTop(_panel) : _wantTop;
            if (!double.IsNaN(left)) _settings.PanelLeft = left;
            if (!double.IsNaN(top)) _settings.PanelTop = top;
            // Persist the scale only once the user explicitly chose one; an
            // auto-derived value must never be latched into the settings.
            if (_scaleTouched)
                _settings.Scale = _panel.PanelScale;
        }

        private static double SafeCoord(double value, double fallback) =>
            double.IsNaN(value) || double.IsInfinity(value) ? fallback : Math.Max(0, value);

        private void RunOnUi(Action action)
        {
            if (_panel.Dispatcher.CheckAccess())
                action();
            else
                _panel.Dispatcher.BeginInvoke(action);
        }
    }
}
