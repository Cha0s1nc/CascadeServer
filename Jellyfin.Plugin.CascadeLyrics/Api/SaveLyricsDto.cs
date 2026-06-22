using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.CascadeLyrics.Api;

/// <summary>Request body for saving lyrics.</summary>
public class SaveLyricsDto
{
    /// <summary>
    /// Gets or sets the LRC text.
    /// Karaoke lines use: <c>[mm:ss.xx]&lt;mm:ss.xx&gt;word &lt;mm:ss.xx&gt;word</c>
    /// Synced lines use standard LRC: <c>[mm:ss.xx]line text</c>
    /// </summary>
    [Required]
    public string Lrc { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the lyrics type. "karaoke" (default) saves as .slrc,
    /// "synced" saves as .lrc.
    /// </summary>
    public string Type { get; set; } = "karaoke";
}
