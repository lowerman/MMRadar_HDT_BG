using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MMRadar.UI;
using MMRadar.Wallii;

namespace MMRadar.Standalone
{
    public partial class DossierPopup : UserControl
    {
        public event Action CloseRequested;

        private bool _dragging;
        private Point _dragStart;

        /// <summary>Chart series: pre-window rating followed by each game's ending MMR.</summary>
        private List<double> _series = new List<double>();

        private double _layoutScale = 1.0;

        /// <summary>Zoom via font/metric scaling — matches LobbyPanel, keeps text crisp.</summary>
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
            // Whole-pixel metrics only: fractional font sizes make Display-mode
            // ClearType hint digit advances unevenly (see LobbyPanel).
            double R(double v) => Math.Max(1.0, Math.Round(v));
            Resources["PopupWidth"] = R(280.0 * k);
            Resources["PNameFont"] = R(15.0 * k);
            Resources["PSubFont"] = R(11.0 * k);
            Resources["PStatusFont"] = R(12.0 * k);
            Resources["PCapFont"] = R(9.5 * k);
            Resources["PBigCapFont"] = R(10.0 * k);
            Resources["PBigFont"] = R(22.0 * k);
            Resources["PValFont"] = R(12.5 * k);
            Resources["PGameFont"] = R(12.0 * k);
            Resources["PGameChipFont"] = R(11.5 * k);
            Resources["PChartHeight"] = R(36.0 * k);
            Resources["PBadgeFont"] = R(10.0 * k);
            Resources["PColTime"] = new GridLength(R(80.0 * k));
            Resources["PColChip"] = new GridLength(R(36.0 * k));
            Resources["PColDelta"] = new GridLength(R(52.0 * k));
            QueueFitList(); // row height follows the zoom
        }

        // --- games list scrolling ---

        /// <summary>Rows visible at once; the half row hints that the list goes on.</summary>
        private const double VisibleRows = 10.5;

        /// <summary>Rows moved per wheel notch.</summary>
        private const int RowsPerNotch = 1;

        /// <summary>Height of the edge fade that marks hidden rows above / below.</summary>
        private const double FadePx = 18;

        private double _rowHeight;

