using System;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CascadeServer.LyricFetch;
using Jellyfin.Plugin.CascadeServer.Status;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CascadeServer.Api;

/// <summary>
/// Reports, per audio item, whether Kugou has karaoke lyrics available and whether a
/// sidecar has already been downloaded. Backs the Cascade Server dashboard page.
/// Admin-only - this walks the whole library and can trigger live network calls.
/// </summary>
[ApiController]
[Route("CascadeServer/Status")]
[Route("CascadeLyrics/Status")] // Old plugin name. Drop a couple of releases after 2.0.0.0.
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class StatusController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StatusController> _logger;

    /// <summary>Initialises a new instance of <see cref="StatusController"/>.</summary>
    public StatusController(
        ILibraryManager libraryManager,
        IApplicationPaths appPaths,
        IHttpClientFactory httpClientFactory,
        ILogger<StatusController> logger)
    {
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the last-known Kugou/sidecar status for every audio item in the library, from the
    /// report the scheduled "Download lyrics" task last wrote. Tracks it hasn't reached
    /// yet are reported as unchecked rather than missing.
    /// </summary>
    /// <response code="200">Returns the status rows.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStatus()
    {
        var status = new LyricsStatusStore(_appPaths).Load();

        var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true,
        };

        var rows = _libraryManager.GetItemList(query)
            .OfType<Audio>()
            .Where(a => a.Path is not null)
            .Select(item =>
            {
                status.TryGetValue(item.Id, out var entry);
                var artist = item.AlbumArtists?.FirstOrDefault() ?? item.Artists?.FirstOrDefault() ?? string.Empty;
                return new
                {
                    itemId = item.Id,
                    name = item.Name,
                    artist,
                    checkedAtAll = entry is not null,
                    kugouAvailable = entry?.KugouAvailable ?? false,
                    hasKaraoke = entry?.HasKaraoke ?? false,
                    hasSynced = entry?.HasSynced ?? false,
                    hasPlain = entry?.HasPlain ?? false,
                    lastChecked = entry?.LastChecked,
                };
            })
            .OrderBy(r => r.artist)
            .ThenBy(r => r.name)
            .ToList();

        return Ok(rows);
    }

    /// <summary>
    /// Runs the same fetch the scheduled task does for a single item - every source is
    /// queried for whichever sidecars are still missing - and updates the cached report.
    /// Existing sidecars are left alone, so this will not re-download a track that already
    /// has everything.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Returns the updated status row.</response>
    /// <response code="404">Item not found or has no file path.</response>
    [HttpPost("{itemId}/Recheck")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Recheck(Guid itemId, CancellationToken ct)
    {
        var item = _libraryManager.GetItemById(itemId) as Audio;
        if (item?.Path is null) return NotFound();

        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Cascade/1.0)");
        httpClient.Timeout = TimeSpan.FromSeconds(15);

        var entry = (await new LyricsFetcher(_logger).EnsureAsync(item, httpClient, ct)).Entry;

        var store = new LyricsStatusStore(_appPaths);
        var status = store.Load();
        status[itemId] = entry;
        store.Save(status);

        return Ok(new
        {
            itemId,
            name = entry.Name,
            artist = entry.Artist,
            checkedAtAll = true,
            kugouAvailable = entry.KugouAvailable,
            hasKaraoke = entry.HasKaraoke,
            hasSynced = entry.HasSynced,
            hasPlain = entry.HasPlain,
            lastChecked = entry.LastChecked,
        });
    }
}
