using System;
using System.IO;
using System.Xml.Serialization;
using MMRadar.Util;

namespace MMRadar.Config
{
    public class PluginSettings
    {
        public double PanelLeft { get; set; } = 40;
        public double PanelTop { get; set; } = 130;
        public double Scale { get; set; } = 1.0;
        public bool OverlayEnabled { get; set; } = true;
        public bool Collapsed { get; set; }
        public string Theme { get; set; } = "dark";
        public int RecentGamesCount { get; set; } = 10;

        /// <summary>Optional overrides in case the wallii Supabase URL or anon key rotates.</summary>
        public string WalliiBaseUrl { get; set; }
        public string WalliiAnonKey { get; set; }

        /// <summary>Optional override for the full-leaderboard mirror (bgrank.fly.dev).</summary>
        public string OfficialBoardUrl { get; set; }

        [XmlIgnore]
        public string Directory { get; private set; }

        private string FilePath => Path.Combine(Directory, "settings.xml");

        public static PluginSettings Load(string directory)
        {
            var settings = new PluginSettings { Directory = directory };
            try
            {
                var path = Path.Combine(directory, "settings.xml");
                // One-time migration from the plugin's pre-release name.
                if (!File.Exists(path))
                {
                    var legacy = Path.Combine(
                        Path.GetDirectoryName(directory) ?? directory, "BGRankAdvanced", "settings.xml");
                    if (File.Exists(legacy))
                    {
                        System.IO.Directory.CreateDirectory(directory);
                        File.Copy(legacy, path);
                    }
                }
                if (File.Exists(path))
                {
                    using (var stream = File.OpenRead(path))
                    {
                        var loaded = (PluginSettings)new XmlSerializer(typeof(PluginSettings)).Deserialize(stream);
                        loaded.Directory = directory;
                        settings = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to load settings, using defaults", ex);
            }
            // Migration: early builds auto-computed the scale before the overlay window
            // was sized and could latch a uselessly tiny value.
            if (settings.Scale <= 0.55 || double.IsNaN(settings.Scale))
                settings.Scale = 1.0;
            return settings;
        }

        public void Save()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                using (var stream = File.Create(FilePath))
                    new XmlSerializer(typeof(PluginSettings)).Serialize(stream, this);
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to save settings", ex);
            }
        }
    }
}
