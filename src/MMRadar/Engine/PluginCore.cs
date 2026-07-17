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
using Hearthstone_Deck_Tracker.Utility.Extensions;
using HdtConfig = Hearthstone_Deck_Tracker.Config;
using HdtCore = Hearthstone_Deck_Tracker.API.Core;
using HdtLog = Hearthstone_Deck_Tracker.Utility.Logging.Log;

namespace MMRadar.Engine
{
    /// <summary>
    /// Orchestrates everything inside HDT: game state polling, wallii fetches, overlay UI.
    /// This is the only class (besides LobbyTracker) that talks to HDT APIs.
    /// </summary>
    public class PluginCore
    {
        private enum Phase
        {
            Idle,
            WaitingForLobby,
            Fetching,
            Loaded,
        }

        private readonly LobbyTracker _tracker = new LobbyTracker();
        private WalliiService _wallii;
        private PluginSettings _settings;
        private LobbyPanel _panel;
        private PlayerDetailsPopup _popup;

        private Phase _phase = Phase.Idle;
        private LobbyState _lobby;
        private int _fetchGeneration;
        private int _popupGeneration;
        private DateTime _lastLiveRefresh = DateTime.MinValue;
        private bool _hiddenForThisGame;
        private bool _lastFetchFailed;
        private int _fetchRetries;
        private DateTime _nextRetryAtUtc = DateTime.MinValue;
        private bool _previewActive;
        private int _previewGeneration;
        private SettingsWindow _settingsWindow;
        private DateTime _lastPartialLobbyRetry = DateTime.MinValue;

        /// <summary>True once the user has chosen an explicit scale (mouse wheel).</summary>
        private bool _scaleTouched;

        public void Load()
        {
            Logger.InfoSink = m => HdtLog.Info("[MMRadar] " + m);
            Logger.DebugSink = m => HdtLog.Debug("[MMRadar] " + m);
            Logger.ErrorSink = m => HdtLog.Error("[MMRadar] " + m);

            var dir = Path.Combine(HdtConfig.AppDataPath, "MMRadar");
            _settings = PluginSettings.Load(dir);
            ThemeManager.Apply(_settings.Theme);
            _wallii = new WalliiService(
                new WalliiApi(_settings.WalliiBaseUrl, _settings.WalliiAnonKey),
                new OfficialBoardClient(_settings.OfficialBoardUrl, dir));

            _panel = new LobbyPanel { Visibility = System.Windows.Visibility.Collapsed };
            _popup = new PlayerDetailsPopup { Visibility = System.Windows.Visibility.Collapsed };

            Canvas.SetLeft(_panel, SafeCoord(_settings.PanelLeft, 40));
            Canvas.SetTop(_panel, SafeCoord(_settings.PanelTop, 130));
            // Provisional until the overlay window has a real size (see ShowPanel):
            // at plugin load HDT's overlay still has its design-time dimensions.
            _panel.PanelScale = _settings.Scale > 0 ? _settings.Scale : 1.0;
            _popup.PanelScale = _panel.PanelScale;
            _scaleTouched = _settings.Scale > 0;

            OverlayExtensions.SetIsOverlayHitTestVisible(_panel, true);
            OverlayExtensions.SetIsOverlayHitTestVisible(_popup, true);

            _panel.PlayerClicked += OnPlayerClicked;
            _panel.LayoutChanged += SaveLayout;
            _panel.ScaleChangedByUser += () =>
            {
                _scaleTouched = true;
                _popup.PanelScale = _panel.PanelScale;
            };
            _panel.IsCollapsed = _settings.Collapsed;
            _panel.CollapsedChanged += collapsed =>
            {
                _settings.Collapsed = collapsed;
                _settings.Save();
            };
            _popup.CloseRequested += () => _popupGeneration++;

            // Attach to the overlay last so a failure above cannot orphan the controls.
            HdtCore.OverlayCanvas.Children.Add(_panel);
            HdtCore.OverlayCanvas.Children.Add(_popup);

            Logger.Info("Plugin loaded");
        }

