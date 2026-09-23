using System;
using System.Net.Mime;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CascadeServer.LyricFetch;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CascadeServer.Api;

/// <summary>
/// Admin-only: SpicyLyrics for a Spotify track id given by hand, for testing before the
/// plugin can resolve ids on its own (Cascade desktop's <c>cascadeDebug.spicy()</c>).
///
/// Admin-only because it spends the server's key on any id asked for; open to every user
/// it would be a proxy onto SpicyLyrics, which their terms do not allow. Goes through the
/// same 25-day cache as normal playback, so it stays inside the 30-day rule.
/// </summary>
[ApiController]
[Route("CascadeServer/SpicyLyrics/{spotifyId}")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class SpicyLyricsTestController : ControllerBase
{
    private readonly IApplicationPaths _appPaths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SpicyLyricsTestController> _logger;

    /// <summary>Initialises a new instance of <see cref="SpicyLyricsTestController"/>.</summary>
    public SpicyLyricsTestController(
        IApplicationPaths appPaths, IHttpClientFactory httpClientFactory, ILogger<SpicyLyricsTestController> logger)
    {
        _appPaths = appPaths;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Fetches one track by Spotify id and says exactly why when it cannot.</summary>
    /// <param name="spotifyId">A Spotify track id (22 base62 characters).</param>
    /// <response code="200">The same shape the lyrics route sends with <c>syllable=true</c>.</response>
    /// <response code="400">Not a Spotify track id.</response>
    /// <response code="404">SpicyLyrics had nothing, or refused; <c>upstreamStatus</c> says which.</response>
    /// <response code="409">No SpicyLyrics key is set in the plugin's settings.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Get([FromRoute] string spotifyId)
    {
        if (!SpicyLyricsClient.IsValidTrackId(spotifyId))
        {
            return BadRequest(new { error = "invalid_track_id", message = "A Spotify track id is 22 letters and digits." });
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var client = LyricsFetcher.CreateHttpClient(_httpClientFactory);
        var result = await new LyricsFetcher(_logger, _appPaths)
            .TrySpicyByIdAsync(spotifyId, client, respectRecentMiss: false, cts.Token);

        return result.Status switch
        {
            SpicyLyricsStatus.NotConfigured => Conflict(new { error = "not_configured", message = "Set a SpicyLyrics secret key in Cascade Server's settings." }),
            SpicyLyricsStatus.Hit when result.RawJson is not null => Ok(new
            {
                type = "syllable",
                source = "spicylyrics",
                spicy = JsonDocument.Parse(result.RawJson).RootElement.Clone(),
            }),
            _ => NotFound(new { error = "miss", upstreamStatus = result.HttpStatus, message = "SpicyLyrics had nothing for this id, or refused the request (see upstreamStatus; none means a network error or a Retry-After pause)." }),
        };
    }
}
