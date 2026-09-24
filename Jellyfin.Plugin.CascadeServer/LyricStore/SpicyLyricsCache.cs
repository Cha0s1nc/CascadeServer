using System;
using System.Collections.Concurrent;
using System.IO;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.CascadeServer.LyricStore;

/// <summary>
/// Short-lived cache of raw SpicyLyrics responses, keyed by Spotify track id, under
/// {DataPath}/cascade-lyrics/spicy-cache.
///
/// SpicyLyrics' terms require lyrics to be refetched or discarded within 30 days, so this
/// is never permanent and never a sidecar: entries older than <see cref="Ttl"/> are deleted
/// when read and swept by <see cref="Prune"/>. The body is kept exactly as the API sent it
/// because the shape of Body.Content is not documented yet.
/// </summary>
public class SpicyLyricsCache
{
    /// <summary>How long a response is kept. Under the 30-day limit with room to spare.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(25);

    /// <summary>How long a miss suppresses another request for the same track.</summary>
    public static readonly TimeSpan MissTtl = TimeSpan.FromDays(1);

    // ponytail: misses live in memory only and reset on restart. Fine for a rate-limit
    // guard; move them to disk if restarts turn out to hammer the API.
    private static readonly ConcurrentDictionary<string, DateTime> Misses = new();

    private readonly string _dir;

    /// <summary>Initialises a new instance of <see cref="SpicyLyricsCache"/>.</summary>
    public SpicyLyricsCache(IApplicationPaths appPaths)
    {
        _dir = Path.Combine(DataDir.Root(appPaths), "spicy-cache");
    }

    /// <summary>Returns the cached raw response, or <c>null</c> when missing or expired.</summary>
    public string? Get(string spotifyId)
    {
        var path = PathFor(spotifyId);
        try
        {
            if (!File.Exists(path)) return null;
            if (IsExpired(path))
            {
                File.Delete(path);
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Stores a raw response and sweeps out anything expired.</summary>
    public void Put(string spotifyId, string rawJson)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathFor(spotifyId), rawJson);
        Misses.TryRemove(spotifyId, out _);
        Prune();
    }

    /// <summary>Records that SpicyLyrics had nothing for this track.</summary>
    public static void MarkMiss(string spotifyId) => Misses[spotifyId] = DateTime.UtcNow;

    /// <summary>Forgets a miss, so a track a user just linked is asked for right away.</summary>
    public static void ClearMiss(string spotifyId) => Misses.TryRemove(spotifyId, out _);

    /// <summary>Gets a value indicating whether this track missed recently.</summary>
    public static bool IsRecentMiss(string spotifyId)
        => Misses.TryGetValue(spotifyId, out var at) && DateTime.UtcNow - at < MissTtl;

    /// <summary>Deletes every expired entry.</summary>
    public void Prune()
    {
        // ponytail: full directory scan on every Put. Fine for thousands of files; switch to
        // a periodic sweep only if the cache grows far past that.
        if (!Directory.Exists(_dir)) return;
        foreach (var path in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                if (IsExpired(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsExpired(string path) => DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= Ttl;

    // Callers only pass ids that passed SpicyLyricsClient.IsValidTrackId, so this cannot
    // escape the folder.
    private string PathFor(string spotifyId) => Path.Combine(_dir, spotifyId + ".json");
}
