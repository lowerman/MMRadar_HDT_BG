using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MMRadar.Hs;
using MMRadar.UI;
using MMRadar.Util;
using MMRadar.Wallii;

namespace MMRadar.Standalone
{
    /// <summary>
    /// The app's own window. Home answers "is it working?" in one line and
    /// "how am I doing?" with the user's own dossier; Panel and App hold the
    /// settings. Closing it only hides it — MMRadar keeps running in the tray.
    /// </summary>
    public partial class AppWindow : Window
    {
        private const int HomeGames = 5;

        private readonly AppCore _core;
        private readonly Action<bool> _setAutostart;
        private readonly Action _openDataFolder;
        private readonly Func<(string BattleTag, string Region)> _identity;
        private bool _initializing = true;
        private bool _allowClose;
        private int _tab;
        private int _profileGeneration;

        public event Action UpdateRequested;
        public event Action CheckRequested;
        public event Action WhatsNewRequested;

        public AppWindow(AppCore core, bool autostart, Action<bool> setAutostart, Action openDataFolder,
            Func<(string BattleTag, string Region)> identity)
        {
            InitializeComponent();
            _core = core;
            _setAutostart = setAutostart;
            _openDataFolder = openDataFolder;
            _identity = identity;

            var version = UpdateService.CurrentVersion.ToString(3);
            Title = "MMRadar " + version;
            AboutText.Text = $"MMRadar {version} · open source (MIT) · data by wallii.gg and the official leaderboard";

            Settings.PreviewClicked += () => _core.ToggleOverlay();
            Settings.ResetClicked += () => _core.ResetPosition();
            Settings.ThemeSelected += key => _core.SetTheme(key);
            Settings.SortChanged += asc => _core.SetSortAscending(asc);
            Settings.ChipStyleSelected += key => _core.SetChipStyle(key);
            Settings.Sync(_core.Theme, _core.SortAscending, _core.ChipStyle);
            AdaptSharedTexts(Settings); // its tree exists right after InitializeComponent

            // The dossier card is the overlay's; inside a window it is not
            // closable or draggable.
            Dossier.CloseButton.Visibility = Visibility.Collapsed;
            Dossier.Header.Cursor = Cursors.Arrow;

            AutostartBox.IsChecked = autostart;
            AutoUpdateBox.IsChecked = _core.AppSettings.AutoCheckUpdates;
            StyleModeButtons();
            SelectTab(0);
            _initializing = false;
        }

        // --- tabs ---

        private void TabHome_Click(object sender, RoutedEventArgs e) => SelectTab(0);
        private void TabPanel_Click(object sender, RoutedEventArgs e) => SelectTab(1);
        private void TabApp_Click(object sender, RoutedEventArgs e) => SelectTab(2);

        /// <summary>Switches pages by visibility only: inactive pages stay Hidden
        /// (not Collapsed) so they keep reserving space and the window never jumps.</summary>
        private void SelectTab(int tab)
        {
            _tab = tab;
            HomePage.Visibility = tab == 0 ? Visibility.Visible : Visibility.Hidden;
            Settings.Visibility = tab == 1 ? Visibility.Visible : Visibility.Hidden;
            AppPage.Visibility = tab == 2 ? Visibility.Visible : Visibility.Hidden;
            StyleTab(TabHome, tab == 0);
            StyleTab(TabPanel, tab == 1);
            StyleTab(TabApp, tab == 2);
            UpdateDot.Visibility = tab != 0 && UpdateCard.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private static void StyleTab(Button tab, bool active)
        {
            tab.Background = active ? new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
            // Inactive: drop the local colour so the style's muted text and its
            // hover highlight apply again (a local value would override both).
            if (active)
                tab.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF6));
            else
                tab.ClearValue(ForegroundProperty);
        }

        // --- status ---

        public void ShowWelcome() => WelcomeCard.Visibility = Visibility.Visible;

        public void SetStatus(string title, string detail, bool ok)
        {
            StatusTitle.Text = title;
            StatusDetail.Text = string.IsNullOrEmpty(detail) ? "" : " · " + detail;
            StatusDot.Fill = new SolidColorBrush(ok
                ? Color.FromRgb(0x34, 0xC2, 0x86)
                : Color.FromRgb(0x8B, 0x94, 0x9E));
        }

        // --- your stats ---

        private string Mode => _core.AppSettings.ProfileMode == "1" ? "1" : "0";

        private void Solo_Click(object sender, RoutedEventArgs e) => SetMode("0");
        private void Duos_Click(object sender, RoutedEventArgs e) => SetMode("1");

        private void SetMode(string mode)
        {
            if (Mode == mode)
                return;
            _core.AppSettings.ProfileMode = mode;
            _core.AppSettings.Save();
            StyleModeButtons();
            RefreshProfile();
        }

        private void StyleModeButtons()
        {
            var primary = new SolidColorBrush(Color.FromRgb(0xE6, 0xC3, 0x77));
            if (Mode == "1")
            {
                DuosButton.Foreground = primary;
                SoloButton.ClearValue(ForegroundProperty);
            }
            else
            {
                SoloButton.Foreground = primary;
                DuosButton.ClearValue(ForegroundProperty);
            }
        }

