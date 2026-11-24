using System;
using System.IO;
using System.Xml.Serialization;
using MMRadar.Util;

namespace MMRadar.Standalone
{
    /// <summary>
    /// Settings that exist only in the standalone app (app-settings.xml next to
    /// the shared settings.xml) — kept apart so the plugin's settings class
    /// stays untouched.
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// Where the user dragged the player-details card, as a fraction of the
        /// overlay's size (0..1) — survives resolution and window-size changes.
        /// Null = default placement next to the panel.
        /// </summary>
        public double? PopupLeftFraction { get; set; }
        public double? PopupTopFraction { get; set; }

        /// <summary>False until the welcome window has been shown once.</summary>
        public bool WelcomeShown { get; set; }

        /// <summary>Look for a newer release on GitHub at start and every few hours.</summary>
        public bool AutoCheckUpdates { get; set; } = true;

        /// <summary>The newest version the tray balloon already announced (no nagging).</summary>
        public string LastAnnouncedVersion { get; set; }
        [XmlIgnore]
        public string FilePath { get; private set; }

        public static AppSettings Load(string directory)
        {
            var path = Path.Combine(directory, "app-settings.xml");
            try
            {
                if (File.Exists(path))
                {
                    using (var stream = File.OpenRead(path))
                    {
                        var loaded = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(stream);
                        loaded.FilePath = path;
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to load app settings, using defaults", ex);
            }
            return new AppSettings { FilePath = path };
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                using (var stream = File.Create(FilePath))
                    new XmlSerializer(typeof(AppSettings)).Serialize(stream, this);
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to save app settings", ex);
            }
        }
    }
}
