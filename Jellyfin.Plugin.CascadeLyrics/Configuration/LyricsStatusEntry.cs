using System;

namespace Jellyfin.Plugin.CascadeLyrics.Configuration;

/// <summary>Cached result of the last lyrics-availability check for one audio item.</summary>
public class LyricsStatusEntry
{
    /// <summary>Gets or sets the track title at the time it was last checked.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the artist at the time it was last checked.</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether Kugou had word-level karaoke lyrics for this track.</summary>
    public bool KugouAvailable { get; set; }

    /// <summary>Gets or sets a value indicating whether a .slrc (word-level karaoke) sidecar exists next to the track.</summary>
    public bool HasKaraoke { get; set; }

    /// <summary>Gets or sets a value indicating whether a .lrc (line-level synced) sidecar exists next to the track.</summary>
    public bool HasSynced { get; set; }

    /// <summary>Gets or sets a value indicating whether a .txt (plain, untimed) sidecar exists next to the track.</summary>
    public bool HasPlain { get; set; }

    /// <summary>Gets or sets when this entry was last checked or observed.</summary>
    public DateTime LastChecked { get; set; }
}
