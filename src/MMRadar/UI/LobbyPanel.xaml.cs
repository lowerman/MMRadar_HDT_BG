using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using MMRadar.Game;
using MMRadar.Wallii;

namespace MMRadar.UI
{
    /// <summary>
    /// The main overlay card listing the 8 lobby players with rating and avg placement.
    /// Deliberately free of HDT dependencies so it can be exercised in the harness.
    /// </summary>
    public partial class LobbyPanel : UserControl
    {
        public event Action<PlayerSummary> PlayerClicked;
        public event Action LayoutChanged;

        /// <summary>Raised when the user changes the scale via the mouse wheel.</summary>
        public event Action ScaleChangedByUser;

        /// <summary>Raised when the user collapses/expands the panel.</summary>
        public event Action<bool> CollapsedChanged;

        private bool _dragging;
        private Point _dragStart;
        private double _dragDistance;
        private bool _collapsed;
        private List<LobbyRowVm> _rows = new List<LobbyRowVm>();

        public double PanelScale
        {
            get => RootScale.ScaleX;
            set
            {
                var clamped = Math.Max(0.5, Math.Min(value, 2.0));
                RootScale.ScaleX = clamped;
                RootScale.ScaleY = clamped;
            }
        }

        private IReadOnlyList<PlayerSummary> _lastSummaries;
        private string _lastStatus;

        public LobbyPanel()
        {
            InitializeComponent();
            ThemeManager.ApplyToResources(Resources);
            ThemeManager.ThemeChanged += OnThemeChanged;
            // Collapsed panels sit at ~30% opacity until hovered, so they do not
            // draw attention during a game.
            MouseEnter += (s, e) => UpdateIdleOpacity();
            MouseLeave += (s, e) => UpdateIdleOpacity();
        }

        private void OnThemeChanged()
        {
            ThemeManager.ApplyToResources(Resources);
            // Row brushes are computed per theme — rebuild them from the last data.
            if (_lastSummaries != null)
            {
                var status = _lastStatus;
                SetStats(_lastSummaries);
                if (status != null)
                    SetStatus(status);
            }
        }

        /// <summary>Collapsed = a small low-opacity pill instead of the full card.</summary>
        public bool IsCollapsed
        {
            get => _collapsed;
            set
            {
                if (_collapsed == value)
                    return;
                _collapsed = value;
                ApplyCollapsedVisual();
            }
        }

        private void ApplyCollapsedVisual()
        {
            Body.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
            TitleRankPart.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
            CollapseButton.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
            RootBorder.Width = _collapsed ? double.NaN : 272; // NaN = size to content
            Header.Cursor = _collapsed ? Cursors.Hand : Cursors.SizeAll;
            Header.ToolTip = _collapsed ? "Click to expand · drag to move" : null;
            UpdateIdleOpacity();
        }

        private void UpdateIdleOpacity()
        {
            var target = _collapsed && !IsMouseOver ? 0.3 : 1.0;
            RootBorder.BeginAnimation(OpacityProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(160)));
        }

        public void ShowWaiting()
        {
            RowsControl.ItemsSource = _rows = new List<LobbyRowVm>();
            StatusText.Text = "Waiting for lobby…";
            StatusText.Visibility = Visibility.Visible;
        }

        public void ShowLoading(IReadOnlyList<LobbyPlayerInfo> players)
        {
            StatusText.Text = "Loading wallii.gg stats…";
            StatusText.Visibility = Visibility.Visible;
            _rows = players
                .Select(p => new LobbyRowVm
                {
                    Name = p.Name,
                    IsLocal = p.IsLocalPlayer,
                    HasStats = false,
                    RatingText = "…",
                })
                .ToList();
            RowsControl.ItemsSource = _rows;
        }

        public void SetStats(IReadOnlyList<PlayerSummary> summaries)
        {
            _lastSummaries = summaries;
            _lastStatus = null;
            StatusText.Visibility = Visibility.Collapsed;
            _rows = summaries
                .OrderByDescending(s => s.OnLeaderboard || s.FallbackRating != null)
                .ThenByDescending(s => s.OnLeaderboard ? s.Rating : s.FallbackRating ?? 0)
                .ThenBy(s => s.LobbyName, StringComparer.OrdinalIgnoreCase)
                .Select(LobbyRowVm.From)
                .ToList();
            RowsControl.ItemsSource = _rows;
        }

        public void SetStatus(string message)
        {
            _lastStatus = message;
            StatusText.Text = message;
            StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Updates dead/alive greying from the in-game state.</summary>
        public void UpdateLiveState(IReadOnlyList<LobbyPlayerInfo> players)
        {
            foreach (var row in _rows)
            {
                var match = players.FirstOrDefault(p =>
                    string.Equals(p.Name, row.Summary?.LobbyName ?? row.Name, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    row.IsDead = match.IsDead;
            }
        }

        // --- window chrome ---

        private void Row_Click(object sender, RoutedEventArgs e)
        {
            var vm = (sender as FrameworkElement)?.Tag as LobbyRowVm;
            if (vm?.Summary != null && vm.HasStats)
                PlayerClicked?.Invoke(vm.Summary);
        }

        private void CollapseButton_Click(object sender, RoutedEventArgs e)
        {
            IsCollapsed = !_collapsed;
            CollapsedChanged?.Invoke(_collapsed);
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _dragDistance = 0;
            _dragStart = e.GetPosition(Parent as UIElement);
            Header.CaptureMouse();
        }

        private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging)
                return;
            _dragging = false;
            Header.ReleaseMouseCapture();
            LayoutChanged?.Invoke();

            // A click (not a drag) on the collapsed pill expands the panel.
            if (_collapsed && _dragDistance < 4)
            {
                IsCollapsed = false;
                CollapsedChanged?.Invoke(false);
            }
        }

        private void Header_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_dragging && e.LeftButton != MouseButtonState.Pressed)
            {
                _dragging = false;
                Header.ReleaseMouseCapture();
                LayoutChanged?.Invoke();
            }
        }

        private void Header_LostMouseCapture(object sender, MouseEventArgs e)
        {
            // Capture can be force-released (e.g. the panel is hidden mid-drag);
            // without this the panel would later glue itself to the cursor.
            if (_dragging)
            {
                _dragging = false;
                LayoutChanged?.Invoke();
            }
        }

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
            _dragDistance += Math.Abs(dx) + Math.Abs(dy);

            var left = Canvas.GetLeft(this);
            var top = Canvas.GetTop(this);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            var newLeft = left + dx;
            var newTop = top + dy;
            if (parent is FrameworkElement fe && fe.ActualWidth > 100)
            {
                newLeft = Math.Max(0, Math.Min(newLeft, fe.ActualWidth - 60));
                newTop = Math.Max(0, Math.Min(newTop, fe.ActualHeight - 40));
            }
            Canvas.SetLeft(this, newLeft);
            Canvas.SetTop(this, newTop);
        }

        private void Header_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            PanelScale = PanelScale + (e.Delta > 0 ? 0.1 : -0.1);
            ScaleChangedByUser?.Invoke();
            LayoutChanged?.Invoke();
            e.Handled = true;
        }
    }
}