        private void QueueFitList() =>
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitList));

        /// <summary>Caps the list at ~10.5 rows of the current zoom (measured, not guessed).</summary>
        private void FitList()
        {
            var first = GamesControl.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            if (first == null || first.ActualHeight < 1)
                return;
            _rowHeight = first.ActualHeight;
            GamesScroller.MaxHeight = Math.Round(_rowHeight * VisibleRows);
            UpdateListFade();
        }

        private void GamesScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (GamesScroller.ScrollableHeight <= 0 || _rowHeight < 1)
                return;
            e.Handled = true;
            // One notch = one game, instantly, snapped to row boundaries so a row
            // is never sliced at the top. Touchpads send many small deltas:
            // they add up, and every full notch's worth moves one row.
            _wheelAccum += e.Delta;
            var notches = (int)(_wheelAccum / 120);
            if (notches == 0)
                return;
            _wheelAccum -= notches * 120;
            var row = Math.Round(GamesScroller.VerticalOffset / _rowHeight);
            var target = (row - notches * RowsPerNotch) * _rowHeight;
            target = Math.Max(0, Math.Min(target, GamesScroller.ScrollableHeight));
            GamesScroller.ScrollToVerticalOffset(target);
        }

        /// <summary>Wheel delta not yet turned into whole rows (touchpads).</summary>
        private int _wheelAccum;

        private void GamesScroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateListFade();

        /// <summary>
        /// Soft fade on the edge(s) that have more rows behind them, so the list
        /// reads as scrollable without any extra chrome. No fade when it all fits.
        /// </summary>
        private void UpdateListFade()
        {
            if (!(GamesScroller.Template?.FindName("PART_ScrollContentPresenter", GamesScroller) is UIElement presenter))
                return;
            var viewport = GamesScroller.ViewportHeight;
            if (GamesScroller.ScrollableHeight <= 0 || viewport < 1)
            {
                presenter.OpacityMask = null;
                return;
            }
            var f = Math.Min(0.25, FadePx / viewport);
            var moreAbove = GamesScroller.VerticalOffset > 0.5;
            var moreBelow = GamesScroller.VerticalOffset < GamesScroller.ScrollableHeight - 0.5;
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            mask.GradientStops.Add(new GradientStop(moreAbove ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, f));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - f));
            mask.GradientStops.Add(new GradientStop(moreBelow ? Colors.Transparent : Colors.Black, 1));
            mask.Freeze();
            presenter.OpacityMask = mask;
        }

        private class GameRowVm
        {
            public string TimeText { get; set; }
            public string PlacementText { get; set; }
            public Brush PlacementBrush { get; set; }
            public string DeltaText { get; set; }
            public Brush DeltaBrush { get; set; }
            public string MmrText { get; set; }
        }

        private PlayerDetails _lastDetails;

        public DossierPopup()
        {
            InitializeComponent();
            ThemeManager.ApplyToResources(Resources);
            ThemeManager.ThemeChanged += OnThemeChanged;
        }

        private void OnThemeChanged()
        {
            ThemeManager.ApplyToResources(Resources);
            if (_lastDetails != null && ContentPanel.Visibility == Visibility.Visible)
                SetData(_lastDetails);
        }

        public void ShowLoading(PlayerSummary summary)
        {
            SetHeader(summary);
            ContentPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "Loading recent games…";
            StatusText.Visibility = Visibility.Visible;
            Visibility = Visibility.Visible;
        }

        public void ShowError(PlayerSummary summary, string message)
        {
            SetHeader(summary);
            ContentPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
            Visibility = Visibility.Visible;
        }

        public void SetData(PlayerDetails details)
        {
            _lastDetails = details;
            var s = details.Summary;
            SetHeader(s);
            StatusText.Visibility = Visibility.Collapsed;
            ContentPanel.Visibility = Visibility.Visible;

            AvgCaption.Text = details.RecentGames.Count > 0
                ? $"AVG PLACE · LAST {details.RecentGames.Count}"
                : "AVG PLACE";
            if (details.RecentAvg != null)
            {
                BigAvgText.Text = UiHelpers.FormatAvg(details.RecentAvg);
                BigAvgText.Foreground = UiHelpers.AvgSemanticBrush(details.RecentAvg.Value);
            }
            else
            {
                BigAvgText.Text = "–";
                BigAvgText.Foreground = ThemeManager.Freeze(ThemeManager.Current.TextMuted);
            }

            // Computed from the same derived games shown below — always self-consistent.
            DayStatText.Text = details.TodayAvg != null && details.TodayCount > 0
                ? $"{UiHelpers.FormatAvg(details.TodayAvg)} avg · {details.TodayCount} games"
                : "no games";
            WeekStatText.Text = details.Week7Avg != null && details.Week7Count > 0
                ? $"{UiHelpers.FormatAvg(details.Week7Avg)} avg · {details.Week7Count} games"
                : "no games";

            NoGamesText.Visibility = details.RecentGames.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            GamesControl.ItemsSource = details.RecentGames.Select(g => new GameRowVm
            {
                TimeText = UiHelpers.TimeAgo(g.At),
                PlacementText = UiHelpers.FormatPlacement(g.Placement),
                PlacementBrush = UiHelpers.PlacementBrush(g.Placement),
                DeltaText = UiHelpers.FormatDelta(g.DeltaMmr),
                DeltaBrush = g.DeltaMmr >= 0
                    ? UiHelpers.PlacementBrush(2)
                    : new SolidColorBrush(Color.FromArgb(0xFF, 0xF8, 0x71, 0x71)),
                MmrText = UiHelpers.FormatRating(g.EndingMmr),
            }).ToList();

            _wheelAccum = 0;
            GamesScroller.ScrollToTop(); // every dossier opens at the newest game
            QueueFitList();

            BuildChart(details.RecentGames);

            Visibility = Visibility.Visible;
        }

        private void SetHeader(PlayerSummary s)
        {
            NameText.Text = s.DisplayName ?? s.LobbyName;
            LivePanel.Visibility = s.IsLive ? Visibility.Visible : Visibility.Collapsed;
            SubText.Text = s.OnLeaderboard
                ? $"{s.Region} · rank #{s.Rank} · {UiHelpers.FormatRating(s.Rating)} MMR"
                : "not on the tracked leaderboard";
        }

        /// <summary>
        /// The chart plots the MMR trajectory over exactly the games in the list below:
        /// x = game (oldest → newest), y = ending MMR, dashed line = rating before the
        /// first shown game, end dot = current. Colored by the net trend of the window.
        /// </summary>
        private void BuildChart(IReadOnlyList<GameRecord> games)
        {
            if (games == null || games.Count == 0)
            {
                ChartBlock.Visibility = Visibility.Collapsed;
                _series = new List<double>();
                return;
            }
            ChartBlock.Visibility = Visibility.Visible;

            var chronological = games.Reverse().ToList(); // list is newest-first
            var first = chronological[0];

            _series = new List<double> { first.EndingMmr - first.DeltaMmr };
            _series.AddRange(chronological.Select(g => (double)g.EndingMmr));

            var netDelta = (int)(_series[_series.Count - 1] - _series[0]);
            ChartCaption.Text = $"MMR · LAST {games.Count} GAMES";
            ChartDeltaText.Text = UiHelpers.FormatDelta(netDelta);
            ChartDeltaText.Foreground = TrendBrush(netDelta);
            ChartFromText.Text = UiHelpers.TimeAgo(first.At);
            ChartToText.Text = UiHelpers.TimeAgo(chronological[chronological.Count - 1].At);

            RenderSparkline();
        }

        private static SolidColorBrush TrendBrush(int netDelta) => netDelta >= 0
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0x3D, 0xDC, 0x97))
            : new SolidColorBrush(Color.FromArgb(0xFF, 0xF8, 0x71, 0x71));

        private void RenderSparkline()
        {
            SparklineCanvas.Children.Clear();
            var w = SparklineCanvas.ActualWidth;
            var h = SparklineCanvas.ActualHeight > 10 ? SparklineCanvas.ActualHeight : 36.0;
            if (_series.Count < 2 || w < 20)
                return;

            var min = _series.Min();
            var max = _series.Max();
            var range = Math.Max(1.0, max - min);
            var plotW = w - 4; // leave room for the endpoint dot
            var stepX = plotW / (_series.Count - 1);
            double Y(double v) => h - 4 - (h - 8) * (v - min) / range;

            var netDelta = (int)(_series[_series.Count - 1] - _series[0]);
            var trend = TrendBrush(netDelta);
            var trendColor = trend.Color;

            // Dashed baseline at the pre-window rating: everything above it is net gain.
            var baseY = Y(_series[0]);
            SparklineCanvas.Children.Add(new Line
            {
                X1 = 0, X2 = w, Y1 = baseY, Y2 = baseY,
                Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
            });

            var points = new PointCollection();
            for (var i = 0; i < _series.Count; i++)
                points.Add(new Point(i * stepX, Y(_series[i])));

            var fillPoints = new PointCollection(points) { new Point(plotW, h), new Point(0, h) };
            SparklineCanvas.Children.Add(new Polygon
            {
                Points = fillPoints,
                Fill = new SolidColorBrush(Color.FromArgb(0x1C, trendColor.R, trendColor.G, trendColor.B)),
            });
            SparklineCanvas.Children.Add(new Polyline
            {
                Points = points,
                Stroke = trend,
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
            });

            // Emphasized endpoint: the current rating.
            var last = points[points.Count - 1];
            var dot = new Ellipse { Width = 5, Height = 5, Fill = trend };
            Canvas.SetLeft(dot, last.X - 2.5);
            Canvas.SetTop(dot, last.Y - 2.5);
            SparklineCanvas.Children.Add(dot);
        }

        private void SparklineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderSparkline();

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Visibility = Visibility.Collapsed;
            CloseRequested?.Invoke();
        }

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

        private void Header_LostMouseCapture(object sender, MouseEventArgs e)
        {
            _dragging = false;
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

            var left = Canvas.GetLeft(this);
            var top = Canvas.GetTop(this);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;
            Canvas.SetLeft(this, left + dx);
            Canvas.SetTop(this, top + dy);
        }
    }
}
