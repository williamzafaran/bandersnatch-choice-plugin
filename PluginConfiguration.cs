using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BandersnatchChoice
{
    /// <summary>
    /// Plugin configuration model — persisted as XML by Jellyfin.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets a value indicating whether the interactive features are enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets how many seconds before the segment end to show the choice overlay.
        /// </summary>
        public int LeadInSeconds { get; set; } = 10;

        /// <summary>
        /// Gets or sets a value indicating whether auto-detection of the Bandersnatch item is enabled.
        /// Auto-detection checks item title contains "Bandersnatch" AND duration ≈ 5h12m.
        /// </summary>
        public bool AutoDetect { get; set; } = true;

        /// <summary>
        /// Gets or sets an optional manual Jellyfin Item ID override.
        /// When set, auto-detection is bypassed for this specific item.
        /// </summary>
        public string? ManualItemId { get; set; }
    }
}
