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
        private bool _previewActive;
        private int _previewGeneration;
        private SettingsWindow _settingsWindow;
        private DateTime _lastPartialLobbyRetry = DateTime.MinValue;
        private bool _spectateNoticeShown;

        /// <summary>Set by HDT's choices watcher: a card-choice UI is on screen.</summary>
        private volatile bool _choiceCardsVisible;
        private bool _dodgeApplied;
        private bool _choicesHooked;

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
            _settingsCard = new SettingsCard(ToggleOverlay, ResetPosition, SetTheme, SetSortAscending)
            {
                Visibility = System.Windows.Visibility.Collapsed,
            };
            _settingsCard.CloseRequested += HideSettingsCard;

            Canvas.SetLeft(_panel, SafeCoord(_settings.PanelLeft, 40));
            Canvas.SetTop(_panel, SafeCoord(_settings.PanelTop, 130));
            // Provisional until the overlay window has a real size (see ShowPanel):
            // at plugin load HDT's overlay still has its design-time dimensions.
            _panel.PanelScale = _settings.Scale > 0 ? _settings.Scale : 1.0;
            _popup.PanelScale = _panel.PanelScale;
            _scaleTouched = _settings.Scale > 0;

            OverlayExtensions.SetIsOverlayHitTestVisible(_panel, true);
            OverlayExtensions.SetIsOverlayHitTestVisible(_popup, true);
            OverlayExtensions.SetIsOverlayHitTestVisible(_settingsCard, true);

            _panel.PlayerClicked += OnPlayerClicked;
            _panel.LayoutChanged += SaveLayout;
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

            // Attach to the overlay last so a failure above cannot orphan the controls.
            HdtCore.OverlayCanvas.Children.Add(_panel);
            HdtCore.OverlayCanvas.Children.Add(_popup);
            HdtCore.OverlayCanvas.Children.Add(_settingsCard);

            // The same signal HDT uses to dodge its own overlay during discover
            // picks: fires within ~16 ms of the choice UI opening or closing.
            try
            {
                Hearthstone_Deck_Tracker.Hearthstone.Watchers.ChoicesWatcher.Change += OnChoicesChanged;
                _choicesHooked = true;
            }
            catch (Exception ex)
            {
                Logger.Debug("ChoicesWatcher unavailable, falling back to polling: " + ex.Message);
            }

            Logger.Info("Plugin loaded");
        }

        public void Unload()
        {
            try
            {
                if (_choicesHooked)
                {
                    try { Hearthstone_Deck_Tracker.Hearthstone.Watchers.ChoicesWatcher.Change -= OnChoicesChanged; }
                    catch { /* watcher may already be gone */ }
                    _choicesHooked = false;
                }
                SaveLayout();
                ThemeManager.Reset();
                try { _settingsWindow?.Close(); } catch { }
                _settingsWindow = null;
                if (_panel != null)
                {
                    HdtCore.OverlayCanvas.Children.Remove(_panel);
                    HdtCore.OverlayCanvas.Children.Remove(_popup);
                    HdtCore.OverlayCanvas.Children.Remove(_settingsCard);
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
                    HideSettingsCard();     // a stray card over hero-pick hurts most
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
                    else if (!_spectateNoticeShown && _tracker.IsSpectating)
                    {
                        // Spectated games never hand over the roster — but hovering
                        // the leaderboard portraits reveals names one by one (the
                        // hover reader picks them up and the panel fills in).
                        _spectateNoticeShown = true;
                        _panel.SetStatus("Spectating — hover the leaderboard portraits to reveal players");
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
                        _ = FetchLobbyStatsAsync(_lobby, generation);
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
                             (better.GameUuid != null && better.GameUuid != _lobby.GameUuid)))
                        {
                            _lobby = better;
                            _phase = Phase.Fetching;
                            var generation = ++_fetchGeneration;
                            _ = FetchLobbyStatsAsync(better, generation);
                        }
                    }
                }

                // Belt-and-braces: re-evaluate the choice dodge each tick — catches
                // the combat→shop transition with a still-open pick, and serves as
                // the polling path when the watcher subscription failed.
                if (!_choicesHooked)
                {
                    var offered = HdtCore.Game.Player?.OfferedEntityIds;
                    _choiceCardsVisible = offered != null && offered.Count >= 2;
                }
                UpdateChoiceDodge();
            }
            catch (Exception ex)
            {
                Logger.Error("Tick failed", ex);
            }
        }

        private void OnChoicesChanged(object sender, HearthWatcher.EventArgs.ChoicesWatcher args)
        {
            try
            {
                var choice = args?.CurrentChoice;
                _choiceCardsVisible = choice != null && choice.IsVisible &&
                                      choice.Cards != null && choice.Cards.Any();
                RunOnUi(UpdateChoiceDodge);
            }
            catch (Exception ex)
            {
                Logger.Debug("OnChoicesChanged: " + ex.Message);
            }
        }

        /// <summary>
        /// While a card-choice UI (dig, trinket, triple reward, discover) is on
        /// screen, the overlay cards fade to 15% and RELEASE their clicks so the
        /// game cards underneath stay pickable. Combat is skipped (pending picks
        /// sit collapsed there) and so is the hero pick, where the panel is at
        /// its most useful.
        /// </summary>
        private void UpdateChoiceDodge()
        {
            bool dodge;
            try
            {
                dodge = _choiceCardsVisible &&
                        _phase != Phase.Idle && !_hiddenForThisGame &&
                        HdtCore.Game != null &&
                        HdtCore.Game.IsBattlegroundsMatch &&
                        !HdtCore.Game.IsBattlegroundsCombatPhase &&
                        HdtCore.Game.IsBattlegroundsHeroPickingDone;
            }
            catch
            {
                dodge = false;
            }
            if (dodge == _dodgeApplied)
                return;
            _dodgeApplied = dodge;
            foreach (var element in new System.Windows.FrameworkElement[] { _panel, _popup, _settingsCard })
            {
                // The attached property is the real click lever: it adds/removes the
                // element from HDT's clickable-region list; IsHitTestVisible alone
                // would not make the overlay window click-through again.
                OverlayExtensions.SetIsOverlayHitTestVisible(element, !dodge);
                element.BeginAnimation(System.Windows.UIElement.OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(
                        dodge ? 0.15 : 1.0, TimeSpan.FromMilliseconds(150)));
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
                    summary.TeamId = player.TeamId;
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
                HideSettingsCard(); // dossier and settings share the panel-side slot
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
                _settingsCard.Sync(_settings.Theme, _settings.SortAscending);
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
                    _settings.SortAscending, SetSortAscending);
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

        public void SetSortAscending(bool ascending)
        {
            _settings.SortAscending = ascending;
            _settings.Save();
            _panel.SortAscending = ascending; // re-sorts the current lobby in place
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
            _settingsCard.CardScale = 1.0;
            _settings.Save();
            _hiddenForThisGame = false;
            ShowPanel(); // re-applies the auto scale when the overlay has a real size
            if (_settingsCard.Visibility == System.Windows.Visibility.Visible)
                PositionCardNextToPanel(_settingsCard, 292); // follow the panel home
        }

        private void ResetToIdle()
        {
            _phase = Phase.Idle;
            _lobby = null;
            _fetchGeneration++;
            _popupGeneration++;
            _lastFetchFailed = false;
            _fetchRetries = 0;
            _spectateNoticeShown = false;
            _choiceCardsVisible = false;
            UpdateChoiceDodge(); // restore opacity + clickability for the next game
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
                var width = HdtCore.OverlayWindow.ActualWidth;
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

        private void HidePanel()
        {
            _popupGeneration++; // cancel any in-flight details fetch re-showing the popup
            _panel.Visibility = System.Windows.Visibility.Collapsed;
            _popup.Visibility = System.Windows.Visibility.Collapsed;
            HideSettingsCard();
        }

        private void PositionPopupNextToPanel()
        {
            _popup.PanelScale = _panel.PanelScale;
            PositionCardNextToPanel(_popup, 280);
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

            var canvasWidth = HdtCore.OverlayCanvas.ActualWidth;
            if (canvasWidth > 100 && x + cardWidth > canvasWidth)
                x = Math.Max(0, left - cardWidth - 8);

            Canvas.SetLeft(card, x);
            Canvas.SetTop(card, top);
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
