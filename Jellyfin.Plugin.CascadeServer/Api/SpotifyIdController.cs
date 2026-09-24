using System;
using Jellyfin.Plugin.CascadeServer.LyricFetch;
using Jellyfin.Plugin.CascadeServer.LyricStore;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CascadeServer.Api;

/// <summary>
/// Links a song to a Spotify track by hand, for when the automatic lookup finds nothing or
/// the wrong release. Server-wide, like saved lyrics: any signed-in user may set it, and it
/// applies to everyone. A hand link is never overwritten by the lookup; DELETE hands the
/// song back to it.
/// </summary>
[ApiController]
[Route("CascadeServer/SpotifyId/{itemId}")]
[Authorize]
public class SpotifyIdController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;

    /// <summary>Initialises a new instance of <see cref="SpotifyIdController"/>.</summary>
    public SpotifyIdController(ILibraryManager libraryManager, IApplicationPaths appPaths)
    {
        _libraryManager = libraryManager;
        _appPaths = appPaths;
    }

    /// <summary>What the song is linked to: <c>{ spotifyId, manual }</c>, id null when nothing.</summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <response code="200">The current mapping.</response>
    /// <response code="404">No such song.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Get([FromRoute] Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is not Audio) return NotFound();
        var entry = SpotifyIdStore.Get(_appPaths, itemId);
        return Ok(new { spotifyId = entry is { Ids.Length: > 0 } ? entry.Ids[0] : null, manual = entry?.Manual ?? false });
    }

    /// <summary>Links the song to a Spotify track id.</summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="dto">The 22-character Spotify track id (the client parses links).</param>
    /// <response code="204">Linked.</response>
    /// <response code="400">Not a Spotify track id.</response>
    /// <response code="404">No such song.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Set([FromRoute] Guid itemId, [FromBody] SpotifyIdDto dto)
    {
        // Trust boundary: the id becomes a file name in the SpicyLyrics cache, so only the
        // exact Spotify id shape gets through.
        if (!SpicyLyricsClient.IsValidTrackId(dto?.SpotifyId)) return BadRequest(new { error = "Not a Spotify track id." });
        if (_libraryManager.GetItemById(itemId) is not Audio) return NotFound();
        SpotifyIdStore.Set(_appPaths, itemId, new([dto!.SpotifyId!], true, DateTime.UtcNow));
        SpicyLyricsCache.ClearMiss(dto.SpotifyId!);
        return NoContent();
    }

    /// <summary>Removes the link; the automatic lookup runs again next time.</summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <response code="204">Removed (or there was nothing to remove).</response>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Clear([FromRoute] Guid itemId)
    {
        SpotifyIdStore.Remove(_appPaths, itemId);
        return NoContent();
    }
}

/// <summary>Body of POST /CascadeServer/SpotifyId/{itemId}.</summary>
public class SpotifyIdDto
{
    /// <summary>Gets or sets the Spotify track id.</summary>
    public string? SpotifyId { get; set; }
}
