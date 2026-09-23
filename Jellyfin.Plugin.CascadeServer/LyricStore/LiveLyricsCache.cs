using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.CascadeServer.LyricStore;

/// <summary>What the Kugou and LRCLIB fetch found for one item, and when.</summary>
public record CachedLyrics(string? Karaoke, string? Synced, string? Plain, DateTime FetchedAt)
{
    /// <summary>Gets a value indicating whether any source had anything.</summary>
    [JsonIgnore]
    public bool Any => Karaoke is not null || Synced is not null || Plain is not null;
}

/// <summary>
/// Where fetched lyrics go when sidecar writes are disabled: one JSON file per item under
/// {DataPath}/cascade-lyrics/live-cache, never next to the media.
///
/// Entries expire so a source that gains lyrics later is picked up: hits after
/// <see cref="HitTtl"/>, misses after <see cref="MissTtl"/>. Caching misses is what stops
/// every play of a track with no lyrics from querying Kugou and LRCLIB again.
/// </summary>
public class LiveLyricsCache
{
    /// <summary>How long a result with lyrics is kept.</summary>
    public static readonly TimeSpan HitTtl = TimeSpan.FromDays(30);

    /// <summary>How long a result with no lyrics at all is kept.</summary>
    public static readonly TimeSpan MissTtl = TimeSpan.FromDays(1);

    private readonly string _dir;

    /// <summary>Initialises a new instance of <see cref="LiveLyricsCache"/>.</summary>
    public LiveLyricsCache(IApplicationPaths appPaths)
    {
        _dir = Path.Combine(DataDir.Root(appPaths), "live-cache");
    }

    /// <summary>Returns the cached entry, or <c>null</c> when there is none or it has expired.</summary>
    public CachedLyrics? Get(Guid itemId)
    {
        var path = PathFor(itemId);
        CachedLyrics? entry;
        try
        {
            if (!File.Exists(path)) return null;
            entry = JsonSerializer.Deserialize<CachedLyrics>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A half-written file from a concurrent Put reads as a miss; the next Put fixes it.
            return null;
        }

        if (entry is null) return null;
        if (DateTime.UtcNow - entry.FetchedAt < (entry.Any ? HitTtl : MissTtl)) return entry;

        TryDelete(path);
        return null;
    }

    /// <summary>Stores an entry, replacing any existing one.</summary>
    public void Put(Guid itemId, CachedLyrics entry)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathFor(itemId), JsonSerializer.Serialize(entry));
    }

    private string PathFor(Guid itemId) => Path.Combine(_dir, $"{itemId:N}.json");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