        /// <summary>(Re)loads the user's own stats for the current mode.</summary>
        public async void RefreshProfile()
        {
            var generation = ++_profileGeneration;
            var (battleTag, region) = _identity();
            if (string.IsNullOrEmpty(battleTag) || string.IsNullOrEmpty(region))
            {
                ShowProfileNote("Start Hearthstone once — MMRadar picks up your account from the game and shows your stats here.");
                return;
            }

            var modeName = Mode == "1" ? "Duos" : "Solo";
            var stub = new PlayerSummary { LobbyName = GameTracker.StripTag(battleTag) };
            ProfileEmpty.Visibility = Visibility.Collapsed;
            Dossier.ShowLoading(stub);
            Dossier.SubText.Text = $"{region} · {modeName}";

            OwnProfile profile;
            try
            {
                profile = await _core.LoadOwnProfileAsync(battleTag, region, Mode, HomeGames);
            }
            catch (Exception ex)
            {
                Logger.Debug("Own profile failed: " + ex.Message);
                profile = null;
            }
            if (generation != _profileGeneration)
                return; // a newer refresh (mode switch) took over
            Logger.Info($"Home stats: '{stub.LobbyName}' {region} mode={Mode} -> " +
                        (profile == null
                            ? "failed"
                            : $"tracked={profile.Summary?.OnLeaderboard} details={profile.Details != null} " +
                              $"board={profile.Summary?.FallbackRating} below={profile.Summary?.BelowCutoff} " +
                              $"walliiDown={profile.WalliiDown}"));

            if (profile?.Details != null)
            {
                Dossier.SetData(profile.Details);
                return;
            }

            var s = profile?.Summary ?? stub;
            string ratingLine;
            if (s.OnLeaderboard)
                ratingLine = $"{UiHelpers.FormatRating(s.Rating)} MMR · rank #{s.Rank}";
            else if (s.FallbackRating != null)
                ratingLine = $"Official leaderboard: {UiHelpers.FormatRating(s.FallbackRating.Value)} MMR" +
                             (s.FallbackRank != null ? $" · ≈ rank #{s.FallbackRank}" : "");
            else if (s.BelowCutoff)
                ratingLine = "Below the official leaderboard cutoff (8 000 MMR).";
            else
                ratingLine = "Your rating isn't available right now.";

            var note = profile == null || profile.WalliiDown
                ? "wallii.gg is unavailable, so recent games can't be shown right now."
                : "Recent games come from wallii.gg, which tracks only the top players of each region.";
            Dossier.ShowError(s, ratingLine + "\n\n" + note);
            Dossier.SubText.Text = $"{region} · {modeName}";
        }

        private void ShowProfileNote(string text)
        {
            _profileGeneration++;
            Dossier.Visibility = Visibility.Collapsed;
            ProfileEmpty.Visibility = Visibility.Visible;
            ProfileEmptyText.Text = text;
        }

        // --- updates ---

        public void ShowUpdate(UpdateInfo update, bool canInstallInPlace)
        {
            UpdateCard.Visibility = Visibility.Visible;
            UpdateTitle.Text = $"Version {update.Version.ToString(3)} is available";
            UpdateDetail.Text = canInstallInPlace
                ? "Takes a few seconds — MMRadar restarts by itself and keeps your settings."
                : "MMRadar's folder is read-only, so the update has to be downloaded manually.";
            UpdateButton.Content = canInstallInPlace ? "Update now" : "Download";
            UpdateButton.IsEnabled = true;
            if (_tab != 0)
                UpdateDot.Visibility = Visibility.Visible; // don't let it hide behind another tab
        }

        public void SetUpdateBusy(string text)
        {
            UpdateButton.IsEnabled = false;
            UpdateDetail.Text = text;
        }

        public void ShowUpdateError(string text)
        {
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "Try again";
            UpdateDetail.Text = text;
        }

        public void ShowCheckResult(string text)
        {
            CheckButton.IsEnabled = true;
            CheckResult.Text = text;
            CheckResult.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        public void CloseForExit()
        {
            _allowClose = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true; // the app lives on in the tray
                Hide();
            }
            base.OnClosing(e);
        }

        private void UpdateButton_Click(object sender, RoutedEventArgs e) => UpdateRequested?.Invoke();

        private void WhatsNew_Click(object sender, RoutedEventArgs e) => WhatsNewRequested?.Invoke();

        private void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            ShowCheckResult("Checking…");
            CheckButton.IsEnabled = false;
            CheckRequested?.Invoke();
        }

        private void GitHub_Click(object sender, RoutedEventArgs e) =>
            UpdateService.OpenInBrowser($"https://github.com/{UpdateService.Repository}");

        private void DataFolder_Click(object sender, RoutedEventArgs e) => _openDataFolder();

        private void AutostartBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initializing)
                _setAutostart(AutostartBox.IsChecked == true);
        }

        private void AutoUpdateBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_initializing)
                return;
            _core.AppSettings.AutoCheckUpdates = AutoUpdateBox.IsChecked == true;
            _core.AppSettings.Save();
        }

        /// <summary>
        /// The settings block is shared with the HDT plugin and still speaks its
        /// language in two spots; adjust them here so the shared file stays as is.
        /// </summary>
        private static void AdaptSharedTexts(DependencyObject root)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is TextBlock tb)
                {
                    if (tb.Text == "HDT")
                    {
                        tb.Text = "Neutral";
                        if (tb.Parent is FrameworkElement p && p.Parent is Button b)
                            b.ToolTip = "Neutral grey (default)";
                    }
                    else if (tb.Text != null && tb.Text.StartsWith("Drag the header to move the panel."))
                    {
                        tb.Text = "Drag the header to move the panel. The preview shows it even without the game.";
                    }
                }
                if (child is DependencyObject d)
                    AdaptSharedTexts(d);
            }
        }
    }
}
