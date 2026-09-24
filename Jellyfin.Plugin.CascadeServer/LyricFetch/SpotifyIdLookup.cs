using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CascadeServer.LyricStore;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Audio;

namespace Jellyfin.Plugin.CascadeServer.LyricFetch;

/// <summary>
/// Resolves a library item to Spotify track ids, the only key SpicyLyrics accepts: a
/// hand-linked id first (Cascade's "Link a Spotify track"), else what ListenBrainz found,
/// remembered in <see cref="SpotifyIdStore"/>. When SpicyLyrics ships its own lookup it
/// slots in ahead of ListenBrainz here; the callers stay as they are.
/// </summary>
public static class SpotifyIdLookup
{
    /// <summary>The item's candidate ids, most likely first, and whether a user set them.</summary>
    public static async Task<(IReadOnlyList<string> Ids, bool Manual)> FindAsync(
        Audio item, HttpClient client, IApplicationPaths appPaths, CancellationToken ct)
    {
        var entry = SpotifyIdStore.Get(appPaths, item.Id);
        if (entry is { Manual: true }) return (entry.Ids, true);
        if (entry is { Ids.Length: > 0 }) return (entry.Ids, false);
        if (entry is not null && DateTime.UtcNow - entry.CheckedUtc < SpotifyIdStore.MissRetry) return ([], false);

        var artist = item.Artists?.FirstOrDefault() ?? item.AlbumArtists?.FirstOrDefault() ?? string.Empty;
        var ids = await ListenBrainzClient.FindAsync(client, artist, item.Album ?? string.Empty, item.Name ?? string.Empty, ct);
        if (ids is null) return ([], false);   // could not ask: try again next time, remember nothing
        SpotifyIdStore.Set(appPaths, item.Id, new([.. ids], false, DateTime.UtcNow));
        return (ids, false);
    }

    /// <summary>Moves the id that worked to the front, so the next play tries it first.</summary>
    public static void Prefer(IApplicationPaths appPaths, Guid itemId, string spotifyId)
    {
        var entry = SpotifyIdStore.Get(appPaths, itemId);
        if (entry is null || entry.Manual || entry.Ids.Length == 0 || entry.Ids[0] == spotifyId) return;
        SpotifyIdStore.Set(appPaths, itemId, entry with { Ids = [spotifyId, .. entry.Ids.Where(i => i != spotifyId)] });
    }
}
