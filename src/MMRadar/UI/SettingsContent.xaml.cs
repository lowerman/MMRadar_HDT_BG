using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MMRadar.UI
{
    /// <summary>
    /// The settings UI itself, shared by two hosts: the classic window opened
    /// from HDT's Options and the in-overlay card opened from the panel's gear.
    /// </summary>
    public partial class SettingsContent : UserControl
    {
        public event Action<string> ThemeSelected;
        public event Action<bool> SortChanged;
        public event Action<string> ChipStyleSelected;
        public event Action PreviewClicked;
        public event Action ResetClicked;

        public SettingsContent()
        {
            InitializeComponent();
        }

        /// <summary>Refreshes the highlighted choices (call every time the host is shown).</summary>
        public void Sync(string currentTheme, bool sortAscending, string chipStyle)
        {
            HighlightTheme(currentTheme);
            HighlightSort(sortAscending);
            HighlightChips(chipStyle);
        }

        private void Preview_Click(object sender, RoutedEventArgs e) => PreviewClicked?.Invoke();

        private void Reset_Click(object sender, RoutedEventArgs e) => ResetClicked?.Invoke();

        private void ThemeDark_Click(object sender, RoutedEventArgs e) => SelectTheme("dark");

        private void ThemeHdt_Click(object sender, RoutedEventArgs e) => SelectTheme("hdt");

        private void ThemeHs_Click(object sender, RoutedEventArgs e) => SelectTheme("hearthstone");

        private void SelectTheme(string key)
        {
            HighlightTheme(key);
            ThemeSelected?.Invoke(key);
        }

        private void SortDesc_Click(object sender, RoutedEventArgs e) => SelectSort(false);

        private void SortAsc_Click(object sender, RoutedEventArgs e) => SelectSort(true);

        private void SelectSort(bool ascending)
        {
            HighlightSort(ascending);
            SortChanged?.Invoke(ascending);
        }

        private void HighlightTheme(string key)
        {
            var gold = (Brush)Resources["AccentGold"];
            Mark(ThemeDarkButton, key == "dark", gold);
            Mark(ThemeHdtButton, key == "hdt", gold);
            Mark(ThemeHsButton, key == "hearthstone", gold);
        }

        private void HighlightSort(bool ascending)
        {
            var gold = (Brush)Resources["AccentGold"];
            Mark(SortDescButton, !ascending, gold);
            Mark(SortAscButton, ascending, gold);
        }

        private void ChipsClassic_Click(object sender, RoutedEventArgs e) => SelectChips("classic");

        private void ChipsTint_Click(object sender, RoutedEventArgs e) => SelectChips("tint");

        private void ChipsBare_Click(object sender, RoutedEventArgs e) => SelectChips("bare");

        private void SelectChips(string key)
        {
            HighlightChips(key);
            ChipStyleSelected?.Invoke(key);
        }

        private void HighlightChips(string key)
        {
            var gold = (Brush)Resources["AccentGold"];
            Mark(ChipsClassicButton, key != "tint" && key != "bare", gold);
            Mark(ChipsTintButton, key == "tint", gold);
            Mark(ChipsBareButton, key == "bare", gold);
        }

        private static void Mark(Button button, bool selected, Brush accent)
        {
            button.BorderBrush = selected ? accent : Brushes.Transparent;
        }
    }
}
