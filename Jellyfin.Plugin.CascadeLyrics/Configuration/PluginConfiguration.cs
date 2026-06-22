using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CascadeLyrics.Configuration;

/// <summary>Plugin configuration (extend as needed).</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether to also check for a .slrc sidecar
    /// file next to the audio file when no server-stored lyrics exist.
    /// Useful if you already have .slrc files on your media drives.
    /// </summary>
    public bool CheckSidecarFiles { get; set; } = true;
}
