using System;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CascadeServer.LyricFetch;
using Jellyfin.Plugin.CascadeServer.LyricStore;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CascadeServer.Api;

/// <summary>
/// Provides GET / POST / DELETE endpoints for enhanced karaoke lyrics.
///
/// Lyrics are stored as sidecar files next to the audio file:
///   {audioFile}.slrc  - karaoke (word-level Enhanced LRC)
///   {audioFile}.lrc   - synced (line-level LRC)
///
/// Legacy data-dir files ({JellyfinData}/data/cascade-lyrics/{itemId}.*) are
/// read as a fallback so existing installs keep working, but new writes always
/// go to the sidecar location.
///
/// Any authenticated Jellyfin user can read and write lyrics.
/// </summary>
[ApiController]
[Route("CascadeServer/Lyrics/{itemId}")]
[Route("Audio/{itemId}/CascadeLyrics")] // Old plugin name. Drop a couple of releases after 2.0.0.0.
[Produces(MediaTypeNames.Application.Json)]
public class LyricsController : ControllerBase
{
    // A live fetch is not tied to the request: Cascade gives up after 8s, and cancelling on
    // disconnect would throw away a slow first fetch, cache nothing, and repeat on every play.
    private static readonly TimeSpan LiveFetchBudget = TimeSpan.FromSeconds(45);

    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LyricsController> _logger;

