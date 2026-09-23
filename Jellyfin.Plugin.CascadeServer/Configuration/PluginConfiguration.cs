using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CascadeServer.Configuration;

/// <summary>
/// Plugin settings, edited from the form on Web/status.html. Jellyfin's plugin
/// configuration API is admin-only, so the SpicyLyrics key is never exposed to
/// normal users.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether fetched lyrics are kept out of the
    /// media folders. When on, the scheduled task and Recheck store what they fetch
    /// in a cache under Jellyfin's data folder instead of writing .slrc/.lrc/.txt next
    /// to the audio, and the lyrics GET fetches live on a miss. Lyrics saved from
    /// Cascade's editor are user-authored and still go next to the media.
    /// </summary>
    public bool DisableSidecarWrites { get; set; }

    /// <summary>
    /// Gets or sets the SpicyLyrics API secret key (sl_sk_...). Empty means
    /// SpicyLyrics is off.
    /// </summary>
    public string SpicyLyricsSecretKey { get; set; } = string.Empty;
}
