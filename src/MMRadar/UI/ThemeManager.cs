using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace MMRadar.UI
{
    /// <summary>
    /// One selectable color theme. Semantic colors (placement scale, MMR deltas,
    /// live-red) are intentionally shared across themes — they carry meaning.
    /// </summary>
    public class ThemePalette
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }

        public Color PanelBackground { get; set; }
        public Color PanelBorder { get; set; }
        public Color TextPrimary { get; set; }
        public Color TextSecondary { get; set; }
        public Color TextMuted { get; set; }
        public Color Accent { get; set; }
        public Color ChipBackground { get; set; }
        public Color RowHover { get; set; }
        public Color Divider { get; set; }
        public Color SubtleFill { get; set; }

        /// <summary>Filled chips = solid color + dark text (max contrast); otherwise tinted.</summary>
        public bool FilledChips { get; set; }
    }

    public static class ThemeManager
    {
        /// <summary>Solid dark: the readability-first default preferred by most users.</summary>
        public static readonly ThemePalette Dark = new ThemePalette
        {
            Key = "dark",
            DisplayName = "Dark",
            Description = "solid, maximum readability",
            PanelBackground = FromHex("#F517181B"),
            PanelBorder = FromHex("#26FFFFFF"),
            TextPrimary = FromHex("#FFF4F6F8"),
            TextSecondary = FromHex("#FFAEB4BB"),
            TextMuted = FromHex("#FF7C828A"),
            Accent = FromHex("#FFE6C377"),
            ChipBackground = FromHex("#24FFFFFF"),
            RowHover = FromHex("#16FFFFFF"),
            Divider = FromHex("#16FFFFFF"),
            SubtleFill = FromHex("#10FFFFFF"),
            FilledChips = true,
        };

        public static readonly ThemePalette Glass = new ThemePalette
        {
            Key = "glass",
            DisplayName = "Glass",
            Description = "translucent and airy",
            PanelBackground = FromHex("#C217181B"),
            PanelBorder = FromHex("#24FFFFFF"),
            TextPrimary = FromHex("#FFF4F6F8"),
            TextSecondary = FromHex("#FFAEB4BB"),
            TextMuted = FromHex("#FF7C828A"),
            Accent = FromHex("#FFE6C377"),
            ChipBackground = FromHex("#1CFFFFFF"),
            RowHover = FromHex("#12FFFFFF"),
            Divider = FromHex("#12FFFFFF"),
            SubtleFill = FromHex("#0AFFFFFF"),
            FilledChips = false,
        };

        public static readonly ThemePalette Hearthstone = new ThemePalette
        {
            Key = "hearthstone",
            DisplayName = "Hearthstone",
            Description = "warm, blends with the game",
            PanelBackground = FromHex("#F0191612"),
            PanelBorder = FromHex("#33DCB97A"),
            TextPrimary = FromHex("#FFF2EDE3"),
            TextSecondary = FromHex("#FFA89F90"),
            TextMuted = FromHex("#FF6E675A"),
            Accent = FromHex("#FFE3B65C"),
            ChipBackground = FromHex("#24FFFFFF"),
            RowHover = FromHex("#14FFFFFF"),
            Divider = FromHex("#16FFFFFF"),
            SubtleFill = FromHex("#0DFFFFFF"),
            FilledChips = true,
        };

        public static readonly IReadOnlyList<ThemePalette> All =
            new[] { Dark, Glass, Hearthstone };

        public static ThemePalette Current { get; private set; } = Dark;

        /// <summary>Raised after the current theme changes; UI re-applies its resources.</summary>
        public static event Action ThemeChanged;

        public static void Apply(string key)
        {
            Current = All.FirstOrDefault(t =>
                          string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase))
                      ?? Dark;
            try
            {
                ThemeChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Util.Logger.Error("ThemeChanged handler failed", ex);
            }
        }

        /// <summary>Drops all subscribers (called on plugin unload).</summary>
        public static void Reset() => ThemeChanged = null;

        /// <summary>Writes the palette into a control's resource dictionary (DynamicResource keys).</summary>
        public static void ApplyToResources(System.Windows.ResourceDictionary resources)
        {
            var p = Current;
            resources["PanelBackground"] = Freeze(p.PanelBackground);
            resources["PanelBorder"] = Freeze(p.PanelBorder);
            resources["TextPrimary"] = Freeze(p.TextPrimary);
            resources["TextSecondary"] = Freeze(p.TextSecondary);
            resources["TextMuted"] = Freeze(p.TextMuted);
            resources["Accent"] = Freeze(p.Accent);
            resources["Gold"] = Freeze(p.Accent);
            resources["ChipBackground"] = Freeze(p.ChipBackground);
            resources["RowHover"] = Freeze(p.RowHover);
            resources["DividerBrush"] = Freeze(p.Divider);
            resources["SubtleFill"] = Freeze(p.SubtleFill);
        }

        internal static SolidColorBrush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Color FromHex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    }
}
