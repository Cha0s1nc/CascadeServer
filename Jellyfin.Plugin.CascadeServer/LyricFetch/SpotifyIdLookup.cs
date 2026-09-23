using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.CascadeServer.LyricFetch;

/// <summary>
/// Seam for resolving a library item to a Spotify track id, which is the only key
/// SpicyLyrics accepts.
/// </summary>
public static class SpotifyIdLookup
{
    /// <summary>
    /// Returns the item's Spotify track id, or <c>null</c> when unknown.
    /// </summary>
    // ponytail: no resolver yet, so this always returns null and the SpicyLyrics slot is
    // always skipped. Planned: MusicBrainz recording id -> Spotify id, cached (see the
    // prototype script in the desktop repo). Replace this body, not its callers.
    public static string? Find(BaseItem item) => null;
}
