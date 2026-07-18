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
        public Brush NameBrush { get; set; }

        /// <summary>Zebra tint shared by both rows of a duos team (transparent in solo).</summary>
        public Brush RowBackground { get; set; } = Brushes.Transparent;
        public bool IsLocal { get; set; }
        public bool HasStats { get; set; }
        public string RatingText { get; set; }
        public Brush RatingBrush { get; set; }
        public string AvgText { get; set; }
        public Brush AvgBrush { get; set; }
        public Brush AvgTextBrush { get; set; }
        public bool IsLive { get; set; }
        public string TooltipText { get; set; }

        public Visibility LiveVisibility => IsLive ? Visibility.Visible : Visibility.Collapsed;
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
            // Your own row shows a plain gold "you" instead of your (possibly long)
            // nickname — you know who you are, and the panel stays narrow.
            var vm = new LobbyRowVm
            {
                Summary = s,
                Name = s.IsLocalPlayer ? "you" : s.DisplayName ?? s.LobbyName,
                NameBrush = s.IsLocalPlayer
                    ? ThemeManager.Freeze(ThemeManager.Current.Accent)
                    : ThemeManager.Freeze(ThemeManager.Current.TextPrimary),
                IsLocal = s.IsLocalPlayer,
                HasStats = s.OnLeaderboard,
                IsLive = s.IsLive,
            };
            var theme0 = ThemeManager.Current;
            vm.RatingBrush = ThemeManager.Freeze(theme0.RatingText);
            if (s.OnLeaderboard)
            {
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
                vm.TooltipText = $"{s.Region} · rank #{s.Rank} · avg place {UiHelpers.FormatAvg(s.BestAvg)} over {s.GamesWeek} games in 7 days" +
                                 "\nClick for recent games";
            }
            else if (s.FallbackRating != null)
            {
                // On the official leaderboard, but not tracked by wallii: rating plus a
                // rank computed from the full board (tooltip); no clickable dossier.
                vm.RatingText = UiHelpers.FormatRating(s.FallbackRating.Value);
                vm.TooltipText = (s.FallbackRank != null
                                     ? $"≈ rank #{s.FallbackRank} on the official leaderboard\n"
                                     : "Official leaderboard rating\n") +
                                 "Detailed stats are only available for wallii-tracked players";
            }
            else if (s.BelowCutoff)
            {
                // Confirmed absent from the official board: below the ~8000 cutoff.
                vm.RatingText = "<" + UiHelpers.FormatRating(8000);
                vm.RatingBrush = ThemeManager.Freeze(theme0.TextMuted);
                vm.TooltipText = "Below the official leaderboard cutoff (~8 000 MMR)\nNo detailed stats available";
            }
            else
            {
                vm.RatingText = "—";
                vm.RatingBrush = ThemeManager.Freeze(theme0.TextMuted);
                vm.TooltipText = "Rating unavailable\n(leaderboard could not be fetched)";
            }
            return vm;
        }
    }
}
