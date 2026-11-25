using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MMRadar.UI;

namespace MMRadar.Standalone
{
    /// <summary>
    /// In-overlay settings card (see the XAML note). Toggled by the panel's
    /// gear button; the host hides it on match transitions.
    /// </summary>
    public partial class OverlaySettingsCard : UserControl
    {
        public event Action CloseRequested;

        private double _scale = 1.0;

        public OverlaySettingsCard(
            Action togglePreview,
            Action resetPosition,
            Action<string> onThemeSelected,
            Action<bool> onSortChanged,
            Action<string> onChipStyleSelected)
        {
            InitializeComponent();
            Body.PreviewClicked += () => togglePreview?.Invoke();
            Body.ResetClicked += () => resetPosition?.Invoke();
            Body.ThemeSelected += key => onThemeSelected?.Invoke(key);
            Body.SortChanged += asc => onSortChanged?.Invoke(asc);
            Body.ChipStyleSelected += key => onChipStyleSelected?.Invoke(key);
        }

        /// <summary>Refreshes highlighted choices; call right before showing.</summary>
        public void Sync(string currentTheme, bool sortAscending, string chipStyle) =>
            Body.Sync(currentTheme, sortAscending, chipStyle);

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

        // --- header drag (same as the dossier; the spot is not remembered) ---

        private bool _dragging;
        private Point _dragStart;

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _dragStart = e.GetPosition(Parent as UIElement);
            Header.CaptureMouse();
        }

        private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            Header.ReleaseMouseCapture();
        }

        private void Header_LostMouseCapture(object sender, MouseEventArgs e) => _dragging = false;

        private void Header_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging)
                return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragging = false;
                if (Header.IsMouseCaptured)
                    Header.ReleaseMouseCapture();
                return;
            }
            var parent = Parent as UIElement;
            if (parent == null)
                return;
            var pos = e.GetPosition(parent);
            var dx = pos.X - _dragStart.X;
            var dy = pos.Y - _dragStart.Y;
            _dragStart = pos;

            var left = Canvas.GetLeft(this);
            var top = Canvas.GetTop(this);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;
            Canvas.SetLeft(this, left + dx);
            Canvas.SetTop(this, top + dy);
        }
    }
}
