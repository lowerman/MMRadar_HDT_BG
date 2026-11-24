using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MMRadar.Standalone
{
    /// <summary>
    /// The app's single window: status, updates, the panel's look (one compact
    /// row per setting) and app options. Closing it only hides it — MMRadar
    /// keeps running in the tray.
    /// </summary>
    public partial class AppWindow : Window
    {
        private static readonly Brush Selected = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xC3, 0x77)));

        private readonly AppCore _core;
        private readonly Action<bool> _setAutostart;
        private readonly Action _openDataFolder;
        private bool _initializing = true;
        private bool _allowClose;

        public event Action UpdateRequested;
        public event Action CheckRequested;
        public event Action WhatsNewRequested;

        public AppWindow(AppCore core, bool autostart, Action<bool> setAutostart, Action openDataFolder)
        {
            InitializeComponent();
            _core = core;
            _setAutostart = setAutostart;
            _openDataFolder = openDataFolder;

            var version = UpdateService.CurrentVersion.ToString(3);
            Title = "MMRadar";
            VersionText.Text = "v" + version;

            AutostartBox.IsChecked = autostart;
            AutoUpdateBox.IsChecked = _core.AppSettings.AutoCheckUpdates;
            SyncPanelChoices();
            // The in-game gear card can change the same settings meanwhile.
            Activated += (s, e) => SyncPanelChoices();
            _initializing = false;
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

        // --- panel settings ---

        private void ThemeDark_Click(object sender, RoutedEventArgs e) => SetTheme("dark");
        private void ThemeNeutral_Click(object sender, RoutedEventArgs e) => SetTheme("hdt");
        private void ThemeWarm_Click(object sender, RoutedEventArgs e) => SetTheme("hearthstone");

        private void SetTheme(string key)
        {
            _core.SetTheme(key);
            SyncPanelChoices();
        }

        private void SortDesc_Click(object sender, RoutedEventArgs e) => SetSort(false);
        private void SortAsc_Click(object sender, RoutedEventArgs e) => SetSort(true);

        private void SetSort(bool ascending)
        {
            _core.SetSortAscending(ascending);
            SyncPanelChoices();
        }

        private void ChipsClassic_Click(object sender, RoutedEventArgs e) => SetChips("classic");
        private void ChipsTint_Click(object sender, RoutedEventArgs e) => SetChips("tint");
        private void ChipsText_Click(object sender, RoutedEventArgs e) => SetChips("bare");

        private void SetChips(string key)
        {
            _core.SetChipStyle(key);
            SyncPanelChoices();
        }

        private void Preview_Click(object sender, RoutedEventArgs e) => _core.ToggleOverlay();

        private void Reset_Click(object sender, RoutedEventArgs e) => _core.ResetPosition();

        /// <summary>Outlines the current choice of every panel setting.</summary>
        private void SyncPanelChoices()
        {
            var theme = _core.Theme;
            Mark(ThemeDark, theme == "dark");
            Mark(ThemeNeutral, theme != "dark" && theme != "hearthstone");
            Mark(ThemeWarm, theme == "hearthstone");
            Mark(SortDesc, !_core.SortAscending);
            Mark(SortAsc, _core.SortAscending);
            var chips = _core.ChipStyle;
            Mark(ChipsClassic, chips != "tint" && chips != "bare");
            Mark(ChipsTint, chips == "tint");
            Mark(ChipsText, chips == "bare");
        }

        private static void Mark(Button button, bool selected)
        {
            if (selected)
                button.BorderBrush = Selected;
            else
                button.ClearValue(BorderBrushProperty);
        }

        private static Brush Freeze(Brush b)
        {
            b.Freeze();
            return b;
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
    }
}
