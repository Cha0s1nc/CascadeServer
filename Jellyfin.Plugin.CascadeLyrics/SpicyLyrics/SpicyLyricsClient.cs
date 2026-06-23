// ── SpicyLyrics integration (SCAFFOLDED — not wired in yet) ──────────────────
//
// To activate:
//   1. Set your API key in the plugin config (add SpicyLyricsApiKey to PluginConfiguration.cs)
//   2. In DownloadLyricsTask.ProcessItemAsync, call FetchSpicyLyricsAsync() before FetchSyncLrcAsync()
//   3. Save result to SlrcPath(itemId) (it returns enhanced LRC)
//
// To remove: delete this file and the SpicyLyrics folder. Nothing else references it.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CascadeLyrics.SpicyLyrics;

/// <summary>
/// Fetches word-level karaoke lyrics from the SpicyLyrics Dev API.
/// Requires an API key from @spikerko on Discord (https://devapi.spicylyrics.org/).
/// </summary>
public class SpicyLyricsClient
{
    // Base URL — update if the dev API moves
    private const string BaseUrl = "https://devapi.spicylyrics.org";

    private readonly ILogger _logger;

    public SpicyLyricsClient(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Fetches word-level lyrics for a track. Returns enhanced LRC text (.slrc format)
    /// or null if not found / API unavailable.
    /// </summary>
    /// <param name="client">Shared HttpClient (caller owns lifetime).</param>
    /// <param name="apiKey">SpicyLyrics Dev API key.</param>
    /// <param name="title">Track title.</param>
    /// <param name="artist">Primary artist name.</param>
    /// <param name="album">Album name (optional but improves matching).</param>
    /// <param name="durationSeconds">Track duration in seconds (optional but improves matching).</param>
    /// <param name="spotifyToken">
    ///   Optional Spotify OAuth token — some SpicyLyrics endpoints require it for track
    ///   matching. Leave null to skip Spotify-dependent endpoints.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string?> FetchAsync(
        HttpClient client,
        string apiKey,
        string title,
        string artist,
        string album,
        int durationSeconds,
        string? spotifyToken,
        CancellationToken ct)
    {
        // ── NOTE ────────────────────────────────────────────────────────────────
        // The exact endpoint paths and response shape are not yet confirmed.
        // Once you have the API key and can test, update:
        //   1. The URL path below
        //   2. The JSON property names in ParseLrc()
        //   3. Remove the TODO comments
        // ────────────────────────────────────────────────────────────────────────

        try
        {
            // TODO: confirm endpoint path with @spikerko — guessing /lyrics/search for now
            var url = $"{BaseUrl}/lyrics/search"
                    + $"?track={Uri.EscapeDataString(title)}"
                    + $"&artist={Uri.EscapeDataString(artist)}"
                    + (album.Length > 0 ? $"&album={Uri.EscapeDataString(album)}" : string.Empty)
                    + (durationSeconds > 0 ? $"&duration={durationSeconds}" : string.Empty);

            _logger.LogDebug("[SpicyLyrics] GET {Url}", url);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-API-Key", apiKey);  // TODO: confirm header name
            if (spotifyToken is not null)
                req.Headers.Add("X-Spotify-Token", spotifyToken);  // TODO: confirm header name

            using var resp = await client.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[SpicyLyrics] HTTP {Status} for \"{Title}\" by {Artist}",
                    (int)resp.StatusCode, title, artist);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            return ParseLrc(doc.RootElement, title, artist);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[SpicyLyrics] Exception fetching \"{Title}\" by {Artist}", title, artist);
            return null;
        }
    }

    /// <summary>
    /// Extracts enhanced LRC text from the API response JSON.
    /// TODO: update property names once the real response shape is known.
    /// </summary>
    private string? ParseLrc(JsonElement root, string title, string artist)
    {
        // Option A: API returns ready-made LRC text
        if (root.TryGetProperty("lrc", out var lrcEl))
        {
            var lrc = lrcEl.GetString();
            if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
        }

        // Option B: API returns a "syncedLyrics" field (like LRCLIB)
        if (root.TryGetProperty("syncedLyrics", out var syncEl))
        {
            var lrc = syncEl.GetString();
            if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
        }

        // Option C: API returns a "lyrics" field (like SyncLRC)
        if (root.TryGetProperty("lyrics", out var lyricsEl))
        {
            var lrc = lyricsEl.GetString();
            if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
        }

        _logger.LogDebug("[SpicyLyrics] No usable lyrics field in response for \"{Title}\" by {Artist}",
            title, artist);
        return null;
    }
}
