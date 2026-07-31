using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CascadeLyrics.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CascadeLyrics.ScheduledTasks;

/// <summary>
/// Scheduled task that iterates all audio items in the library and downloads
/// lyrics into sidecar files next to the audio file. Every source is queried for
/// every track — see <see cref="LyricsFetcher"/> for the sidecar types and the
/// no-overwrite rule.
///
/// Every item processed gets an entry recorded in the Kugou-availability report
/// via <see cref="LyricsStatusStore"/>, which backs the "Cascade Lyrics"
/// dashboard tab.
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
        "Downloads karaoke lyrics from Kugou (.slrc) and synced (.lrc) or plain (.txt) lyrics " +
        "from LRCLIB as sidecar files next to each audio file. Both sources are checked for " +
        "every track. Existing sidecars are never overwritten.";

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
            .Where(a => a.Path is not null)
            .ToList();

        var total = items.Count;
        if (total == 0)
        {
            progress.Report(100);
            return;
        }

        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Cascade/1.0)");
        httpClient.Timeout = TimeSpan.FromSeconds(15);

        var fetcher     = new LyricsFetcher(_logger);
        var statusStore = new LyricsStatusStore(_appPaths);
        var status      = statusStore.Load();

        var completed    = 0;
        var savedKaraoke = 0;
        var savedSynced  = 0;
        var savedPlain   = 0;

        try
        {
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var queriedNetwork = false;

                try
                {
                    var result = await fetcher.EnsureAsync(item, httpClient, cancellationToken);
                    status[item.Id] = result.Entry;
                    queriedNetwork = result.QueriedNetwork;
                    if (result.WroteKaraoke) savedKaraoke++;
                    if (result.WroteSynced) savedSynced++;
                    if (result.WrotePlain) savedPlain++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to process lyrics for {Title}", item.Name);
                    queriedNetwork = true;
                }

                completed++;
                progress.Report(100.0 * completed / total);

                // Flush progress periodically so the dashboard tab can show live results on
                // large libraries instead of an empty table until the whole scan finishes.
                if (completed % 25 == 0) statusStore.Save(status);

                // Be polite to the APIs — but only when we actually called one. Tracks whose
                // sidecars are all present touch no network, so a repeat run over a populated
                // library doesn't spend hours sleeping between no-ops.
                if (queriedNetwork) await Task.Delay(300, cancellationToken);
            }
        }
        finally
        {
            // Save whatever was gathered even if the task was cancelled partway through.
            statusStore.Save(status);
        }

        _logger.LogInformation(
            "Cascade lyrics download complete. Saved {Karaoke} karaoke, {Synced} synced, " +
            "{Plain} plain across {Total} tracks.",
            savedKaraoke, savedSynced, savedPlain, total);
    }
}
