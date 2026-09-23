using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CascadeServer.LyricFetch;

/// <summary>Fetches line-synced and plain lyrics from LRCLIB.</summary>
public class LrclibClient
{
    // LRCLIB asks callers to identify themselves rather than spoof a browser. Kugou keeps
    // the browser-ish UA set on the shared HttpClient.
    private const string UserAgent = "CascadeServer/2.0.0 (https://github.com/Cha0s1nc/CascadeServer)";

    private readonly ILogger _logger;

    /// <summary>Initialises a new instance of <see cref="LrclibClient"/>.</summary>
    public LrclibClient(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Queries LRCLIB and returns its synced and plain lyrics, either of which may be <c>null</c>.</summary>
    public async Task<(string? Synced, string? Plain)> FetchAsync(
        HttpClient client, string title, string artist, string album, int duration, CancellationToken ct)
    {
        try
        {
            var url = "https://lrclib.net/api/get"
                    + $"?track_name={Uri.EscapeDataString(title)}"
                    + $"&artist_name={Uri.EscapeDataString(artist)}"
                    + (album.Length > 0 ? $"&album_name={Uri.EscapeDataString(album)}" : string.Empty)
                    + (duration > 0    ? $"&duration={duration}"                       : string.Empty);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return (null, null);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            return (ReadString(doc, "syncedLyrics"), ReadString(doc, "plainLyrics"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LRCLIB fetch failed for \"{Title}\"", title);
            return (null, null);
        }
    }

    private static string? ReadString(JsonDocument doc, string property)
        => doc.RootElement.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
