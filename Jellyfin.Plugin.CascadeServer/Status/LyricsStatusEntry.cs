using System;

namespace Jellyfin.Plugin.CascadeServer.Status;

/// <summary>Cached result of the last lyrics-availability check for one audio item.</summary>
public class LyricsStatusEntry
{
    /// <summary>Gets or sets the track title at the time it was last checked.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the artist at the time it was last checked.</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether Kugou had word-level karaoke lyrics for this track.</summary>
    public bool KugouAvailable { get; set; }

    /// <summary>Gets or sets a value indicating whether word-level karaoke is stored (.slrc sidecar or the data-dir cache).</summary>
    public bool HasKaraoke { get; set; }

    /// <summary>Gets or sets a value indicating whether line-level synced lyrics are stored (.lrc sidecar or the cache).</summary>
    public bool HasSynced { get; set; }

    /// <summary>Gets or sets a value indicating whether plain, untimed lyrics are stored (.txt sidecar or the cache).</summary>
    public bool HasPlain { get; set; }

    /// <summary>Gets or sets a value indicating whether any of that came from the data-dir cache rather than sidecars.</summary>
    public bool Cached { get; set; }

    /// <summary>Gets or sets when this entry was last checked or observed.</summary>
    public DateTime LastChecked { get; set; }
}