        public void Unload()
        {
            try
            {
                SaveLayout();
                ThemeManager.Reset();
                try { _settingsWindow?.Close(); } catch { }
                _settingsWindow = null;
                if (_panel != null)
                {
                    HdtCore.OverlayCanvas.Children.Remove(_panel);
                    HdtCore.OverlayCanvas.Children.Remove(_popup);
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
                if (HdtCore.Game == null || !_settings.OverlayEnabled)
                    return;

                if (HdtCore.Game.IsInMenu || !HdtCore.Game.IsBattlegroundsMatch)
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
                    _panel.ShowWaiting();
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
                }
                else if (_phase == Phase.Loaded || _phase == Phase.Fetching)
                {
                    if ((DateTime.UtcNow - _lastLiveRefresh).TotalSeconds >= 2)
                    {
                        _lastLiveRefresh = DateTime.UtcNow;
                        if (_lobby != null)
                        {
                            _tracker.AttachHeroEntities(_lobby.Players);
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
                        _ = FetchLobbyStatsAsync(_lobby, generation);
                    }

                    // A partial roster (e.g. right after a reconnect) keeps improving as
                    // more names surface — upgrade the panel whenever we learn new ones.
                    if (_phase == Phase.Loaded && _lobby != null && _lobby.Players.Count < 8 &&
                        (DateTime.UtcNow - _lastPartialLobbyRetry).TotalSeconds >= 5)
                    {
                        _lastPartialLobbyRetry = DateTime.UtcNow;
                        var better = _tracker.TryResolveLobby();
                        if (better != null && better.Players.Count > _lobby.Players.Count)
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

        private async Task FetchLobbyStatsAsync(LobbyState lobby, int generation)
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
                statusMessage = ex is WalliiAuthException
                    ? "wallii.gg key rejected — see plugin settings"
                    : "wallii.gg unavailable — will retry";
                _lastFetchFailed = true;
                _nextRetryAtUtc = DateTime.UtcNow.AddSeconds(30);
                summaries = lobby.Players
                    .Select(p => new PlayerSummary { LobbyName = p.Name, OnLeaderboard = false })
                    .ToList();
            }

            // Decorate with in-game info.
            foreach (var summary in summaries)
            {
                var player = lobby.Players.FirstOrDefault(p =>
                    string.Equals(p.Name, summary.LobbyName, StringComparison.OrdinalIgnoreCase));
                if (player != null)
                {
                    summary.IsLocalPlayer = player.IsLocalPlayer;
                    summary.HeroCardId = player.HeroCardId;
                    summary.GamePlayerId = player.PlayerId;
                }
            }

            RunOnUi(() =>
            {
                if (generation != _fetchGeneration)
                    return;
                _phase = Phase.Loaded;
                _panel.SetStats(summaries);
                if (statusMessage != null)
                    _panel.SetStatus(statusMessage);
            });
        }

        private async void OnPlayerClicked(PlayerSummary summary)
        {
            var generation = ++_popupGeneration;
            try
            {
                PositionPopupNextToPanel();
                _popup.ShowLoading(summary);
                var details = await _wallii
                    .GetPlayerDetailsAsync(summary, _lobby?.GameMode ?? "0", _settings.RecentGamesCount)
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
                _panel.ShowWaiting();
                _panel.SetStatus("PREVIEW — loading top players…");
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
                status = "PREVIEW — current top 8 · wallii.gg";
                if (lobby.Count == 0)
                    throw new WalliiApiException("empty top-players result", null);
            }
            catch (Exception ex)
            {
                Logger.Debug("Top-8 preview unavailable, using sample data: " + ex.Message);
                lobby = SampleData.Lobby();
                status = "PREVIEW — sample data (wallii.gg unavailable)";
                live = false;
            }

            RunOnUi(() =>
            {
                if (generation != _previewGeneration || !_previewActive)
                    return;
                _panel.SetStats(lobby);
                _panel.SetStatus(status);
                if (live)
                {
                    PositionPopupNextToPanel();
                    var first = lobby.Find(p => p.OnLeaderboard);
                    if (first != null)
                        OnPlayerClicked(first);
                }
            });
        }

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
                _settingsWindow = new SettingsWindow(ToggleOverlay, ResetPosition, _settings.Theme, SetTheme);
                try { _settingsWindow.Owner = HdtCore.MainWindow; }
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
            _settings.PanelLeft = 40;
            _settings.PanelTop = 130;
            // Back to resolution-based auto scale.
            _settings.Scale = 0;
            _scaleTouched = false;
            _panel.PanelScale = 1.0;
            _popup.PanelScale = 1.0;
            _settings.Save();
            _hiddenForThisGame = false;
            ShowPanel(); // re-applies the auto scale when the overlay has a real size
        }

        private void ResetToIdle()
        {
            _phase = Phase.Idle;
            _lobby = null;
            _fetchGeneration++;
            _popupGeneration++;
            _lastFetchFailed = false;
            _fetchRetries = 0;
            // Whatever lobby info HDT still holds now belongs to the finished game.
            _tracker.MarkCurrentLobbyInfoStale();
            _tracker.Reset();
            if (!_previewActive)
                HidePanel();
        }

        private void ShowPanel()
        {
            ApplyAutoScale();
            _panel.Visibility = System.Windows.Visibility.Visible;
        }

        /// <summary>
        /// Auto mode: match the game-window resolution so the panel keeps the same
        /// on-screen proportion everywhere (1080p ×1.0, 1440p ×1.33, 4K ×2.0).
        /// Only runs once the overlay window has been sized to the real game window,
        /// and is never persisted — the user's own wheel-zoom takes over for good.
        /// </summary>
        private void ApplyAutoScale()
        {
            if (_scaleTouched)
                return;
            try
            {
                var width = HdtCore.OverlayWindow.ActualWidth;
                if (width < 1000)
                    return; // overlay not sized to the game yet
                var auto = Math.Max(1.0, Math.Min(width / 1920.0, 2.0));
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

        private void HidePanel()
        {
            _popupGeneration++; // cancel any in-flight details fetch re-showing the popup
            _panel.Visibility = System.Windows.Visibility.Collapsed;
            _popup.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void PositionPopupNextToPanel()
        {
            var left = Canvas.GetLeft(_panel);
            var top = Canvas.GetTop(_panel);
            if (double.IsNaN(left)) left = 40;
            if (double.IsNaN(top)) top = 130;

            var scale = _panel.PanelScale;
            var measured = _panel.ActualWidth;
            var panelWidth = (measured > 50 ? measured : 272) * scale;
            var popupWidth = 272 * scale;
            var x = left + panelWidth + 8;

            var canvasWidth = HdtCore.OverlayCanvas.ActualWidth;
            if (canvasWidth > 100 && x + popupWidth > canvasWidth)
                x = Math.Max(0, left - popupWidth - 8);

            _popup.PanelScale = scale;
            Canvas.SetLeft(_popup, x);
            Canvas.SetTop(_popup, top);
        }

        private void SaveLayout()
        {
            try
            {
                var left = Canvas.GetLeft(_panel);
                var top = Canvas.GetTop(_panel);
                if (!double.IsNaN(left)) _settings.PanelLeft = left;
                if (!double.IsNaN(top)) _settings.PanelTop = top;
                // Persist the scale only once the user explicitly chose one; an
                // auto-derived value must never be latched into the settings.
                if (_scaleTouched)
                    _settings.Scale = _panel.PanelScale;
                _settings.Save();
            }
            catch (Exception ex)
            {
                Logger.Error("SaveLayout failed", ex);
            }
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
