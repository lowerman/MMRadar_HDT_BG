using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MMRadar.UI
{
    /// <summary>
    /// In-overlay settings card (see the XAML note). Toggled by the panel's
    /// gear button; the host hides it on match transitions.
    /// </summary>
    public partial class SettingsCard : UserControl
    {
        public event Action CloseRequested;

        private double _scale = 1.0;

        public SettingsCard(
            Action togglePreview,
            Action resetPosition,
            Action<string> onThemeSelected,
            Action<bool> onSortChanged)
        {
            InitializeComponent();
            Body.PreviewClicked += () => togglePreview?.Invoke();
            Body.ResetClicked += () => resetPosition?.Invoke();
            Body.ThemeSelected += key => onThemeSelected?.Invoke(key);
            Body.SortChanged += asc => onSortChanged?.Invoke(asc);
        }

        /// <summary>Refreshes highlighted choices; call right before showing.</summary>
        public void Sync(string currentTheme, bool sortAscending) =>
            Body.Sync(currentTheme, sortAscending);

        /// <summary>
        /// Matches the panel's zoom. A LayoutTransform (not RenderTransform)
        /// re-runs layout at the scaled size, so the text stays crisp.
        /// </summary>
        public double CardScale
        {
            get => _scale;
            set
            {
                var clamped = Math.Max(0.5, Math.Min(value, 2.0));
                if (Math.Abs(clamped - _scale) < 0.001)
                    return;
                _scale = clamped;
                Root.LayoutTransform = clamped == 1.0
                    ? null
                    : new ScaleTransform(clamped, clamped);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    }
}
