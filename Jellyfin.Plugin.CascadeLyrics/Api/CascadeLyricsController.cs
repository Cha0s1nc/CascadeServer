using System;
using System.Net.Mime;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CascadeLyrics.Api;

/// <summary>
/// Provides GET / POST / DELETE endpoints for enhanced karaoke lyrics.
///
/// Lyrics are stored as sidecar files next to the audio file:
///   {audioFile}.slrc  — karaoke (word-level Enhanced LRC)
///   {audioFile}.lrc   — synced (line-level LRC)
///
/// Legacy data-dir files ({JellyfinData}/data/cascade-lyrics/{itemId}.*) are
/// read as a fallback so existing installs keep working, but new writes always
/// go to the sidecar location.
///
/// Any authenticated Jellyfin user can read and write lyrics.
/// </summary>
[ApiController]
[Route("Audio/{itemId}/CascadeLyrics")]
[Produces(MediaTypeNames.Application.Json)]
public class CascadeLyricsController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<CascadeLyricsController> _logger;

    /// <summary>Initialises a new instance of <see cref="CascadeLyricsController"/>.</summary>
    public CascadeLyricsController(
        ILibraryManager libraryManager,
        IApplicationPaths appPaths,
        ILogger<CascadeLyricsController> logger)
    {
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _logger = logger;
    }

    // ── Legacy data-dir helpers (read-only fallback) ──────────────────────────

    private string LegacySlrcPath(Guid itemId)
        => Path.Combine(_appPaths.DataPath, "cascade-lyrics", $"{itemId:N}.slrc");

    private string LegacyLrcPath(Guid itemId)
        => Path.Combine(_appPaths.DataPath, "cascade-lyrics", $"{itemId:N}.lrc");

    // ── Endpoints ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets lyrics for the specified audio item.
    ///
    /// Priority:
    ///   1. {audioFile}.slrc sidecar  — karaoke (word-level)
    ///   2. {audioFile}.lrc  sidecar  — synced  (line-level)
    ///   3. Legacy data-dir .slrc     — karaoke (backward compat)
    ///   4. Legacy data-dir .lrc      — synced  (backward compat)
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <response code="200">Returns <c>{ "lrc": "...", "type": "karaoke"|"synced" }</c>.</response>
    /// <response code="404">No lyrics found for this item.</response>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetLyrics([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);

        if (item?.Path is not null)
        {
            // 1. Sidecar .slrc — karaoke (word-level, highest priority)
            var slrcSidecar = Path.ChangeExtension(item.Path, ".slrc");
            if (System.IO.File.Exists(slrcSidecar))
            {
                var lrc = System.IO.File.ReadAllText(slrcSidecar);
                _logger.LogDebug("Serving sidecar karaoke for {ItemId} from {Path}", itemId, slrcSidecar);
                return Ok(new { lrc, type = "karaoke" });
            }

            // 2. Sidecar .lrc — synced (line-level)
            var lrcSidecar = Path.ChangeExtension(item.Path, ".lrc");
            if (System.IO.File.Exists(lrcSidecar))
            {
                var lrc = System.IO.File.ReadAllText(lrcSidecar);
                _logger.LogDebug("Serving sidecar synced for {ItemId} from {Path}", itemId, lrcSidecar);
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

        return NotFound();
    }

    /// <summary>
    /// Saves lyrics for the specified audio item as a sidecar file.
    /// Any authenticated user can save lyrics — they are shared server-wide.
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
