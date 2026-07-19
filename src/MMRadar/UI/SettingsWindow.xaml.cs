using System;
using System.Windows;

namespace MMRadar.UI
{
    /// <summary>
    /// Classic dialog host for <see cref="SettingsContent"/>, opened from
    /// Options → Tracker → Plugins → Settings. The in-game path uses
    /// <see cref="SettingsCard"/> on the overlay instead.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        public SettingsWindow(
            Action togglePreview,
            Action resetPosition,
            string currentTheme,
            Action<string> onThemeSelected,
            bool sortAscending,
            Action<bool> onSortChanged)
        {
            InitializeComponent();
            Body.PreviewClicked += () => togglePreview?.Invoke();
            Body.ResetClicked += () => resetPosition?.Invoke();
            Body.ThemeSelected += key => onThemeSelected?.Invoke(key);
            Body.SortChanged += asc => onSortChanged?.Invoke(asc);
            Body.Sync(currentTheme, sortAscending);
        }
    }
}
