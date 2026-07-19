using System;
using System.Globalization;
using System.Windows.Media;

namespace MMRadar.UI
{
    internal static class UiHelpers
    {
        public static Color AvgPlacementColor(double avg)
        {
            if (avg < 2.5) return (Color)ColorConverter.ConvertFromString("#FF3DD08F");
            if (avg < 3.2) return (Color)ColorConverter.ConvertFromString("#FF8FC65A");
            if (avg < 4.0) return (Color)ColorConverter.ConvertFromString("#FFDFB93E");
            if (avg < 4.8) return (Color)ColorConverter.ConvertFromString("#FFE8924D");
            return (Color)ColorConverter.ConvertFromString("#FFEF6F6A");
        }

        public static Brush AvgPlacementBrush(double avg)
        {
            var brush = new SolidColorBrush(AvgPlacementColor(avg));
            brush.Freeze();
            return brush;
        }

        /// <summary>Translucent tint of the placement color, for airy chip backgrounds.</summary>
        public static Brush AvgPlacementTintBrush(double avg, byte alpha = 0x2C)
        {
            var c = AvgPlacementColor(avg);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }

        public static Brush PlacementBrush(double placement)
        {
            if (placement <= 1) return Frozen("#FFF0C568");
            if (placement <= 4) return Frozen("#FF2FBF83");
            return Frozen("#FFD9534F");
        }

        public static string FormatRating(int rating) =>
            rating.ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ");

        public static string FormatAvg(double? avg) =>
            avg == null ? "–" : avg.Value.ToString("0.00", CultureInfo.InvariantCulture);

        public static string FormatDelta(int delta) =>
            (delta > 0 ? "+" : delta < 0 ? "-" : "") + FormatRating(Math.Abs(delta));

        public static string FormatPlacement(double placement) =>
            placement % 1 == 0
                ? ((int)placement).ToString(CultureInfo.InvariantCulture)
                : placement.ToString("0.#", CultureInfo.InvariantCulture);

        public static string TimeAgo(DateTimeOffset at)
        {
            var diff = DateTimeOffset.UtcNow - at;
            if (diff < TimeSpan.Zero)
                return at.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);
            if (diff.TotalMinutes < 1)
                return "just now";
            if (diff.TotalMinutes < 60)
                return $"{(int)diff.TotalMinutes} min ago";
            if (diff.TotalHours < 24)
                return $"{(int)diff.TotalHours} hr ago";
            if (diff.TotalDays < 7)
                return $"{(int)diff.TotalDays} d ago";
            return at.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);
        }

        private static Brush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
