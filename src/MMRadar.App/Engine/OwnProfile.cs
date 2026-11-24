using MMRadar.Wallii;

namespace MMRadar.Standalone
{
    /// <summary>The user's own stats for the app window's Home tab.</summary>
    public sealed class OwnProfile
    {
        /// <summary>Always set: wallii row when tracked, otherwise rating-only.</summary>
        public PlayerSummary Summary { get; set; }

        /// <summary>Recent games etc. — only for wallii-tracked players.</summary>
        public PlayerDetails Details { get; set; }

        /// <summary>wallii.gg could not be reached (the official board may still answer).</summary>
        public bool WalliiDown { get; set; }
    }
}
