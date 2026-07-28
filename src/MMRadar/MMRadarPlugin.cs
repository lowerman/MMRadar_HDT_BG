using System;
using System.Windows.Controls;
using MMRadar.Engine;
using MMRadar.Util;
using Hearthstone_Deck_Tracker.Plugins;

namespace MMRadar
{
    public class MMRadarPlugin : IPlugin
    {
        private PluginCore _core;
        private MenuItem _menuItem;
        private DateTime _lastTick = DateTime.MinValue;

        public string Name => "MMRadar";

        public string Description =>
            "Battlegrounds lobby overlay: opponents' leaderboard ratings and average placement " +
            "powered by wallii.gg. Click any player to see their recent games.";

        public string ButtonText => "Settings";

        public string Author => "lowerman";

        public Version Version => new Version(1, 0, 10);

        public MenuItem MenuItem => _menuItem;

        public void OnLoad()
        {
            try
            {
                _core = new PluginCore();
                _core.Load();
                BuildMenu();
            }
            catch (Exception ex)
            {
                Logger.Error("OnLoad failed", ex);
                try { _core?.Unload(); } catch { /* best-effort cleanup */ }
                _core = null; // never tick a half-initialized core
            }
        }

        public void OnUnload()
        {
            try
            {
                _core?.Unload();
                _core = null;
            }
            catch (Exception ex)
            {
                Logger.Error("OnUnload failed", ex);
            }
        }

        public void OnButtonPress() => _core?.OpenSettings();

        public void OnUpdate()
        {
            if (_core == null)
                return;
            if ((DateTime.UtcNow - _lastTick).TotalMilliseconds < 500)
                return;
            _lastTick = DateTime.UtcNow;
            _core.Tick();
        }

        private void BuildMenu()
        {
            // Plain on/off checkbox like other plugins. Preview and reset live in the
            // settings dialog (Options -> Tracker -> Plugins -> Settings).
            _menuItem = new MenuItem
            {
                Header = "MMRadar",
                IsCheckable = true,
                IsChecked = _core.OverlayEnabled,
                ToolTip = "Enable or disable the Battlegrounds overlay",
            };
            _menuItem.Click += (s, e) =>
            {
                if (_core != null)
                    _core.OverlayEnabled = _menuItem.IsChecked;
            };
        }
    }
}
