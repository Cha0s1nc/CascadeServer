using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.CascadeLyrics.Configuration;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.CascadeLyrics.Services;

/// <summary>
/// Persists the per-item Kugou/sidecar availability report to a single JSON file in the
/// plugin's data directory, so the status dashboard page can read the results of the last
/// scan without re-querying Kugou for every track on every page load.
/// </summary>
public class LyricsStatusStore
{
    private readonly string _path;

    /// <summary>Initialises a new instance of <see cref="LyricsStatusStore"/>.</summary>
    public LyricsStatusStore(IApplicationPaths appPaths)
    {
        var dir = Path.Combine(appPaths.DataPath, "cascade-lyrics");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "kugou-status.json");
    }

    /// <summary>Loads the last saved report, or an empty one if none exists yet.</summary>
    public Dictionary<Guid, LyricsStatusEntry> Load()
    {
        if (!File.Exists(_path)) return new Dictionary<Guid, LyricsStatusEntry>();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<Guid, LyricsStatusEntry>>(json)
                   ?? new Dictionary<Guid, LyricsStatusEntry>();
        }
        catch
        {
            return new Dictionary<Guid, LyricsStatusEntry>();
        }
    }

    /// <summary>Overwrites the report with the given data.</summary>
    public void Save(Dictionary<Guid, LyricsStatusEntry> status)
        => File.WriteAllText(_path, JsonSerializer.Serialize(status));
}
