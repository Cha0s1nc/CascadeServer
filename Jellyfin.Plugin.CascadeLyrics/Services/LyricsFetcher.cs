using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CascadeLyrics.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CascadeLyrics.Services;

/// <summary>
/// Fetches lyrics for a single track from every source and writes them as sidecar files
/// next to the audio file:
///   {audioFile}.slrc — karaoke, word-level Enhanced LRC, from Kugou
///   {audioFile}.lrc  — synced,  line-level LRC, from LRCLIB syncedLyrics
///   {audioFile}.txt  — plain,   untimed text,   from LRCLIB plainLyrics
///
/// Sources are queried independently: finding karaoke on Kugou does not stop the LRCLIB
/// lookup. The goal is preservation, so we keep everything each source will give us.
///
/// Nothing already on disk is ever overwritten — a source is only queried when its own
/// output file is missing. That keeps hand-edited lyrics safe and stops repeat runs from
/// re-downloading the whole library.
///
/// Shared by the scheduled download task and the on-demand status recheck endpoint so the
/// two cannot drift apart.
/// </summary>
public class LyricsFetcher
{
    // LRCLIB asks callers to identify themselves rather than spoof a browser. Kugou keeps
    // the browser-ish UA set on the shared HttpClient.
    private const string LrclibUserAgent = "CascadeLyrics/1.0.0 (https://github.com/Cha0s1nc/CascadeSLRC)";

    private readonly ILogger _logger;
    private readonly KugouLyricsClient _kugou;

    /// <summary>Initialises a new instance of <see cref="LyricsFetcher"/>.</summary>
    public LyricsFetcher(ILogger logger)
    {
        _logger = logger;
        _kugou = new KugouLyricsClient(logger);
    }

    /// <summary>
    /// The set of sidecars written during one <see cref="EnsureAsync"/> call, plus the resulting
    /// status row. <c>QueriedNetwork</c> is false when every sidecar was already on disk and no
    /// source was contacted, which lets callers skip their inter-track rate-limit delay — on a
    /// fully populated library a repeat run is otherwise almost entirely sleeping.
    /// </summary>
    public record EnsureResult(
        LyricsStatusEntry Entry, bool WroteKaraoke, bool WroteSynced, bool WrotePlain, bool QueriedNetwork);

    /// <summary>
    /// Downloads whatever sidecars are missing for <paramref name="item"/> and returns the
    /// status entry describing what is on disk afterwards.
    /// </summary>
    public async Task<EnsureResult> EnsureAsync(Audio item, HttpClient client, CancellationToken ct)
    {
        var slrcPath = Path.ChangeExtension(item.Path, ".slrc");
        var lrcPath  = Path.ChangeExtension(item.Path, ".lrc");
        var txtPath  = Path.ChangeExtension(item.Path, ".txt");

        var title  = item.Name ?? string.Empty;
        var artist = item.AlbumArtists?.FirstOrDefault()
                  ?? item.Artists?.FirstOrDefault()
                  ?? string.Empty;
        var album  = item.Album ?? string.Empty;
        var duration = item.RunTimeTicks.HasValue
            ? (int)(item.RunTimeTicks.Value / TimeSpan.TicksPerSecond)
            : 0;

        var hadKaraoke = File.Exists(slrcPath);
        var wroteKaraoke = false;
        var wroteSynced  = false;
        var wrotePlain   = false;

        // Without a title and artist there is nothing to search on — just report the files.
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            return new EnsureResult(
                BuildEntry(title, artist, hadKaraoke, slrcPath, lrcPath, txtPath), false, false, false, false);

        var queriedNetwork = false;

        // ── Kugou → .slrc ────────────────────────────────────────────────────
        var kugouHit = false;
        if (!hadKaraoke)
        {
            queriedNetwork = true;
            var karaoke = await _kugou.FetchKaraokeAsync(client, title, artist, duration * 1000, ct);
            if (karaoke is not null)
            {
                await File.WriteAllTextAsync(slrcPath, karaoke, ct);
                _logger.LogDebug("Saved karaoke sidecar for \"{Title}\" by {Artist}", title, artist);
                kugouHit = true;
                wroteKaraoke = true;
            }
        }

        // ── LRCLIB → .lrc, falling back to .txt ──────────────────────────────
        // Runs regardless of what Kugou did. A track that already has only a .txt is still
        // re-queried, since LRCLIB may have gained a synced version since the last run.
        if (!File.Exists(lrcPath))
        {
            queriedNetwork = true;
            var (synced, plain) = await FetchLrclibAsync(client, title, artist, album, duration, ct);

            if (!string.IsNullOrWhiteSpace(synced))
            {
                await File.WriteAllTextAsync(lrcPath, synced, ct);
                _logger.LogDebug("Saved synced sidecar for \"{Title}\" by {Artist}", title, artist);
                wroteSynced = true;
            }
            else if (!string.IsNullOrWhiteSpace(plain) && !File.Exists(txtPath))
            {
                await File.WriteAllTextAsync(txtPath, plain, ct);
                _logger.LogDebug("Saved plain sidecar for \"{Title}\" by {Artist}", title, artist);
                wrotePlain = true;
            }
        }

        var entry = BuildEntry(title, artist, hadKaraoke || kugouHit, slrcPath, lrcPath, txtPath);
        return new EnsureResult(entry, wroteKaraoke, wroteSynced, wrotePlain, queriedNetwork);
    }

    private static LyricsStatusEntry BuildEntry(
        string title, string artist, bool kugouAvailable, string slrcPath, string lrcPath, string txtPath)
        => new()
        {
            Name = title,
            Artist = artist,
            KugouAvailable = kugouAvailable,
            HasKaraoke = File.Exists(slrcPath),
            HasSynced = File.Exists(lrcPath),
            HasPlain = File.Exists(txtPath),
            LastChecked = DateTime.UtcNow,
        };

    /// <summary>Queries LRCLIB and returns its synced and plain lyrics, either of which may be <c>null</c>.</summary>
    private async Task<(string? Synced, string? Plain)> FetchLrclibAsync(
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
            request.Headers.UserAgent.ParseAdd(LrclibUserAgent);

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
        => doc.RootElement.TryGetProperty(property, out var el) ? el.GetString() : null;
}
