using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CascadeLyrics.ScheduledTasks;

/// <summary>
/// Scheduled task that iterates all audio items in the library and downloads
/// karaoke lyrics from SyncLRC (.slrc) and synced lyrics from LRCLIB (.lrc).
/// Existing files are kept unless a better format is found.
/// </summary>
public class DownloadLyricsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<DownloadLyricsTask> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initialises a new instance of <see cref="DownloadLyricsTask"/>.</summary>
    public DownloadLyricsTask(
        ILibraryManager libraryManager,
        IApplicationPaths appPaths,
        ILogger<DownloadLyricsTask> logger,
        IHttpClientFactory httpClientFactory)
    {
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc/>
    public string Name => "Download Cascade Lyrics";

    /// <inheritdoc/>
    public string Key => "CascadeLyricsDownload";

    /// <inheritdoc/>
    public string Description =>
        "Downloads karaoke lyrics from SyncLRC and synced lyrics from LRCLIB for all audio items. " +
        "Stores karaoke as .slrc and synced as .lrc. Skips items that already have karaoke stored.";

    /// <inheritdoc/>
    public string Category => "Cascade Lyrics";

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Weekly on Sunday at 3am
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        };
    }

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true,
        };

        var items = _libraryManager.GetItemList(query)
            .OfType<Audio>()
            .ToList();

        var total = items.Count;
        if (total == 0)
        {
            progress.Report(100);
            return;
        }

        var dataDir = Path.Combine(_appPaths.DataPath, "cascade-lyrics");
        Directory.CreateDirectory(dataDir);

        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Cascade/1.0");
        httpClient.Timeout = TimeSpan.FromSeconds(15);

        var completed = 0;
        var savedKaraoke = 0;
        var savedSynced = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var (k, s) = await ProcessItemAsync(item, dataDir, httpClient, cancellationToken);
                if (k) savedKaraoke++;
                if (s) savedSynced++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to process lyrics for {Title}", item.Name);
            }

            completed++;
            progress.Report(100.0 * completed / total);

            // Be polite to the APIs
            await Task.Delay(300, cancellationToken);
        }

        _logger.LogInformation(
            "Cascade lyrics download complete. Saved {Karaoke} karaoke, {Synced} synced out of {Total} tracks.",
            savedKaraoke, savedSynced, total);
    }

    private async Task<(bool savedKaraoke, bool savedSynced)> ProcessItemAsync(
        Audio item, string dataDir, HttpClient httpClient, CancellationToken ct)
    {
        var idStr = item.Id.ToString("N");
        var slrcPath = Path.Combine(dataDir, $"{idStr}.slrc");
        var lrcPath  = Path.Combine(dataDir, $"{idStr}.lrc");

        var title    = item.Name ?? string.Empty;
        var artist   = item.AlbumArtists?.FirstOrDefault()
                    ?? item.Artists?.FirstOrDefault()
                    ?? string.Empty;
        var album    = item.Album ?? string.Empty;
        var duration = item.RunTimeTicks.HasValue
            ? (int)(item.RunTimeTicks.Value / TimeSpan.TicksPerSecond)
            : 0;

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            return (false, false);

        var savedKaraoke = false;
        var savedSynced  = false;

        // ── 1. Try SyncLRC for karaoke (.slrc) ───────────────────────────────
        if (!System.IO.File.Exists(slrcPath))
        {
            var karaoke = await FetchSyncLrcAsync(httpClient, title, artist, album, duration, ct);
            if (karaoke is not null)
            {
                await System.IO.File.WriteAllTextAsync(slrcPath, karaoke, ct);
                _logger.LogDebug("Saved karaoke for \"{Title}\" by {Artist}", title, artist);
                savedKaraoke = true;
            }
        }

        // ── 2. Try LRCLIB for synced (.lrc) — only if no karaoke stored ──────
        if (!System.IO.File.Exists(slrcPath) && !System.IO.File.Exists(lrcPath))
        {
            var synced = await FetchLrclibAsync(httpClient, title, artist, album, duration, ct);
            if (synced is not null)
            {
                await System.IO.File.WriteAllTextAsync(lrcPath, synced, ct);
                _logger.LogDebug("Saved synced lyrics for \"{Title}\" by {Artist}", title, artist);
                savedSynced = true;
            }
        }

        return (savedKaraoke, savedSynced);
    }

    private async Task<string?> FetchSyncLrcAsync(
        HttpClient client, string title, string artist, string album, int duration, CancellationToken ct)
    {
        try
        {
            var url = "https://api.synclrc.dev/lyrics"
                    + $"?track={Uri.EscapeDataString(title)}"
                    + $"&artist={Uri.EscapeDataString(artist)}"
                    + "&type=karaoke"
                    + (album.Length > 0 ? $"&album={Uri.EscapeDataString(album)}" : string.Empty)
                    + (duration > 0    ? $"&duration={duration}"                  : string.Empty);

            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("karaoke", out var el))
            {
                var val = el.GetString();
                return string.IsNullOrWhiteSpace(val) ? null : val;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "SyncLRC fetch failed for \"{Title}\"", title);
        }

        return null;
    }

    private async Task<string?> FetchLrclibAsync(
        HttpClient client, string title, string artist, string album, int duration, CancellationToken ct)
    {
        try
        {
            var url = "https://lrclib.net/api/get"
                    + $"?track_name={Uri.EscapeDataString(title)}"
                    + $"&artist_name={Uri.EscapeDataString(artist)}"
                    + (album.Length > 0 ? $"&album_name={Uri.EscapeDataString(album)}" : string.Empty)
                    + (duration > 0    ? $"&duration={duration}"                       : string.Empty);

            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("syncedLyrics", out var el))
            {
                var val = el.GetString();
                return string.IsNullOrWhiteSpace(val) ? null : val;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LRCLIB fetch failed for \"{Title}\"", title);
        }

        return null;
    }
}
