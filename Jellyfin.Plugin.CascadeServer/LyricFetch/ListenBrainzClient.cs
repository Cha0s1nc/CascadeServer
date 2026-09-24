using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.CascadeServer.LyricFetch;

/// <summary>
/// Finds Spotify track ids for a song by title, artist and album through ListenBrainz
/// Labs' spotify-id-from-metadata, which maps from MetaBrainz's own copy of Spotify's
/// catalogue: no Spotify account, key or scraping. Measured on a real library sample, it
/// found 48 of 60 songs; the misses were gaps in its data, not in how songs were asked
/// for. It is a Labs (experimental) endpoint, so any failure just means "no ids".
/// </summary>
public static class ListenBrainzClient
{
    private const string Endpoint = "https://labs.api.listenbrainz.org/spotify-id-from-metadata/json";

    // MetaBrainz asks clients to identify themselves and not to hammer the service.
    // ponytail: one request at a time, at least 1s apart, server-wide; a real rate-limit
    // reader (X-RateLimit-* headers) only if this ever gets a 429.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _last = DateTime.MinValue;
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Spotify track ids for the song, most likely first (one per release); empty when
    /// ListenBrainz has none, <c>null</c> when it could not be asked (so a failure is not
    /// remembered as a miss). Asks with the album first, then without it, since a single
    /// or a compilation often files the same recording under another release name.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> FindAsync(
        HttpClient client, string artist, string album, string title, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return [];
        var ids = await QueryAsync(client, artist, album, title, ct);
        if (ids is { Count: 0 } && !string.IsNullOrWhiteSpace(album)) ids = await QueryAsync(client, artist, string.Empty, title, ct);
        return ids;
    }

    private static async Task<List<string>?> QueryAsync(
        HttpClient client, string artist, string album, string title, CancellationToken ct)
    {
        var url = $"{Endpoint}?artist_name={Uri.EscapeDataString(artist)}" +
                  $"&release_name={Uri.EscapeDataString(album)}&track_name={Uri.EscapeDataString(title)}";
        await Gate.WaitAsync(ct);
        try
        {
            var wait = _last + Spacing - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(LrclibClient.UserAgent);
            using var response = await client.SendAsync(request, ct);
            _last = DateTime.UtcNow;
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return [];
            if (!doc.RootElement[0].TryGetProperty("spotify_track_ids", out var list) || list.ValueKind != JsonValueKind.Array) return [];
            return list.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                .Where(SpicyLyricsClient.IsValidTrackId)
                .Select(id => id!)
                .Distinct()
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
