using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CascadeServer.Api;

/// <summary>
/// Tells a client whether this plugin is installed, and what it can do.
///
/// Cascade needs to know so it can disable the features that depend on this
/// plugin rather than offering controls that quietly do nothing. It cannot ask
/// Jellyfin: listing plugins requires elevation, and Cascade signs in as a
/// normal user. The lyrics route cannot answer either, because a server
/// without the plugin and a track without lyrics both come back 404.
///
/// Reaching this route at all is the answer, so a client only needs the
/// status. The body is there for a client that wants to know what this build
/// supports rather than assuming from a version number.
///
/// Any authenticated user can call it. It stays behind auth because there is no
/// reason to tell an unauthenticated scanner what is installed.
/// </summary>
[ApiController]
[Route("CascadeServer/Info")]
[Route("CascadeLyrics/Info")] // Old plugin name. Drop a couple of releases after 2.0.0.0.
[Produces(MediaTypeNames.Application.Json)]
public class InfoController : ControllerBase
{
    /// <summary>
    /// What this plugin build supports, for a client that has to work against
    /// several. A client should treat an unknown entry as something it does not
    /// understand rather than an error, and a missing entry as unsupported.
    /// </summary>
    private static readonly string[] Capabilities =
    {
        "lyrics-read",     // GET  /CascadeServer/Lyrics/{itemId}
        "lyrics-write",    // POST and DELETE on the same route
        "karaoke",         // word-level .slrc, not just line-level .lrc
    };

    // Only while a SpicyLyrics key is set: GET ?syllable=true can then return a raw
    // SpicyLyrics body (Spotify ids resolve through SpotifyIdLookup), and
    // /CascadeServer/SpotifyId/{itemId} links a song to a Spotify track by hand.
    private const string Syllable = "syllable";
    private const string SpotifyLink = "spotify-link";

    /// <summary>
    /// Reports that the plugin is present, with its version and capabilities.
    /// </summary>
    /// <response code="200">
    /// Returns <c>{ "name": "...", "version": "...", "capabilities": [...] }</c>.
    /// Reaching this at all is the answer: a server without the plugin has no
    /// such route and returns 404.
    /// </response>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetInfo()
    {
        return Ok(new
        {
            name = "Cascade Server",
            version = Plugin.Instance?.Version?.ToString() ?? "0.0.0.0",
            capabilities = string.IsNullOrWhiteSpace(Plugin.Config.SpicyLyricsSecretKey)
                ? Capabilities
                : [.. Capabilities, Syllable, SpotifyLink],
        });
    }
}
