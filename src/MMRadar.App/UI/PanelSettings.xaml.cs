using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MMRadar.Standalone
{
    /// <summary>
    /// Panel look and placement settings in the app window. The app's own copy of
    /// the plugin's SettingsContent, so the app can change it without touching the plugin.
    /// </summary>
    public partial class PanelSettings : UserControl
    {
        public event Action<string> ThemeSelected;
        public event Action<bool> SortChanged;
        public event Action<string> ChipStyleSelected;
        public event Action PanelToggleClicked;
        public event Action ResetClicked;

        public PanelSettings()
        {
            InitializeComponent();
        }

        private Brush Gold => (Brush)Resources["AccentGold"];

        /// <summary>Refreshes the highlighted choices.</summary>
        public void Sync(string currentTheme, bool sortAscending, string chipStyle)
        {
            HighlightTheme(currentTheme);
            HighlightSort(sortAscending);
            HighlightChips(chipStyle);
        }

        /// <summary>Reflects whether the panel is on screen right now.</summary>
        public void SetPanelShown(bool shown)
        {
            PanelButtonText.Text = shown ? "Hide test panel" : "Show test panel";
            Mark(PanelButton, shown, Gold);
        }

        private void Panel_Click(object sender, RoutedEventArgs e) => PanelToggleClicked?.Invoke();

        private void Reset_Click(object sender, RoutedEventArgs e) => ResetClicked?.Invoke();

        private void ThemeDark_Click(object sender, RoutedEventArgs e) => SelectTheme("dark");

        private void ThemeNeutral_Click(object sender, RoutedEventArgs e) => SelectTheme("hdt");

        private void ThemeWarm_Click(object sender, RoutedEventArgs e) => SelectTheme("hearthstone");

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

        private void ChipsClassic_Click(object sender, RoutedEventArgs e) => SelectChips("classic");

        private void ChipsTint_Click(object sender, RoutedEventArgs e) => SelectChips("tint");

        private void ChipsBare_Click(object sender, RoutedEventArgs e) => SelectChips("bare");

        private void SelectChips(string key)
        {
            HighlightChips(key);
            ChipStyleSelected?.Invoke(key);
        }

        private void HighlightTheme(string key)
        {
            Mark(ThemeDarkButton, key == "dark", Gold);
            Mark(ThemeNeutralButton, key == "hdt", Gold);
            Mark(ThemeWarmButton, key == "hearthstone", Gold);
        }

        private void HighlightSort(bool ascending)
        {
            Mark(SortDescButton, !ascending, Gold);
            Mark(SortAscButton, ascending, Gold);
        }

        private void HighlightChips(string key)
        {
            Mark(ChipsClassicButton, key != "tint" && key != "bare", Gold);
            Mark(ChipsTintButton, key == "tint", Gold);
            Mark(ChipsBareButton, key == "bare", Gold);
        }

        private static void Mark(Button button, bool selected, Brush accent)
        {
            button.BorderBrush = selected ? accent : Brushes.Transparent;
        }
    }
}
