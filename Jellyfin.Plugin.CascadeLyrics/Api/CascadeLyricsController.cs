using System;
using System.IO;
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
/// Storage priority (GET):
///   1. {JellyfinData}/data/cascade-lyrics/{itemId}.slrc  — uploaded via this API
///   2. {audioFile}.slrc sidecar file                     — pre-existing files on disk
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns the data directory, creating it if needed.</summary>
    private string GetDataDir()
    {
        var dataDir = Path.Combine(_appPaths.DataPath, "cascade-lyrics");
        Directory.CreateDirectory(dataDir);
        return dataDir;
    }

    private string SlrcPath(Guid itemId) => Path.Combine(GetDataDir(), $"{itemId:N}.slrc");
    private string LrcPath(Guid itemId)  => Path.Combine(GetDataDir(), $"{itemId:N}.lrc");

    /// <summary>
    /// Returns a (path, type) tuple for a sidecar lyrics file next to the audio file,
    /// or null if none exists. Checks .slrc (karaoke) first, then .lrc (synced).
    /// </summary>
    private (string Path, string Type)? FindSidecar(Guid itemId)
    {
        if (Plugin.Instance?.Configuration.CheckSidecarFiles != true)
            return null;

        var item = _libraryManager.GetItemById(itemId);
        if (item?.Path is null)
            return null;

        var slrc = Path.ChangeExtension(item.Path, ".slrc");
        if (System.IO.File.Exists(slrc)) return (slrc, "karaoke");

        var lrc = Path.ChangeExtension(item.Path, ".lrc");
        if (System.IO.File.Exists(lrc)) return (lrc, "synced");

        return null;
    }

    // ── Endpoints ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets enhanced (karaoke) lyrics for the specified audio item.
    /// Returns <c>{ "lrc": "..." }</c> on success, or 404 if none are stored.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <response code="200">Returns the raw enhanced LRC text.</response>
    /// <response code="404">No karaoke lyrics found for this item.</response>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetLyrics([FromRoute] Guid itemId)
    {
        // 1. Karaoke (.slrc) — highest priority
        var slrcPath = SlrcPath(itemId);
        if (System.IO.File.Exists(slrcPath))
        {
            var lrc = System.IO.File.ReadAllText(slrcPath);
            _logger.LogDebug("Serving karaoke lyrics for {ItemId}", itemId);
            return Ok(new { lrc, type = "karaoke" });
        }

        // 2. Synced (.lrc) — fallback
        var lrcPath = LrcPath(itemId);
        if (System.IO.File.Exists(lrcPath))
        {
            var lrc = System.IO.File.ReadAllText(lrcPath);
            _logger.LogDebug("Serving synced lyrics for {ItemId}", itemId);
            return Ok(new { lrc, type = "synced" });
        }

        // 3. Sidecar next to the audio file (.slrc karaoke or .lrc synced)
        var sidecar = FindSidecar(itemId);
        if (sidecar is not null)
        {
            var lrc = System.IO.File.ReadAllText(sidecar.Value.Path);
            _logger.LogDebug("Serving sidecar {Type} for {ItemId} from {Path}", sidecar.Value.Type, itemId, sidecar.Value.Path);
            return Ok(new { lrc, type = sidecar.Value.Type });
        }

        return NotFound();
    }

    /// <summary>
    /// Saves enhanced (karaoke) lyrics for the specified audio item.
    /// Any authenticated user can save lyrics — they are shared server-wide.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="dto">The LRC content to store.</param>
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

        var isSynced = string.Equals(dto.Type, "synced", StringComparison.OrdinalIgnoreCase);
        var storagePath = isSynced ? LrcPath(itemId) : SlrcPath(itemId);

        System.IO.File.WriteAllText(storagePath, dto.Lrc);

        _logger.LogInformation(
            "Saved {Type} lyrics for item {ItemId} ({Length} chars)",
            isSynced ? "synced" : "karaoke", itemId, dto.Lrc.Length);

        return NoContent();
    }

    /// <summary>
    /// Deletes stored enhanced lyrics for the specified audio item.
    /// Does not affect sidecar files on disk.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <response code="204">Lyrics deleted.</response>
    /// <response code="404">No stored lyrics found for this item.</response>
    [HttpDelete]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DeleteLyrics([FromRoute] Guid itemId)
    {
        var slrc = SlrcPath(itemId);
        var lrc  = LrcPath(itemId);

        if (!System.IO.File.Exists(slrc) && !System.IO.File.Exists(lrc))
            return NotFound();

        if (System.IO.File.Exists(slrc)) System.IO.File.Delete(slrc);
        if (System.IO.File.Exists(lrc))  System.IO.File.Delete(lrc);

        _logger.LogInformation("Deleted cascade lyrics for item {ItemId}", itemId);
        return NoContent();
    }
}
