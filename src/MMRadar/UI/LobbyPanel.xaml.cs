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

        private double _layoutScale = 1.0;

        /// <summary>
        /// Zoom factor. Applied by scaling font sizes and layout metrics — never a
        /// RenderTransform, which would blur the text at any non-1.0 zoom.
        /// </summary>
        public double PanelScale
        {
            get => _layoutScale;
            set
            {
                var clamped = Math.Max(0.5, Math.Min(value, 2.0));
                if (Math.Abs(clamped - _layoutScale) < 0.001)
                    return;
                _layoutScale = clamped;
                ApplyLayoutScale();
            }
        }

        private void ApplyLayoutScale()
        {
            var k = _layoutScale;
            Resources["RowFontSize"] = 16.0 * k;
            Resources["ChipFontSize"] = 13.5 * k;
            Resources["RowHeight"] = 24.0 * k;
            Resources["NameMaxWidth"] = 150.0 * k;
            Resources["BadgeFontSize"] = 10.5 * k;
            Resources["MarkFontSize"] = 12.0 * k;
            Resources["DotSize"] = 6.0 * k;
            Resources["TitleFontSize"] = 11.0 * k;
            Resources["CtxFontSize"] = 11.0 * k;
            Resources["StatusFontSize"] = 11.5 * k;
            Resources["ChipColWidth"] = new GridLength(52.0 * k);
            RootBorder.MinWidth = _collapsed ? 0 : 192.0 * k;
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
            HeaderContextText.Visibility = !_collapsed && _hasLobbyContext
                ? Visibility.Visible
                : Visibility.Collapsed;
            RootBorder.MinWidth = _collapsed ? 0 : 192.0 * _layoutScale;
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
            HeaderContextText.Visibility = Visibility.Collapsed;
            StatusText.Text = "Waiting for lobby…";
            StatusText.Visibility = Visibility.Visible;
        }

        public void ShowLoading(IReadOnlyList<LobbyPlayerInfo> players)
        {
            HeaderContextText.Visibility = Visibility.Collapsed;
            StatusText.Text = "Loading wallii.gg stats…";
            StatusText.Visibility = Visibility.Visible;
            _rows = players
                .Select(p => new LobbyRowVm
                {
                    Name = p.IsLocalPlayer ? "you" : p.Name,
                    NameBrush = p.IsLocalPlayer
                        ? ThemeManager.Freeze(ThemeManager.Current.Accent)
                        : ThemeManager.Freeze(ThemeManager.Current.TextPrimary),
                    IsLocal = p.IsLocalPlayer,
                    HasStats = false,
                    RatingText = "…",
                })
                .ToList();
            RowsControl.ItemsSource = _rows;
        }

        private static int EffectiveRating(PlayerSummary s) =>
            s.OnLeaderboard ? s.Rating : s.FallbackRating ?? (s.BelowCutoff ? 1 : 0);

        public void SetStats(IReadOnlyList<PlayerSummary> summaries)
        {
            _lastSummaries = summaries;
            _lastStatus = null;
            StatusText.Visibility = Visibility.Collapsed;

            if (summaries.Any(s => s.TeamId > 0))
            {
                // Duos: keep teammates together — teams ordered by their strongest
                // member, a thin divider between teams so pairs read as blocks.
                var teams = summaries
                    .GroupBy(s => s.TeamId > 0 ? "t" + s.TeamId : "solo:" + s.LobbyName)
                    .OrderByDescending(g => g.Max(EffectiveRating))
                    .ToList();
                _rows = new List<LobbyRowVm>();
                for (var i = 0; i < teams.Count; i++)
                {
                    var first = true;
                    foreach (var s in teams[i]
                                 .OrderByDescending(EffectiveRating)
                                 .ThenBy(x => x.LobbyName, StringComparer.OrdinalIgnoreCase))
                    {
                        var vm = LobbyRowVm.From(s);
                        if (first && i > 0)
                            vm.TeamSeparatorVisibility = Visibility.Visible;
                        first = false;
                        _rows.Add(vm);
                    }
                }
            }
            else
            {
                // Solo: real ratings first (desc), then below-cutoff, then unknowns.
                _rows = summaries
                    .OrderByDescending(EffectiveRating)
                    .ThenBy(s => s.LobbyName, StringComparer.OrdinalIgnoreCase)
                    .Select(LobbyRowVm.From)
                    .ToList();
            }
            RowsControl.ItemsSource = _rows;
            UpdateLobbyContext(summaries);
        }

        private bool _hasLobbyContext;

        /// <summary>Header summary: average lobby rating and your delta to it.</summary>
        private void UpdateLobbyContext(IReadOnlyList<PlayerSummary> summaries)
        {
            // Players confirmed below the leaderboard cutoff count as 8000 — an upper
            // bound, so the average is marked as approximate.
            int? RatingOf(PlayerSummary s) =>
                s.OnLeaderboard ? s.Rating : s.FallbackRating ?? (s.BelowCutoff ? 8000 : (int?)null);

            var known = summaries.Select(RatingOf).Where(r => r != null).Select(r => r.Value).ToList();
            if (known.Count < 2)
            {
                _hasLobbyContext = false;
                HeaderContextText.Visibility = Visibility.Collapsed;
                return;
            }

            var approximate = summaries.Any(s => s.BelowCutoff);
            var avg = (int)Math.Round(known.Average());
            HeaderContextText.Inlines.Clear();
            HeaderContextText.Inlines.Add(new System.Windows.Documents.Run(
                $"avg {(approximate ? "~" : "")}{UiHelpers.FormatRating(avg)}"));
            HeaderContextText.ToolTip = "Average lobby rating" + (approximate
                ? " (players below the leaderboard cutoff are counted as 8 000)"
                : "");

            var local = summaries.FirstOrDefault(s => s.IsLocalPlayer);
            var localRating = local != null ? RatingOf(local) : null;
            if (localRating != null)
            {
                var delta = localRating.Value - avg;
                HeaderContextText.Inlines.Add(new System.Windows.Documents.Run(" · "));
                HeaderContextText.Inlines.Add(new System.Windows.Documents.Run(UiHelpers.FormatDelta(delta))
                {
                    Foreground = delta >= 0
                        ? UiHelpers.PlacementBrush(2)
                        : ThemeManager.Freeze(System.Windows.Media.Color.FromArgb(0xFF, 0xF8, 0x71, 0x71)),
                    FontWeight = FontWeights.SemiBold,
                });
                HeaderContextText.ToolTip += " · your delta to it";
            }
            _hasLobbyContext = true;
            if (!IsCollapsed)
                HeaderContextText.Visibility = Visibility.Visible;
        }

        public void SetStatus(string message)
        {
            _lastStatus = message;
            StatusText.Text = message;
            StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Updates dead/alive greying and picks up duos team ids as they appear.</summary>
        public void UpdateLiveState(IReadOnlyList<LobbyPlayerInfo> players)
        {
            var teamsChanged = false;
            foreach (var row in _rows)
            {
                var match = players.FirstOrDefault(p =>
                    string.Equals(p.Name, row.Summary?.LobbyName ?? row.Name, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    continue;
                row.IsDead = match.IsDead;
                if (row.Summary != null && match.TeamId > 0 && row.Summary.TeamId != match.TeamId)
                {
                    row.Summary.TeamId = match.TeamId;
                    teamsChanged = true;
                }
            }
            // Team ids often surface a bit after the first render — regroup once known.
            if (teamsChanged && _lastSummaries != null)
                SetStats(_lastSummaries);
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
