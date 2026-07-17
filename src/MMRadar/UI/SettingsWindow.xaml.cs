using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MMRadar.UI
{
    /// <summary>
    /// Tiny settings dialog opened from Options → Tracker → Plugins → Settings.
    /// Kept HDT-free: the plugin core passes plain callbacks.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly Action _togglePreview;
        private readonly Action _resetPosition;
        private readonly Action<string> _onThemeSelected;

        public SettingsWindow(
            Action togglePreview,
            Action resetPosition,
            string currentTheme,
            Action<string> onThemeSelected)
        {
            InitializeComponent();
            _togglePreview = togglePreview;
            _resetPosition = resetPosition;
            _onThemeSelected = onThemeSelected;
            HighlightTheme(currentTheme);
        }

        private void Preview_Click(object sender, RoutedEventArgs e) => _togglePreview?.Invoke();

        private void Reset_Click(object sender, RoutedEventArgs e) => _resetPosition?.Invoke();

        private void ThemeDark_Click(object sender, RoutedEventArgs e) => SelectTheme("dark");

        private void ThemeGlass_Click(object sender, RoutedEventArgs e) => SelectTheme("glass");

        private void ThemeHs_Click(object sender, RoutedEventArgs e) => SelectTheme("hearthstone");

        private void SelectTheme(string key)
        {
            HighlightTheme(key);
            _onThemeSelected?.Invoke(key);
        }

        private void HighlightTheme(string key)
        {
            var gold = (Brush)Resources["AccentGold"];
            Mark(ThemeDarkButton, key == "dark", gold);
            Mark(ThemeGlassButton, key == "glass", gold);
            Mark(ThemeHsButton, key == "hearthstone", gold);
        }

        private static void Mark(Button button, bool selected, Brush accent)
        {
            button.BorderBrush = selected ? accent : Brushes.Transparent;
        }
    }
}
