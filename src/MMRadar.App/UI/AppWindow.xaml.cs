using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MMRadar.Standalone
{
    /// <summary>
    /// The app's own window (everything that is not the overlay): live status,
    /// first-run welcome, updates, the shared settings and app options. Closing
    /// it only hides it — MMRadar keeps running in the tray.
    /// </summary>
    public partial class AppWindow : Window
    {
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

            VersionText.Text = "v" + UpdateService.CurrentVersion.ToString(3);

            Settings.PanelToggleClicked += () => _core.ToggleOverlay();
            Settings.ResetClicked += () => _core.ResetPosition();
            Settings.ThemeSelected += key => _core.SetTheme(key);
            Settings.SortChanged += asc => _core.SetSortAscending(asc);
            Settings.ChipStyleSelected += key => _core.SetChipStyle(key);
            Settings.Sync(_core.Theme, _core.SortAscending, _core.ChipStyle);
            Settings.SetPanelShown(_core.IsPanelShown);
            _core.PanelShownChanged += Settings.SetPanelShown;

            AutostartBox.IsChecked = autostart;
            AutoUpdateBox.IsChecked = _core.AppSettings.AutoCheckUpdates;
            _initializing = false;
        }

        /// <summary>Shows the welcome card (first run only).</summary>
        public void ShowWelcome() => WelcomeCard.Visibility = Visibility.Visible;

        /// <param name="ok">Green dot: connected and working.</param>
        /// <param name="warning">Amber dot: something the user has to act on.</param>
        public void SetStatus(string title, string detail, bool ok, bool warning = false)
        {
            StatusTitle.Text = title;
            StatusDetail.Text = detail;
            StatusDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            StatusDot.Fill = new SolidColorBrush(warning
                ? Color.FromRgb(0xF0, 0xA8, 0x48)
                : ok
                    ? Color.FromRgb(0x34, 0xC2, 0x86)
                    : Color.FromRgb(0x8B, 0x94, 0x9E));
        }

        // --- updates ---

        public void ShowUpdate(UpdateInfo update, bool canInstallInPlace)
        {
            UpdateCard.Visibility = Visibility.Visible;
            UpdateTitle.Text = $"Version {update.Version.ToString(3)} is available";
            UpdateDetail.Text = canInstallInPlace
                ? "Takes a few seconds. MMRadar restarts by itself and keeps your settings."
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
            CheckButton.IsEnabled = false;
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
