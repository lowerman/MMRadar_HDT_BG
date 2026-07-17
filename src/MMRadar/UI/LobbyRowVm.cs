using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using MMRadar.Wallii;

namespace MMRadar.UI
{
    public class LobbyRowVm : INotifyPropertyChanged
    {
        private bool _isDead;

        public PlayerSummary Summary { get; set; }

        public string Name { get; set; }
        public bool IsLocal { get; set; }
        public bool HasStats { get; set; }
        public string RankText { get; set; }
        public string RatingText { get; set; }
        public string AvgText { get; set; }
        public Brush AvgBrush { get; set; }
        public Brush AvgTextBrush { get; set; }
        public bool IsLive { get; set; }
        public Brush RankBrush { get; set; }
        public string TooltipText { get; set; }

        public Visibility LocalChipVisibility => IsLocal ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LiveVisibility => IsLive ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RankVisibility =>
            string.IsNullOrEmpty(RankText) ? Visibility.Hidden : Visibility.Visible;
        public Visibility AvgVisibility => HasStats ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NoDataVisibility => HasStats ? Visibility.Collapsed : Visibility.Visible;

        public bool IsDead
        {
            get => _isDead;
            set
            {
                if (_isDead == value)
                    return;
                _isDead = value;
                OnPropertyChanged(nameof(IsDead));
                OnPropertyChanged(nameof(RowOpacity));
                OnPropertyChanged(nameof(DeadVisibility));
            }
        }

        public double RowOpacity => IsDead ? 0.45 : 1.0;
        public Visibility DeadVisibility => IsDead ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public static LobbyRowVm From(PlayerSummary s)
        {
            var vm = new LobbyRowVm
            {
                Summary = s,
                Name = s.DisplayName ?? s.LobbyName,
                IsLocal = s.IsLocalPlayer,
                HasStats = s.OnLeaderboard,
                IsLive = s.IsLive,
            };
            if (s.OnLeaderboard)
            {
                vm.RankText = "#" + s.Rank;
                vm.RankBrush = s.Rank <= 100
                    ? ThemeManager.Freeze(ThemeManager.Current.Accent)
                    : ThemeManager.Freeze(ThemeManager.Current.TextSecondary);
                vm.RatingText = UiHelpers.FormatRating(s.Rating);
                vm.AvgText = UiHelpers.FormatAvg(s.BestAvg);
                var theme = ThemeManager.Current;
                if (s.BestAvg == null)
                {
                    vm.AvgBrush = ThemeManager.Freeze(theme.ChipBackground);
                    vm.AvgTextBrush = ThemeManager.Freeze(theme.TextMuted);
                }
                else if (theme.FilledChips)
                {
                    // Solid chip + dark text: maximum contrast for the key number.
                    vm.AvgBrush = UiHelpers.AvgPlacementBrush(s.BestAvg.Value);
                    vm.AvgTextBrush = ThemeManager.Freeze(Color.FromArgb(0xFF, 0x12, 0x14, 0x17));
                }
                else
                {
                    vm.AvgBrush = UiHelpers.AvgPlacementTintBrush(s.BestAvg.Value);
                    vm.AvgTextBrush = UiHelpers.AvgPlacementBrush(s.BestAvg.Value);
                }
                vm.TooltipText = $"{s.Region} · rank #{s.Rank} · {s.GamesToday} games today · {s.GamesWeek} this week" +
                                 "\nClick for recent games";
            }
            else
            {
                vm.RankText = null;
                vm.RatingText = "—";
                vm.TooltipText = "Not on the tracked leaderboard\n(below the rating cutoff)";
            }
            return vm;
        }
    }
}
