using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.CascadeServer.LyricStore;

/// <summary>
/// Which Spotify track ids a library item maps to, in {DataPath}/cascade-lyrics/spotify-ids.json.
/// Only ids, never lyrics, so unlike the SpicyLyrics cache this is kept indefinitely.
/// <c>Manual</c> marks an id a user linked by hand: lookups never overwrite it.
/// An entry with no ids is a miss, retried after <see cref="MissRetry"/>.
/// </summary>
public static class SpotifyIdStore
{
    /// <summary>One item's mapping.</summary>
    public record Entry(string[] Ids, bool Manual, DateTime CheckedUtc);

    /// <summary>How long a lookup that found nothing is trusted before asking again.</summary>
    public static readonly TimeSpan MissRetry = TimeSpan.FromDays(7);

    private static readonly object FileLock = new();
    private static ConcurrentDictionary<Guid, Entry>? _entries;
    private static string? _path;

    /// <summary>The item's entry, or <c>null</c> when it was never looked up.</summary>
    public static Entry? Get(IApplicationPaths appPaths, Guid itemId)
        => Load(appPaths).TryGetValue(itemId, out var e) ? e : null;

    /// <summary>Stores the item's entry and writes the file.</summary>
    public static void Set(IApplicationPaths appPaths, Guid itemId, Entry entry)
    {
        Load(appPaths)[itemId] = entry;
        Save();
    }

    /// <summary>Forgets the item, so the next lookup starts over.</summary>
    public static void Remove(IApplicationPaths appPaths, Guid itemId)
    {
        if (Load(appPaths).TryRemove(itemId, out _)) Save();
    }

    private static ConcurrentDictionary<Guid, Entry> Load(IApplicationPaths appPaths)
    {
        if (_entries is not null) return _entries;
        lock (FileLock)
        {
            if (_entries is not null) return _entries;
            _path = Path.Combine(DataDir.Root(appPaths), "spotify-ids.json");
            var loaded = new ConcurrentDictionary<Guid, Entry>();
            try
            {
                if (File.Exists(_path))
                {
                    var raw = JsonSerializer.Deserialize<Dictionary<Guid, Entry>>(File.ReadAllText(_path));
                    foreach (var (k, v) in raw ?? []) if (v?.Ids is not null) loaded[k] = v;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Unreadable: start empty. Worst case every song is looked up again.
            }

            _entries = loaded;
            return _entries;
        }
    }

    private static void Save()
    {
        lock (FileLock)
        {
            if (_entries is null || _path is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Dictionary<Guid, Entry>(_entries)));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
