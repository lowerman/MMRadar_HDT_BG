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

        /// <summary>0 = auto (scale with the game window resolution); >0 = fixed by the user.</summary>
        public double Scale { get; set; } = 0;
        public bool OverlayEnabled { get; set; } = true;
        public bool Collapsed { get; set; }
        public string Theme { get; set; } = "hdt";
        public int RecentGamesCount { get; set; } = 10;

        /// <summary>False = highest rating on top (default); true = lowest on top, like BGRank.</summary>
        public bool SortAscending { get; set; }

        /// <summary>Avg column style: "classic" solid chips, "tint" native tint, "bare" colored digits.</summary>
        public string ChipStyle { get; set; } = "classic";

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
            // Migrations: early builds latched a uselessly tiny auto-value (<=0.55), and
            // later builds saved the fixed default 1.0 — both mean "the user never chose
            // a scale", so return them to auto.
            if (settings.Scale <= 0.55 || settings.Scale == 1.0 || double.IsNaN(settings.Scale))
                settings.Scale = 0;
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