    /// <summary>Initialises a new instance of <see cref="LyricsController"/>.</summary>
    public LyricsController(
        ILibraryManager libraryManager,
        IApplicationPaths appPaths,
        IHttpClientFactory httpClientFactory,
        ILogger<LyricsController> logger)
    {
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ── Legacy data-dir helpers (read-only fallback) ──────────────────────────

    private string LegacySlrcPath(Guid itemId) => DataDir.LyricsPath(_appPaths, itemId, ".slrc");

    private string LegacyLrcPath(Guid itemId) => DataDir.LyricsPath(_appPaths, itemId, ".lrc");

    // ── Endpoints ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets lyrics for the specified audio item.
    ///
    /// Priority:
    ///   0. SpicyLyrics, only when the client passes <c>syllable=true</c>, a key is set and
    ///      the item has a Spotify id - returned raw, never stored as a sidecar
    ///   1. {audioFile}.slrc sidecar  - karaoke (word-level)
    ///   2. {audioFile}.lrc  sidecar  - synced  (line-level)
    ///   3. Legacy data-dir .slrc     - karaoke (backward compat)
    ///   4. Legacy data-dir .lrc      - synced  (backward compat)
    ///   5. With sidecar writes disabled: the data-dir cache, fetching live from Kugou then
    ///      LRCLIB on a miss
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="syllable">
    /// Set by a client that understands the SpicyLyrics response. Without it the slot is
    /// skipped, so a client that only reads <c>lrc</c> never gets a body it cannot use.
    /// </param>
    /// <response code="200">
    /// Returns <c>{ "lrc": "...", "type": "karaoke"|"synced" }</c>, or with
    /// <c>syllable=true</c> possibly <c>{ "type": "syllable", "source": "spicylyrics", "spicy": {...} }</c>
    /// where <c>spicy</c> is the SpicyLyrics response exactly as received.
    /// </response>
    /// <response code="404">No lyrics found for this item.</response>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLyrics([FromRoute] Guid itemId, [FromQuery] bool syllable = false)
    {
        var item = _libraryManager.GetItemById(itemId);

        // 0. SpicyLyrics (opt-in, needs a key and a Spotify id; skipped otherwise)
        if (syllable && item is not null && !string.IsNullOrWhiteSpace(Plugin.Config.SpicyLyricsSecretKey))
        {
            string? raw = null;
            try
            {
                using var cts = new CancellationTokenSource(LiveFetchBudget);
                using var client = LyricsFetcher.CreateHttpClient(_httpClientFactory);
                raw = await new LyricsFetcher(_logger, _appPaths).TrySpicyAsync(item, client, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Out of time: fall through to the other sources.
            }

            if (raw is not null)
            {
                using var doc = JsonDocument.Parse(raw);
                return Ok(new { type = "syllable", source = "spicylyrics", spicy = doc.RootElement.Clone() });
            }
        }

        if (item?.Path is not null)
        {
            var files = Sidecars.For(item.Path);

            // 1. Sidecar .slrc - karaoke (word-level, highest priority)
            if (System.IO.File.Exists(files.Slrc))
            {
                var lrc = System.IO.File.ReadAllText(files.Slrc);
                _logger.LogDebug("Serving sidecar karaoke for {ItemId} from {Path}", itemId, files.Slrc);
                return Ok(new { lrc, type = "karaoke" });
            }

            // 2. Sidecar .lrc - synced (line-level)
            if (System.IO.File.Exists(files.Lrc))
            {
                var lrc = System.IO.File.ReadAllText(files.Lrc);
                _logger.LogDebug("Serving sidecar synced for {ItemId} from {Path}", itemId, files.Lrc);
                return Ok(new { lrc, type = "synced" });
            }
        }

        // 3. Legacy data-dir .slrc
        var legacySlrc = LegacySlrcPath(itemId);
        if (System.IO.File.Exists(legacySlrc))
        {
            var lrc = System.IO.File.ReadAllText(legacySlrc);
            _logger.LogDebug("Serving legacy karaoke for {ItemId}", itemId);
            return Ok(new { lrc, type = "karaoke" });
        }

        // 4. Legacy data-dir .lrc
        var legacyLrc = LegacyLrcPath(itemId);
        if (System.IO.File.Exists(legacyLrc))
        {
            var lrc = System.IO.File.ReadAllText(legacyLrc);
            _logger.LogDebug("Serving legacy synced for {ItemId}", itemId);
            return Ok(new { lrc, type = "synced" });
        }

        // 5. No files, and sidecar writes are off: serve from the data-dir cache, fetching live.
        if (Plugin.Config.DisableSidecarWrites && item is Audio { Path: not null } audio)
        {
            CachedLyrics cached;
            try
            {
                using var cts = new CancellationTokenSource(LiveFetchBudget);
                using var client = LyricsFetcher.CreateHttpClient(_httpClientFactory);
                (cached, _) = await new LyricsFetcher(_logger, _appPaths)
                    .GetOrFetchCachedAsync(audio, client, force: false, cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Live lyrics fetch for {ItemId} ran out of time", itemId);
                return NotFound();
            }

            if (cached.Karaoke is not null) return Ok(new { lrc = cached.Karaoke, type = "karaoke" });
            if (cached.Synced is not null) return Ok(new { lrc = cached.Synced, type = "synced" });
        }

        return NotFound();
    }

    /// <summary>
    /// Saves lyrics for the specified audio item as a sidecar file.
    /// Any authenticated user can save lyrics - they are shared server-wide.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="dto">The LRC content and type to store.</param>
    /// <response code="204">Lyrics saved successfully.</response>
    /// <response code="400">Missing or empty lyrics content.</response>
    [HttpPost]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult SaveLyrics([FromRoute] Guid itemId, [FromBody] SaveLyricsDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto?.Lrc))
            return BadRequest(new { error = "Lyrics content (lrc) is required." });

        var item = _libraryManager.GetItemById(itemId);
        if (item?.Path is null)
            return NotFound(new { error = "Item not found or has no file path." });

        var isSynced = string.Equals(dto.Type, "synced", StringComparison.OrdinalIgnoreCase);
        var ext = isSynced ? ".lrc" : ".slrc";
        var sidecarPath = Path.ChangeExtension(item.Path, ext);
        var savedPath = sidecarPath;

        try
        {
            System.IO.File.WriteAllText(sidecarPath, dto.Lrc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Media library is mounted read-only in some deployments (e.g. Jellyfin
            // running in a container with the library bind-mounted ro). Fall back to
            // the legacy data-dir location, which the GET endpoint already checks.
            _logger.LogWarning(
                ex, "Sidecar write failed at {Path}, falling back to legacy data dir", sidecarPath);

            savedPath = isSynced ? LegacyLrcPath(itemId) : LegacySlrcPath(itemId);
            Directory.CreateDirectory(Path.GetDirectoryName(savedPath)!);
            System.IO.File.WriteAllText(savedPath, dto.Lrc);
        }

        _logger.LogInformation(
            "Saved {Type} lyrics for {ItemId} at {Path} ({Length} chars)",
            isSynced ? "synced" : "karaoke", itemId, savedPath, dto.Lrc.Length);

        return NoContent();
    }

    /// <summary>
    /// Deletes sidecar lyrics for the specified audio item.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <response code="204">Lyrics deleted.</response>
    /// <response code="404">No lyrics found for this item.</response>
    [HttpDelete]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DeleteLyrics([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item?.Path is null)
            return NotFound(new { error = "Item not found or has no file path." });

        var slrc = Path.ChangeExtension(item.Path, ".slrc");
        var lrc  = Path.ChangeExtension(item.Path, ".lrc");

        if (!System.IO.File.Exists(slrc) && !System.IO.File.Exists(lrc))
            return NotFound();

        if (System.IO.File.Exists(slrc)) System.IO.File.Delete(slrc);
        if (System.IO.File.Exists(lrc))  System.IO.File.Delete(lrc);

        _logger.LogInformation("Deleted sidecar lyrics for {ItemId}", itemId);
        return NoContent();
    }
}
