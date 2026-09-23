using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CascadeServer.LyricFetch;

/// <summary>Outcome of a SpicyLyrics request.</summary>
public enum SpicyLyricsStatus
{
    /// <summary>No secret key is set, so nothing was requested.</summary>
    NotConfigured,

    /// <summary>No lyrics, an error, or backing off after a rate limit.</summary>
    Miss,

    /// <summary>Lyrics were returned; the raw response is in the result.</summary>
    Hit,
}

/// <summary>A SpicyLyrics result; <c>RawJson</c> is set only on a hit.</summary>
public record SpicyLyricsResult(SpicyLyricsStatus Status, string? RawJson = null);

/// <summary>
/// Fetches syllable-timed lyrics from the SpicyLyrics API
/// (https://developers.spicylyrics.org/docs, checked 2026-09-22):
/// <c>GET https://api.spicylyrics.org/v1/lyrics/{spotifyTrackId}</c> with
/// <c>Authorization: Bearer sl_sk_...</c>, returning an envelope <c>{Body, Status, Type}</c>.
///
/// The response is returned raw and never interpreted here. The shape of Body.Content is
/// not publicly documented, and error codes and rate-limit headers are not either, so any
/// non-200 is a miss and a Retry-After, if sent, pauses all requests until it passes.
/// </summary>
public partial class SpicyLyricsClient
{
    private const string BaseUrl = "https://api.spicylyrics.org/v1";

    private static DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;

    private readonly ILogger _logger;

    /// <summary>Initialises a new instance of <see cref="SpicyLyricsClient"/>.</summary>
    public SpicyLyricsClient(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether <paramref name="id"/> looks like a Spotify track id (22 base62 chars).</summary>
    public static bool IsValidTrackId(string? id) => id is not null && TrackIdRegex().IsMatch(id);

    /// <summary>Requests lyrics for one Spotify track.</summary>
    public async Task<SpicyLyricsResult> FetchAsync(
        HttpClient client, string? secretKey, string spotifyTrackId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secretKey)) return new(SpicyLyricsStatus.NotConfigured);
        if (!IsValidTrackId(spotifyTrackId)) return new(SpicyLyricsStatus.Miss);
        if (DateTimeOffset.UtcNow < _backoffUntil) return new(SpicyLyricsStatus.Miss);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/lyrics/{spotifyTrackId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey.Trim());
            // Not required (confirmed with SpicyLyrics, 2026-09-23), but it tells them which
            // client a request came from, same identity LRCLIB gets.
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd(LrclibClient.UserAgent);

            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                NoteRetryAfter(response.Headers.RetryAfter);
                _logger.LogDebug("SpicyLyrics HTTP {Status} for {TrackId}", (int)response.StatusCode, spotifyTrackId);
                return new(SpicyLyricsStatus.Miss);
            }

            var raw = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(raw);   // only to refuse a non-JSON body
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Body", out _)
                ? new(SpicyLyricsStatus.Hit, raw)
                : new(SpicyLyricsStatus.Miss);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "SpicyLyrics fetch failed for {TrackId}", spotifyTrackId);
            return new(SpicyLyricsStatus.Miss);
        }
    }

    private void NoteRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        DateTimeOffset? until = retryAfter?.Delta is { } delta ? DateTimeOffset.UtcNow + delta : retryAfter?.Date;
        if (until is null || until <= _backoffUntil) return;
        _backoffUntil = until.Value;
        _logger.LogInformation("SpicyLyrics asked to retry after {Until}; pausing requests", until);
    }

    [GeneratedRegex("^[A-Za-z0-9]{22}$")]
    private static partial Regex TrackIdRegex();
}
