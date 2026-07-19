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
        private readonly Action<bool> _onSortChanged;

        public SettingsWindow(
            Action togglePreview,
            Action resetPosition,
            string currentTheme,
            Action<string> onThemeSelected,
            bool sortAscending,
            Action<bool> onSortChanged)
        {
            InitializeComponent();
            _togglePreview = togglePreview;
            _resetPosition = resetPosition;
            _onThemeSelected = onThemeSelected;
            _onSortChanged = onSortChanged;
            HighlightTheme(currentTheme);
            HighlightSort(sortAscending);
        }

        private void Preview_Click(object sender, RoutedEventArgs e) => _togglePreview?.Invoke();

        private void Reset_Click(object sender, RoutedEventArgs e) => _resetPosition?.Invoke();

        private void ThemeDark_Click(object sender, RoutedEventArgs e) => SelectTheme("dark");

        private void ThemeHdt_Click(object sender, RoutedEventArgs e) => SelectTheme("hdt");

        private void ThemeHs_Click(object sender, RoutedEventArgs e) => SelectTheme("hearthstone");

        private void SelectTheme(string key)
        {
            HighlightTheme(key);
            _onThemeSelected?.Invoke(key);
        }

        private void SortDesc_Click(object sender, RoutedEventArgs e) => SelectSort(false);

        private void SortAsc_Click(object sender, RoutedEventArgs e) => SelectSort(true);

        private void SelectSort(bool ascending)
        {
            HighlightSort(ascending);
            _onSortChanged?.Invoke(ascending);
        }

        private void HighlightSort(bool ascending)
        {
            var gold = (Brush)Resources["AccentGold"];
            Mark(SortDescButton, !ascending, gold);
            Mark(SortAscButton, ascending, gold);
        }

        private void HighlightTheme(string key)
        {
            var gold = (Brush)Resources["AccentGold"];
            Mark(ThemeDarkButton, key == "dark", gold);
            Mark(ThemeHdtButton, key == "hdt", gold);
            Mark(ThemeHsButton, key == "hearthstone", gold);
        }

        private static void Mark(Button button, bool selected, Brush accent)
        {
            button.BorderBrush = selected ? accent : Brushes.Transparent;
        }
    }
}
